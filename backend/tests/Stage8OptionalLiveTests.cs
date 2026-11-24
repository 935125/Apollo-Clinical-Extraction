using System.Net.Http.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Apollo.Backend.Tests;

public sealed class Stage8LiveAuthorizationAttribute : FactAttribute
{
    public Stage8LiveAuthorizationAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUTHORIZE_STAGE8_SINGLE_LIVE_CALL") != "SYNTHETIC_ONE_REQUEST_NO_RETRIES")
            Skip = "Future explicit authorization only: one synthetic Foundry request, no retries/fallback, in-memory persistence.";
    }
}

public sealed class Stage8SingleRequestBudget : DelegatingHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Interlocked.Increment(ref Calls) != 1) throw new InvalidOperationException("Single-live-request budget exhausted");
        return base.SendAsync(request, ct);
    }
}

public class Stage8OptionalLiveTests
{
    [Stage8LiveAuthorization]
    public async Task OptionalSingleAuthorizedGptRequest_NoRetries_NoFallback_NoApplicationDatabase()
    {
        string directory = Path.Combine(Evaluation.HeldOutBenchmarkRunner.Backend,"logs","stage8-live-authorization"); Directory.CreateDirectory(directory);
        // Consumed before sending. Repeated runs require a separately reviewed new authorization.
        await using var marker = new FileStream(Path.Combine(directory,"single-call.consumed"),FileMode.CreateNew);
        var budget = new Stage8SingleRequestBudget();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtractionRepository>(); services.AddSingleton<IExtractionRepository,InMemoryExtractionRepository>();
                services.PostConfigure<ExtractionOptions>(o => { o.Provider="MicrosoftFoundry"; o.FallbackProvider="None"; o.AllowDirectLocal=false; });
                services.PostConfigure<FoundryOptions>(o => { o.MaxRetries=0; o.AllowLocalFallback=false; o.AllowParsingFallback=false; o.MaxCompletionTokens=2048; });
                services.AddHttpClient<IFoundryClinicalExtractor,FoundryClinicalExtractor>().AddHttpMessageHandler(() => budget);
            });
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/extractions",new ClinicalExtractionRequest("Plan: Continue metformin 500 mg, one tablet by mouth once daily. Order CBC today."));
        Assert.Equal(1,budget.Calls); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("MicrosoftFoundry",result.ModelMetadata.FinalExecutedProvider); Assert.False(result.ModelMetadata.FallbackTriggered);
        await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),System.Text.Json.JsonSerializer.Serialize(result));
    }
}
