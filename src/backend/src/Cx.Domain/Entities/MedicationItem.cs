using Cx.Domain.Enums;

namespace Cx.Domain.Entities;

public record EvidenceSpan(
    string Quote,
    bool Verified,
    int? Start,
    int? End,
    string? SectionCode
);

public record ValidationFinding(
    string Code,
    string Severity,
    string? Field,
    string Message
);

public record QualityInfo(
    decimal? Score,
    IReadOnlyList<string> Flags
);

public class MedicationItem
{
    public Guid MedicationId { get; init; } = Guid.NewGuid();
    public int ItemIndex { get; init; }
    public string NameText { get; init; } = default!;
    public string? StrengthText { get; init; }
    public string? DoseText { get; init; }
    public string? RouteCode { get; init; }
    public string? FrequencyCode { get; init; }
    public string? FormulationCode { get; init; }
    public MedicationAssertionStatus AssertionStatus { get; init; }
    public EvidenceSpan Evidence { get; init; } = default!;
    public NormalizationStatus NormalizationStatus { get; set; } = NormalizationStatus.NotAttempted;
    public Guid? MatchedDrugMasterId { get; set; }
    public QualityInfo Quality { get; init; } = new(null, Array.Empty<string>());
    public ItemReviewStatus ReviewStatus { get; set; } = ItemReviewStatus.Pending;
    public long RowVersion { get; set; } = 1;

    public bool IsWritebackEligible(int currentNoteVersion, int extractionNoteVersion)
    {
        // Clinical safety guard: Must be confirmed or corrected, new order intent, matched, and from current version
        if (currentNoteVersion != extractionNoteVersion) return false;
        if (ReviewStatus != ItemReviewStatus.Confirmed && ReviewStatus != ItemReviewStatus.Corrected) return false;
        if (AssertionStatus != MedicationAssertionStatus.NewOrder) return false;
        if (NormalizationStatus != NormalizationStatus.Matched || MatchedDrugMasterId == null) return false;
        if (!Evidence.Verified) return false;
        return true;
    }
}

