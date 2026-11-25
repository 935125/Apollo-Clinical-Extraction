namespace Apollo.Backend.Clinical;

public sealed class LocalClinicalPipelineOptions
{
    public const string SectionName = "LocalOnnx";
    // Explicit opt-in until the independently adjudicated clinical release gate is met.
    public string PipelineVersion { get; set; } = "Legacy";
    public double TimeoutSeconds { get; set; } = 30;
    public bool IsUpgraded => PipelineVersion.Equals("CandidateV2", StringComparison.OrdinalIgnoreCase);
    public bool IsValid => IsUpgraded || PipelineVersion.Equals("Legacy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Offline ablations only. These switches are not a second production routing configuration.</summary>
public sealed record LocalPipelineFeatures(bool MedicationCandidates = true, bool Investigations = true,
    bool Assertions = true, bool Terminology = true, bool Attributes = true, bool Sections = true, bool ContextualWindows = true);

public sealed record CandidateTrace(string Text, string Category, string Section, int Start, int End,
    double? NerScore, string DetectionSource, string CatalogMatch, IReadOnlyList<string> MedicationContext,
    string Context, string Status, string? FinalClassification, string Reason,
    IReadOnlyList<string> States);

public sealed record LocalEntitySpan(string Category, int Start, int End, string Text,
    double? NerScore, string DetectionSource, string CatalogMatch = "unresolved");
