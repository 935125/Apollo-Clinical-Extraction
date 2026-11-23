using System.Diagnostics;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

/// <summary>
/// Local BC5CDR INT8 ONNX clinical extractor. Reuses the singleton <see cref="SlidingWindowOnnxEngine"/>
/// session without reloading per request. Note: Local ONNX provides reduced clinical extraction coverage
/// compared with Microsoft Foundry GPT-6 Sol (e.g., complex conditional instructions and non-formulary drugs).
/// </summary>
public sealed class OnnxClinicalExtractor(IServiceProvider services) : ILocalClinicalExtractor
{
    public string ProviderName => "LocalOnnx";

    public Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var engine = services.GetRequiredService<SlidingWindowOnnxEngine>();
        var tokenizer = services.GetRequiredService<WordPieceTokenizer>();
        var pipeline = services.GetRequiredService<ClinicalPipelineService>();
        if (!engine.HasOnnxModel || !tokenizer.LoadedFromArtifact)
            throw new ClinicalExtractionException("ONNX_ARTIFACT_UNAVAILABLE", 503, "Local model weights or tokenizer are unavailable.");
        var watch = Stopwatch.StartNew();
        var receipt = new OnnxExecutionReceipt();
        var result = pipeline.ProcessPlatformExtraction(new(source), correlationId, receipt);
        ct.ThrowIfCancellationRequested();
        if (receipt.WindowsExecuted == 0)
            throw new ClinicalExtractionException("ONNX_INFERENCE_NOT_EXECUTED", 503, "The local model did not execute inference.");
        var attempt = new ProviderAttemptDto("onnx", "bc5cdr_int8", null, "accepted", null,
            Math.Round(watch.Elapsed.TotalMilliseconds, 2), true, OnnxWindows: receipt.WindowsExecuted,
            InferenceDurationMs: receipt.InferenceDurationMs, ModelMedicationMentions: receipt.ModelMedicationMentions,
            RuleMedicationMentions: receipt.RuleMedicationMentions, RuleInvestigationMentions: receipt.RuleInvestigationMentions);
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
