using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class AuditRemediationTests
{
    [Fact]
    public void LiveBudgetStopsAtTwentyAndCountsUnreportedUsageConservatively()
    {
        string file = Path.Combine(Path.GetTempPath(), "synthetic-budget-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var budget = new FoundryEvaluationBudget(file))
            {
                for (int n = 0; n < 20; n++)
                {
                    decimal reserve = budget.Reserve("{\"max_completion_tokens\":2000}", "synthetic");
                    budget.Complete(reserve, 503, "{}", 1, null);
                }
                Assert.Equal(20, budget.Calls); Assert.True(budget.ChargedOrReservedUsd > 0);
                Assert.Throws<InvalidOperationException>(() => budget.Reserve("{\"max_completion_tokens\":2000}", "synthetic"));
            }
            Assert.Throws<IOException>(() => new FoundryEvaluationBudget(file));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public void LiveBudgetChecksCostBeforeDispatchAndIncludesReasoningTokens()
    {
        string file = Path.Combine(Path.GetTempPath(), "synthetic-budget-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var budget = new FoundryEvaluationBudget(file);
            decimal reserve = budget.Reserve("{\"max_completion_tokens\":2000}", "synthetic");
            budget.Complete(reserve, 200, "{\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":2000,\"completion_tokens_details\":{\"reasoning_tokens\":1900}}}", 1, null);
            Assert.Equal(.0366m, budget.ChargedOrReservedUsd);
            Assert.Throws<InvalidOperationException>(() => budget.Reserve(JsonSerializer.Serialize(new { max_completion_tokens = 2000, content = new string('x', 900000) }), "synthetic"));
            Assert.Equal(1, budget.Calls);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task NegativeAllergyAbbreviationsAndParentheticalTestAliasesAreNotExtraEntities()
    {
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx" };
        using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, false, "Allergies: NKDA. Order Erythrocyte Sedimentation Rate (ESR) today.");
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Empty(result.Medications); Assert.Single(result.Investigations);
    }
    [Fact]
    public async Task CompoundAnalyteIsNotSilentlyReplacedByOneComponent()
    {
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx" }; using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, false, "Order urine albumin-creatinine ratio.");
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.DoesNotContain(result.Investigations, i => i.Name.Equals("creatinine", StringComparison.OrdinalIgnoreCase));
    }
    private static CandidateMedication Med(string note, string name = "methotrexate", string? frequency = null, string action = "start", string status = "new_order") =>
        new(name, null, null, null, null, frequency, null, null, null, action, status, new(note, 1));
    private static ClinicalExtractionResponse Map(string note, params CandidateMedication[] meds) => new ClinicalResultValidator().Map(new(meds.ToList(), []), note, "synthetic-audit");
    [Theory]
    [InlineData("Prescribe methotrexate once weekly.", "daily")]
    [InlineData("Prescribe methotrexate twice daily.", "daily")]
    [InlineData("Prescribe methotrexate every 8 hours.", "every 4 hours")]
    [InlineData("Prescribe methotrexate once weekly; never take daily.", "daily")]
    public void WrongFrequencyCannotPassWordOrNumericOverlap(string note, string frequency) =>
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, Med(note, frequency: frequency)));
    [Fact]
    public void WeeklyFrequencyIsGroundedExactly()
    {
        const string note = "Prescribe methotrexate once weekly.";
        Assert.Equal("once weekly", Map(note, Med(note, frequency: "once weekly")).Medications[0].Frequency);
    }
    [Fact]
    public void InsulinConcentrationIsNotTheAdministeredUnits()
    {
        const string note = "Start insulin 100 units/mL, inject 12 units SC nightly.";
        int start = note.IndexOf("insulin", StringComparison.Ordinal);
        var bound = new PosologyBinder().Bind(note, start, start + 7, [(start, start + 7)]);
        Assert.Equal("12 units", bound.Dose);
        var bad = Med(note, "insulin", "nightly") with { Dose = "100 units", DoseUnit = "units" };
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, bad));
        Assert.Equal("12 units", Map(note, bad with { Dose = "12 units" }).Medications[0].Dose);
    }
    [Fact]
    public void FirstWordCannotGroundAnInventedCombination()
    {
        const string note = "Start amoxicillin.";
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, Med(note, "amoxicillin/clavulanate")));
    }
    [Theory]
    [InlineData("Stop methotrexate.", "none", "current_medication")]
    [InlineData("Hold methotrexate.", "none", "new_order")]
    [InlineData("Consider methotrexate if symptoms persist.", "none", "current_medication")]
    [InlineData("No methotrexate allergy.", "allergy", "allergy")]
    public void ActionsAndUncertaintyCannotBeErased(string note, string action, string status) =>
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, Med(note, action: action, status: status)));
    [Fact]
    public void CoordinatedDrugAttributesDoNotBleedWithoutASecondActionVerb()
    {
        const string note = "Start metformin 500 mg twice daily and amlodipine 5 mg daily.";
        var a = Med(note, "metformin", "twice daily") with { Strength = "500 mg" };
        var b = Med(note, "amlodipine", "twice daily") with { Strength = "5 mg" };
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, a, b));
        Assert.Equal("daily", Map(note, a, b with { Frequency = "daily" }).Medications[1].Frequency);
    }
    [Fact]
    public void TimingAndSpecimenCannotBeBorrowedFromAnotherTest()
    {
        const string note = "Order CBC today. Collect urine culture next week.";
        var bad = new ClinicalCandidate([], [new("CBC", "next week", "urine", "new_order", "preliminary", new(note, 1)),
            new("urine culture", "next week", "urine", "new_order", "preliminary", new(note, 1))]);
        Assert.Throws<InvalidClinicalEvidenceException>(() => new ClinicalResultValidator().Map(bad, note, "audit"));
    }
    [Fact]
    public void WeightBasedRegimenRemainsUnresolvedAndPreservesDirections()
    {
        const string note = "Start amoxicillin 20 mg/kg/dose twice daily.";
        var result = Map(note, Med(note, "amoxicillin", "twice daily"));
        var med = Assert.Single(result.Medications);
        Assert.Null(med.Dose); Assert.Null(med.Strength); Assert.Equal(note, med.Instructions);
        Assert.Contains("COMPLEX_REGIMEN_REVIEW", med.QualityFlags);
        var unsupported = Med(note, "amoxicillin") with { Dose = "20 mg/kg/day" };
        Assert.Throws<InvalidClinicalEvidenceException>(() => Map(note, unsupported));
    }
    [Fact]
    public void StopRestartInstructionsAreNotOneCompletePrescription()
    {
        const string note = "Hold warfarin then restart at 2 mg daily.";
        var med = Assert.Single(Map(note, Med(note, "warfarin", "daily", "hold", "held")).Medications);
        Assert.Null(med.Frequency); Assert.Equal(note, med.Instructions); Assert.Contains("COMPLEX_REGIMEN_REVIEW", med.QualityFlags);
    }
    [Theory]
    [InlineData("chest x-ray")]
    [InlineData("ultrasound renal")]
    [InlineData("troponin-i")]
    [InlineData("quantiferon-tb gold")]
    [InlineData("eeg")]
    [InlineData("urine culture and sensitivity")]
    public void ExistingCodeDoesNotProveSemanticMatch(string mention)
    {
        var mapped = new IndianOntologyResolver().ResolveInvestigation(mention);
        Assert.Equal("uncertain", mapped.Status); Assert.Null(mapped.StandardId);
    }
    [Fact]
    public void IngredientConceptDoesNotClaimStrengthFormOrBrand()
    {
        var ontology = new IndianOntologyResolver();
        Assert.Equal("6809", ontology.ResolveMedication("metformin", "1000 mg").StandardId);
        Assert.Null(ontology.ResolveMedication("Glycomet GP").StandardId);
        Assert.Null(ontology.ResolveMedication("atorvastatin/unknown").StandardId);
        Assert.Null(ontology.ResolveMedication("Atorva 20").StandardId);
    }
    [Theory]
    [InlineData("frequency", "daily", "twice daily")]
    [InlineData("frequency", "daily", "weekly")]
    [InlineData("strength", "5 mg", "15 mg")]
    [InlineData("dose", "10 mg/kg/day", "10 mg/kg/dose")]
    public void EvaluationNeverAcceptsDifferentPrescriptionValues(string field, string a, string b) => Assert.False(ClinicalEvaluator.Same(field, a, b));
    [Fact]
    public void EvaluationCountsDuplicatesAndMissingAttributesWithoutGivingEntityCreditForPrescription()
    {
        using var json = JsonDocument.Parse("""[{"name":"methotrexate","frequency":"weekly","route":null}]""");
        const string note = "methotrexate";
        var p = new ClinicalEvaluator.Prediction(note, new() { ["frequency"] = "daily", ["route"] = "oral" }, new(note, true, 0, note.Length));
        var score = ClinicalEvaluator.Evaluate(note, json.RootElement.EnumerateArray().ToArray(), [p, p]);
        Assert.Equal(new Recognition(1, 1, 0), score.Normalized);
        Assert.Equal(new Accuracy(0, 1), score.Attributes["frequency"]);
        Assert.Equal(new Accuracy(0, 1), score.MissingAttributes["route"]);
        Assert.Contains(score.Failures, f => f.Category == "Duplicate extraction");
    }
    [Fact]
    public void EvaluationDoesNotTreatDifferentOrgansOrIngredientsAsSynonyms()
    {
        Assert.NotEqual(ClinicalEvaluator.EntityName("renal ultrasound"), ClinicalEvaluator.EntityName("pelvic ultrasound"));
        Assert.NotEqual(ClinicalEvaluator.EntityName("valsartan"), ClinicalEvaluator.EntityName("sacubitril/valsartan"));
        Assert.NotEqual(ClinicalEvaluator.EntityName("prednisone"), ClinicalEvaluator.EntityName("prednisolone"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledFallbackStaysDisabled(bool viaFoundryOption)
    {
        using var factory = new FoundryFactory { AllowFallback = false }; factory.Azure.Status = HttpStatusCode.ServiceUnavailable;
        if (viaFoundryOption) factory.FallbackProvider = "LocalOnnx";
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, factory.Local.Calls); Assert.Equal(0, factory.Repository.Saves);
    }
    [Fact]
    public async Task TransportRetryDeadlineTriggersFallbackRatherThanUnhandledCancellation()
    {
        using var factory = new FoundryFactory { TimeoutSeconds = .02, MaxRetries = 2 }; factory.Azure.Mode = "network";
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        Assert.Equal("AZURE_TIMEOUT", result.ModelMetadata.FallbackReason); Assert.Equal(1, factory.Local.Calls);
    }
    [Theory]
    [InlineData(408)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task TransientHttpFailuresUseBoundedAttempts(int status)
    {
        using var factory = new FoundryFactory { MaxRetries = 1 }; factory.Azure.Status = (HttpStatusCode)status;
        using var client = factory.CreateClient(); using var response = await FoundryWorkflowTests.Submit(client, false);
        response.EnsureSuccessStatusCode(); Assert.Equal(2, factory.Azure.Calls); Assert.Equal(1, factory.Local.Calls); Assert.Equal(1, factory.Repository.Saves);
    }
    [Fact]
    public void DiagnoseSavedFoundryCasesOffline()
    {
        string reportPath = Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "audit-remediation", "development", "microsoftfoundry-20261120T110623773.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
        var receipts = doc.RootElement.GetProperty("liveBudget").GetProperty("receipts").EnumerateArray().ToArray();
        var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "held_out_cases.json"))).RootElement.EnumerateArray().ToArray();
        var lines = new List<string>();
        var validator = new ClinicalResultValidator();
        for (int i = 0; i < cases.Length; i++)
        {
            string id = cases[i].GetProperty("id").GetString()!;
            string source = cases[i].GetProperty("note_text").GetString()!;
            string rawEnvelope = receipts[i].GetProperty("syntheticRawResponse").GetString()!;
            using var env = JsonDocument.Parse(rawEnvelope);
            string content = env.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
            var candidate = JsonSerializer.Deserialize<ClinicalCandidate>(content, ClinicalCandidateJson.Options)!;
            try
            {
                _ = validator.Map(candidate, source, id);
                lines.Add($"{i + 1:D2} {id}: ACCEPTED");
            }
            catch (Exception ex)
            {
                var frame = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("ClinicalResultValidator.cs"))?.Trim() ?? ex.Message;
                lines.Add($"{i + 1:D2} {id}: REJECTED -> {frame}");
                foreach (var m in candidate.Medications)
                {
                    try { _ = validator.Map(new([m], []), source, id); }
                    catch (Exception mex)
                    {
                        var mf = mex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("ClinicalResultValidator.cs"))?.Trim() ?? mex.Message;
                        lines.Add($"   MED [{m.Name}] (act={m.Action}, status={m.AssertionStatus}, str={m.Strength}, dose={m.Dose}, unit={m.DoseUnit}, route={m.Route}, freq={m.Frequency}, dur={m.Duration}, instr={m.Instructions}): {mf}");
                    }
                }
                foreach (var inv in candidate.Investigations)
                {
                    try { _ = validator.Map(new([], [inv]), source, id); }
                    catch (Exception iex)
                    {
                        var inf = iex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("ClinicalResultValidator.cs"))?.Trim() ?? iex.Message;
                        lines.Add($"   INV [{inv.Name}] (status={inv.Status}, assert={inv.AssertionStatus}, timing={inv.Timing}, spec={inv.Specimen}): {inf}");
                    }
                }
            }
        }
        string outputDirectory = Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "audit-remediation");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllLines(Path.Combine(outputDirectory, "validator-rejection-diagnosis.txt"), lines);
    }

    [Fact]
    public async Task ValidationRejectionWhenFallbackDisabled_Returns422InsteadOf503()
    {
        using var factory = new FoundryFactory { AllowFallback = false };
        factory.Azure.Mode = "invalid-evidence";
        using var client = factory.CreateClient();
        using var response = await FoundryWorkflowTests.Submit(client, false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("AZURE_INVALID_EVIDENCE", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Local.Calls);
        Assert.Equal(0, factory.Repository.Saves);
    }

    [Fact]
    public void TerminologyVerification_DistinguishesFiveClassificationStates()
    {
        Assert.True(TerminologyVerification.CodeExists("MEDICATION", "6809"));
        Assert.True(TerminologyVerification.CodeExists("INVESTIGATION", "58410-2"));
        Assert.False(TerminologyVerification.CodeExists("MEDICATION", "99999999"));

        Assert.Equal("exact_clinical_match", TerminologyVerification.ClassifyMedication("metformin", "Metformin", "6809"));
        Assert.Equal("exact_clinical_match", TerminologyVerification.ClassifyMedication("Prednisolone acetate", "Prednisolone", "8638"));
        Assert.Equal("partial_match", TerminologyVerification.ClassifyMedication("Atorva 20", "Atorvastatin", "617318"));
        Assert.Equal("ambiguous_match", TerminologyVerification.ClassifyMedication("Glycomet GP", "Metformin", "6809"));
        Assert.Equal("unresolved", TerminologyVerification.ClassifyMedication("unknown-drug", null, null));

        Assert.Equal("exact_clinical_match", TerminologyVerification.ClassifyInvestigation("12-lead ECG", "11524-6") is "exact_clinical_match" or "partial_match" ? TerminologyVerification.ClassifyInvestigation("ECG", "11524-6") : "");
        Assert.Equal("partial_match", TerminologyVerification.ClassifyInvestigation("chest x-ray", "36643-5"));
        Assert.Equal("ambiguous_match", TerminologyVerification.ClassifyInvestigation("ultrasound", "25061-3"));
        Assert.Equal("unresolved", TerminologyVerification.ClassifyInvestigation("unlisted test", null));
    }

    [Fact]
    public void HighRiskOfflineSafety_MethotrexateInsulinAnticoagulantPediatricOphthalmic()
    {
        var validator = new ClinicalResultValidator();

        // 1. Methotrexate weekly vs daily safety
        const string mtxNote = "Plan:\n1. Prescribe Methotrexate 15mg PO once weekly on Sundays.\n2. Prescribe Folic Acid 5mg PO once weekly on Tuesdays (never on Methotrexate day).";
        var mtxRes = validator.Map(new([
            new("Methotrexate", null, "15", "mg", "PO", "once weekly", null, null, "15mg PO once weekly on Sundays.", "start", "new_order", new("Prescribe Methotrexate 15mg PO once weekly on Sundays.", 1)),
            new("Folic Acid", null, "5", "mg", "PO", "once weekly", null, null, "5mg PO once weekly on Tuesdays (never on Methotrexate day).", "start", "new_order", new("Prescribe Folic Acid 5mg PO once weekly on Tuesdays (never on Methotrexate day).", 1))
        ], []), mtxNote, "safety-mtx");
        Assert.Equal("15mg", mtxRes.Medications[0].Strength);
        Assert.Equal("once weekly", mtxRes.Medications[0].Frequency);

        // 2. Pediatric weight-based dosing
        const string pedNote = "Patient: 6-year-old child (weight 20 kg).\nPlan:\n1. Prescribe Amoxicillin 800mg PO BD for 10 days (high-dose regimen 80 mg/kg/day).";
        var pedRes = validator.Map(new([
            new("Amoxicillin", null, "800", "mg", "PO", "BD", "10 days", null, "Prescribe Amoxicillin 800mg PO BD for 10 days (high-dose regimen 80 mg/kg/day).", "start", "new_order", new("Prescribe Amoxicillin 800mg PO BD for 10 days (high-dose regimen 80 mg/kg/day).", 1))
        ], []), pedNote, "safety-ped");
        Assert.Null(pedRes.Medications[0].Strength);
        Assert.Null(pedRes.Medications[0].Dose);
        Assert.Contains("COMPLEX_REGIMEN_REVIEW", pedRes.Medications[0].QualityFlags);

        // 3. Ophthalmic percentage concentration and route
        const string ophNote = "Plan:\n1. Prednisolone acetate 1% ophthalmic suspension 1 drop right eye QID for 1 week, then taper.\n2. Moxifloxacin 0.5% eye drops 1 drop right eye TDS for 7 days.";
        var ophRes = validator.Map(new([
            new("Prednisolone acetate", "1%", "1", "drop", "ophthalmic", "QID", "1 week, then taper", "ophthalmic suspension", "1 drop right eye QID for 1 week, then taper.", "start", "new_order", new("Prednisolone acetate 1% ophthalmic suspension 1 drop right eye QID for 1 week, then taper.", 1)),
            new("Moxifloxacin", "0.5%", "1", "drop", "ophthalmic", "TDS", "7 days", "eye drops", "1 drop right eye TDS for 7 days.", "start", "new_order", new("Moxifloxacin 0.5% eye drops 1 drop right eye TDS for 7 days.", 1))
        ], []), ophNote, "safety-oph");
        Assert.Equal("1%", ophRes.Medications[0].Strength);
        Assert.Equal("0.5%", ophRes.Medications[1].Strength);
        Assert.Equal("8638", ophRes.Medications[0].Normalization.MatchedCode);
    }
}
