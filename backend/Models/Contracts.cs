using System.Text.Json.Serialization;

namespace Apollo.Backend.Models;

// ============================================================================
// Platform & Review UI Contracts (cx-review-ui & platform-api.v1.yaml)
// ============================================================================

public record ClinicalExtractionRequest(
    [property: JsonPropertyName("noteText")] string NoteText,
    [property: JsonPropertyName("patientId")] string? PatientId = null,
    [property: JsonPropertyName("encounterId")] string? EncounterId = null,
    [property: JsonPropertyName("options")] Dictionary<string, object?>? Options = null
);

public record EvidenceDto(
    [property: JsonPropertyName("quote")] string Quote,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("start")] int? Start,
    [property: JsonPropertyName("end")] int? End
);

public record FindingDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("field")] string? Field,
    [property: JsonPropertyName("message")] string Message
);

public record NormalizationDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("matchedCode")] string? MatchedCode,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("semanticStatus")] string? SemanticStatus = null,
    [property: JsonPropertyName("candidateCode")] string? CandidateCode = null,
    [property: JsonPropertyName("terminologySource")] string? TerminologySource = null,
    [property: JsonPropertyName("terminologyVersion")] string? TerminologyVersion = null,
    [property: JsonPropertyName("mappingNote")] string? MappingNote = null
);

public record EntityProvenanceDto(
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("ruleModified")] bool RuleModified = false,
    [property: JsonPropertyName("modifications")] List<string>? Modifications = null,
    [property: JsonPropertyName("uncertaintyReason")] string? UncertaintyReason = null,
    [property: JsonPropertyName("quarantined")] bool Quarantined = false,
    [property: JsonPropertyName("triggeredAlertIds")] List<string>? TriggeredAlertIds = null
);

public record ExtractedMedicationDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("strength")] string? Strength,
    [property: JsonPropertyName("dose")] string? Dose,
    [property: JsonPropertyName("route")] string? Route,
    [property: JsonPropertyName("frequency")] string? Frequency,
    [property: JsonPropertyName("duration")] string? Duration,
    [property: JsonPropertyName("formulation")] string? Formulation,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("assertionStatus")] string AssertionStatus,
    [property: JsonPropertyName("evidence")] EvidenceDto Evidence,
    [property: JsonPropertyName("qualityFlags")] List<string> QualityFlags,
    [property: JsonPropertyName("findings")] List<FindingDto> Findings,
    [property: JsonPropertyName("normalization")] NormalizationDto Normalization,
    [property: JsonPropertyName("doseUnit")] string? DoseUnit = null,
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("origin")] string Origin = "LLM_EXTRACTED",
    [property: JsonPropertyName("provenance")] EntityProvenanceDto? Provenance = null
);

public record ExtractedInvestigationDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("timing")] string? Timing,
    [property: JsonPropertyName("specimen")] string? Specimen,
    [property: JsonPropertyName("assertionStatus")] string AssertionStatus,
    [property: JsonPropertyName("evidence")] EvidenceDto Evidence,
    [property: JsonPropertyName("qualityFlags")] List<string> QualityFlags,
    [property: JsonPropertyName("findings")] List<FindingDto> Findings,
    [property: JsonPropertyName("normalization")] NormalizationDto Normalization,
    [property: JsonPropertyName("matchedCode")] string? MatchedCode = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("status")] string Status = "preliminary",
    [property: JsonPropertyName("clinicalCategory")] string? ClinicalCategory = null,
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("resultValue")] string? ResultValue = null,
    [property: JsonPropertyName("origin")] string Origin = "LLM_EXTRACTED",
    [property: JsonPropertyName("provenance")] EntityProvenanceDto? Provenance = null
);

public record SectionSpanDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("heading")] string Heading,
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End
);

public record PreprocessedDto(
    [property: JsonPropertyName("charLength")] int CharLength,
    [property: JsonPropertyName("sections")] List<SectionSpanDto> Sections
);

