using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Apollo.Backend.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")))
            Skip = "Set TEST_POSTGRES_CONNECTION to run isolated real PostgreSQL/pgvector tests.";
    }
}
public sealed class IsolatedDatabase : IAsyncDisposable
{
    public string Schema { get; } = "foundry_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    private string Admin => Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION")!;
    public async Task Initialize()
    {
        string database = new NpgsqlConnectionStringBuilder(Admin).Database ?? "";
        if (!database.StartsWith("cx_audit_test", StringComparison.Ordinal))
            throw new InvalidOperationException("Destructive fixtures require a dedicated database named cx_audit_test*. Do not target the application database.");
        if (!Regex.IsMatch(Schema, "^foundry_test_[0-9a-f]{32}$")) throw new InvalidOperationException("Unsafe test schema");
        await using var admin = new NpgsqlConnection(Admin); await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA \"{Schema}\";", admin); await create.ExecuteNonQueryAsync();
        ConnectionString = new NpgsqlConnectionStringBuilder(Admin) { SearchPath = Schema + ",public", IncludeErrorDetail = false }.ConnectionString;
    }
    public async Task<int> Count(string table)
    {
        if (table is not ("extraction_runs" or "extracted_medications" or "extracted_investigations" or "oov_quarantine_items" or "entity_review_audit")) throw new ArgumentException("Invalid table");
        await using var conn = new NpgsqlConnection(ConnectionString); await conn.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*)::int FROM \"{Schema}\".{table};", conn);
        return (int)(await command.ExecuteScalarAsync())!;
    }
    public async ValueTask DisposeAsync()
    {
        if (!Regex.IsMatch(Schema, "^foundry_test_[0-9a-f]{32}$")) throw new InvalidOperationException("Unsafe test schema");
        await using var admin = new NpgsqlConnection(Admin); await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE;", admin); await drop.ExecuteNonQueryAsync();
    }
}
public class FoundryPostgresTests
{
    [PostgresFact]
    public async Task RuntimeRole_CanSaveAndReadWithoutSchemaOrDatabaseCreation()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        var adminRepository = new SqlExtractionRepository(database.ConnectionString, NullLogger<SqlExtractionRepository>.Instance);
        await adminRepository.EnsureSchemaAsync();
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA \"{database.Schema}\" TO cx_runtime; GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA \"{database.Schema}\" TO cx_runtime; GRANT DELETE ON extracted_medications, extracted_investigations TO cx_runtime;", admin);
            await grant.ExecuteNonQueryAsync();
        }
        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = "cx_runtime" }.ConnectionString;
        using var factory = new FoundryFactory { DatabaseConnection = connection, DatabaseAutoMigrate = false };
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.NotNull(await client.GetFromJsonAsync<ClinicalExtractionResponse>("/api/v1/extractions/" + result.ExtractionId));
        await using var runtime = new NpgsqlConnection(connection); await runtime.OpenAsync();
        await using var ddl = new NpgsqlCommand("CREATE TABLE forbidden_runtime_ddl(id int);", runtime);
        var error = await Assert.ThrowsAsync<PostgresException>(() => ddl.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
        await using var privileges = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper OR rolcreaterole FROM pg_roles WHERE rolname=current_user", runtime);
        Assert.False((bool)(await privileges.ExecuteScalarAsync())!);
    }
    [PostgresFact]
    public async Task TextAndPdf_PrimaryAndRealOnnx_SaveOneParentWithChildrenAndProviderMetadata()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        foreach (bool outage in new[] { false, true })
            foreach (bool pdf in new[] { false, true })
            {
                using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
                if (outage) factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
                using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, pdf);
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
                var restored = await client.GetFromJsonAsync<ClinicalExtractionResponse>("/api/v1/extractions/" + result.ExtractionId);
                Assert.NotNull(restored); Assert.Equal(outage ? "onnx" : "foundry", restored.ModelMetadata.Provider);
                Assert.Equal(outage ? "AZURE_UNAVAILABLE" : null, restored.ModelMetadata.FallbackReason);
                Assert.Equal(result.NoteText, restored.NoteText);
                Assert.Equal(3, restored.Medications.Count); Assert.Equal(2, restored.Investigations.Count);
                Assert.Contains(restored.Medications, m => m.Name.Equals("metformin", StringComparison.OrdinalIgnoreCase) && m.DoseUnit!.Contains("tablet") && m.Action == "continue");
                Assert.Equal(outage ? 1 : 0, factory.Local.Calls);
            }
        Assert.Equal(4, await database.Count("extraction_runs"));
        Assert.Equal(12, await database.Count("extracted_medications"));
        Assert.Equal(8, await database.Count("extracted_investigations"));
        Assert.Equal(12, await database.Count("oov_quarantine_items")); // CBC method, chest views and Penicillin remain unresolved for each run.
    }
    [PostgresFact]
    public async Task FailureDuringQuarantineInsert_RollsBackParentAndAllChildren_WithoutModelRetry()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        var repository = new SqlExtractionRepository(database.ConnectionString, NullLogger<SqlExtractionRepository>.Instance);
        await repository.EnsureSchemaAsync();
        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync();
            await using var trigger = new NpgsqlCommand("""
                CREATE FUNCTION reject_test_quarantine() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'synthetic quarantine failure'; END $$;
                CREATE TRIGGER reject_test_quarantine BEFORE INSERT ON oov_quarantine_items
                FOR EACH ROW EXECUTE FUNCTION reject_test_quarantine();
                """, conn);
            await trigger.ExecuteNonQueryAsync();
        }
        using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("PERSISTENCE_FAILED", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls);
        foreach (string table in new[] { "extraction_runs", "extracted_medications", "extracted_investigations", "oov_quarantine_items" }) Assert.Equal(0, await database.Count(table));
    }
    [PostgresFact]
    public async Task HistoricalRun_WithNullProviderMetadata_RemainsReadable()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        await using (var conn = new NpgsqlConnection(database.ConnectionString))
        {
            await conn.OpenAsync(); await using var update = new NpgsqlCommand("UPDATE extraction_runs SET provider_metadata = NULL;", conn); await update.ExecuteNonQueryAsync();
        }
        var restored = await client.GetFromJsonAsync<ClinicalExtractionResponse>("/api/v1/extractions/" + result.ExtractionId);
        Assert.NotNull(restored); Assert.Null(restored.ModelMetadata.Provider); Assert.Equal(3, restored.Medications.Count);
    }

    [PostgresFact]
    public async Task EntityReviewWorkflow_PersistsVersionHistory_ValidatesCodesAndComments_AndNeverOverwritesOriginalAiCandidate()
    {
        await using var database = new IsolatedDatabase();
        await database.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode();
        var extraction = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        string extId = extraction.ExtractionId;
        var origMed = extraction.Medications[0];

        // 1. Reject without comment must return 400 REVIEW_COMMENT_REQUIRED
        using var invalidReject = await client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "MEDICATION",
            EntityIndex: 0,
            ReviewStatus: "REJECTED",
            ReviewComment: null
        ));
        Assert.Equal(HttpStatusCode.BadRequest, invalidReject.StatusCode);
        Assert.Contains("REVIEW_COMMENT_REQUIRED", await invalidReject.Content.ReadAsStringAsync());

        // 2. Invalid RxNorm code format must return 400 INVALID_RXNORM_CODE
        using var invalidRxNorm = await client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "MEDICATION",
            EntityIndex: 0,
            ReviewStatus: "CORRECTED",
            CorrectedEntity: new Dictionary<string, object?> { ["name"] = origMed.Name, ["rxNormCode"] = "NOT-A-CODE" }
        ));
        Assert.Equal(HttpStatusCode.BadRequest, invalidRxNorm.StatusCode);
        Assert.Contains("INVALID_RXNORM_CODE", await invalidRxNorm.Content.ReadAsStringAsync());

        // 3. Submit v1 ACCEPTED decision
        using var v1Resp = await client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "MEDICATION",
            EntityIndex: 0,
            ReviewStatus: "ACCEPTED",
            ReviewComment: "Initial pharmacist verification."
        ));
        v1Resp.EnsureSuccessStatusCode();
        var v1 = (await v1Resp.Content.ReadFromJsonAsync<EntityReviewRecordDto>())!;
        Assert.Equal(1, v1.VersionNumber);
        Assert.Equal("ACCEPTED", v1.ReviewStatus);
        Assert.Equal("LOCAL_DEMO_MODE", v1.AuthMode);
        Assert.Equal("Unauthenticated Session (Local Demonstration Mode)", v1.ReviewerIdentity);

        // 4. Submit v2 CORRECTED decision modifying dose while preserving original AI candidate
        using var v2Resp = await client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "MEDICATION",
            EntityIndex: 0,
            ReviewStatus: "CORRECTED",
            CorrectedEntity: new Dictionary<string, object?>
            {
                ["name"] = origMed.Name,
                ["strength"] = "500 mg",
                ["dose"] = "2 tablets",
                ["route"] = "orally",
                ["frequency"] = "twice daily",
                ["rxNormCode"] = "6809"
            },
            ReviewComment: "Corrected administered tablet count per outpatient discharge reconciliation."
        ));
        v2Resp.EnsureSuccessStatusCode();
        var v2 = (await v2Resp.Content.ReadFromJsonAsync<EntityReviewRecordDto>())!;
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal("CORRECTED", v2.ReviewStatus);

        // 5. Submit investigation review (NEEDS_CLARIFICATION with comment)
        using var invResp = await client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "INVESTIGATION",
            EntityIndex: 0,
            ReviewStatus: "NEEDS_CLARIFICATION",
            ReviewComment: "Confirm automated vs manual differential CBC panel order."
        ));
        invResp.EnsureSuccessStatusCode();

        // 6. Read-after-write verification: GET /api/v1/extractions/{id} must return immutable original entities + full audit history
        var reloaded = await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{extId}");
        Assert.NotNull(reloaded);
        Assert.Equal(origMed.Name, reloaded.Medications[0].Name);
        Assert.Equal(origMed.Dose, reloaded.Medications[0].Dose);
        Assert.NotNull(reloaded.EntityReviews);
        Assert.Equal(3, reloaded.EntityReviews.Count);
        Assert.Equal(3, await database.Count("entity_review_audit"));
    }

    [PostgresFact]
    public async Task EntityReviewWorkflow_ConcurrentSubmissions_ProduceStrictlyMonotonicUniqueVersionNumbers()
    {
        await using var database = new IsolatedDatabase();
        await database.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode();
        var extraction = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        string extId = extraction.ExtractionId;

        var tasks = Enumerable.Range(1, 5).Select(i => client.PostAsJsonAsync($"/api/v1/extractions/{extId}/reviews", new EntityReviewSubmissionRequest(
            EntityType: "MEDICATION",
            EntityIndex: 0,
            ReviewStatus: "ACCEPTED",
            ReviewComment: $"Concurrent reviewer check #{i}"
        ))).ToArray();

        var responses = await Task.WhenAll(tasks);
        int succeeded = responses.Count(r => r.IsSuccessStatusCode);
        Assert.True(succeeded >= 1, "At least one concurrent review must succeed under serializable isolation.");
        foreach (var r in responses)
        {
            // Under Serializable isolation, either 200 OK or a serialization retry/conflict may occur; no partial or duplicate versions allowed
            Assert.True(r.IsSuccessStatusCode || (int)r.StatusCode >= 400);
        }

        var reviews = await client.GetFromJsonAsync<List<EntityReviewRecordDto>>($"/api/v1/extractions/{extId}/reviews");
        Assert.NotNull(reviews);
        Assert.Equal(succeeded, reviews.Count);
        Assert.Equal(Enumerable.Range(1, succeeded), reviews.Select(r => r.VersionNumber).OrderBy(v => v));
    }

    [PostgresFact]
    public async Task Stage6_StaleConcurrentReview_Is409_AndAuditRemainsImmutable()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient();
        using var extracted = await FoundryWorkflowTests.Submit(client, false); extracted.EnsureSuccessStatusCode();
        var original = (await extracted.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        string path = $"/api/v1/extractions/{original.ExtractionId}/reviews";
        var request = new EntityReviewSubmissionRequest("MEDICATION", 0, "ACCEPTED", ExpectedVersion: 0);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PostAsJsonAsync(path, request)));
        Assert.Single(outcomes, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(4, outcomes.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var reviews = (await client.GetFromJsonAsync<List<EntityReviewRecordDto>>(path))!;
        Assert.Single(reviews); Assert.Equal(1, reviews[0].VersionNumber);
        Assert.Equal(original.Medications[0].Evidence, reviews[0].OriginalEvidence);
        var reloaded = (await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{original.ExtractionId}"))!;
        Assert.Equal(original.Medications[0].Dose, reloaded.Medications[0].Dose);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        string migration = await File.ReadAllTextAsync(Path.Combine(Evaluation.HeldOutBenchmarkRunner.Backend, "db", "platform", "migrations", "V0008__clinical_review_audit.sql"));
        await using (var migrate = new NpgsqlCommand(migration, connection)) await migrate.ExecuteNonQueryAsync();
        await using var privileges = new NpgsqlCommand("SELECT has_table_privilege('cx_runtime', 'entity_review_audit', 'SELECT'), has_table_privilege('cx_runtime', 'entity_review_audit', 'INSERT'), has_table_privilege('cx_runtime', 'entity_review_audit', 'UPDATE'), has_table_privilege('cx_runtime', 'entity_review_audit', 'DELETE')", connection);
        await using var reader = await privileges.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1)); Assert.False(reader.GetBoolean(2)); Assert.False(reader.GetBoolean(3));
    }
}

