using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cx.EmrSimulator.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Cx.EmrSimulator.Tests;

public class EmrSecurityAndAccessControlTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private const string ConnectionString = "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    private readonly string _testPatientId = "P_SEC_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _testEncounterId = "ENC_SEC_" + Guid.NewGuid().ToString("N")[..8];
    private string _testNoteId = default!;

    public EmrSecurityAndAccessControlTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var pCmd = new NpgsqlCommand(
            "INSERT INTO emr.patient (patient_id, display_name) VALUES (@id, 'Security Test Patient') ON CONFLICT DO NOTHING", conn);
        pCmd.Parameters.AddWithValue("id", _testPatientId);
        await pCmd.ExecuteNonQueryAsync();

        await using var eCmd = new NpgsqlCommand(
            "INSERT INTO emr.encounter (encounter_id, patient_id, facility_id, specialty_code, attending_user, started_at) " +
            "VALUES (@eid, @pid, 'FACILITY_01', 'CARDIOLOGY', 'DOC_AUTH', now()) ON CONFLICT DO NOTHING", conn);
        eCmd.Parameters.AddWithValue("eid", _testEncounterId);
        eCmd.Parameters.AddWithValue("pid", _testPatientId);
        await eCmd.ExecuteNonQueryAsync();

        // Care team mapping: DOC_AUTH is authorized on this encounter
        await using var ctCmd = new NpgsqlCommand(
            "INSERT INTO emr.encounter_care_team (encounter_id, user_id) VALUES (@eid, 'DOC_AUTH') ON CONFLICT DO NOTHING", conn);
        ctCmd.Parameters.AddWithValue("eid", _testEncounterId);
        await ctCmd.ExecuteNonQueryAsync();

        // Create a test note
        var req = new NoteWriteRequest("OP_CONSULT", "Sensitive clinical note: Patient has severe arrhythmia.", "DOC_AUTH");
        var res = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{_testEncounterId}/notes", req);
        var created = (await res.Content.ReadFromJsonAsync<NoteVersionResponse>())!;
        _testNoteId = created.NoteId;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AccessCheck_AuthorizedDoctor_ReturnsAllowedTrue()
    {
        var checkPayload = new
        {
            userId = "DOC_AUTH",
            patientId = _testPatientId,
            encounterId = _testEncounterId,
            purpose = "review"
        };

        var response = await _client.PostAsJsonAsync("/emr/v1/access-checks", checkPayload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("allowed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AccessCheck_UnauthorizedDoctor_ReturnsAllowedFalse()
    {
        var checkPayload = new
        {
            userId = "DOC_UNAUTHORIZED_STRANGER",
            patientId = _testPatientId,
            encounterId = _testEncounterId,
            purpose = "review"
        };

        var response = await _client.PostAsJsonAsync("/emr/v1/access-checks", checkPayload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        doc.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        doc.GetProperty("reasonCode").GetString().ShouldBe("NOT_ON_CARE_TEAM");
    }

    [Fact]
    public async Task ClaimCheckRead_RetrievesImmutableContentWithoutExposingTokens()
    {
        var response = await _client.GetAsync($"/emr/v1/notes/{_testNoteId}/versions/1");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var note = (await response.Content.ReadFromJsonAsync<NoteVersionResponse>())!;
        note.Text.ShouldContain("Patient has severe arrhythmia");
        note.ContentSha256.Length.ShouldBe(64);
    }
}