public record ModelMetadataDto(
    [property: JsonPropertyName("adapterType")] string AdapterType,
    [property: JsonPropertyName("modelDeployment")] string ModelDeployment,
    [property: JsonPropertyName("promptVersion")] string PromptVersion,
    [property: JsonPropertyName("promptTokens")] int? PromptTokens = null,
    [property: JsonPropertyName("completionTokens")] int? CompletionTokens = null,
    [property: JsonPropertyName("totalTokens")] int? TotalTokens = null,
    [property: JsonPropertyName("estimatedCostUsd")] double? EstimatedCostUsd = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("azureDeployment")] string? AzureDeployment = null,
    [property: JsonPropertyName("fallbackReason")] string? FallbackReason = null,
    [property: JsonPropertyName("inferenceExecuted")] bool? InferenceExecuted = null,
    [property: JsonPropertyName("attempts")] List<ProviderAttemptDto>? Attempts = null,
    [property: JsonPropertyName("schemaValidationStatus")] string? SchemaValidationStatus = null,
    [property: JsonPropertyName("modelSha256")] string? ModelSha256 = null,
    [property: JsonPropertyName("tokenizerSha256")] string? TokenizerSha256 = null,
    [property: JsonPropertyName("executionMode")] string? ExecutionMode = null,
    [property: JsonPropertyName("reviewCandidates")] List<SourceReviewCandidateDto>? ReviewCandidates = null
);

public record ProviderAttemptDto(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("deployment")] string? Deployment,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("durationMs")] double DurationMs,
    [property: JsonPropertyName("inferenceExecuted")] bool InferenceExecuted,
    [property: JsonPropertyName("promptTokens")] int? PromptTokens = null,
    [property: JsonPropertyName("completionTokens")] int? CompletionTokens = null,
    [property: JsonPropertyName("totalTokens")] int? TotalTokens = null,
    [property: JsonPropertyName("onnxWindows")] int? OnnxWindows = null,
    [property: JsonPropertyName("inferenceDurationMs")] double? InferenceDurationMs = null,
    [property: JsonPropertyName("modelMedicationMentions")] int? ModelMedicationMentions = null,
    [property: JsonPropertyName("ruleMedicationMentions")] int? RuleMedicationMentions = null,
    [property: JsonPropertyName("ruleInvestigationMentions")] int? RuleInvestigationMentions = null
);

public record ValidationDimensionDto(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("evaluatedCount")] int EvaluatedCount,
    [property: JsonPropertyName("passedCount")] int PassedCount,
    [property: JsonPropertyName("warningCount")] int WarningCount,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("summary")] string Summary
);

public record ValidationSummaryDto(
    [property: JsonPropertyName("hasErrors")] bool HasErrors,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("warningCount")] int WarningCount,
    [property: JsonPropertyName("dimensions")] List<ValidationDimensionDto>? Dimensions = null
);

public record ClinicalSafetyAlertDto(
    [property: JsonPropertyName("alertId")] string AlertId,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("involvedEntities")] List<string> InvolvedEntities,
    [property: JsonPropertyName("recommendation")] string Recommendation,
    [property: JsonPropertyName("alertType")] string AlertType = "DERIVED_CLINICAL_INFERENCE",
    [property: JsonPropertyName("temporality")] string Temporality = "CURRENT_ACTIVE",
    [property: JsonPropertyName("thresholdCriterion")] string? ThresholdCriterion = null,
    [property: JsonPropertyName("observedFact")] string? ObservedFact = null,
    [property: JsonPropertyName("evidenceQuote")] string? EvidenceQuote = null,
    [property: JsonPropertyName("startChar")] int? StartChar = null,
    [property: JsonPropertyName("endChar")] int? EndChar = null
);

public record ClinicalExtractionResponse(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("extractionId")] string ExtractionId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("extractedAt")] string ExtractedAt,
    [property: JsonPropertyName("executionDurationMs")] double ExecutionDurationMs,
    [property: JsonPropertyName("modelMetadata")] ModelMetadataDto ModelMetadata,
    [property: JsonPropertyName("preprocessed")] PreprocessedDto Preprocessed,
    [property: JsonPropertyName("medications")] List<ExtractedMedicationDto> Medications,
    [property: JsonPropertyName("investigations")] List<ExtractedInvestigationDto> Investigations,
    [property: JsonPropertyName("validationSummary")] ValidationSummaryDto ValidationSummary,
    [property: JsonPropertyName("safetyAlerts")] List<ClinicalSafetyAlertDto>? SafetyAlerts = null,
    [property: JsonPropertyName("embedding")] float[]? Embedding = null,
    [property: JsonPropertyName("noteText")] string? NoteText = null,
    [property: JsonPropertyName("inputType")] string InputType = "text",
    [property: JsonPropertyName("sourceFileName")] string? SourceFileName = null,
    [property: JsonPropertyName("saved")] bool Saved = false,
    [property: JsonPropertyName("entityReviews")] List<EntityReviewRecordDto>? EntityReviews = null
);

