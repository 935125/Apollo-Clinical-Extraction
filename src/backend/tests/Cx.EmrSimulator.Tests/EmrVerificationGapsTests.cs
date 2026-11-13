using System.Net;
using System.Net.Http.Json;
using Cx.EmrSimulator.Models;
using Cx.EmrSimulator.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Cx.EmrSimulator.Tests;

public class EmrVerificationGapsTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private const string ConnectionString = "Host=localhost;Port=5433;Database=emr_sim;Username=postgres;Password=";
    private readonly string _testPatientId = "P_VERIF_" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _testEncounterId = "ENC_VERIF_" + Guid.NewGuid().ToString("N")[..8];

    public EmrVerificationGapsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var pCmd = new NpgsqlCommand(
            "INSERT INTO emr.patient (patient_id, display_name) VALUES (@id, 'Verif Patient') ON CONFLICT DO NOTHING", conn);
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
    public async Task Gap1_FaultInjection_OutboxFailureRollsBackClinicalNoteCompletely()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<EmrNoteService>();
        var request = new NoteWriteRequest("OP_CONSULT", "Fault injection text", "DOC_FAIL");

        // Execute fault injection which crashes before outbox commit
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await service.CreateNoteWithFaultInjectedOutboxAsync(_testEncounterId, request);
        });

        // Verify with independent connection that NEITHER note, version, NOR outbox exists
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var noteCmd = new NpgsqlCommand(
            "SELECT count(*) FROM emr.clinical_note_version WHERE authored_by = 'DOC_FAIL'", conn);
        var versionCount = (long)(await noteCmd.ExecuteScalarAsync())!;
        versionCount.ShouldBe(0, "Note version must be rolled back");

        await using var outboxCmd = new NpgsqlCommand(
            "SELECT count(*) FROM emr.extraction_outbox WHERE payload::text LIKE '%DOC_FAIL%'", conn);
        var outboxCount = (long)(await outboxCmd.ExecuteScalarAsync())!;
        outboxCount.ShouldBe(0, "Outbox event must not be committed");
    }

    [Fact]
    public async Task Gap2_ConcurrentAmendments_PreserveMonotonicVersionOrderingAndConflictDetection()
    {
        // 1. Create note version 1
        var v1Req = new NoteWriteRequest("OP_CONSULT", "Initial note text", "DOC_001");
        var createRes = await _client.PostAsJsonAsync($"/emr-sim/v1/encounters/{_testEncounterId}/notes", v1Req);
        createRes.StatusCode.ShouldBe(HttpStatusCode.Created);
        var note = (await createRes.Content.ReadFromJsonAsync<NoteVersionResponse>())!;

        // 2. Launch two concurrent amendments attempting to amend version 1 simultaneously
        var amendReqA = new NoteWriteRequest("OP_CONSULT", "Amendment A by Doctor A", "DOC_A");
        var amendReqB = new NoteWriteRequest("OP_CONSULT", "Amendment B by Doctor B", "DOC_B");

        var taskA = Task.Run(async () =>
        {
            using var msg = new HttpRequestMessage(HttpMethod.Put, $"/emr-sim/v1/notes/{note.NoteId}")
            {
                Content = JsonContent.Create(amendReqA)
            };
            msg.Headers.Add("If-Match", "\"1\"");
            return await _client.SendAsync(msg);
        });

        var taskB = Task.Run(async () =>
        {
            using var msg = new HttpRequestMessage(HttpMethod.Put, $"/emr-sim/v1/notes/{note.NoteId}")
            {
                Content = JsonContent.Create(amendReqB)
            };
            msg.Headers.Add("If-Match", "\"1\"");
            return await _client.SendAsync(msg);
        });

        var responses = await Task.WhenAll(taskA, taskB);

        // Exactly one should succeed with 200 OK (Version 2)
        // The other must be rejected with 412 Precondition Failed (stale version update detected)
        var statusCodes = responses.Select(r => r.StatusCode).ToList();
        statusCodes.ShouldContain(HttpStatusCode.OK);
        statusCodes.ShouldContain(HttpStatusCode.PreconditionFailed);

        // Verify database current_version is exactly 2
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var verCmd = new NpgsqlCommand(
            "SELECT current_version FROM emr.clinical_note WHERE note_id = @id", conn);
        verCmd.Parameters.AddWithValue("id", note.NoteId);
        var currentVersion = (int)(await verCmd.ExecuteScalarAsync())!;
        currentVersion.ShouldBe(2);
    }

    [Fact]
    public async Task Gap4_DatabaseRolePermissions_PreventWorkerFromMutatingOrdersOrAudit()
    {
        // Connect to CX platform DB as cx_worker_role
        // Verify it cannot DELETE from audit or INSERT into order/writeback
        var cxWorkerConnStr = "Host=localhost;Port=5432;Database=cx_platform;Username=postgres;Password=";
        await using var conn = new NpgsqlConnection(cxWorkerConnStr);
        await conn.OpenAsync();

        // Switch session role to cx_worker_role
        await using var roleCmd = new NpgsqlCommand("SET ROLE cx_worker_role;", conn);
        await roleCmd.ExecuteNonQueryAsync();

        // 1. Worker should be prohibited from deleting from audit.audit_event
        await using var delCmd = new NpgsqlCommand("DELETE FROM audit.audit_event;", conn);
        await Should.ThrowAsync<PostgresException>(async () =>
        {
            await delCmd.ExecuteNonQueryAsync();
        });

        // 2. Worker should be prohibited from inserting into writeback_item
        await using var insCmd = new NpgsqlCommand(
            "INSERT INTO cx.writeback_item (writeback_item_id, writeback_operation_id, review_decision_id, source_system, encounter_id, master_item_code, emr_idempotency_key, order_payload, status) " +
            "VALUES (uuidv7(), uuidv7(), uuidv7(), 'EMR', 'ENC', 'DRUG01', uuidv7(), '{}'::jsonb, 'pending');", conn);
        await Should.ThrowAsync<PostgresException>(async () =>
        {
            await insCmd.ExecuteNonQueryAsync();
        });
    }
}

