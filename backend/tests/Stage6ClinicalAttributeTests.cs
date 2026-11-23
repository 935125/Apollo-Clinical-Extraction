using Apollo.Backend.Clinical;

namespace Apollo.Backend.Tests;

public class Stage6ClinicalAttributeTests
{
    [Fact]
    public void MissingNeighbourEntity_CannotSupplyMedicationAttributes()
    {
        const string source = "Start aspirin, and start unknownDrug 50 mg PO twice daily for 4 days.";
        var bound = new PosologyBinder().Bind(source, 6, 13, [(6,13)]);
        Assert.Null(bound.Strength); Assert.Null(bound.Route);
        Assert.Null(bound.Frequency); Assert.Null(bound.Duration);
    }

    [Fact]
    public void CoordinatedTiming_IsShared_IndependentTimingDoesNotLeak()
    {
        const string source = "Order CBC and CRP in 9 days; order ECG tomorrow.";
        Assert.Equal("in 9 days", LocalClinicalAttributes.Timing(source, 6, 9, [(6,9),(14,17),(39,42)]));
        const string separate = "Order CBC today and CRP in 9 days.";
        Assert.Equal("today", LocalClinicalAttributes.Timing(separate, 6, 9, [(6,9),(20,23)]));
        Assert.Equal("in 9 days", LocalClinicalAttributes.Timing(separate, 20, 23, [(6,9),(20,23)]));
    }

    [Fact]
    public void SpecimenRequiresExplicitLocalEvidence()
    {
        Assert.Null(LocalClinicalAttributes.Specimen("Creatinine", "Creatinine 2.0 mg/dL"));
        Assert.Equal("Serum", LocalClinicalAttributes.Specimen("Serum creatinine", "Serum creatinine and urine culture"));
        Assert.Null(LocalClinicalAttributes.Specimen("CBC", "CBC and serum sodium"));
    }

    [Theory]
    [InlineData("Start aspirin 81 mg 1 tablet PO every 8 hours as needed for pain.", "every 8 hours as needed")]
    [InlineData("Start aspirin 81 mg 1 tablet PO once weekly on Mondays, never daily.", "WEEKLY")]
    public void PrnAndWeeklyInstructionsRemainDistinct(string source, string expected)
    {
        int start = source.IndexOf("aspirin", StringComparison.Ordinal);
        Assert.Equal(expected, new PosologyBinder().Bind(source, start, start+7, [(start,start+7)]).Frequency);
    }
}
