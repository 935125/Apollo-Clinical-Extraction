using System.Net.Http.Json;
using Apollo.Backend.Models;
using Npgsql;
using Apollo.Backend.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Apollo.Backend.Tests;

public class Stage7PostgresTests
{
    [PostgresFact]
    public async Task V0008_TransactionRollbackAndReapply_PreserveExistingAuditRows()
    {
        await using var db = new IsolatedDatabase(); await db.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = db.ConnectionString };
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var extraction = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        using var review = await client.PostAsJsonAsync($"/api/v1/extractions/{extraction.ExtractionId}/reviews", new EntityReviewSubmissionRequest("MEDICATION", 0, "ACCEPTED", ReviewComment: "Isolated migration test fixture; not clinician approval", ExpectedVersion: 0)); review.EnsureSuccessStatusCode();
        await using var conn = new NpgsqlConnection(db.ConnectionString); await conn.OpenAsync();
        await using (var prepare = new NpgsqlCommand("DROP INDEX ux_entity_review_version; GRANT UPDATE, DELETE ON entity_review_audit TO cx_runtime;", conn)) await prepare.ExecuteNonQueryAsync();
        string sql = await File.ReadAllTextAsync(Path.Combine(Evaluation.HeldOutBenchmarkRunner.Backend, "db/platform/migrations/V0008__clinical_review_audit.sql"));
        async Task<(int Count, bool Update, bool Index)> State()
        {
            await using var cmd = new NpgsqlCommand("SELECT (SELECT count(*)::int FROM entity_review_audit), has_table_privilege('cx_runtime','entity_review_audit','UPDATE'), to_regclass('ux_entity_review_version') IS NOT NULL", conn);
            await using var reader = await cmd.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetBoolean(1), reader.GetBoolean(2));
        }
        Assert.Equal((1,true,false), await State());
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var migrate = new NpgsqlCommand(sql, conn, tx); await migrate.ExecuteNonQueryAsync();
            Assert.Equal((1,false,true), await State()); await tx.RollbackAsync();
        }
        Assert.Equal((1,true,false), await State());
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await using var migrate = new NpgsqlCommand(sql, conn, tx); await migrate.ExecuteNonQueryAsync(); await tx.CommitAsync();
        }
        Assert.Equal((1,false,true), await State());
    }

    [PostgresFact]
    public async Task NeuralReviewCandidates_PersistWithSourceEvidence_WithoutBecomingPrescriptions()
    {
        await using var db = new IsolatedDatabase(); await db.Initialize();
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx", DatabaseConnection = db.ConnectionString };
        using var client = factory.CreateClient();
        const string note = "Past Medical History: Patient previously took Theophylline 200 mg daily, stopped 2 years ago.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note)); response.EnsureSuccessStatusCode();
        var extraction = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var saved = (await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{extraction.ExtractionId}"))!;
        Assert.DoesNotContain(saved.Medications, m => m.Name == "Theophylline");
        var candidate = Assert.Single(saved.ModelMetadata.ReviewCandidates!, c => c.Name == "Theophylline");
        Assert.Equal(candidate.Evidence.Quote, note[candidate.Evidence.Start!.Value..candidate.Evidence.End!.Value]);
        Assert.Equal("NEURAL_ONNX", saved.ModelMetadata.ExecutionMode); Assert.Equal(0, factory.Azure.Calls);
    }
    [PostgresFact]
    public async Task ExistingExtractionId_CannotOverwriteCandidateOrChildren()
    {
        await using var db = new IsolatedDatabase(); await db.Initialize();
        using var factory = new FoundryFactory { DatabaseConnection = db.ConnectionString };
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var original = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        var repo = factory.Services.GetRequiredService<IExtractionRepository>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => repo.SaveExtractionAsync("duplicate-test", original with { NoteText = "replacement must not persist", Medications = [] }));
        Assert.Equal("23505", error.SqlState);
        var unchanged = (await repo.GetExtractionAsync(original.ExtractionId))!;
        Assert.Equal(original.NoteText, unchanged.NoteText); Assert.Equal(original.Medications.Count, unchanged.Medications.Count);
        Assert.Equal(original.Medications.Count, await db.Count("extracted_medications")); Assert.Equal(1, await db.Count("extraction_runs"));
        var memory = new InMemoryExtractionRepository(); await memory.SaveExtractionAsync("first", original);
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.SaveExtractionAsync("duplicate", original with { Medications = [] }));
        Assert.Equal(original.Medications.Count, (await memory.GetExtractionAsync(original.ExtractionId))!.Medications.Count);
    }

}
