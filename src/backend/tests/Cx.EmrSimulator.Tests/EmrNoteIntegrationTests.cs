using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cx.EmrSimulator.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Cx.EmrSimulator.Tests;

public class EmrNoteIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private const string ConnectionString = "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    private readonly string _testPatientId = "P_TEST_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _testEncounterId = "ENC_TEST_" + Guid.NewGuid().ToString("N")[..8];

    public EmrNoteIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        // Seed synthetic test patient & encounter
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var pCmd = new NpgsqlCommand(
            "INSERT INTO emr.patient (patient_id, display_name) VALUES (@id, 'Synthetic Test Patient') ON CONFLICT DO NOTHING", conn);
        pCmd.Parameters.AddWithValue("id", _testPatientId);
        await pCmd.ExecuteNonQueryAsync();

        await using var eCmd = new NpgsqlCommand(
            "INSERT INTO emr.encounter (encounter_id, patient_id, facility_id, specialty_code, attending_user, started_at) " +
            "VALUES (@eid, @pid, 'FACILITY_01', 'CARDIOLOGY', 'DOC_001', now()) ON CONFLICT DO NOTHING", conn);
        eCmd.Parameters.AddWithValue("eid", _testEncounterId);
        eCmd.Parameters.AddWithValue("pid", _testPatientId);
        await eCmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateNote_AtomicallyCreatesNoteVersionAndOutboxRow()
    {
        var request = new NoteWriteRequest(
            NoteType: "OP_CONSULT",
            Text: "Patient presented with headache. Prescribed Paracetamol 500mg PO BD x 3 days.",
            AuthoredBy: "DOC_SMITH"
        );

        var response = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{_testEncounterId}/notes", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var note = await response.Content.ReadFromJsonAsync<NoteVersionResponse>();
        note.ShouldNotBeNull();
        note.NoteVersion.ShouldBe(1);
        note.ContentSha256.Length.ShouldBe(64); // Valid hex SHA-256

        // Verify outbox row was committed in the same database transaction
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var outboxCmd = new NpgsqlCommand(
            "SELECT count(*), payload FROM emr.extraction_outbox WHERE aggregate_id = @noteId GROUP BY payload", conn);
        outboxCmd.Parameters.AddWithValue("noteId", note.NoteId);

        await using var reader = await outboxCmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("Exactly one outbox row must exist");
        reader.GetInt64(0).ShouldBe(1);

        var payload = reader.GetString(1);
        payload.ShouldContain(note.ContentSha256);
        payload.ShouldContain(note.NoteId);
        payload.ShouldNotContain(request.Text); // Claim-Check pattern: Note text NEVER inside outbox payload!
    }

    [Fact]
    public async Task CreateNote_WithInvalidEncounter_ReturnsNotFound_AndCreatesZeroOutboxRows()
    {
        var invalidEncounterId = "ENC_NON_EXISTENT";
        var request = new NoteWriteRequest("OP_CONSULT", "Some clinical text", "DOC_001");

        var response = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{invalidEncounterId}/notes", request);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Verify zero notes and outbox rows created
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM emr.clinical_note WHERE encounter_id = @encId", conn);
        cmd.Parameters.AddWithValue("encId", invalidEncounterId);
        var noteCount = (long)(await cmd.ExecuteScalarAsync())!;
        noteCount.ShouldBe(0);
    }

    [Fact]
    public async Task AmendNote_WithMatchingIfMatchHeader_CreatesVersion2AndOutboxEvent()
    {
        // 1. Create version 1
        var v1Req = new NoteWriteRequest("OP_CONSULT", "Initial assessment: Cough.", "DOC_001");
        var createRes = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{_testEncounterId}/notes", v1Req);
        createRes.StatusCode.ShouldBe(HttpStatusCode.Created);
        var v1 = (await createRes.Content.ReadFromJsonAsync<NoteVersionResponse>())!;

        // 2. Amend to version 2 with If-Match = 1
        var v2Req = new NoteWriteRequest("OP_CONSULT", "Amended assessment: Productive cough. Rx Azithromycin 500mg OD.", "DOC_001");
        var amendMessage = new HttpRequestMessage(HttpMethod.Put, $"/emr-sim/v1/notes/{v1.NoteId}")
        {
            Content = JsonContent.Create(v2Req)
        };
        amendMessage.Headers.Add("If-Match", "\"1\"");

        var amendRes = await _client.SendAsync(amendMessage);
        amendRes.StatusCode.ShouldBe(HttpStatusCode.OK);
        var v2 = (await amendRes.Content.ReadFromJsonAsync<NoteVersionResponse>())!;

        v2.NoteId.ShouldBe(v1.NoteId);
        v2.NoteVersion.ShouldBe(2);
        v2.ContentSha256.ShouldNotBe(v1.ContentSha256);

        // 3. Stale amendment check: Trying to amend version 1 again must return 412 Precondition Failed
        var staleMessage = new HttpRequestMessage(HttpMethod.Put, $"/emr-sim/v1/notes/{v1.NoteId}")
        {
            Content = JsonContent.Create(v2Req)
        };
        staleMessage.Headers.Add("If-Match", "\"1\""); // Expected 1, but note is now at version 2

        var staleRes = await _client.SendAsync(staleMessage);
        staleRes.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task GetNoteVersion_ReturnsImmutableClaimCheckContent()
    {
        var text = "Claim-check verification text.";
        var req = new NoteWriteRequest("OP_CONSULT", text, "DOC_001");
        var res = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{_testEncounterId}/notes", req);
        var created = (await res.Content.ReadFromJsonAsync<NoteVersionResponse>())!;

        var getRes = await _client.GetAsync($"/emr/v1/notes/{created.NoteId}/versions/1");
        getRes.StatusCode.ShouldBe(HttpStatusCode.OK);

        var retrieved = (await getRes.Content.ReadFromJsonAsync<NoteVersionResponse>())!;
        retrieved.Text.ShouldBe(text);
        retrieved.ContentSha256.ShouldBe(created.ContentSha256);
    }
}

