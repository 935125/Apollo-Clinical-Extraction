using System.Text.Json;
using Apollo.Backend.Clinical;

namespace Apollo.Backend.Tests;

public class FoundryValidatorSafetyTests
{
    private static readonly ClinicalResultValidator Validator = new();
    private static CandidateMedication Medication(string source) => FoundryValidatorOncologyTests.Med("Cytarabine", source,
        "100 mg/m2", "mg/m2", "IV", "continuous infusion", "Days 1-7", assertion: "current_medication");

    [Fact]
    public void Chemotherapy_DoseAdministrationAndDaysRemainAssociatedWithEachDrug()
    {
        const string source = "Chemotherapy Regimen:\nIV Cytarabine 100 mg/m2 continuous IV infusion (Days 1-7) + IV Daunorubicin 60 mg/m2 IV push (Days 1-3).";
        var result = Validator.Map(new([
            Medication(source),
            FoundryValidatorOncologyTests.Med("Daunorubicin", source, "60 mg/m2", "mg/m2", "IV push", duration: "Days 1-3", assertion: "current_medication")
        ], []), source, "oncology-positive");
        Assert.Equal("100 mg/m2", result.Medications[0].Dose); Assert.Null(result.Medications[0].Strength);
        Assert.Equal("60 mg/m2", result.Medications[1].Dose); Assert.Null(result.Medications[1].Strength);
        Assert.Equal("continuous infusion", result.Medications[0].Provenance!.ClinicalContext!.Administration);
        Assert.Equal("Days 1-7", result.Medications[0].Provenance!.ClinicalContext!.AdministrationSchedule);
        Assert.Equal("Days 1-3", result.Medications[1].Provenance!.ClinicalContext!.AdministrationSchedule);
    }

    [Fact]
    public void RelativeExpressionMisplacedInStrength_IsPreservedAsDoseWithoutCalculatingAbsoluteMilligrams()
    {
        const string source = "Cytarabine 100 mg/m2 IV.";
        var m = FoundryValidatorOncologyTests.Med("Cytarabine", source, route: "IV", strength: "100 mg/m2", assertion: "current_medication");
        var result = Assert.Single(Validator.Map(new([m], []), source, "relative-strength").Medications);
        Assert.Null(result.Strength); Assert.Equal("100 mg/m2", result.Dose); Assert.Equal("mg/m2", result.DoseUnit);
    }