// ============================================================================
// Entity-Level Clinical Review & Audit Trail Contracts (Stage 5)
// ============================================================================

public record EntityReviewSubmissionRequest(
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityIndex")] int EntityIndex,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("correctedEntity")] Dictionary<string, object?>? CorrectedEntity = null,
    [property: JsonPropertyName("reviewComment")] string? ReviewComment = null,
    [property: JsonPropertyName("expectedVersion")] int? ExpectedVersion = null
);

public record EntityReviewRecordDto(
    [property: JsonPropertyName("reviewId")] string ReviewId,
    [property: JsonPropertyName("extractionId")] string ExtractionId,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityIndex")] int EntityIndex,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("originalEntity")] Dictionary<string, object?> OriginalEntity,
    [property: JsonPropertyName("correctedEntity")] Dictionary<string, object?>? CorrectedEntity,
    [property: JsonPropertyName("originalEvidence")] EvidenceDto OriginalEvidence,
    [property: JsonPropertyName("reviewAction")] string ReviewAction,
    [property: JsonPropertyName("reviewerIdentity")] string ReviewerIdentity,
    [property: JsonPropertyName("authMode")] string AuthMode,
    [property: JsonPropertyName("reviewComment")] string? ReviewComment,
    [property: JsonPropertyName("reviewedAt")] string ReviewedAt,
    [property: JsonPropertyName("versionNumber")] int VersionNumber
);

// ============================================================================
// Apollo Bento Studio REST Contracts (/v1/ner/predict)
// ============================================================================

public record ClinicalNERRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("apply_assertion")] bool ApplyAssertion = true,
    [property: JsonPropertyName("enable_grounding")] bool EnableGrounding = true,
    [property: JsonPropertyName("include_fhir")] bool IncludeFhir = true,
    [property: JsonPropertyName("include_text")] bool IncludeText = true
);

public record ClinicalEntityResponse(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start_char")] int StartChar,
    [property: JsonPropertyName("end_char")] int EndChar,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("assertion")] string Assertion = "PRESENT",
    [property: JsonPropertyName("certainty")] string? Certainty = null,
    [property: JsonPropertyName("temporality")] string? Temporality = null,
    [property: JsonPropertyName("experiencer")] string? Experiencer = null,
    [property: JsonPropertyName("section")] string? Section = null,
    [property: JsonPropertyName("concept_code")] string? ConceptCode = null,
    [property: JsonPropertyName("canonical_name")] string? CanonicalName = null
);

public record StructuredMedicationResponse(
    [property: JsonPropertyName("drug")] string Drug,
    [property: JsonPropertyName("drug_span")] List<int> DrugSpan,
    [property: JsonPropertyName("dosage")] string? Dosage = null,
    [property: JsonPropertyName("dose")] string? Dose = null,
    [property: JsonPropertyName("frequency")] string? Frequency = null,
    [property: JsonPropertyName("route")] string? Route = null,
    [property: JsonPropertyName("duration")] string? Duration = null,
    [property: JsonPropertyName("certainty")] string? Certainty = null,
    [property: JsonPropertyName("experiencer")] string? Experiencer = null,
    [property: JsonPropertyName("concept_code")] string? ConceptCode = null,
    [property: JsonPropertyName("attribute_spans")] Dictionary<string, List<int>>? AttributeSpans = null
)
{
    [JsonIgnore]
    public string Medication => Drug;
}

public record ClinicalNERResponse(
    [property: JsonPropertyName("entities")] List<ClinicalEntityResponse> Entities,
    [property: JsonPropertyName("structured_medications")] List<StructuredMedicationResponse> StructuredMedications,
    [property: JsonPropertyName("micro_card")] string? MicroCard,
    [property: JsonPropertyName("fhir_bundle")] Dictionary<string, object?>? FhirBundle,
    [property: JsonPropertyName("latency_ms")] double LatencyMs,
    [property: JsonPropertyName("model_version")] string? ModelVersion = null,
    [property: JsonPropertyName("text")] string? Text = null
);

// ============================================================================
// PII & Privacy Models
// ============================================================================

public record FlaggedPiiItem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("matched")] string Matched,
    [property: JsonPropertyName("count")] int Count
);

public record PiiErrorResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("flagged")] List<FlaggedPiiItem> Flagged,
    [property: JsonPropertyName("error_code")] string ErrorCode = "PII_LEAKAGE_DETECTED"
);

// ============================================================================
// Background Job & Asynchronous Outbox Event Models
// ============================================================================

