using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Apollo.Backend.Tests;

public class Stage7BrowserFixtureTests
{
    [PostgresFact]
    public async Task IsolatedBrowserFixture_CanSeedSavedProviderResultsWithoutCallingModels()
    {
        var database = new IsolatedDatabase(); await database.Initialize();
        bool retain = Environment.GetEnvironmentVariable("STAGE7_RETAIN_BROWSER_FIXTURE") == "true";
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            string backend = Evaluation.HeldOutBenchmarkRunner.Backend;
            var primary = JsonSerializer.Deserialize<ClinicalExtractionResponse>(await File.ReadAllTextAsync(Path.Combine(backend, "artifacts/live-final-verification/foundry-final-extraction.json")), options)!;
            using var localCapture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backend, "..", "docs/stage6-evidence/after.json")));
            var local = localCapture.RootElement.GetProperty("cases")[0].GetProperty("finalPipeline").Deserialize<ClinicalExtractionResponse>(options)!;
            var repository = new SqlExtractionRepository(database.ConnectionString, NullLogger<SqlExtractionRepository>.Instance);
            primary = primary with { ExtractionId = "1713c76e-b355-4be3-b02f-e9d581b82415", EntityReviews = [], Saved = true };
            local = local with { ExtractionId = "639ebd21-ece0-4180-b2cd-7eda82f0e820", EntityReviews = [], Saved = true };
            await repository.SaveExtractionAsync(primary.ExtractionId, primary);
            await repository.SaveExtractionAsync(local.ExtractionId, local);
            Assert.Equal(2, await database.Count("extraction_runs")); Assert.Equal(0, await database.Count("entity_review_audit"));
            if (retain)
                await File.WriteAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(backend, "..", "docs", "stage8-evidence"), "browser-fixture.json"), JsonSerializer.Serialize(new
                {
                    schema = database.Schema, database = "cx_audit_test", seededExtractions = 2, liveFoundryCalls = 0,
                    isolation = "Copied synthetic saved-provider fixtures; no original records or existing review decisions copied/modified"
                }, options));
        }
        finally { if (!retain) await database.DisposeAsync(); }
    }
}