    [Theory]
    [InlineData("dose", "60 mg/m2", "MEDICATION_ATTRIBUTE_UNSUPPORTED")]
    [InlineData("dose", "200 mg", "MEDICATION_ATTRIBUTE_UNSUPPORTED")]
    [InlineData("dose", "100 mg", "MEDICATION_RELATIVE_DOSE_TRUNCATED")]
    [InlineData("route", "IM", "MEDICATION_ROUTE_UNSUPPORTED")]
    [InlineData("frequency", "Q8H", "MEDICATION_FREQUENCY_UNSUPPORTED")]
    [InlineData("duration", "Days 1-3", "MEDICATION_DURATION_UNSUPPORTED")]
    [InlineData("instructions", "IV push", "MEDICATION_INSTRUCTIONS_UNSUPPORTED")]
    public void AdjacentOncologyAttributes_CannotBeBorrowedOrCalculated(string field, string value, string rule)
    {
        const string source = "Cytarabine 100 mg/m2 continuous IV infusion (Days 1-7) + Daunorubicin 60 mg/m2 IV push (Days 1-3).";
        var first = Medication(source);
        first = field switch
        {
            "dose" => first with { Dose = value, DoseUnit = value.EndsWith("/m2") ? "mg/m2" : "mg" },
            "route" => first with { Route = value }, "frequency" => first with { Frequency = value },
            "duration" => first with { Duration = value }, _ => first with { Instructions = value }
        };
        var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([first,
            FoundryValidatorOncologyTests.Med("Daunorubicin", source, "60 mg/m2", "mg/m2", "IV", assertion: "current_medication")], []), source, "oncology-negative"));
        Assert.Equal(rule, failure.Diagnostic.RuleId); Assert.Equal(0, failure.Diagnostic.EntityIndex);
    }

    [Fact]
    public void PrefixRoutes_BelongToTheirOwnMedication_EvenWhenSecondDrugIsOmittedByTheProvider()
    {
        const string source = "IV Meropenem 1 g q8h and IM Ceftriaxone 2 g daily.";
        var valid = FoundryValidatorOncologyTests.Med("Meropenem", source, "1 g", "g", "IV", "Q8H", assertion: "current_medication");
        Assert.Equal("IV", Assert.Single(Validator.Map(new([valid], []), source, "prefix-route").Medications).Route);
        var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([valid with { Route = "IM" }], []), source, "prefix-route-negative"));
        Assert.Equal("MEDICATION_ROUTE_UNSUPPORTED", failure.Diagnostic.RuleId);
    }

    [Theory]
    [InlineData("CBC performed yesterday.", "CBC", "historical", "unknown", "completed")]
    [InlineData("CBC was 6.5 g/dL.", "CBC", "historical", "final", "result_documented")]
    [InlineData("Platelets 92,000/uL.", "Platelets", "historical", "final", "result_documented")]
    [InlineData("Hemoglobin 9.6 g/dL.", "Hemoglobin", "historical", "final", "result_documented")]
    [InlineData("Blood cultures grew Escherichia coli.", "Blood cultures", "historical", "final", "result_documented")]
    [InlineData("Blood cultures showed no growth after 48 hours.", "Blood cultures", "historical", "final", "result_documented")]
    [InlineData("Cytogenetics: 46,XX[20] (normal karyotype).", "Cytogenetics", "historical", "final", "result_documented")]
    [InlineData("Prior Investigations:\nMRI last year.", "MRI", "historical", "unknown", "historical")]
    [InlineData("Report on 01/12/2026 at 09:00 AM for CBC.", "CBC", "new_order", "preliminary", "scheduled")]
    [InlineData("Follow-Up Plan:\nCBC next week.", "CBC", "new_order", "preliminary", "planned")]
    [InlineData("Repeat CBC if fever persists.", "CBC", "conditional", "preliminary", "conditional")]
    [InlineData("No repeat CBC required.", "CBC", "negated", "unknown", "negated")]
    [InlineData("CBC not performed.", "CBC", "negated", "unknown", "negated")]
    public void InvestigationClinicalEvents_RemainDistinct(string source, string name, string assertion, string status, string detail)
    {
        var result = Assert.Single(Validator.Map(new([], [new(name, null, null, assertion, status, new(source, 1))]), source, "temporal-positive").Investigations);
        Assert.Equal(detail, result.Provenance!.ClinicalContext!.Assertion);
        Assert.Equal(assertion, result.AssertionStatus);
    }

    [Theory]
    [InlineData("Report on 01/12/2026 at 09:00 AM for CBC.", "historical", "unknown", "INVESTIGATION_HISTORICAL_CONTEXT_UNSUPPORTED")]
    [InlineData("CBC performed yesterday.", "new_order", "preliminary", "INVESTIGATION_TEMPORAL_ASSERTION_MISMATCH")]
    [InlineData("CBC performed yesterday.", "historical", "final", "INVESTIGATION_FINAL_RESULT_UNSUPPORTED")]
    [InlineData("Repeat CBC if fever persists.", "new_order", "preliminary", "INVESTIGATION_CONDITIONAL_ASSERTION_MISMATCH")]
    [InlineData("No repeat CBC required.", "new_order", "preliminary", "INVESTIGATION_NEGATION_ASSERTION_MISMATCH")]
    [InlineData("CBC tomorrow.", "conditional", "preliminary", "INVESTIGATION_CONDITION_UNSUPPORTED")]
    public void UnsupportedInvestigationEvents_StillFail(string source, string assertion, string status, string rule)
    {
        var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([], [new("CBC", null, null, assertion, status, new(source, 1))]), source, "temporal-negative"));
        Assert.Equal(rule, failure.Diagnostic.RuleId); Assert.Equal("investigation", failure.Diagnostic.EntityType);
    }

    [Theory]
    [InlineData("Allergy History:\nAspirin (anaphylaxis).", "Aspirin")]
    [InlineData("No Ibuprofen prescribed.", "Ibuprofen")]
    [InlineData("Discontinue Warfarin immediately.", "Warfarin")]
    [InlineData("Previously received Meropenem for 10 days.", "Meropenem")]
    public void InactiveMedicationEvidence_CannotBecomeAnActivePrescription(string source, string name)
    {
        var candidate = FoundryValidatorOncologyTests.Med(name, source, assertion: "current_medication");
        Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([candidate], []), source, "inactive-negative"));
    }

    [Fact]
    public void TimingAndSpecimenCannotMoveBetweenTests_InOneSentenceOrAcrossSections()
    {
        foreach (string source in new[] { "Order CBC today and MRI tomorrow.", "Order CBC today.\nFollow-Up Plan:\nMRI tomorrow." })
        {
            var candidate = new ClinicalCandidate([], [new("CBC", "tomorrow", null, "new_order", "preliminary", new(source, 1)),
                new("MRI", "tomorrow", null, "new_order", "preliminary", new(source, 1))]);
            var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(candidate, source, "timing-negative"));
            Assert.Equal("INVESTIGATION_TIMING_UNSUPPORTED", failure.Diagnostic.RuleId);
        }
        const string urine = "Order CBC and urine culture today.";
        Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([], [new("CBC", null, "urine", "new_order", "preliminary", new(urine, 1)),
            new("urine culture", null, "urine", "new_order", "preliminary", new(urine, 1))]), urine, "specimen-negative"));
    }

    [Fact]
    public void ExactQuote_Occurrence_EntityName_AndNoteVersionAreMandatory()
    {
        const string source = "Cytarabine 100 mg/m2 IV.";
        var med = FoundryValidatorOncologyTests.Med("Cytarabine", source, "100 mg/m2", "mg/m2", "IV", assertion: "current_medication");
        foreach (var pair in new[] {
            (med with { Evidence = new("Cytarabine was given 100 mg/m2 IV.", 1) }, "EVIDENCE_QUOTE_NOT_IN_SOURCE"),
            (med with { Evidence = new(source, 2) }, "EVIDENCE_OCCURRENCE_NOT_FOUND"),
            (med with { Name = "Daunorubicin" }, "ENTITY_NAME_NOT_IN_EVIDENCE") })
        {
            var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([pair.Item1], []), source, "verbatim-negative"));
            Assert.Equal(pair.Item2, failure.Diagnostic.RuleId);
            Assert.Equal("source_evidence", failure.Diagnostic.Category);
        }
        Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([med], []), source.Replace("100", "200"), "different-version"));
    }

    [Fact]
    public void DiagnosticsUseOriginalEntityIndex_AndContainNoSourceOrModelData()
    {
        const string source = "Continue Metformin 500 mg PO daily. SecretSyntheticMarker.";
        var good = FoundryValidatorOncologyTests.Med("Metformin", source, route: "PO", frequency: "daily", assertion: "current_medication", action: "continue", strength: "500 mg");
        var invalid = good with { Name = "SecretSyntheticMarker", Evidence = new("InventedSensitiveQuote", 1) };
        var failure = Assert.Throws<InvalidClinicalEvidenceException>(() => Validator.Map(new([good, invalid], []), source, "sensitive-correlation-marker"));
        Assert.Equal(1, failure.Diagnostic.EntityIndex); Assert.Equal("medication", failure.Diagnostic.EntityType);
        string diagnostic = JsonSerializer.Serialize(failure.Diagnostic) + failure.Message;
        Assert.DoesNotContain("Metformin", diagnostic); Assert.DoesNotContain("SecretSyntheticMarker", diagnostic);
        Assert.DoesNotContain("InventedSensitiveQuote", diagnostic); Assert.DoesNotContain("sensitive-correlation-marker", diagnostic);
    }

    [Fact]
    public void UnresolvedTerminologyIsDistinctFromInvalidEvidence_AndSpecificIngredientsKeepTheirIdentity()
    {
        const string source = "Start Zorvaximab 120 mg IV weekly.\nPrednisolone acetate 1% ophthalmic suspension.";
        var result = Validator.Map(new([
            FoundryValidatorOncologyTests.Med("Zorvaximab", source, "120 mg", "mg", "IV", "weekly", assertion: "new_order", action: "start"),
            FoundryValidatorOncologyTests.Med("Prednisolone acetate", source, route: "ophthalmic", formulation: "ophthalmic suspension", strength: "1%", assertion: "current_medication")
        ], []), source, "terminology");
        Assert.False(result.ValidationSummary.HasErrors);
        Assert.All(result.Medications, m => Assert.Null(m.Normalization.MatchedCode));
        Assert.All(result.Medications, m => Assert.Contains(m.QualityFlags, flag => flag.StartsWith("TERMINOLOGY_")));
    }
}
