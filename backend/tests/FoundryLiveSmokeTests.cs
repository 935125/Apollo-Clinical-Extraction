using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Apollo.Backend.Tests.Evaluation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Apollo.Backend.Tests;

public sealed class FoundrySingleCallAuthorizerAttribute : FactAttribute
{
    public FoundrySingleCallAuthorizerAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST"), "SINGLE_CALL_MAX_1USD", StringComparison.Ordinal))
        {
            Skip = "Set AUTHORIZE_SINGLE_LIVE_DISCHARGE_TEST=SINGLE_CALL_MAX_1USD to execute the single authorized GPT-6.1 Sol PDF verification request.";
        }
    }
}

public sealed class SingleCallCaptureHandler : DelegatingHandler
{
    private int _callCount;
    public int CallCount => _callCount;
    public string? CapturedRequestBody { get; private set; }
    public string? CapturedResponseBody { get; private set; }
    public int? CapturedStatusCode { get; private set; }
    public double LatencyMs { get; private set; }
    public decimal PreDispatchEstimatedMaxUsd { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        int current = Interlocked.Increment(ref _callCount);
        if (current > 1)
            throw new InvalidOperationException($"HARD LIMIT VIOLATION: Attempted call #{current} when maximum authorized live calls is 1.");

        CapturedRequestBody = await request.Content!.ReadAsStringAsync(ct);
        using var reqDoc = JsonDocument.Parse(CapturedRequestBody);
        int maxComp = reqDoc.RootElement.GetProperty("max_completion_tokens").GetInt32();
        int promptByteEstimate = Encoding.UTF8.GetByteCount(CapturedRequestBody) + 1024;
        PreDispatchEstimatedMaxUsd = (promptByteEstimate * FoundryEvaluationBudget.InputUsdPerMillion +
                                      maxComp * FoundryEvaluationBudget.OutputUsdPerMillion) / 1_000_000m;
        if (PreDispatchEstimatedMaxUsd > 1.00m)
            throw new InvalidOperationException($"Pre-dispatch worst-case cost estimate (${PreDispatchEstimatedMaxUsd:F4}) exceeds $1.00 USD cap.");

        var sw = Stopwatch.StartNew();
        var response = await base.SendAsync(request, ct);
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
        CapturedResponseBody = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();
        LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
        CapturedStatusCode = (int)response.StatusCode;
        return response;
    }
}

public sealed class FoundryLiveFactAttribute : FactAttribute
{
    public FoundryLiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FOUNDRY_LIVE_TEST"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Foundry__Endpoint")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Foundry__DeploymentName") ?? Environment.GetEnvironmentVariable("Foundry__Deployment")))
        {
            Skip = "Set FOUNDRY_LIVE_TEST=true, Foundry__Endpoint, and Foundry__DeploymentName (plus Entra login or Foundry__ApiKey) to run live Microsoft Foundry smoke test.";
        }
    }
}

