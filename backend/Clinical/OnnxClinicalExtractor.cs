using System.Diagnostics;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Clinical;

/// <summary>
/// Local BC5CDR INT8 ONNX clinical extractor. Reuses the singleton <see cref="SlidingWindowOnnxEngine"/>
/// session without reloading per request. Clinical interpretation is selected independently through
/// LocalOnnx:PipelineVersion; both versions retain explicit human-review findings.
/// </summary>
public sealed class OnnxClinicalExtractor(IServiceProvider services) : ILocalClinicalExtractor
{
    public string ProviderName => "LocalOnnx";

    public Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, CancellationToken ct)
        => ExtractAsync(source, correlationId, allowVerifiedSyntheticIdentifiers: false, ct);

    public Task<ClinicalExtractionResponse> ExtractAsync(
        string source,
        string correlationId,
        bool allowVerifiedSyntheticIdentifiers,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = services.GetRequiredService<SlidingWindowOnnxEngine>();
        var tokenizer = services.GetRequiredService<WordPieceTokenizer>();
        var pipeline = services.GetRequiredService<ClinicalPipelineService>();
        if (!engine.HasOnnxModel || !tokenizer.LoadedFromArtifact)
            throw new ClinicalExtractionException("ONNX_ARTIFACT_UNAVAILABLE", 503, "Local model weights or tokenizer are unavailable.");
        var watch = Stopwatch.StartNew();
        var receipt = new OnnxExecutionReceipt();
        var localOptions = services.GetService<IOptions<LocalClinicalPipelineOptions>>()?.Value ?? new();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(localOptions.TimeoutSeconds));
        ClinicalExtractionResponse result;
        try
        {
            result = pipeline.ProcessPlatformExtraction(new(source), correlationId, receipt,
                skipPrivacyValidation: allowVerifiedSyntheticIdentifiers, cancellationToken: timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ClinicalExtractionException("ONNX_TIMEOUT", 503, "Local extraction exceeded its configured processing time.");
        }
        ct.ThrowIfCancellationRequested();
        if (receipt.WindowsExecuted == 0)
            throw new ClinicalExtractionException("ONNX_INFERENCE_NOT_EXECUTED", 503, "The local model did not execute inference.");
        var attempt = new ProviderAttemptDto("onnx", "bc5cdr_int8", null, "accepted", null,
            Math.Round(watch.Elapsed.TotalMilliseconds, 2), true, OnnxWindows: receipt.WindowsExecuted,
            InferenceDurationMs: receipt.InferenceDurationMs, ModelMedicationMentions: receipt.ModelMedicationMentions,
            RuleMedicationMentions: receipt.RuleMedicationMentions, RuleInvestigationMentions: receipt.RuleInvestigationMentions);
        (services.GetService<ClinicalResultValidator>() ?? new ClinicalResultValidator()).ValidateLocal(result, source);
        return Task.FromResult(ClinicalReview.Apply(result with
        {
            ModelMetadata = result.ModelMetadata with
            {
                ModelSha256 = OnnxArtifactIntegrity.ModelSha256,
                TokenizerSha256 = OnnxArtifactIntegrity.TokenizerSha256,
                ExecutionMode = receipt.ExecutionMode,
                Provider = "onnx",
                Model = "bc5cdr_int8",
                InferenceExecuted = true,
                PromptTokens = null,
                CompletionTokens = null,
                TotalTokens = null,
                EstimatedCostUsd = null,
                Attempts = [attempt],
                SchemaValidationStatus = "not_applicable"
            }
        }, reducedCoverage: true));
    }
}
