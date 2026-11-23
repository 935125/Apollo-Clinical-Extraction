using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Apollo.Backend.Tests;

public sealed class TrackingRepository : InMemoryExtractionRepository
{
    public int Saves;
    public bool FailWrites;
    public override Task SaveExtractionAsync(string id, ClinicalExtractionResponse response, CancellationToken ct = default, IReadOnlyList<QuarantineItemDto>? quarantineItems = null)
    {
        Saves++;
        if (FailWrites) throw new InvalidOperationException("Synthetic persistence failure");
        return base.SaveExtractionAsync(id, response, ct, quarantineItems);
    }
}
public sealed class CountingLocal(IServiceProvider services) : ILocalClinicalExtractor
{
    public string ProviderName => "LocalOnnx";
    public int Calls;
    public Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return new OnnxClinicalExtractor(services).ExtractAsync(source, correlationId, ct);
    }
}
public sealed class SimulatedAzure : HttpMessageHandler
{
    public int Calls;
    public string Mode = "success";
    public HttpStatusCode Status = HttpStatusCode.OK;
    public readonly System.Collections.Concurrent.ConcurrentQueue<HttpStatusCode> StatusSequence = new();
    public readonly System.Collections.Concurrent.ConcurrentBag<string> CorrelationIds = new();
    public int? RetryAfterSeconds;
    public string? Source;
    public JsonElement? Request;
    public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        CorrelationIds.Add(request.Headers.TryGetValues("X-Correlation-ID", out var correlations) ? correlations.Single() : "not_recorded");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Request = body.RootElement.Clone();
        Source = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Equal("gpt-6.1-sol", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("https://synthetic.openai.azure.com/openai/v1/chat/completions", request.RequestUri!.ToString());
        Started.TrySetResult();
        if (Mode == "network") throw new HttpRequestException("synthetic connection failure");
        if (Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var currentStatus = StatusSequence.TryDequeue(out var queuedStatus) ? queuedStatus : Status;
        if (currentStatus != HttpStatusCode.OK)
        {
            var failed = new HttpResponseMessage(currentStatus) { Content = JsonContent.Create(new { error = new { code = Mode == "policy" ? "content_filter" : Mode == "context" ? "context_length_exceeded" : "synthetic_failure" } }) };
            if (RetryAfterSeconds is int seconds) failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return failed;
        }
        if (Mode == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{invalid JSON") };
        var candidate = Candidates(Source!);
        if (Mode == "empty") candidate = new([], []);
        if (Mode == "invalid-evidence") candidate = candidate with { Medications = candidate.Medications.Select((m, n) => n == 0 ? m with { Evidence = new("fabricated evidence", 1) } : m).ToList() };
        if (Mode == "cross-drug") candidate = candidate with { Medications = candidate.Medications.Select(m => m.Name == "amlodipine" ? m with { Strength = "500 mg" } : m).ToList() };
        if (Mode == "repeated") candidate = new([], [new("CBC", "today", null, "new_order", "preliminary", new("Order a CBC today.", 2))]);
        return new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                model = "gpt-6.1-sol-2026-09-29",
                choices = new[] { new { finish_reason = Mode == "truncated" ? "length" : "stop", message = new { content = Mode == "bad-schema" ? "{}" : JsonSerializer.Serialize(candidate, ClinicalCandidateJson.Options), refusal = Mode == "refusal" ? "synthetic policy refusal" : null } } },
                usage = new { prompt_tokens = 71, completion_tokens = 29, total_tokens = 100 }
            })
        };
    }
    public static ClinicalCandidate Candidates(string source)
    {
        if (!source.Contains("metformin", StringComparison.OrdinalIgnoreCase)) return new([], [new("CBC", "today", null, "new_order", "preliminary", new("Order a CBC today.", 1))]);
        return new([
            new("metformin", "500 mg", "two tablets", "tablets", "by mouth", "twice daily", "7 days", "tablets", null, "continue", "current_medication", new("Continue metformin 500 mg tablets, two tablets by mouth twice daily for 7 days.", 1)),
            new("amlodipine", null, null, null, null, null, null, null, null, "stop", "discontinued", new("Stop amlodipine.", 1)),
            new("Penicillin", null, null, null, null, null, null, null, null, "allergy", "allergy", new("Penicillin is listed as an allergy.", 1))
        ], [
            new("CBC", "today", null, "new_order", "preliminary", new("Order a CBC today.", 1)),
            new("chest X-ray", null, null, "conditional", "preliminary", new("Consider a chest X-ray only if cough persists.", 1))
        ]);
    }
}
public sealed class FoundryFactory : WebApplicationFactory<Program>
{
    public readonly SimulatedAzure Azure = new();
    public readonly TrackingRepository Repository = new();
    public CountingLocal Local = null!;
    public string? DatabaseConnection;
    public bool DatabaseAutoMigrate = true;
    public bool MissingTokenizer;
    public bool MissingModel;
    public double TimeoutSeconds = 5;
    public int MaxRetries = 0;
    public bool AllowFallback = true;
    public bool AllowParsingFallback;
    public bool AllowDirectLocal = true;
    public int CircuitThreshold = 5;
    public TimeProvider? Clock;
    public string? DeploymentOverride;
    public string? FallbackProvider;
    public string PrimaryProvider = "MicrosoftFoundry";
    public string? EndpointOverride;
    public string? ApiKeyOverride;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtractionRepository>();
            if (DatabaseConnection == null) services.AddSingleton<IExtractionRepository>(Repository);
            else services.AddSingleton<IExtractionRepository>(sp => new SqlExtractionRepository(DatabaseConnection,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlExtractionRepository>>(), DatabaseAutoMigrate));
            services.PostConfigure<ExtractionOptions>(o =>
            {
                o.AllowDirectLocal = AllowDirectLocal; o.DirectLocalReason = "isolated_test";
                o.Provider = PrimaryProvider;
                o.FallbackProvider = FallbackProvider ?? (AllowFallback ? "LocalOnnx" : "None");
            });
            services.PostConfigure<FoundryOptions>(o =>
            {
                o.Endpoint = EndpointOverride ?? "https://synthetic.openai.azure.com/openai/v1/";
                o.DeploymentName = DeploymentOverride ?? "gpt-6.1-sol";
                o.Deployment = DeploymentOverride ?? "gpt-6.1-sol";
                o.TargetModel = "gpt-6.1-sol";
                o.AuthenticationMode = "ApiKey";
                o.ApiKey = ApiKeyOverride ?? "synthetic-test-key";
                o.TimeoutSeconds = TimeoutSeconds;
                o.MaxRetries = MaxRetries;
                o.RetryBaseDelayMs = 25;
                o.AllowLocalFallback = AllowFallback;
                o.AllowParsingFallback = AllowParsingFallback;
                o.CircuitFailureThreshold = CircuitThreshold;
            });
            if (Clock != null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(Clock); }
            services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>().ConfigurePrimaryHttpMessageHandler(() => Azure);
            services.RemoveAll<ILocalClinicalExtractor>();
            services.AddSingleton<ILocalClinicalExtractor>(sp => Local = new CountingLocal(sp));
            if (MissingTokenizer)
            {
                services.RemoveAll<WordPieceTokenizer>();
                services.AddSingleton(new WordPieceTokenizer());
            }
            if (MissingModel)
            {
                services.RemoveAll<SlidingWindowOnnxEngine>();
                services.AddSingleton(sp => new SlidingWindowOnnxEngine("missing-model.onnx", sp.GetRequiredService<WordPieceTokenizer>()));
            }
        });
    }
}

