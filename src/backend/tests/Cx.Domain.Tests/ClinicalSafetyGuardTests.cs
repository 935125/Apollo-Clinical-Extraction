using Cx.Domain.Entities;
using Cx.Domain.Enums;
using Shouldly;
using Xunit;

namespace Cx.Domain.Tests;

public class ClinicalSafetyGuardTests
{
    [Fact]
    public void StaleNoteVersion_IsNeverWritebackEligible()
    {
        var item = new MedicationItem
        {
            NameText = "Paracetamol",
            AssertionStatus = MedicationAssertionStatus.NewOrder,
            Evidence = new EvidenceSpan("Paracetamol 500mg PO BD", true, 0, 23, null),
            NormalizationStatus = NormalizationStatus.Matched,
            MatchedDrugMasterId = Guid.NewGuid(),
            ReviewStatus = ItemReviewStatus.Confirmed
        };

        // Note in EMR was amended to version 2, while review was on version 1
        item.IsWritebackEligible(currentNoteVersion: 2, extractionNoteVersion: 1).ShouldBeFalse();
    }

    [Fact]
    public void NegatedOrHistoricalMedication_IsNeverWritebackEligible()
    {
        var negatedItem = new MedicationItem
        {
            NameText = "Aspirin",
            AssertionStatus = MedicationAssertionStatus.Negated,
            Evidence = new EvidenceSpan("No Aspirin", true, 0, 10, null),
            NormalizationStatus = NormalizationStatus.Matched,
            MatchedDrugMasterId = Guid.NewGuid(),
            ReviewStatus = ItemReviewStatus.Confirmed
        };

        negatedItem.IsWritebackEligible(currentNoteVersion: 1, extractionNoteVersion: 1).ShouldBeFalse();
    }

    [Fact]
    public void ValidConfirmedNewOrder_OnCurrentVersion_IsWritebackEligible()
    {
        var item = new MedicationItem
        {
            NameText = "Amoxicillin",
            AssertionStatus = MedicationAssertionStatus.NewOrder,
            Evidence = new EvidenceSpan("Amoxicillin 500mg TDS", true, 0, 20, null),
            NormalizationStatus = NormalizationStatus.Matched,
            MatchedDrugMasterId = Guid.NewGuid(),
            ReviewStatus = ItemReviewStatus.Confirmed
        };

        item.IsWritebackEligible(currentNoteVersion: 1, extractionNoteVersion: 1).ShouldBeTrue();
    }
}

