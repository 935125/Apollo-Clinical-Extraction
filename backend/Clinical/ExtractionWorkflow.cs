using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Apollo.Backend.Security;
using Apollo.Backend.Storage;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Clinical;

public sealed class ExtractionWorkflow(
    IFoundryClinicalExtractor foundry,
    ILocalClinicalExtractor local,
    ClinicalResultValidator validator,
    IExtractionRepository repository,
    IOptions<FoundryOptions> options,
    IOptions<ExtractionOptions> extractionOptions,
    ILogger<ExtractionWorkflow> logger)
{
    public async Task<ClinicalExtractionResponse> ExtractAndSaveAsync(
        string noteText,
        string correlationId,
        string inputType,
        string? sourceFileName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(noteText))
            throw new ClinicalExtractionException("EMPTY_NOTE_TEXT", 400, "A clinical note is required.");
        if (noteText.Length > 65536)
            throw new ClinicalExtractionException("NOTE_EXCEEDS_64KIB_LIMIT", 400, "The note exceeds 65,536 characters.");

        // Pattern-based identifier screening precedes inference; it is not full de-identification.
        ZeroLeakageValidator.Validate(noteText);

        var watch = Stopwatch.StartNew();
        string extractionId = Guid.NewGuid().ToString("D");
        string primaryProvider = (extractionOptions.Value.Provider ?? "LocalOnnx").Trim();
        ClinicalExtractionResponse accepted;
        FoundryExtraction? capturedAzure = null;

        if (primaryProvider.Equals("LocalOnnx", StringComparison.OrdinalIgnoreCase))
        {
            accepted = await local.ExtractAsync(noteText, correlationId, ct);
        }
        else if (!primaryProvider.Equals("MicrosoftFoundry", StringComparison.OrdinalIgnoreCase))
        {
            throw new ClinicalExtractionException(
                "EXTRACTION_PROVIDER_INVALID",
                500,
                "Configured Extraction:Provider is not supported. Use MicrosoftFoundry or LocalOnnx.");
        }
        else
        {
            FoundryExtraction? azure = null;
            try
            {
                azure = await foundry.ExtractAsync(noteText, ct);
                capturedAzure = azure;
                accepted = validator.Map(azure.Candidate, noteText, correlationId);
                accepted = accepted with
                {
                    ModelMetadata = new(
                        "microsoft_foundry",
                        azure.Attempt.Deployment!,
                        "clinical-v3",
                        azure.Attempt.PromptTokens,
                        azure.Attempt.CompletionTokens,
                        azure.Attempt.TotalTokens,
                        null,
                        "foundry",
                        azure.Attempt.Model,
                        azure.Attempt.Deployment,
                        null,
                        true,
                        [azure.Attempt],
                        "passed")
                };
            }
            catch (InvalidClinicalEvidenceException)
            {
                logger.LogWarning("Clinical evidence rejected: AZURE_INVALID_EVIDENCE");
                var attempt = azure!.Attempt with { Outcome = "rejected", Reason = "AZURE_INVALID_EVIDENCE" };
                capturedAzure = azure with { Attempt = attempt };
                accepted = await Fallback(noteText, correlationId, new("AZURE_INVALID_EVIDENCE", attempt), ct);
            }
            catch (FoundryAttemptException failure)
            {
                accepted = await Fallback(noteText, correlationId, failure, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        accepted = accepted with
        {
            ExtractionId = extractionId,
            CorrelationId = correlationId,
            ExecutionDurationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2),
            InputType = inputType,
            SourceFileName = sourceFileName,
            NoteText = noteText,
            Saved = true
        };

        var quarantine = Quarantine(accepted);
        try
        {
            // One call, one transaction: provider failures cannot catch persistence failures.
            await repository.SaveExtractionAsync(extractionId, accepted, ct, quarantine);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Persistence failed for {ExtractionId}; error type {ErrorType}", extractionId, ex.GetType().Name);
            throw new PersistenceFailureException();
        }

        if (extractionOptions.Value.EnableSyntheticDiagnosticCapture)
        {
            TryWriteSyntheticDiagnosticCapture(accepted, capturedAzure, quarantine);
        }

        return accepted;
    }

    private void TryWriteSyntheticDiagnosticCapture(
        ClinicalExtractionResponse accepted,
        FoundryExtraction? azure,
        IReadOnlyList<QuarantineItemDto> quarantine)
    {
        try
        {
            string? outputDir = extractionOptions.Value.SyntheticDiagnosticOutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDir))
                return;
            Directory.CreateDirectory(outputDir);
            string sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accepted.NoteText ?? "")));
            var capture = new
            {
                capturedAt = DateTimeOffset.UtcNow,
                governancePolicy = "SYNTHETIC_TEST_INPUT_ONLY",
                documentIdentifier = accepted.SourceFileName ?? accepted.CorrelationId,
                documentSha256 = sha256,
                reconstructedText = accepted.NoteText,
                preprocessedSections = accepted.Preprocessed.Sections,
                modelProvenance = accepted.ModelMetadata,
                rawStructuredModelResponse = azure?.RawStructuredContent,
                deserializedCandidate = azure?.Candidate,
                validationSummary = accepted.ValidationSummary,
                medications = accepted.Medications,
                investigations = accepted.Investigations,
                quarantineItems = quarantine,
                safetyAlerts = accepted.SafetyAlerts,
                finalPersistedJson = accepted
            };
            string filePath = Path.Combine(outputDir, $"diagnostic-trace-{accepted.CorrelationId}-{accepted.ExtractionId}.json");
            File.WriteAllText(filePath, JsonSerializer.Serialize(capture, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        catch
        {
            // Diagnostic capture never blocks extraction completion
        }
    }

    private async Task<ClinicalExtractionResponse> Fallback(
        string source,
        string correlationId,
        FoundryAttemptException failure,
        CancellationToken ct)
    {
        if (failure.OperatorFault)
        {
            logger.LogError("Foundry configuration/authentication fault: {Reason}", failure.Reason);
            int status = failure.Reason is "AZURE_CREDENTIALS_INVALID" or "AZURE_CREDENTIALS_MISSING" ? 401 : 503;
            throw new ClinicalExtractionException(
                failure.Reason,
                status,
                $"Microsoft Foundry failed due to a configuration or authentication error ({failure.Reason}). Local fallback does not conceal credential or configuration faults.");
        }

        logger.LogWarning("Foundry attempt rejected: {Reason}", failure.Reason);
        ct.ThrowIfCancellationRequested();

        string fallbackProvider = (extractionOptions.Value.FallbackProvider ?? "LocalOnnx").Trim();
        if (!options.Value.AllowLocalFallback || !fallbackProvider.Equals("LocalOnnx", StringComparison.OrdinalIgnoreCase))
        {
            if (failure.Reason is "AZURE_INVALID_EVIDENCE" or "AZURE_UNUSABLE_JSON" or "AZURE_TRUNCATED")
                throw new ClinicalExtractionException(
                    failure.Reason,
                    422,
                    $"Primary clinical extraction output failed deterministic validation ({failure.Reason}) and local fallback is disabled.");
            throw new ClinicalExtractionException("EXTRACTION_UNAVAILABLE", 503, "The primary extractor is unavailable and local processing is disabled.");
        }

        try
        {
            var result = await local.ExtractAsync(source, correlationId, ct);
            string schemaStatus = failure.Reason is "AZURE_UNUSABLE_JSON" or "AZURE_INVALID_EVIDENCE" ? "failed" : "not_applicable";
            return result with
            {
                ModelMetadata = result.ModelMetadata with
                {
                    FallbackReason = failure.Reason,
                    SchemaValidationStatus = schemaStatus,
                    Attempts = [failure.Attempt, .. result.ModelMetadata.Attempts ?? []]
                }
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Local extractor unavailable; error type {ErrorType}", ex.GetType().Name);
            throw new ClinicalExtractionException("EXTRACTION_UNAVAILABLE", 503, "Neither clinical extractor could produce an accepted result.");
        }
    }

    private static List<QuarantineItemDto> Quarantine(ClinicalExtractionResponse result)
    {
        var entities = result.Medications.Select(m => (Category: "MEDICATION", m.Name, m.Normalization, m.Evidence))
            .Concat(result.Investigations.Select(i => (Category: "INVESTIGATION", i.Name, i.Normalization, i.Evidence)));
        return entities.Where(e => e.Normalization.Status != "matched" || e.Normalization.Confidence < SlidingWindowOnnxEngine.FailClosedConfidenceThreshold)
            .Select(e => new QuarantineItemDto(
                Guid.NewGuid().ToString("D"),
                result.ExtractionId,
                e.Category,
                e.Name,
                e.Name.Trim().ToLowerInvariant(),
                e.Normalization.Confidence,
                e.Normalization.Status == "uncertain" ? "uncertain_strength" : e.Category == "MEDICATION" ? "unmapped_rxnorm" : "unmapped_loinc",
                e.Evidence.Start,
                e.Evidence.End,
                ZeroLeakageValidator.Scrub(e.Evidence.Quote),
                "pending")).ToList();
    }
}
