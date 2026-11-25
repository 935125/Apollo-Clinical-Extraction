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
    ILogger<ExtractionWorkflow> logger,
    FoundryCircuitBreaker circuit,
    SyntheticDataPrivacyPolicy privacyPolicy)
{
    public async Task<ClinicalExtractionResponse> ExtractAndSaveAsync(
        string noteText,
        string correlationId,
        string inputType,
        string? sourceFileName,
        CancellationToken ct,
        Guid? durableExtractionId = null,
        ExtractionPrivacyContext? privacyContext = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(noteText))
            throw new ClinicalExtractionException("EMPTY_NOTE_TEXT", 400, "A clinical note is required.");
        if (noteText.Length > 65536)
            throw new ClinicalExtractionException("NOTE_EXCEEDS_64KIB_LIMIT", 400, "The note exceeds 65,536 characters.");

        string primaryProvider = (extractionOptions.Value.Provider ?? "MicrosoftFoundry").Trim();
        var privacyDecision = privacyPolicy.Evaluate(privacyContext, primaryProvider);
        if (!privacyDecision.AllowSyntheticIdentifiers)
        {
            // Pattern-based identifier screening precedes inference; fail closed for all real, unknown, or unverified records.
            ZeroLeakageValidator.Validate(noteText);
        }
        else
        {
            var detected = ZeroLeakageValidator.Scan(noteText);
            if (detected.Count > 0)
            {
                logger.LogInformation(
                    "Authorized Development synthetic test record permitted under {PolicyReason}; provider={Provider}; patientRef={PatientRef}; eventId={EventId}; identifierCategories={Categories}",
                    privacyDecision.ReasonCode,
                    primaryProvider,
                    privacyContext?.PatientReference,
                    privacyContext?.EventId,
                    string.Join(",", detected.Select(d => d.Type).Distinct()));
            }
        }

        var watch = Stopwatch.StartNew();
        string extractionId = (durableExtractionId ?? Guid.NewGuid()).ToString("D");
        ClinicalExtractionResponse accepted;
        FoundryExtraction? capturedAzure = null;

        if (primaryProvider.Equals("LocalOnnx", StringComparison.OrdinalIgnoreCase))
        {
            if (ProviderRoutingPolicy.DirectLocalFault(extractionOptions.Value) is string localFault)
                throw new ClinicalExtractionException(localFault, 503, "Direct LocalOnnx requires an explicit authorized offline or isolated-test selection.");
            accepted = await local.ExtractAsync(noteText, correlationId, privacyDecision.AllowSyntheticIdentifiers, ct);
            accepted = accepted with { ModelMetadata = accepted.ModelMetadata with { RoutingMode = "explicit_offline", PrimaryInvocationStatus = "not_attempted_offline", FallbackTriggered = false } };
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
            string? configurationFault = ProviderRoutingPolicy.ConfigurationFault(options.Value);
            if (configurationFault != null)
                throw ProviderRoutingPolicy.Reject(new(configurationFault, new("foundry", options.Value.TargetModel, options.Value.EffectiveDeployment, "blocked_configuration", configurationFault, 0, false, HttpAttempts: 0, CorrelationId: correlationId), true));
            try
            {
                if (!circuit.Enter(options.Value))
                    throw new FoundryAttemptException("AZURE_CIRCUIT_OPEN", new("foundry", options.Value.TargetModel, options.Value.EffectiveDeployment, "circuit_open", "AZURE_CIRCUIT_OPEN", 0, false, HttpAttempts: 0, CorrelationId: correlationId));
                using var invocation = FoundryInvocationContext.Begin(correlationId);
                azure = await foundry.ExtractAsync(noteText, ct);
                capturedAzure = azure;
                var foundryAttempts = new List<ProviderAttemptDto> { azure.Attempt };
                int maxRepairRetries = Math.Clamp(options.Value.MaxEvidenceRepairRetries, 0, 2);
                int repairAttempt = 0;
                while (true)
                {
                    try
                    {
                        accepted = inputType.Equals("emr", StringComparison.OrdinalIgnoreCase)
                            ? validator.MapResilient(azure.Candidate, noteText, correlationId, logger)
                            : validator.Map(azure.Candidate, noteText, correlationId);
                        if (accepted.ValidationSummary.HasErrors) throw new InvalidClinicalEvidenceException(ClinicalValidationRule.VALIDATION_SUMMARY_ERRORS);
                        break;
                    }
                    catch (InvalidClinicalEvidenceException rejection) when (repairAttempt < maxRepairRetries && !string.IsNullOrWhiteSpace(azure.RawStructuredContent))
                    {
                        repairAttempt++;
                        logger.LogWarning(
                            "Foundry evidence validation failed on attempt {Attempt}; triggering bounded self-repair ({RepairAttempt}/{MaxRepair}): rule={ValidationRuleId}; category={ValidationCategory}; entityType={ValidationEntityType}; entityIndex={ValidationEntityIndex}; field={ValidationField}",
                            repairAttempt, repairAttempt, maxRepairRetries, rejection.Diagnostic.RuleId, rejection.Diagnostic.Category,
                            rejection.Diagnostic.EntityType, rejection.Diagnostic.EntityIndex, rejection.Diagnostic.Field);
                        foundryAttempts[^1] = azure.Attempt with { Outcome = "rejected", Reason = "AZURE_INVALID_EVIDENCE" };
                        azure = await foundry.RepairAsync(noteText, azure.RawStructuredContent!, rejection.Diagnostic, ct);
                        capturedAzure = azure;
                        foundryAttempts.Add(azure.Attempt);
                    }
                }
                circuit.Success();
                accepted = accepted with
                {
                    ModelMetadata = new(
                        "microsoft_foundry",
                        azure.Attempt.Deployment!,
                        "clinical-v3",
                        foundryAttempts.Sum(a => a.PromptTokens ?? 0) is > 0 ? foundryAttempts.Sum(a => a.PromptTokens ?? 0) : azure.Attempt.PromptTokens,
                        foundryAttempts.Sum(a => a.CompletionTokens ?? 0) is > 0 ? foundryAttempts.Sum(a => a.CompletionTokens ?? 0) : azure.Attempt.CompletionTokens,
                        foundryAttempts.Sum(a => a.TotalTokens ?? 0) is > 0 ? foundryAttempts.Sum(a => a.TotalTokens ?? 0) : azure.Attempt.TotalTokens,
                        null,
                        "foundry",
                        azure.Attempt.Model,
                        azure.Attempt.Deployment,
                        null,
                        true,
                        foundryAttempts,
                        "passed", RoutingMode: "primary", PrimaryInvocationStatus: "accepted", FallbackTriggered: false)
                };
            }
            catch (InvalidClinicalEvidenceException rejection)
            {
                circuit.Success();
                logger.LogWarning("Clinical evidence rejected: {Code}; rule={ValidationRuleId}; category={ValidationCategory}; entityType={ValidationEntityType}; entityIndex={ValidationEntityIndex}; field={ValidationField}",
                    "AZURE_INVALID_EVIDENCE", rejection.Diagnostic.RuleId, rejection.Diagnostic.Category,
                    rejection.Diagnostic.EntityType, rejection.Diagnostic.EntityIndex, rejection.Diagnostic.Field);
                var attempt = azure!.Attempt with { Outcome = "rejected", Reason = "AZURE_INVALID_EVIDENCE" };
                capturedAzure = azure with { Attempt = attempt };
                throw ProviderRoutingPolicy.Reject(new("AZURE_INVALID_EVIDENCE", attempt), rejection.Diagnostic);
            }
            catch (FoundryAttemptException failure)
            {
                if (ProviderRoutingPolicy.IsTransient(failure.Reason) && failure.Reason != "AZURE_CIRCUIT_OPEN") circuit.TransientFailure(options.Value);
                else if (failure.Reason != "AZURE_CIRCUIT_OPEN") circuit.Success();
                bool allowFallbackSynthetic = privacyPolicy.Evaluate(privacyContext, "LocalOnnx").AllowSyntheticIdentifiers;
                accepted = await Fallback(noteText, correlationId, failure, allowFallbackSynthetic, ct);
            }
            catch (ClinicalExtractionException) { circuit.Success(); throw; }
            catch (OperationCanceledException) { circuit.Success(); throw; }

        }

        ct.ThrowIfCancellationRequested();
        accepted = accepted with
        {
            ModelMetadata = accepted.ModelMetadata with
            {
                RequestedPrimaryProvider = primaryProvider,
                FinalExecutedProvider = accepted.ModelMetadata.Provider == "foundry" ? "MicrosoftFoundry" : "LocalOnnx",
                ExtractionStatus = "completed_requires_review",
                ValidationResult = accepted.ValidationSummary.HasErrors ? "review_required" : "source_validated_requires_review",
                Attempts = accepted.ModelMetadata.Attempts?.Select(a => a with { CorrelationId = correlationId }).ToList()
            },
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
        bool allowVerifiedSyntheticIdentifiers,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ProviderRoutingPolicy.AllowsFallback(failure, options.Value, extractionOptions.Value))
            throw ProviderRoutingPolicy.Reject(failure);
        logger.LogWarning("Eligible Foundry failure; executing verified fallback: {Reason}", failure.Reason);

        try
        {
            var result = await local.ExtractAsync(source, correlationId, allowVerifiedSyntheticIdentifiers, ct);
            if (result.ValidationSummary.HasErrors)
                throw new ClinicalExtractionException("ONNX_VALIDATION_REJECTED", 422, "Fallback output requires clinical review and was not accepted.");
            string schemaStatus = failure.Reason == "AZURE_UNUSABLE_JSON" ? "failed" : failure.Reason == "AZURE_TRUNCATED" ? "incomplete" : "not_applicable";
            return result with
            {
                ModelMetadata = result.ModelMetadata with
                {
                    FallbackReason = failure.Reason,
                    FallbackTriggered = true,
                    RoutingMode = "fallback",
                    PrimaryInvocationStatus = failure.Attempt.Outcome,
                    SchemaValidationStatus = schemaStatus,
                    Attempts = [failure.Attempt, .. result.ModelMetadata.Attempts ?? []]
                }
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Local extractor unavailable; error type {ErrorType}", ex.GetType().Name);
            throw new ClinicalExtractionException("EXTRACTION_UNAVAILABLE", 503, "Neither clinical extractor could produce an accepted result.")
            { Routing = new("MicrosoftFoundry", [failure.Attempt, new("onnx", "bc5cdr_int8", null, "rejected", ex is ClinicalExtractionException localFailure ? localFailure.Code : "ONNX_UNAVAILABLE", 0, false, CorrelationId: correlationId)], true, failure.Reason, "failed_requires_review") };
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