public record NoteVersionCommittedEvent(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("occurredAt")] string OccurredAt,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("sourceSystem")] string SourceSystem,
    [property: JsonPropertyName("patientId")] string PatientId,
    [property: JsonPropertyName("encounterId")] string EncounterId,
    [property: JsonPropertyName("noteId")] string NoteId,
    [property: JsonPropertyName("noteVersion")] int NoteVersion,
    [property: JsonPropertyName("contentSha256")] string ContentSha256,
    [property: JsonPropertyName("charLength")] int CharLength,
    [property: JsonPropertyName("noteType")] string NoteType,
    [property: JsonPropertyName("facilityId")] string FacilityId,
    [property: JsonPropertyName("committedAt")] string CommittedAt,
    [property: JsonPropertyName("trigger")] string Trigger = "realtime"
);

// ============================================================================
// Active Learning & OOV Quarantine Contracts
// ============================================================================

public record QuarantineItemDto(
    [property: JsonPropertyName("quarantineId")] string QuarantineId,
    [property: JsonPropertyName("extractionId")] string? ExtractionId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("mention")] string Mention,
    [property: JsonPropertyName("normalizedMention")] string NormalizedMention,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("startChar")] int? StartChar,
    [property: JsonPropertyName("endChar")] int? EndChar,
    [property: JsonPropertyName("scrubbedContext")] string ScrubbedContext,
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("mappedCode")] string? MappedCode = null,
    [property: JsonPropertyName("mappedDisplay")] string? MappedDisplay = null,
    [property: JsonPropertyName("reviewerNotes")] string? ReviewerNotes = null,
    [property: JsonPropertyName("createdAt")] string CreatedAt = "",
    [property: JsonPropertyName("reviewedAt")] string? ReviewedAt = null
);

public record CreateQuarantineItemRequest(
    [property: JsonPropertyName("extractionId")] string? ExtractionId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("mention")] string Mention,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reason")] string Reason = "fail_closed_oov",
    [property: JsonPropertyName("startChar")] int? StartChar = null,
    [property: JsonPropertyName("endChar")] int? EndChar = null,
    [property: JsonPropertyName("context")] string Context = ""
);

public record QuarantineResolutionRequest(
    [property: JsonPropertyName("reviewStatus")] string ReviewStatus,
    [property: JsonPropertyName("mappedCode")] string? MappedCode = null,
    [property: JsonPropertyName("mappedDisplay")] string? MappedDisplay = null,
    [property: JsonPropertyName("reviewerNotes")] string? ReviewerNotes = null
);

// ============================================================================
// Semantic Vector Search & Active Learning Feedback Loop Contracts
// ============================================================================

public record SemanticSearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("limit")] int Limit = 10,
    [property: JsonPropertyName("minSimilarity")] double MinSimilarity = 0.05
);

public record SemanticSearchResultDto(
    [property: JsonPropertyName("extraction")] ClinicalExtractionResponse Extraction,
    [property: JsonPropertyName("similarityScore")] double SimilarityScore,
    [property: JsonPropertyName("matchedHighlights")] List<string> MatchedHighlights
);

public record ActiveLearningFeedbackDto(
    [property: JsonPropertyName("feedbackId")] string FeedbackId,
    [property: JsonPropertyName("quarantineId")] string QuarantineId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("mention")] string Mention,
    [property: JsonPropertyName("normalizedMention")] string NormalizedMention,
    [property: JsonPropertyName("mappedCode")] string MappedCode,
    [property: JsonPropertyName("mappedDisplay")] string MappedDisplay,
    [property: JsonPropertyName("scrubbedContext")] string ScrubbedContext,
    [property: JsonPropertyName("promptExemplar")] string PromptExemplar,
    [property: JsonPropertyName("createdAt")] string CreatedAt
);

public record ActiveLearningStateDto(
    [property: JsonPropertyName("learnedMedicationCount")] int LearnedMedicationCount,
    [property: JsonPropertyName("learnedInvestigationCount")] int LearnedInvestigationCount,
    [property: JsonPropertyName("datasetEntriesCount")] int DatasetEntriesCount,
    [property: JsonPropertyName("promptExemplars")] List<string> PromptExemplars,
    [property: JsonPropertyName("recentFeedback")] List<ActiveLearningFeedbackDto> RecentFeedback
);

public record SourceReviewCandidateDto(string Name, string Category, double Confidence, EvidenceDto Evidence, string Reason, bool ReviewRequired = true);