public sealed class FoundryLiveSmokeTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [FoundrySingleCallAuthorizer]
    public async Task SingleAuthorizedLivePdfVerification_CapturesRawAndFinalAndPersistsToIsolatedPostgres()
    {
        string pdfPath = Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "Fixtures", "Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf");
        Assert.True(File.Exists(pdfPath), $"Fixture not found at {pdfPath}");
        byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath);
        string pdfSha256 = Convert.ToHexString(SHA256.HashData(pdfBytes));

        var reconstructed = PdfTextReconstructor.Reconstruct(new MemoryStream(pdfBytes, writable: false));
        Assert.True(reconstructed.ReconstructedText.Length > 1000, $"Unexpected length: {reconstructed.ReconstructedText.Length}");
        Assert.Contains("Azithromycin 500 mg orally once daily for 3 days", reconstructed.ReconstructedText);

        string artifactDir = Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "live-final-verification");
        Directory.CreateDirectory(artifactDir);

        await using var database = new IsolatedDatabase();
        await database.Initialize();

        var captureHandler = new SingleCallCaptureHandler();
        string correlationId = "live-final-" + Guid.NewGuid().ToString("N")[..12];

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtractionRepository>();
                services.AddSingleton<IExtractionRepository>(sp => new SqlExtractionRepository(
                    database.ConnectionString,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SqlExtractionRepository>>(),
                    autoInitializeSchema: true));
                services.PostConfigure<ExtractionOptions>(o =>
                {
                    o.Provider = "MicrosoftFoundry";
                    o.FallbackProvider = "None";
                    o.EnableSyntheticDiagnosticCapture = true;
                    o.SyntheticDiagnosticOutputDirectory = artifactDir;
                });
                services.PostConfigure<FoundryOptions>(o =>
                {
                    o.AllowLocalFallback = false;
                    o.MaxRetries = 0;
                    o.MaxCompletionTokens = 6000;
                    o.TimeoutSeconds = 90;
                });
                services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>()
                    .AddHttpMessageHandler(() => captureHandler);
            });
        });

        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf");

        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extractions/pdf") { Content = form };
        requestMsg.Headers.Add("X-Correlation-ID", correlationId);

        var swTotal = Stopwatch.StartNew();
        using var response = await client.SendAsync(requestMsg);
        swTotal.Stop();

        string finalResponseText = await response.Content.ReadAsStringAsync();
        Assert.Equal(1, captureHandler.CallCount);

        // 1. Save raw Foundry HTTP response before any C# validation or post-processing
        Assert.False(string.IsNullOrWhiteSpace(captureHandler.CapturedResponseBody), "Raw Foundry HTTP response was empty.");
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-raw-http-envelope.json"), captureHandler.CapturedResponseBody!);
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-http-response-body.json"), finalResponseText);

        using var rawEnvelopeDoc = JsonDocument.Parse(captureHandler.CapturedResponseBody!);
        var rawRoot = rawEnvelopeDoc.RootElement;
        string? actualModel = rawRoot.TryGetProperty("model", out var mProp) ? mProp.GetString() : null;
        string? finishReason = rawRoot.GetProperty("choices")[0].GetProperty("finish_reason").GetString();
        string rawStructuredContent = rawRoot.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        int? promptTokens = rawRoot.TryGetProperty("usage", out var uProp) && uProp.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : null;
        int? completionTokens = rawRoot.TryGetProperty("usage", out var uProp2) && uProp2.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : null;
        int? totalTokens = rawRoot.TryGetProperty("usage", out var uProp3) && uProp3.TryGetProperty("total_tokens", out var tt) ? tt.GetInt32() : null;
        int? reasoningTokens = rawRoot.TryGetProperty("usage", out var uProp4) &&
                              uProp4.TryGetProperty("completion_tokens_details", out var ctd) &&
                              ctd.TryGetProperty("reasoning_tokens", out var rt) ? rt.GetInt32() : null;

        decimal? actualEstimatedUsd = (promptTokens.HasValue && completionTokens.HasValue)
            ? (promptTokens.Value * FoundryEvaluationBudget.InputUsdPerMillion +
               completionTokens.Value * FoundryEvaluationBudget.OutputUsdPerMillion) / 1_000_000m
            : null;

        var rawCaptureArtifact = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            correlationId,
            pdfFileName = "Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf",
            pdfSha256,
            reconstructedCharLength = reconstructed.ReconstructedText.Length,
            reconstructedTextSent = reconstructed.ReconstructedText,
            promptVersion = FoundryClinicalExtractor.CurrentPromptVersion,
            schemaVersion = "1.0.0",
            effectiveDeployment = "gpt-6.1-sol",
            requestMaxCompletionTokens = 6000,
            requestMaxRetries = 0,
            allowLocalFallback = false,
            httpStatusCode = captureHandler.CapturedStatusCode,
            providerReportedModel = actualModel,
            finishReason,
            promptTokens,
            completionTokens,
            reasoningTokens,
            totalTokens,
            httpLatencyMs = captureHandler.LatencyMs,
            totalEndpointLatencyMs = Math.Round(swTotal.Elapsed.TotalMilliseconds, 2),
            preDispatchMaxEstimateUsd = captureHandler.PreDispatchEstimatedMaxUsd,
            actualEstimatedCostUsd = actualEstimatedUsd,
            pricingSource = FoundryEvaluationBudget.PricingSource,
            rawOpenAiEnvelope = JsonSerializer.Deserialize<JsonElement>(captureHandler.CapturedResponseBody!),
            rawStructuredModelContent = JsonSerializer.Deserialize<JsonElement>(rawStructuredContent)
        };
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-raw-response.json"), JsonSerializer.Serialize(rawCaptureArtifact, PrettyJson));

        var deserializedCandidate = JsonSerializer.Deserialize<ClinicalCandidate>(rawStructuredContent, ClinicalCandidateJson.Options)!;
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-deserialized-candidate.json"), JsonSerializer.Serialize(deserializedCandidate, PrettyJson));

        Assert.True(response.IsSuccessStatusCode, $"Live extraction failed with HTTP {(int)response.StatusCode}: {finalResponseText}");
        var finalExtraction = JsonSerializer.Deserialize<ClinicalExtractionResponse>(finalResponseText, PrettyJson)!;
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-final-extraction.json"), JsonSerializer.Serialize(finalExtraction, PrettyJson));

        // 2. Verify provider provenance & no ONNX fallback
        Assert.Equal("foundry", finalExtraction.ModelMetadata.Provider);
        Assert.True(finalExtraction.ModelMetadata.InferenceExecuted);
        Assert.Null(finalExtraction.ModelMetadata.FallbackReason);
        Assert.Equal("passed", finalExtraction.ModelMetadata.SchemaValidationStatus);

        // 3. Verify PostgreSQL read-after-write persistence in isolated schema
        var restoredFromPostgres = await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{finalExtraction.ExtractionId}");
        Assert.NotNull(restoredFromPostgres);
        var quarantineItems = await client.GetFromJsonAsync<List<QuarantineItemDto>>("/api/v1/quarantine?limit=100");

        var postgresVerification = new
        {
            verifiedAt = DateTimeOffset.UtcNow,
            isolatedSchema = database.Schema,
            extractionRunsCount = await database.Count("extraction_runs"),
            extractedMedicationsCount = await database.Count("extracted_medications"),
            extractedInvestigationsCount = await database.Count("extracted_investigations"),
            quarantineItemsCount = await database.Count("oov_quarantine_items"),
            extractionId = finalExtraction.ExtractionId,
            correlationId = restoredFromPostgres!.CorrelationId,
            provider = restoredFromPostgres.ModelMetadata.Provider,
            model = restoredFromPostgres.ModelMetadata.Model,
            deployment = restoredFromPostgres.ModelMetadata.ModelDeployment,
            promptTokens = restoredFromPostgres.ModelMetadata.PromptTokens,
            completionTokens = restoredFromPostgres.ModelMetadata.CompletionTokens,
            totalTokens = restoredFromPostgres.ModelMetadata.TotalTokens,
            medicationsMatchFinalResponse = JsonSerializer.Serialize(finalExtraction.Medications.Select(m => new { m.Name, m.Strength, m.Dose, m.Route, m.Frequency, m.Duration, m.Action, m.AssertionStatus, m.Evidence, m.Normalization.Status, m.Normalization.MatchedCode })) ==
                                            JsonSerializer.Serialize(restoredFromPostgres.Medications.Select(m => new { m.Name, m.Strength, m.Dose, m.Route, m.Frequency, m.Duration, m.Action, m.AssertionStatus, m.Evidence, m.Normalization.Status, m.Normalization.MatchedCode })),
            investigationsMatchFinalResponse = JsonSerializer.Serialize(finalExtraction.Investigations.Select(i => new { i.Name, i.Timing, i.Specimen, i.AssertionStatus, i.Status, i.Evidence, NormStatus = i.Normalization.Status, i.MatchedCode })) ==
                                               JsonSerializer.Serialize(restoredFromPostgres.Investigations.Select(i => new { i.Name, i.Timing, i.Specimen, i.AssertionStatus, i.Status, i.Evidence, NormStatus = i.Normalization.Status, i.MatchedCode })),
            restoredExtraction = restoredFromPostgres,
            quarantineRecords = quarantineItems?.Where(q => q.ExtractionId == finalExtraction.ExtractionId).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "postgres-persistence-verification.json"), JsonSerializer.Serialize(postgresVerification, PrettyJson));
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "validation-dimensions.json"), JsonSerializer.Serialize(finalExtraction.ValidationSummary, PrettyJson));
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "clinical-safety-audit.json"), JsonSerializer.Serialize(finalExtraction.SafetyAlerts, PrettyJson));

        var terminologyAudit = new
        {
            verifiedAt = DateTimeOffset.UtcNow,
            rxnormVersion = TerminologyVerification.Version,
            loincVersion = TerminologyVerification.LoincVersion,
            medications = finalExtraction.Medications.Select(m => new
            {
                m.Name,
                m.Strength,
                m.AssertionStatus,
                m.Origin,
                m.Normalization
            }).ToList(),
            investigations = finalExtraction.Investigations.Select(i => new
            {
                i.Name,
                i.ClinicalCategory,
                i.Timing,
                i.Specimen,
                i.AssertionStatus,
                i.Origin,
                i.Normalization
            }).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "terminology-semantic-audit.json"), JsonSerializer.Serialize(terminologyAudit, PrettyJson));

        output.WriteLine($"Single live verification succeeded: model={actualModel}, tokens={promptTokens}/{completionTokens}/{totalTokens} (reasoning={reasoningTokens}), latency={captureHandler.LatencyMs}ms, meds={finalExtraction.Medications.Count}, invs={finalExtraction.Investigations.Count}");
    }
    [FoundryLiveFact]
    public async Task LiveFoundryDeployment_ExtractsFictionalClinicalNote_WithActualMetadata()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtractionRepository>();
                services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
                services.PostConfigure<ExtractionOptions>(o =>
                {
                    o.Provider = "MicrosoftFoundry";
                    o.FallbackProvider = "None";
                });
            });
        });

        using var client = factory.CreateClient();
        const string fictionalNote = "Fictional outpatient note. Plan: Start metformin 500 mg, 1 tablet by mouth twice daily for 30 days. Order CBC today.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(fictionalNote));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("foundry", result.ModelMetadata.Provider);
        Assert.True(result.ModelMetadata.InferenceExecuted);
        Assert.Equal("passed", result.ModelMetadata.SchemaValidationStatus);
        Assert.NotEmpty(result.Medications);
        Assert.NotEmpty(result.Investigations);
    }

    [FoundryLiveFact]
    public async Task LiveFoundryDeployment_ExtractsStage2Note_WithoutFallback()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IExtractionRepository>();
                services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
                services.PostConfigure<ExtractionOptions>(o =>
                {
                    o.Provider = "MicrosoftFoundry";
                    o.FallbackProvider = "None";
                });
            });
        });

        using var client = factory.CreateClient();
        const string note = "Adult patient with type 2 diabetes. Continue metformin 500 mg orally twice daily for 14 days. Start amoxicillin 500 mg orally three times daily for 5 days. Order CBC and serum creatinine. Consider chest X-ray only if cough persists. Patient denies penicillin allergy.";
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
        string content = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Failed with status {response.StatusCode}: {content}");

        var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("foundry", result.ModelMetadata.Provider);
    }
}

