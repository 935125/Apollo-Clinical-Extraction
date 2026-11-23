using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;

namespace Apollo.Backend.Tests;

public sealed class Stage8Clock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan value) => now += value;
}

public class Stage8RoutingTests
{
    [Fact]
    public async Task ValidGptAndReadiness_DoNotEvenInitializeMissingOnnx()
    {
        using var factory = new FoundryFactory { MissingModel = true };
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(0, factory.Azure.Calls);
        using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(1, factory.Repository.Saves);
        Assert.Equal("MicrosoftFoundry", result.ModelMetadata.RequestedPrimaryProvider);
        Assert.Equal("MicrosoftFoundry", result.ModelMetadata.FinalExecutedProvider);
        Assert.False(result.ModelMetadata.FallbackTriggered); Assert.Equal("primary", result.ModelMetadata.RoutingMode);
        Assert.Equal("accepted", result.ModelMetadata.PrimaryInvocationStatus);
        Assert.Equal("source_validated_requires_review", result.ModelMetadata.ValidationResult);
        Assert.Single(result.ModelMetadata.Attempts!); Assert.Equal(1, result.ModelMetadata.Attempts![0].HttpAttempts);
    }

    [Theory]
    [InlineData("invalid-evidence")]
    [InlineData("cross-drug")]
    public async Task ClinicalEvidenceRejection_CannotBeEnabledAsParsingFallback(string mode)
    {
        using var factory = new FoundryFactory { AllowParsingFallback = true }; factory.Azure.Mode = mode;
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("AZURE_INVALID_EVIDENCE", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("bad-schema")]
    [InlineData("truncated")]
    public async Task ParsingFallback_RequiresExplicitPolicyAndRetainsPrimaryRejection(string mode)
    {
        using var factory = new FoundryFactory { AllowParsingFallback = true }; factory.Azure.Mode = mode;
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.True(result.ModelMetadata.FallbackTriggered); Assert.Equal("fallback", result.ModelMetadata.RoutingMode);
        Assert.Equal("MicrosoftFoundry", result.ModelMetadata.RequestedPrimaryProvider); Assert.Equal("LocalOnnx", result.ModelMetadata.FinalExecutedProvider);
        Assert.Equal(1, factory.Local.Calls); Assert.Equal(1, factory.Azure.Calls);
        Assert.Equal("rejected", result.ModelMetadata.Attempts![0].Outcome); Assert.True(result.ModelMetadata.Attempts![1].OnnxWindows > 0);
        Assert.Equal(mode == "truncated" ? "incomplete" : "failed", result.ModelMetadata.SchemaValidationStatus);
    }

    [Fact]
    public async Task MissingDeploymentAndUnauthorizedOffline_FailReadinessWithoutSubstitution()
    {
        using var missing = new FoundryFactory { DeploymentOverride = "" }; using var client = missing.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, missing.Azure.Calls); Assert.Equal(0, missing.Local.Calls);
        using var unauthorized = new FoundryFactory { PrimaryProvider = "LocalOnnx", AllowDirectLocal = false }; using var offline = unauthorized.CreateClient();
        using var failed = await FoundryWorkflowTests.Submit(offline, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode); Assert.Contains("DIRECT_LOCAL_NOT_AUTHORIZED", await failed.Content.ReadAsStringAsync());
        Assert.Equal(0, unauthorized.Azure.Calls); Assert.Equal(0, unauthorized.Local.Calls);
    }

    [Fact]
    public async Task AuthorizedOffline_IsNotFoundryFallback()
    {
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx" }; using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, true); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("LocalOnnx", result.ModelMetadata.RequestedPrimaryProvider); Assert.Equal("LocalOnnx", result.ModelMetadata.FinalExecutedProvider);
        Assert.Equal("explicit_offline", result.ModelMetadata.RoutingMode); Assert.Equal("not_attempted_offline", result.ModelMetadata.PrimaryInvocationStatus);
        Assert.False(result.ModelMetadata.FallbackTriggered); Assert.Null(result.ModelMetadata.FallbackReason); Assert.Equal(0, factory.Azure.Calls);
        Assert.Equal("NEURAL_ONNX", result.ModelMetadata.ExecutionMode); Assert.True(result.ModelMetadata.Attempts![0].OnnxWindows > 0);
    }

