using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Models;

namespace Apollo.Backend.Tests;

public class Stage8PostgresProvenanceTests
{
    [PostgresFact]
    public async Task PrimaryFallbackAndAuthorizedOffline_KeepDistinctRoutingAfterDatabaseReload()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        var saved = new List<object>();
        foreach (string mode in new[] { "primary", "fallback", "explicit_offline" })
        {
            using var factory = new FoundryFactory { DatabaseConnection = database.ConnectionString, PrimaryProvider = mode == "explicit_offline" ? "LocalOnnx" : "MicrosoftFoundry" };
            if (mode == "fallback") factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
            using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Correlation-ID", "stage8-pg-"+mode);
            using var response = await FoundryWorkflowTests.Submit(client, mode == "explicit_offline"); response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
            var reloaded = (await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{result.ExtractionId}"))!;
            Assert.Equal(JsonSerializer.Serialize(result.ModelMetadata), JsonSerializer.Serialize(reloaded.ModelMetadata));
            Assert.Equal(mode, reloaded.ModelMetadata.RoutingMode); Assert.Equal(mode == "fallback", reloaded.ModelMetadata.FallbackTriggered);
            Assert.Equal(mode == "primary" ? "MicrosoftFoundry" : "LocalOnnx", reloaded.ModelMetadata.FinalExecutedProvider);
            Assert.Equal(mode == "explicit_offline" ? "LocalOnnx" : "MicrosoftFoundry", reloaded.ModelMetadata.RequestedPrimaryProvider);
            Assert.Equal("source_validated_requires_review", reloaded.ModelMetadata.ValidationResult);
            Assert.All(reloaded.ModelMetadata.Attempts!, attempt => Assert.Equal(reloaded.CorrelationId, attempt.CorrelationId));
            saved.Add(new { mode, extractionId=result.ExtractionId, modelMetadata=reloaded.ModelMetadata, simulatedPrimaryHttpRequests=factory.Azure.Calls, localExtractions=factory.Local.Calls });
        }
        Assert.Equal(3, await database.Count("extraction_runs")); Assert.Equal(0, await database.Count("entity_review_audit"));
        string directory = Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(Evaluation.HeldOutBenchmarkRunner.Backend,"..","docs/stage8-evidence");
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory,"postgres-provenance.json"),JsonSerializer.Serialize(new { isolatedDatabase="cx_audit_test", rows=saved, applicationDatabaseTargeted=false },new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true}));
    }
}
