using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Security;

public sealed class SyntheticDataPrivacyOptions
{
    public const string SectionName = "Security:SyntheticData";

    /// <summary>
    /// Disabled by default across all environments. May only take effect in Development
    /// with LocalDemoMode enabled and a backend-verified synthetic PostgreSQL patient record.
    /// </summary>
    public bool AllowAuthorizedSyntheticIdentifiers { get; set; } = false;

    /// <summary>
    /// Independent opt-in gate for sending backend-verified synthetic notes containing fictional
    /// identifiers to Microsoft Foundry in Development. Disabled by default in appsettings.json.
    /// Real or unverified notes are never exempt under any configuration.
    /// </summary>
    public bool AllowExternalFoundryForVerifiedSynthetic { get; set; } = false;

    /// <summary>
    /// Explicit allowlist of authorized synthetic patient UHIDs (e.g. P100234..P100237).
    /// When non-empty, only listed patient references may use the Development synthetic-data policy.
    /// </summary>
    public string[] AllowedPatientReferences { get; set; } = [];
}

public enum PrivacyProvenanceKind
{
    UnverifiedOrReal = 0,
    BackendVerifiedSyntheticEmrRecord = 1
}

/// <summary>
/// Server-constructed provenance object. Never deserialized from HTTP requests,
/// query parameters, or client headers (prevents client-spoofed isSynthetic flags).
/// </summary>
public sealed record ExtractionPrivacyContext(
    PrivacyProvenanceKind Kind,
    Guid? PatientId = null,
    string? PatientReference = null,
    Guid? EncounterId = null,
    Guid? NoteId = null,
    int? NoteVersion = null,
    Guid? EventId = null,
    string? VerifiedSourceHash = null,
    bool DatabaseSyntheticFlag = false)
{
    public static readonly ExtractionPrivacyContext Unverified = new(PrivacyProvenanceKind.UnverifiedOrReal);

    public bool HasVerifiedDatabaseSyntheticRecord =>
        Kind == PrivacyProvenanceKind.BackendVerifiedSyntheticEmrRecord
        && DatabaseSyntheticFlag
        && PatientId is { } pid && pid != Guid.Empty
        && !string.IsNullOrWhiteSpace(PatientReference)
        && EncounterId is { } eid && eid != Guid.Empty
        && NoteId is { } nid && nid != Guid.Empty
        && NoteVersion is > 0
        && EventId is { } evId && evId != Guid.Empty
        && !string.IsNullOrWhiteSpace(VerifiedSourceHash);
}

public sealed record SyntheticPrivacyDecision(bool AllowSyntheticIdentifiers, string ReasonCode);

/// <summary>
/// Enforces strict fail-closed ZeroLeakageValidator screening for all real, unknown,
/// direct-API, or non-Development clinical records, while permitting realistic fictional
/// identifiers strictly for backend-verified synthetic test dataset records in Development.
/// </summary>
public sealed partial class SyntheticDataPrivacyPolicy(
    IHostEnvironment environment,
    IConfiguration configuration,
    IOptions<SyntheticDataPrivacyOptions> options)
{
    [GeneratedRegex(@"^P\d{6,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefaultSyntheticPatientRefRegex();

    public SyntheticPrivacyDecision Evaluate(ExtractionPrivacyContext? context, string targetProvider)
    {
        if (!environment.IsDevelopment())
            return new(false, "STRICT_ENVIRONMENT_NOT_DEVELOPMENT");

        if (!ClinicalApiSecurity.IsDemo(configuration, environment))
            return new(false, "STRICT_LOCAL_DEMO_MODE_DISABLED");

        var opts = options.Value;
        if (!opts.AllowAuthorizedSyntheticIdentifiers)
            return new(false, "STRICT_SYNTHETIC_POLICY_DISABLED");

        if (context is null || !context.HasVerifiedDatabaseSyntheticRecord)
            return new(false, "STRICT_UNVERIFIED_OR_REAL_RECORD");

        string patientRef = context.PatientReference!.Trim();
        var allowlist = opts.AllowedPatientReferences?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToArray() ?? [];

        if (allowlist.Length > 0)
        {
            if (!allowlist.Contains(patientRef, StringComparer.OrdinalIgnoreCase))
                return new(false, "STRICT_PATIENT_REFERENCE_NOT_IN_ALLOWLIST");
        }
        else if (!DefaultSyntheticPatientRefRegex().IsMatch(patientRef))
        {
            return new(false, "STRICT_PATIENT_REFERENCE_FORMAT_UNVERIFIED");
        }

        bool isExternalFoundry = targetProvider.Equals("MicrosoftFoundry", StringComparison.OrdinalIgnoreCase);
        if (isExternalFoundry && !opts.AllowExternalFoundryForVerifiedSynthetic)
            return new(false, "STRICT_EXTERNAL_FOUNDRY_SYNTHETIC_DISABLED");

        return new(true, "AUTHORIZED_DEV_SYNTHETIC_DATASET");
    }
}