    [Fact]
    public async Task TransientRetrySuccess_PreservesCorrelationAndNeverCallsOnnx()
    {
        using var factory = new FoundryFactory { MaxRetries = 2 };
        factory.Azure.StatusSequence.Enqueue(HttpStatusCode.ServiceUnavailable); factory.Azure.StatusSequence.Enqueue(HttpStatusCode.OK);
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Correlation-ID", "stage8-retry-correlation");
        using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(2, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(1, factory.Repository.Saves);
        Assert.All(factory.Azure.CorrelationIds, value => Assert.Equal(result.CorrelationId, value));
        Assert.Equal(2, result.ModelMetadata.Attempts![0].HttpAttempts); Assert.Equal(result.CorrelationId, result.ModelMetadata.Attempts![0].CorrelationId);
    }

    [Fact]
    public async Task RetryAfterBeyondBudget_DoesNotRetryEarlierThanServerGuidance()
    {
        using var factory = new FoundryFactory { MaxRetries = 4, TimeoutSeconds = .1 }; factory.Azure.Status = HttpStatusCode.TooManyRequests; factory.Azure.RetryAfterSeconds = 60;
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(1, factory.Azure.Calls); Assert.Equal("AZURE_RATE_LIMITED", result.ModelMetadata.FallbackReason); Assert.Equal(1, factory.Local.Calls);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterExhaustedRequests_AndAllowsBoundedRecoveryProbe()
    {
        var clock = new Stage8Clock(); using var factory = new FoundryFactory { CircuitThreshold = 2, Clock = clock }; factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
        using var client = factory.CreateClient();
        for (int i = 0; i < 2; i++) { using var response = await FoundryWorkflowTests.Submit(client, false); response.EnsureSuccessStatusCode(); }
        using var opened = await FoundryWorkflowTests.Submit(client, false); opened.EnsureSuccessStatusCode();
        var fallback = (await opened.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(2, factory.Azure.Calls); Assert.Equal(3, factory.Local.Calls); Assert.Equal("AZURE_CIRCUIT_OPEN", fallback.ModelMetadata.FallbackReason);
        Assert.Equal(0, fallback.ModelMetadata.Attempts![0].HttpAttempts);
        clock.Advance(TimeSpan.FromSeconds(31)); factory.Azure.Status = HttpStatusCode.OK;
        using var recovered = await FoundryWorkflowTests.Submit(client, false); recovered.EnsureSuccessStatusCode();
        var result = (await recovered.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("primary", result.ModelMetadata.RoutingMode); Assert.Equal(3, factory.Azure.Calls); Assert.Equal(3, factory.Local.Calls);
    }

    [Theory]
    [InlineData(401, "success", 401)]
    [InlineData(403, "success", 403)]
    [InlineData(400, "context", 422)]
    public async Task NonTransientFaults_AreNotRetriedOrConvertedToSuccessfulFallback(int status, string mode, int expected)
    {
        using var factory = new FoundryFactory { MaxRetries = 4 }; factory.Azure.Status = (HttpStatusCode)status; factory.Azure.Mode = mode;
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(expected, (int)response.StatusCode); Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
        Assert.DoesNotContain("synthetic-test-key", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task ClientOptions_CannotSelectDirectOnnxOrOverrideOperatorRouting()
    {
        using var factory = new FoundryFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(FoundryWorkflowTests.Note, Options: new() { ["provider"]="LocalOnnx", ["allowDirectLocal"]=true }));
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("MicrosoftFoundry", result.ModelMetadata.FinalExecutedProvider); Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls);
    }

}
