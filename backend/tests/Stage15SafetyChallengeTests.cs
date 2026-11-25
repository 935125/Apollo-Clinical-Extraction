using Apollo.Backend.Clinical;

namespace Apollo.Backend.Tests;

/// <summary>Separate challenge cases; engineering regression tests, not independent clinical validation.</summary>
public class Stage15SafetyChallengeTests(Stage15ModelFixture fixture) : IClassFixture<Stage15ModelFixture>
{
    [Theory]
    [InlineData("Do not start Aspirin 75 mg PO daily.","Aspirin","negated")]
    [InlineData("No Ibuprofen 400 mg PO daily.","Ibuprofen","negated")]
    [InlineData("Acyclovir was not started.","Acyclovir","negated")]
    [InlineData("Amoxicillin was discussed but not prescribed.","Amoxicillin","negated")]
    [InlineData("No plans to start Aspirin.","Aspirin","negated")]
    [InlineData("Aspirin is not to be administered.","Aspirin","negated")]
    [InlineData("The patient might start Acyclovir 400 mg PO BD.","Acyclovir","uncertain")]
    [InlineData("No known allergy to Aspirin.","Aspirin","negated")]
    [InlineData("Continue Aspirin 75 mg PO daily; no vomiting.","Aspirin","current_medication")]
    [InlineData("If fever has not improved, start Acyclovir 400 mg PO BD.","Acyclovir","conditional")]
    [InlineData("Start Acyclovir 400 mg PO BD when vesicles recur.","Acyclovir","conditional")]
    [InlineData("Current Medications: Aspirin 75 mg PO PRN when required.","Aspirin","current_medication")]
    [InlineData("Completed Meropenem 1 g IV q8h course.","Meropenem","historical")]
    [InlineData("Continue Prednisolone 40 mg daily for 5 days then stop.","Prednisolone","current_medication")]
    [InlineData("Allergy History: Aspirin.\nDischarge Medications: Acyclovir 400 mg PO BD.","Aspirin","allergy")]
    [InlineData("Allergy History: Aspirin.\nDischarge Medications: Acyclovir 400 mg PO BD.","Acyclovir","current_medication")]
    public void MedicationContextDoesNotCreateUnsafeActiveOrders(string note,string name,string status)
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(note),"safety-challenge");
        Assert.Equal(status,Assert.Single(result.Medications,m=>m.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).AssertionStatus);
        LocalEvidenceValidator.Validate(result,note);
    }

    [Theory]
    [InlineData("No CBC required today.","negated")]
    [InlineData("Repeat CBC when fever recurs.","conditional")]
    [InlineData("CBC was not performed.","negated")]
    [InlineData("Blood cultures were negative.","historical")]
    [InlineData("CBC planned tomorrow; no transfusion required.","new_order")]
    [InlineData("Order CBC today and renal function tests next week.","new_order")]
    public void InvestigationIntentDoesNotUseUnrelatedNegation(string note,string status)
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(note),"safety-test");
        Assert.Equal(status,result.Investigations[0].AssertionStatus);
        LocalEvidenceValidator.Validate(result,note);
    }

    [Fact]
    public void BroadInvestigationsAndAmbiguousBrandsHaveNoInventedCode()
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new("Continue Glycomet GP 1 tablet daily.\nOrder CBC, renal function panel, CT scan and blood culture & sensitivity."),"specificity");
        Assert.Null(Assert.Single(result.Medications).Normalization.MatchedCode);
        Assert.Null(Assert.Single(result.Medications).Normalization.DisplayName);
        Assert.All(result.Investigations,i=>Assert.Null(i.MatchedCode));
    }

    [Fact]
    public void DiagnosticThresholdsAndBlastResultsCannotBecomeMedications()
    {
        const string note="Chemotherapy Administered: Cytarabine 100 mg/m2 IV.\n- Complications: Previously treated with benzydamine mouthwash.\n- Count Recovery (Day 22): Bone marrow biopsy confirmed remission with 2% blasts.\nRepeat treatment if serum potassium rises above 5 mmol/L.";
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(note),"thresholds");
        Assert.DoesNotContain(result.Medications,m=>m.Name.Equals("blasts",StringComparison.OrdinalIgnoreCase) || m.Name.Contains("potassium",StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Medications,m=>Assert.Equal("historical",m.AssertionStatus));
    }

    [Theory]
    [InlineData("Start Sodium Bicarbonate 650 mg, 1 tablet PO daily.","Sodium Bicarbonate")]
    [InlineData("Give potassium 10 mEq IV.","potassium")]
    public void AdministeredSaltsAreDistinguishedFromDiagnosticAnalytes(string note,string name)
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(note),"administered-salt");
        Assert.Equal(name,Assert.Single(result.Medications).Name);
        Assert.Empty(result.Investigations);
        Assert.Equal("new_order",result.Medications[0].AssertionStatus);
    }

    [Fact]
    public void ChemotherapyDaysStayWithEachDrugWithoutInventingAbsoluteDoseOrDuration()
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new("Chemotherapy Regimen:\nCytarabine 100 mg/m2 continuous IV infusion (Days 1-7) + Daunorubicin 60 mg/m2 IV push (Days 1-3)."),"chemo-days");
        Assert.Equal("Days 1-7",result.Medications[0].Provenance!.ClinicalContext!.AdministrationSchedule);
        Assert.Equal("Days 1-3",result.Medications[1].Provenance!.ClinicalContext!.AdministrationSchedule);
        Assert.All(result.Medications,m=>Assert.Null(m.Duration));
        Assert.Equal("100 mg/m2",result.Medications[0].Dose); Assert.Equal("60 mg/m2",result.Medications[1].Dose);
    }

    [Fact]
    public void LaboratoryConcentrationsCannotBecomeAdministeredMassDoses()
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new("Current Medications:\nGlucose detected at 90 mg/dL.\nSerum Acyclovir level measured at 5 ng/mL."),"lab-values");
        Assert.DoesNotContain(result.Medications,m=>m.Name.Equals("Glucose",StringComparison.OrdinalIgnoreCase));
        var mention=Assert.Single(result.Medications,m=>m.Name=="Acyclovir");
        Assert.Equal("uncertain",mention.AssertionStatus); Assert.Null(mention.Dose); Assert.Null(mention.Strength);
    }

    [Fact]
    public void DocumentedEsterCannotBeNormalizedBySilentlyRemovingItsSuffix()
    {
        var medication=VersionedClinicalTerminology.Resolve("MEDICATION","prednisolone acetate","1%");
        Assert.Null(medication.MatchedCode); Assert.Equal("uncertain",medication.Status);
        Assert.Equal("broader",medication.SemanticStatus); Assert.Equal("prednisolone acetate",medication.DisplayName);
        Assert.Equal("8638",VersionedClinicalTerminology.Resolve("MEDICATION","prednisolone").MatchedCode);
    }
}
