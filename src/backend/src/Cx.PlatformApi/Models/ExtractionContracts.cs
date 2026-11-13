using System.Text.Json.Serialization;

namespace Cx.PlatformApi.Models;

public record ClinicalExtractionRequest(
    [property: JsonPropertyName("noteText")] string NoteText,
    [property: JsonPropertyName("patientId")] string? PatientId = null,
    [property: JsonPropertyName("encounterId")] string? EncounterId = null
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
    [property: JsonPropertyName("confidence")] double Confidence
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
    [property: JsonPropertyName("normalization")] NormalizationDto Normalization
);

public record ExtractedInvestigationDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("timing")] string? Timing,
    [property: JsonPropertyName("specimen")] string? Specimen,
    [property: JsonPropertyName("assertionStatus")] string AssertionStatus,
    [property: JsonPropertyName("evidence")] EvidenceDto Evidence,
    [property: JsonPropertyName("qualityFlags")] List<string> QualityFlags,
    [property: JsonPropertyName("findings")] List<FindingDto> Findings,
    [property: JsonPropertyName("normalization")] NormalizationDto Normalization
);

public record ValidationSummaryDto(
    [property: JsonPropertyName("hasErrors")] bool HasErrors,
    [property: JsonPropertyName("errorCount")] int ErrorCount,
    [property: JsonPropertyName("warningCount")] int WarningCount
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
    [property: JsonPropertyName("estimatedCostUsd")] double? EstimatedCostUsd = null
);

public record ClinicalExtractionResponse(
    [property: JsonPropertyName("extractionId")] string ExtractionId,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("extractedAt")] DateTime ExtractedAt,
    [property: JsonPropertyName("executionDurationMs")] double ExecutionDurationMs,
    [property: JsonPropertyName("modelMetadata")] ModelMetadataDto ModelMetadata,
    [property: JsonPropertyName("preprocessed")] PreprocessedDto Preprocessed,
    [property: JsonPropertyName("medications")] List<ExtractedMedicationDto> Medications,
    [property: JsonPropertyName("investigations")] List<ExtractedInvestigationDto> Investigations,
    [property: JsonPropertyName("validationSummary")] ValidationSummaryDto ValidationSummary
);
