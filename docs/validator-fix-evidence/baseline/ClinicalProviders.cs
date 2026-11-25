using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public sealed class ExtractionOptions
{
    public const string SectionName = "Extraction";
    public string Provider { get; set; } = "MicrosoftFoundry";
    public bool AllowDirectLocal { get; set; }
    public string? DirectLocalReason { get; set; }
    public string FallbackProvider { get; set; } = "LocalOnnx";
    /// <summary>
    /// Disabled by default for patient data governance. When enabled strictly for synthetic test runs,
    /// preserves stage-by-stage extraction trace artifacts including raw structured model output.
    /// </summary>
    public bool EnableSyntheticDiagnosticCapture { get; set; } = false;
    public string? SyntheticDiagnosticOutputDirectory { get; set; }
}

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? Deployment { get; set; }
    public string? TargetModel { get; set; }
    public string? AuthenticationMode { get; set; }
    public string Authentication { get; set; } = "EntraId";
    public string? ApiKey { get; set; }
    public double TimeoutSeconds { get; set; } = 45;
    public int MaxRetries { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 250;
    public int MaxCompletionTokens { get; set; } = 16000;
    public bool AllowLocalFallback { get; set; } = true;
    public bool AllowParsingFallback { get; set; } = false;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitBreakSeconds { get; set; } = 30;
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";
    public string? ManagedIdentityClientId { get; set; }

    public string? EffectiveDeployment =>
        !string.IsNullOrWhiteSpace(DeploymentName) ? DeploymentName : Deployment;

    public string EffectiveAuthenticationMode =>
        !string.IsNullOrWhiteSpace(AuthenticationMode) ? AuthenticationMode : Authentication;
}

// The provider returns clinical fields only. IDs, verification, terminology and
// persistence metadata always belong to .NET. Every nullable member is required
// in the strict schema and must be JSON null when it is not documented.
public record CandidateEvidence(string Quote, int Occurrence);
public record CandidateMedication(string Name, string? Strength, string? Dose, string? DoseUnit,
    string? Route, string? Frequency, string? Duration, string? Formulation,
    string? Instructions, string Action, string AssertionStatus, CandidateEvidence Evidence);
public record CandidateInvestigation(string Name, string? Timing, string? Specimen,
    string AssertionStatus, string Status, CandidateEvidence Evidence);
public record ClinicalCandidate(List<CandidateMedication> Medications, List<CandidateInvestigation> Investigations);

public static class ClinicalCandidateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static readonly JsonNode Schema = Options.GetJsonSchemaAsNode(typeof(ClinicalCandidate),
        new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (_, node) =>
            {
                if (node is JsonObject obj && obj["properties"] is JsonObject properties)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                }
                return node;
            }
        });
}

public record FoundryExtraction(ClinicalCandidate Candidate, ProviderAttemptDto Attempt, string? RawStructuredContent = null);
public interface IClinicalExtractionProvider
{
    string ProviderName { get; }
    Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, CancellationToken ct);
}
public interface IFoundryClinicalExtractor
{
    Task<FoundryExtraction> ExtractAsync(string source, CancellationToken ct);
}
public interface ILocalClinicalExtractor : IClinicalExtractionProvider
{
    Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, bool allowVerifiedSyntheticIdentifiers, CancellationToken ct)
        => ExtractAsync(source, correlationId, ct);
}

public sealed class FoundryAttemptException(string reason, ProviderAttemptDto attempt, bool operatorFault = false)
    : Exception(reason)
{
    public string Reason { get; } = reason;
    public ProviderAttemptDto Attempt { get; } = attempt;
    public bool OperatorFault { get; } = operatorFault;
}
public class ClinicalExtractionException(string code, int status, string safeMessage) : Exception(safeMessage)
{
    public ExtractionFailureProvenance? Routing { get; init; }
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed class PersistenceFailureException() : ClinicalExtractionException(
    "PERSISTENCE_FAILED", 503, "The extraction could not be saved. Your source note is retained.");
// Do not retain source-bearing diagnostic strings in exception objects or traces.
public sealed class InvalidClinicalEvidenceException() : Exception("Unsupported clinical evidence");