public class FoundryWorkflowTests
{
    public const string Note = "Continue metformin 500 mg tablets, two tablets by mouth twice daily for 7 days. Stop amlodipine. Order a CBC today. Consider a chest X-ray only if cough persists. Penicillin is listed as an allergy.";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimarySuccess_TextAndPdf_UseStrictSchema_AndNeverExtractWithOnnx(bool pdf)
    {
        using var factory = new FoundryFactory(); using var client = factory.CreateClient();
        using var response = await Submit(client, pdf);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        AssertClinical(result);
        Assert.Equal("foundry", result.ModelMetadata.Provider);
        Assert.Equal("gpt-6.1-sol-2026-09-29", result.ModelMetadata.Model);
        Assert.Equal(71, result.ModelMetadata.PromptTokens); Assert.Equal(29, result.ModelMetadata.CompletionTokens);
        Assert.Null(result.ModelMetadata.EstimatedCostUsd); Assert.Null(result.ModelMetadata.FallbackReason);
        Assert.Equal(0, factory.Local.Calls); Assert.Equal(1, factory.Azure.Calls); Assert.Equal(1, factory.Repository.Saves);
        Assert.Single(await factory.Repository.ListExtractionsAsync());
        Assert.Equal(result.NoteText, factory.Azure.Source);
        var history = await client.GetFromJsonAsync<ClinicalExtractionResponse>("/api/v1/extractions/" + result.ExtractionId);
        Assert.Equal(JsonSerializer.Serialize(result.ModelMetadata), JsonSerializer.Serialize(history!.ModelMetadata));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureHttpOutage_ExecutesRealOnnxExactlyOnce_AndRecordsRequestReceipt(bool pdf)
    {
        using var factory = new FoundryFactory(); factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
        using var client = factory.CreateClient(); using var response = await Submit(client, pdf);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        AssertClinical(result);
        Assert.Equal("onnx", result.ModelMetadata.Provider); Assert.Equal("AZURE_UNAVAILABLE", result.ModelMetadata.FallbackReason);
        Assert.Null(result.ModelMetadata.CompletionTokens); Assert.Null(result.ModelMetadata.EstimatedCostUsd);
        Assert.True(result.ModelMetadata.InferenceExecuted); Assert.Equal(2, result.ModelMetadata.Attempts!.Count);
        Assert.True(result.ModelMetadata.Attempts[1].OnnxWindows > 0); Assert.True(result.ModelMetadata.Attempts[1].InferenceExecuted);
        Assert.Equal(1, factory.Local.Calls); Assert.Equal(1, factory.Azure.Calls); Assert.Equal(1, factory.Repository.Saves);
    }
    [Theory]
    [InlineData("invalid-evidence", "AZURE_INVALID_EVIDENCE")]
    [InlineData("cross-drug", "AZURE_INVALID_EVIDENCE")]
    [InlineData("truncated", "AZURE_TRUNCATED")]
    [InlineData("bad-schema", "AZURE_UNUSABLE_JSON")]
    [InlineData("malformed", "AZURE_UNUSABLE_JSON")]
    public async Task RejectedAzureOutput_FailsForReview_WithoutLocalFallback(string mode, string reason)
    {
        using var factory = new FoundryFactory(); factory.Azure.Mode = mode;
        using var client = factory.CreateClient(); using var response = await Submit(client, false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(reason, error.RootElement.GetProperty("code").GetString());
        Assert.False(error.RootElement.GetProperty("routing").GetProperty("fallbackTriggered").GetBoolean());
        Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
        if (mode != "malformed") Assert.Equal(100, error.RootElement.GetProperty("routing").GetProperty("attempts")[0].GetProperty("totalTokens").GetInt32());
    }
    [Fact]
    public async Task Throttled429_RetriesBeforeActivatingOnnxFallback()
    {
        using var factory = new FoundryFactory { MaxRetries = 2 };
        factory.Azure.Status = HttpStatusCode.TooManyRequests;
        using var client = factory.CreateClient();
        using var response = await Submit(client, false);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("AZURE_RATE_LIMITED", result.ModelMetadata.FallbackReason);
        Assert.Equal(3, factory.Azure.Calls); // 1 initial + 2 retries
        Assert.Equal(1, factory.Local.Calls);
    }
    [Theory]
    [InlineData(401, 401, "AZURE_CREDENTIALS_INVALID")]
    [InlineData(404, 503, "AZURE_DEPLOYMENT_MISCONFIGURED")]
    public async Task PermanentAuthAndConfigFaults_AreNotSilentlyConcealedByFallback(int azureStatus, int expectedHttp, string expectedCode)
    {
        using var factory = new FoundryFactory();
        factory.Azure.Status = (HttpStatusCode)azureStatus;
        using var client = factory.CreateClient();
        using var response = await Submit(client, false);
        Assert.Equal(expectedHttp, (int)response.StatusCode);
        Assert.Contains(expectedCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Local.Calls);
        Assert.Equal(0, factory.Repository.Saves);
    }
    [Fact]
    public async Task MissingEndpointOrApiKey_FailsFastWithoutFallback()
    {
        using var missingEndpoint = new FoundryFactory { EndpointOverride = "" };
        using var client1 = missingEndpoint.CreateClient();
        using var r1 = await Submit(client1, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r1.StatusCode);
        Assert.Contains("AZURE_NOT_CONFIGURED", await r1.Content.ReadAsStringAsync());
        Assert.Equal(0, missingEndpoint.Local.Calls);

        using var missingKey = new FoundryFactory { ApiKeyOverride = "" };
        using var client2 = missingKey.CreateClient();
        using var r2 = await Submit(client2, false);
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
        Assert.Contains("AZURE_CREDENTIALS_MISSING", await r2.Content.ReadAsStringAsync());
        Assert.Equal(0, missingKey.Local.Calls);
    }
    [Fact]
    public async Task LocalOnlyProviderConfiguration_BypassesCloudAndUsesLocalOnnxDirectly()
    {
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx" };
        using var client = factory.CreateClient();
        using var response = await Submit(client, false);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("onnx", result.ModelMetadata.Provider);
        Assert.Null(result.ModelMetadata.FallbackReason);
        Assert.Equal(0, factory.Azure.Calls);
        Assert.Equal(1, factory.Local.Calls);
    }
    [Fact]
    public void UncertainMedication_EmitsHumanReviewRequiredWarning()
    {
        const string note = "Possibly start metformin 500 mg by mouth twice daily.";
        var candidate = new ClinicalCandidate([
            new("metformin", "500 mg", null, null, "by mouth", "twice daily", null, null, null, "start", "uncertain", new(note, 1))
        ], []);
        var result = new ClinicalResultValidator().Map(candidate, note, "uncertain-review");
        Assert.Equal("uncertain", result.Medications[0].AssertionStatus);
        Assert.Contains("HUMAN_REVIEW_REQUIRED", result.Medications[0].QualityFlags);
        Assert.True(result.ValidationSummary.WarningCount >= 1);
    }
    [Theory]
    [InlineData(403, "success", 403)]
    [InlineData(400, "policy", 422)]
    [InlineData(200, "refusal", 422)]
    public async Task AccessAndContentPolicyDenials_DoNotUseLocalFallback(int status, string mode, int expected)
    {
        using var factory = new FoundryFactory(); factory.Azure.Status = (HttpStatusCode)status; factory.Azure.Mode = mode;
        using var client = factory.CreateClient(); using var response = await Submit(client, false);
        Assert.Equal(expected, (int)response.StatusCode); Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
    }
    [Fact]
    public async Task ZeroFindings_IsSuccessfulWithoutFallback()
    {
        using var factory = new FoundryFactory(); factory.Azure.Mode = "empty";
        using var client = factory.CreateClient(); using var response = await Submit(client, false, "No medications or investigations documented.");
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Empty(result.Medications); Assert.Empty(result.Investigations); Assert.True(result.Saved); Assert.Equal(0, factory.Local.Calls);
    }
    [Fact]
    public async Task AttemptTimeout_FallsBackButCallerCancellationDoesNot()
    {
        using var timeoutFactory = new FoundryFactory { TimeoutSeconds = 0.04 }; timeoutFactory.Azure.Mode = "wait";
        using var timeoutClient = timeoutFactory.CreateClient(); using var response = await Submit(timeoutClient, false);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("AZURE_TIMEOUT", result.ModelMetadata.FallbackReason); Assert.Equal(1, timeoutFactory.Local.Calls);
        using var cancelled = new FoundryFactory(); cancelled.Azure.Mode = "wait";
        using var client = cancelled.CreateClient(); using var cts = new CancellationTokenSource();
        var task = client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(Note), cts.Token);
        await cancelled.Azure.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, cancelled.Local.Calls); Assert.Equal(0, cancelled.Repository.Saves);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOnnxArtifact_CannotProduceRuleOnlySuccess(bool tokenizer)
    {
        using var factory = new FoundryFactory { MissingModel = !tokenizer, MissingTokenizer = tokenizer }; factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
        using var client = factory.CreateClient(); using var response = await Submit(client, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("EXTRACTION_UNAVAILABLE", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
    }
    [Fact]
    public async Task MissingLocalArtifact_DoesNotBlockPrimarySuccess()
    {
        using var factory = new FoundryFactory { MissingModel = true };
        using var client = factory.CreateClient(); using var response = await Submit(client, false);
        response.EnsureSuccessStatusCode(); Assert.Equal(0, factory.Local.Calls);
    }
    [Fact]
    public async Task DatabaseFailure_DoesNotRetryEitherModel()
    {
        using var factory = new FoundryFactory(); factory.Repository.FailWrites = true;
        using var client = factory.CreateClient(); using var response = await Submit(client, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Contains("PERSISTENCE_FAILED", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(1, factory.Repository.Saves); Assert.Empty(await factory.Repository.ListExtractionsAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivacyRejection_StopsBeforeCloudAndLocal(bool pdf)
    {
        using var factory = new FoundryFactory(); using var client = factory.CreateClient();
        using var response = await Submit(client, pdf, "Patient phone +91 9876543210. Start metformin.");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.Equal(0, factory.Azure.Calls); Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
    }
    [Fact]
    public async Task LongNote_SendsTheLastSection_AndUsesItsExactOffset()
    {
        string note = string.Concat(Enumerable.Repeat("No relevant findings. ", 1800)) + "Order a CBC today.";
        using var factory = new FoundryFactory(); using var client = factory.CreateClient(); using var response = await Submit(client, false, note);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(note, factory.Azure.Source); Assert.Equal(note.IndexOf("Order a CBC"), result.Investigations[0].Evidence.Start); Assert.Equal(0, factory.Local.Calls);
    }
    [Fact]
    public async Task RepeatedEvidence_UsesExplicitOccurrence()
    {
        using var factory = new FoundryFactory(); factory.Azure.Mode = "repeated";
        using var client = factory.CreateClient(); using var response = await Submit(client, false, "Order a CBC today. Order a CBC today.");
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal(19, result.Investigations[0].Evidence.Start);
    }
    [Fact]
    public void PdfWrappedPosology_IsVerifiedAgainstExactSourceWithoutRemovingLineBreaks()
    {
        string note = Note.Replace("tablets, two tablets", "tablets,\ntwo tablets");
        var candidate = SimulatedAzure.Candidates(Note);
        candidate = candidate with { Medications = candidate.Medications.Select((m, n) => n == 0 ? m with { Evidence = new(m.Evidence.Quote.Replace("tablets, two tablets", "tablets,\ntwo tablets"), 1) } : m).ToList() };
        var result = new ClinicalResultValidator().Map(candidate, note, "wrapped-pdf");
        Assert.Equal("two tablets", result.Medications[0].Dose);
        Assert.Equal(result.Medications[0].Evidence.Quote, note[result.Medications[0].Evidence.Start!.Value..result.Medications[0].Evidence.End!.Value]);
    }
    [Fact]
    public void StopActionAndDose_DoNotLeakAcrossDrugsInTheSameSentence()
    {
        const string note = "Stop amlodipine and continue metformin 500 mg two tablets by mouth twice daily for 7 days.";
        var evidence = new CandidateEvidence(note, 1);
        var candidate = new ClinicalCandidate([
            new("amlodipine", null, null, null, null, null, null, null, null, "stop", "discontinued", evidence),
            new("metformin", "500 mg", "two tablets", "tablets", "by mouth", "twice daily", "7 days", null, null, "continue", "current_medication", evidence)
        ], []);
        var result = new ClinicalResultValidator().Map(candidate, note, "scoped-actions");
        Assert.Equal("stop", result.Medications[0].Action); Assert.Null(result.Medications[0].Strength);
        Assert.Equal("continue", result.Medications[1].Action); Assert.Equal("two tablets", result.Medications[1].Dose);
    }
    [Fact]
    public void ExplicitDoseChangeAndTiming_AreRetainedInInstructions()
    {
        const string note = "Increase metformin 500 mg to two tablets by mouth twice daily after 7 days.";
        var candidate = new ClinicalCandidate([new("metformin", "500 mg", "two tablets", "tablets", "by mouth", "twice daily", null, null, note, "increase", "current_medication", new(note, 1))], []);
        var result = new ClinicalResultValidator().Map(candidate, note, "dose-change");
        Assert.Equal(note, result.Medications[0].Instructions); Assert.Equal("increase", result.Medications[0].Action);
    }
    [Fact]
    public async Task AllergyFhirExport_DoesNotTurnPenicillinIntoAnActivePrescription()
    {
        using var factory = new FoundryFactory(); using var client = factory.CreateClient(); using var response = await Submit(client, false);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        using var bundle = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/extractions/{result.ExtractionId}/fhir"));
        var resources = bundle.RootElement.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
        Assert.Single(resources, r => r.GetProperty("resourceType").GetString() == "AllergyIntolerance" && r.GetProperty("code").GetProperty("text").GetString() == "Penicillin");
        Assert.Equal(2, resources.Count(r => r.GetProperty("resourceType").GetString() == "MedicationRequest"));
    }
    [Fact]
    public void SmallerNumericSubstring_CannotPassAsTheDocumentedDose()
    {
        const string note = "Continue metformin 500 mg 15 tablets by mouth twice daily.";
        var unsupported = new ClinicalCandidate([new("metformin", "500 mg", "5 tablets", "tablets", "by mouth", "twice daily", null, null, null, "continue", "current_medication", new(note, 1))], []);
        Assert.Throws<InvalidClinicalEvidenceException>(() => new ClinicalResultValidator().Map(unsupported, note, "numeric-grounding"));
    }
    [Fact]
    public void ADocumentedStrengthUnit_CannotBeMisassignedToATabletDose()
    {
        var candidate = SimulatedAzure.Candidates(Note);
        candidate = candidate with { Medications = candidate.Medications.Select((m, n) => n == 0 ? m with { DoseUnit = "mg" } : m).ToList() };
        Assert.Throws<InvalidClinicalEvidenceException>(() => new ClinicalResultValidator().Map(candidate, Note, "unit-grounding"));
    }
    [Fact]
    public void SupportedRouteAndFrequencyAliases_AreGroundedThroughTheExistingBinder()
    {
        var candidate = SimulatedAzure.Candidates(Note);
        candidate = candidate with { Medications = candidate.Medications.Select((m, n) => n == 0 ? m with { Route = "oral", Frequency = "BID" } : m).ToList() };
        var result = new ClinicalResultValidator().Map(candidate, Note, "grounded-aliases");
        Assert.Equal("oral", result.Medications[0].Route); Assert.Equal("BID", result.Medications[0].Frequency);
    }
    private static void AssertClinical(ClinicalExtractionResponse result)
    {
        Assert.True(result.Saved);
        var m = Assert.Single(result.Medications, m => m.Name.Equals("metformin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("500 mg", m.Strength); Assert.Equal("two tablets", m.Dose); Assert.Contains("tablet", m.DoseUnit!);
        Assert.Contains(m.Route, new[] { "PO", "by mouth" }); Assert.Contains(m.Frequency, new[] { "BID", "twice daily" }); Assert.Contains("7 days", m.Duration!);
        var stopped = Assert.Single(result.Medications, m => m.Action == "stop"); Assert.Equal("amlodipine", stopped.Name, ignoreCase: true);
        Assert.Contains(result.Medications, m => m.Name.Equals("Penicillin", StringComparison.OrdinalIgnoreCase) && m.AssertionStatus == "allergy");
        Assert.Contains(result.Investigations, i => i.Name.Equals("CBC", StringComparison.OrdinalIgnoreCase) && i.AssertionStatus == "new_order" && i.Timing == "today");
        Assert.Contains(result.Investigations, i => i.Name.Equals("chest X-ray", StringComparison.OrdinalIgnoreCase) && i.AssertionStatus == "conditional");
        foreach (var evidence in result.Medications.Select(m => m.Evidence).Concat(result.Investigations.Select(i => i.Evidence)))
        {
            Assert.True(evidence.Verified); Assert.Equal(evidence.Quote, result.NoteText![evidence.Start!.Value..evidence.End!.Value]);
        }
    }
    public static async Task<HttpResponseMessage> Submit(HttpClient client, bool pdf, string note = Note)
    {
        if (!pdf) return await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        using var form = new MultipartFormDataContent(); using var file = new ByteArrayContent(CreatePdf(note));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf"); form.Add(file, "file", "synthetic-foundry.pdf");
        return await client.PostAsync("/api/v1/extractions/pdf", form);
    }
    public static byte[] CreatePdf(string text)
    {
        string escaped = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        string content = $"BT /F1 10 Tf 32 720 Td ({escaped}) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1400 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream"];
        var output = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (int n = 0; n < objects.Length; n++) { offsets.Add(Encoding.ASCII.GetByteCount(output.ToString())); output.Append($"{n + 1} 0 obj\n{objects[n]}\nendobj\n"); }
        int xref = Encoding.ASCII.GetByteCount(output.ToString()); output.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) output.Append($"{offset:0000000000} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"); return Encoding.ASCII.GetBytes(output.ToString());
    }
}
