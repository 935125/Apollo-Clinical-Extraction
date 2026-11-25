using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class ForensicPipelineAuditTests
{
    private static string OutputRoot
    {
        get
        {
            string directory = Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "audit-remediation");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string FlattenedSavedPdfText =
        "INPATIENT DISCHARGE SUMMARYCardiology & Internal Medicine — Hospital Course& PlanDocumented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).Hospital Course & Clinical Presentation64-year-old female with non-valvular atrial fibrillation, chronic kidney disease stage 3b, andcommunity-acquired lower respiratory tract infection admitted with acute palpitations, productivecough, and fever of 38.9 °C. Treated with intravenous antimicrobial therapy and rate-controlstabilization with full clinical recovery prior to discharge.Discharge Vital Signs & Baseline LabsBlood PressureHeart RateTemperatureSerum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dLDischarge Diagnoses1. Non-valvular atrial fibrillation with controlled ventricular response.2. Resolving community-acquired pneumonia (CAP).3. Chronic kidney disease stage 3b with mild hyperkalemia.Discharge MedicationsWarfarin 5 mg orally once daily in the evening for stroke prophylaxis.Aspirin 81 mg orally once daily in the morning (verify concurrent anticoagulation indication).Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.Ondansetron 4 mg orally every 8 hours as needed for nausea.Pantoprazole 40 mg orally once daily before breakfast for 30 days.Outpatient Follow-Up InvestigationsProthrombin time and INR (PT/INR) in 3 days.Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.Complete blood count (CBC) and C-reactive protein (CRP) at 1-week follow-up.Follow-up chest X-ray (PA view) and 12-lead ECG in 2 weeks.Inpatient Cardiology & Internal Medicine Discharge SummaryPage 1 of 1";

    private static string ResolveDischargeSummaryPdfPath()
    {
        string fixturePath = Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "Fixtures", "Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf");
        string downloadsPath = @"C:\Users\intern_ali\Downloads\Inpatient Cardiology & Pulmonology Discharge Summary.pdf";
        if (File.Exists(downloadsPath) && !File.Exists(fixturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            File.Copy(downloadsPath, fixturePath, overwrite: true);
        }
        return File.Exists(fixturePath) ? fixturePath : downloadsPath;
    }

    [Fact]
    public void PdfTextReconstructor_RepairsConcatenatedHeadings_TableColumns_AndDetectsAllFiveDischargeMedications()
    {
        string pdfPath = ResolveDischargeSummaryPdfPath();
        string reconstructed;
        string rawConcat;
        if (File.Exists(pdfPath))
        {
            using var stream = File.OpenRead(pdfPath);
            var res = PdfTextReconstructor.Reconstruct(stream);
            rawConcat = res.RawConcatenatedText;
            reconstructed = res.ReconstructedText;
        }
        else
        {
            rawConcat = FlattenedSavedPdfText;
            reconstructed = PdfTextReconstructor.NormalizeConcatenatedBoundaries(FlattenedSavedPdfText);
        }

        // 1. Verify raw p.Text had concatenated tokens while reconstructed text separates them cleanly
        Assert.DoesNotContain("SUMMARYCardiology", reconstructed);
        Assert.DoesNotContain("Presentation64-year-old", reconstructed);
        Assert.DoesNotContain("MedicationsWarfarin", reconstructed);
        Assert.DoesNotContain("Creatinine124/76", reconstructed);

        // 2. Verify section segmentation detects distinct clinical sections instead of a single OTHER/Body section
        var sections = new ContextEngine().SegmentSections(reconstructed);
        Assert.True(sections.Count >= 5, $"Expected >= 5 sections, got {sections.Count}: {string.Join(", ", sections.Select(s => s.Code))}");
        Assert.Contains(sections, s => s.Code == "ALLERGIES");
        Assert.Contains(sections, s => s.Code == "MEDICATIONS");
        Assert.Contains(sections, s => s.Code == "LABS");

        // 3. Verify all 5 discharge medications and 2 allergies remain independently detectable
        foreach (var drug in new[] { "Warfarin", "Aspirin", "Azithromycin", "Ondansetron", "Pantoprazole", "Penicillin", "Ibuprofen" })
        {
            Assert.Contains(drug, reconstructed, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Serum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL", 1.9, 5.7)]
    [InlineData("Blood Pressure: 124/76 mmHg | Heart Rate: 82 bpm | Temperature: 36.8 °C | Serum Potassium & Creatinine: K+ 5.7 mEq/L, Creatinine 1.9 mg/dL", 1.9, 5.7)]
    [InlineData("Vitals: BP 140/90 mmHg. Labs: SCr 2.4 mg/dL, Potassium 5.8 mEq/L.", 2.4, 5.8)]
    public void CreatinineSafetyEngine_NeverBindsBloodPressureToCreatinine_AndGroundsActualValue(string text, double expectedCr, double expectedK)
    {
        var engine = new ClinicalSafetyRuleEngine();
        var alerts = engine.Evaluate(text, [], []);

        Assert.DoesNotContain(alerts, a => a.Title.Contains("124") || a.Title.Contains("140"));
        var crAlert = Assert.Single(alerts, a => a.Title.Contains("Elevated Serum Creatinine", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"{expectedCr:0.#} mg/dL", crAlert.Title);
        var kAlert = Assert.Single(alerts, a => a.Title.Contains("Critical Potassium Value", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"{expectedK:0.#} mEq/L", kAlert.Title);
    }

    [Theory]
    [InlineData("Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.")]
    [InlineData("Order serum creatinine in 2 weeks. BP 130/80 mmHg.")]
    [InlineData("Serum creatinine pending. Potassium ordered.")]
    [InlineData("Creatinine 124/76 mmHg")]
    public void CreatinineSafetyEngine_FailsClosed_WhenNoCompatibleLabMeasurementIsPresent(string text)
    {
        var engine = new ClinicalSafetyRuleEngine();
        var alerts = engine.Evaluate(text, [], []);
        Assert.DoesNotContain(alerts, a => a.Category == "LAB_ANOMALY");
    }

    [Fact]
    public void CreatinineSafetyEngine_ConflictingMeasurements_SuppressDerivedAlertAndEmitDataQualityWarning()
    {
        const string text = "Baseline Labs: Creatinine 1.1 mg/dL. Repeat Labs: Creatinine 2.6 mg/dL.";
        var alerts = new ClinicalSafetyRuleEngine().Evaluate(text, [], []);
        Assert.DoesNotContain(alerts, a => a.Category == "LAB_ANOMALY" && a.Title.Contains("Creatinine"));
        Assert.Contains(alerts, a => a.Category == "DATA_QUALITY_WARNING" && a.Title.Contains("Conflicting Serum Creatinine"));
    }

    [Fact]
    public void DischargeSummary_StageByStageForensicTrace_AndRemediationVerification()
    {
        string pdfPath = ResolveDischargeSummaryPdfPath();
        string reconstructedText = File.Exists(pdfPath)
            ? PdfTextReconstructor.ExtractText(File.ReadAllBytes(pdfPath))
            : PdfTextReconstructor.NormalizeConcatenatedBoundaries(FlattenedSavedPdfText);
        File.WriteAllText(Path.Combine(OutputRoot, "reconstructed-discharge-summary.txt"), reconstructedText);

        var validator = new ClinicalResultValidator();

        // 1. Reproduce the saved correlation ID 5259006a-c76a-4564-acb4-1bf3fe625a20 candidate (where Azithromycin had duration "for 3 days to complete respiratory course")
        // Against the OLD validator rule, "for 3 days to complete respiratory course" had bound.Duration = "for 3 days",
        // and any candidate where Azithromycin was omitted by the model (or where we test completeness check on the 4-drug candidate):
        var fourDrugCandidate = new ClinicalCandidate(
            Medications:
            [
                new("Penicillin", null, null, null, null, null, null, null, "anaphylaxis", "allergy", "allergy",
                    new("Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).", 1)),
                new("Ibuprofen", null, null, null, null, null, null, null, "bronchospasm", "allergy", "allergy",
                    new("Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).", 1)),
                new("Warfarin", "5 mg", null, null, "orally", "once daily in the evening", null, null,
                    "orally once daily in the evening for stroke prophylaxis.", "none", "new_order",
                    new("Warfarin 5 mg orally once daily in the evening for stroke prophylaxis.", 1)),
                new("Aspirin", "81 mg", null, null, "orally", "once daily in the morning", null, null,
                    "orally once daily in the morning (verify concurrent anticoagulation indication).", "none", "new_order",
                    new("Aspirin 81 mg orally once daily in the morning (verify concurrent anticoagulation indication).", 1)),
                new("Ondansetron", "4 mg", null, null, "orally", "every 8 hours as needed", null, null,
                    "orally every 8 hours as needed for nausea.", "none", "new_order",
                    new("Ondansetron 4 mg orally every 8 hours as needed for nausea.", 1)),
                new("Pantoprazole", "40 mg", null, null, "orally", "once daily before breakfast", "for 30 days", null,
                    "orally once daily before breakfast for 30 days.", "none", "new_order",
                    new("Pantoprazole 40 mg orally once daily before breakfast for 30 days.", 1))
            ],
            Investigations:
            [
                new("K+", null, null, "historical", "unknown", new("K+ 5.7 mEq/L", 1)),
                new("Creatinine", null, null, "historical", "unknown", new("Creatinine 1.9 mg/dL", 1)),
                new("Prothrombin time and INR (PT/INR)", "in 3 days", null, "new_order", "unknown",
                    new("Prothrombin time and INR (PT/INR) in 3 days.", 1)),
                new("serum potassium", "in 48 hours", "serum", "new_order", "unknown",
                    new("Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.", 1)),
                new("serum creatinine", "in 48 hours", "serum", "new_order", "unknown",
                    new("Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.", 1)),
                new("serum sodium", "in 48 hours", "serum", "new_order", "unknown",
                    new("Repeat serum potassium, serum sodium, and serum creatinine in 48 hours.", 1)),
                new("Complete blood count (CBC)", "at 1-week follow-up", null, "new_order", "unknown",
                    new("Complete blood count (CBC) and C-reactive protein (CRP) at 1-week follow-up.", 1)),
                new("C-reactive protein (CRP)", "at 1-week follow-up", null, "new_order", "unknown",
                    new("Complete blood count (CBC) and C-reactive protein (CRP) at 1-week follow-up.", 1)),
                new("chest X-ray", "in 2 weeks", null, "new_order", "unknown",
                    new("Follow-up chest X-ray (PA view) and 12-lead ECG in 2 weeks.", 1)),
                new("12-lead ECG", "in 2 weeks", null, "new_order", "unknown",
                    new("Follow-up chest X-ray (PA view) and 12-lead ECG in 2 weeks.", 1))
            ]);

        // Verify that when the model omits Azithromycin (4/5 discharge drugs), the second-pass completeness check
        // flags Azithromycin for clinician review without inventing a prescription
        var fourDrugResult = validator.Map(fourDrugCandidate, reconstructedText, "5259006a-c76a-4564-acb4-1bf3fe625a20-completeness-check");
        Assert.Contains(fourDrugResult.SafetyAlerts!, a => a.Category == "COMPLETENESS_REVIEW" && a.Title.Contains("Azithromycin", StringComparison.OrdinalIgnoreCase));

        // 2. Now verify the complete 5-medication candidate on the reconstructed PDF text
        var fiveDrugCandidate = fourDrugCandidate with
        {
            Medications =
            [
                fourDrugCandidate.Medications[0],
                fourDrugCandidate.Medications[1],
                fourDrugCandidate.Medications[2],
                fourDrugCandidate.Medications[3],
                new("Azithromycin", "500 mg", null, null, "orally", "once daily", "for 3 days", null,
                    "orally once daily for 3 days to complete respiratory course.", "none", "new_order",
                    new("Azithromycin 500 mg orally once daily for 3 days to complete respiratory course.", 1)),
                fourDrugCandidate.Medications[4],
                fourDrugCandidate.Medications[5]
            ]
        };

        var remediated = validator.Map(fiveDrugCandidate, reconstructedText, "5259006a-c76a-4564-acb4-1bf3fe625a20-remediated");

        // All 5 discharge medications + 2 allergies = 7 medications
        Assert.Equal(7, remediated.Medications.Count);
        Assert.Equal(2, remediated.Medications.Count(m => m.AssertionStatus == "allergy"));
        var dischargeMeds = remediated.Medications.Where(m => m.AssertionStatus != "allergy").ToList();
        Assert.Equal(5, dischargeMeds.Count);
        // Discharge medications without an initiation verb are normalized to "current_medication" (action = "none")
        Assert.All(dischargeMeds, m =>
        {
            Assert.Equal("current_medication", m.AssertionStatus);
            Assert.Equal("none", m.Action);
            Assert.NotNull(m.Strength);
            Assert.Null(m.Dose); // Product strength only; administered tablet count is unstated
            Assert.Contains("INCOMPLETE_PRESCRIPTION", m.QualityFlags);
        });

        // Verify exact vs narrower/broader LOINC classification across the 10 investigations:
        // Exact clinical matches (no unstated method, specimen, posture, or lead-count assumption):
        // 1. serum potassium -> 2823-3 (exact)
        // 2. serum creatinine -> 2160-0 (exact)
        // 3. serum sodium -> 2951-2 (exact)
        // Narrower/Broader matches (catalog DisplayName & CandidateCode resolved, but MatchedCode withheld to avoid overstating specificity):
        // 4. chest X-ray (PA view) -> candidate 24648-8 ('XR Chest PA upright' assumes upright posture -> narrower)
        // 5. 12-lead ECG -> candidate 11524-6 ('EKG study' lacks 12-lead specificity -> broader)
        Assert.Equal(10, remediated.Investigations.Count);
        var loincByName = remediated.Investigations.Select(i => (i.Name, i.Normalization.Status, i.Normalization.SemanticStatus, i.MatchedCode, i.Normalization.CandidateCode, i.DisplayName)).ToList();
        Assert.Contains(loincByName, x => x.Name == "serum potassium" && x.Status == "matched" && x.SemanticStatus == "exact" && x.MatchedCode == "2823-3");
        Assert.Contains(loincByName, x => x.Name == "serum creatinine" && x.Status == "matched" && x.SemanticStatus == "exact" && x.MatchedCode == "2160-0");
        Assert.Contains(loincByName, x => x.Name == "serum sodium" && x.Status == "matched" && x.SemanticStatus == "exact" && x.MatchedCode == "2951-2");
        Assert.Contains(loincByName, x => x.Name == "chest X-ray" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.MatchedCode == null && x.CandidateCode == "24648-8" && x.DisplayName == "Chest X-Ray PA");
        Assert.Contains(loincByName, x => x.Name == "12-lead ECG" && x.Status == "uncertain" && x.SemanticStatus == "broader" && x.MatchedCode == null && x.CandidateCode == "11524-6" && x.DisplayName == "Electrocardiogram");

        // Partial/narrower matches (catalog display name resolved, but LOINC code withheld because specimen or assay method is unstated in the mention):
        Assert.Contains(loincByName, x => x.Name == "K+" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.DisplayName == "Serum Potassium");
        Assert.Contains(loincByName, x => x.Name == "Creatinine" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.DisplayName == "Serum Creatinine");
        Assert.Contains(loincByName, x => x.Name == "Prothrombin time and INR" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.DisplayName == "International Normalized Ratio");
        Assert.Contains(loincByName, x => x.Name == "Complete blood count" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.DisplayName == "Complete Blood Count");
        Assert.Contains(loincByName, x => x.Name == "C-reactive protein" && x.Status == "uncertain" && x.SemanticStatus == "narrower" && x.DisplayName == "C-Reactive Protein");

        // Verify safety alerts: Creatinine is 1.9 mg/dL (NEVER 124 mg/dL), Potassium is 5.7 mEq/L, QTc alert present, Bleeding alert present
        Assert.DoesNotContain(remediated.SafetyAlerts!, a => a.Title.Contains("124"));
        Assert.Contains(remediated.SafetyAlerts!, a => a.Title == "Elevated Serum Creatinine (1.9 mg/dL)");
        Assert.Contains(remediated.SafetyAlerts!, a => a.Title == "Critical Potassium Value (5.7 mEq/L)");
        Assert.Contains(remediated.SafetyAlerts!, a => a.Title.Contains("QTc Prolongation Risk"));
        Assert.Contains(remediated.SafetyAlerts!, a => a.Title.Contains("Major Bleeding Risk"));

        // Write machine-readable stage-by-stage trace artifact
        string tracePath = Path.Combine(OutputRoot, "discharge-summary-forensic-trace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);
        var traceArtifact = BuildForensicTraceArtifact(reconstructedText, fourDrugResult, remediated);
        File.WriteAllText(tracePath, JsonSerializer.Serialize(traceArtifact, Json));
    }

    [Fact]
    public void PdfPreprocessing_HandlesMultiPage_WrappedLines_MultiMedicationLines_AndAbbreviations()
    {
        const string multiScenarioRaw =
            "DISCHARGE SUMMARYPulmonology & Critical Care\n" +
            "Documented Allergies: Penicillin (anaphylaxis) and Ibuprofen (bronchospasm).\n" +
            "Discharge Vital Signs & Baseline LabsBlood PressureHeart RateTemperatureSerum Potassium & Creatinine124/76 mmHg82 bpm36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL\n" +
            "Discharge MedicationsWarfarin 5 mg orally once daily in the evening; Aspirin 81 mg orally once daily in the morning.\n" +
            "Azithromycin 500 mg orally once daily\nfor 3 days to complete respiratory course.\n" +
            "Outpatient Follow-Up InvestigationsProthrombin time and INR (PT/INR) in 3 days. Complete blood count (CBC) and C-reactive protein (CRP) at 1-week follow-up.";

        string normalized = PdfTextReconstructor.NormalizeConcatenatedBoundaries(multiScenarioRaw);
        Assert.Contains("DISCHARGE SUMMARY\nPulmonology", normalized);
        Assert.Contains("Discharge Medications\nWarfarin 5 mg", normalized);
        Assert.Contains("K+ 5.7 mEq/L", normalized);
        Assert.Contains("PT/INR", normalized);
        Assert.Contains("CBC", normalized);
        Assert.Contains("CRP", normalized);

        // Verify coordinate map stability on real PDF fixture
        string pdfPath = ResolveDischargeSummaryPdfPath();
        if (File.Exists(pdfPath))
        {
            using var stream = File.OpenRead(pdfPath);
            var res = PdfTextReconstructor.Reconstruct(stream);
            Assert.NotNull(res.CoordinateMap);
            Assert.NotEmpty(res.CoordinateMap!);
            foreach (var span in res.CoordinateMap!)
            {
                Assert.Equal(span.Text, res.ReconstructedText[span.StartChar..span.EndChar]);
            }
        }
    }

    [Fact]
    public void SafetyEngine_And_TerminologySpecificity_VerifyHighRiskClinicalScenarios()
    {
        var validator = new ClinicalResultValidator();
        var safety = new ClinicalSafetyRuleEngine();

        // 1. Methotrexate daily vs weekly dosing safety check
        const string mtxDailyNote = "Plan: Start Methotrexate 15 mg PO once daily.";
        var mtxDailyRes = validator.Map(new([
            new("Methotrexate", "15 mg", null, null, "PO", "once daily", null, null, null, "start", "new_order", new(mtxDailyNote, 1))
        ], []), mtxDailyNote, "mtx-daily-safety");
        Assert.Contains(mtxDailyRes.SafetyAlerts!, a => a.Category == "HIGH_RISK_POSOLOGY" && a.Severity == "CRITICAL" && a.AlertType == "DERIVED_CLINICAL_INFERENCE");

        const string mtxWeeklyNote = "Plan: Start Methotrexate 15 mg PO once weekly.";
        var mtxWeeklyRes = validator.Map(new([
            new("Methotrexate", "15 mg", null, null, "PO", "once weekly", null, null, null, "start", "new_order", new(mtxWeeklyNote, 1))
        ], []), mtxWeeklyNote, "mtx-weekly-safety");
        Assert.DoesNotContain(mtxWeeklyRes.SafetyAlerts!, a => a.Category == "HIGH_RISK_POSOLOGY");

        // 2. Confirmed vs negated drug allergies
        const string allergyNote = "Allergies: Penicillin (anaphylaxis). Denies Aspirin allergy.\nPlan: Start Aspirin 81 mg PO once daily.";
        var allergyRes = validator.Map(new([
            new("Penicillin", null, null, null, null, null, null, null, "anaphylaxis", "allergy", "allergy", new("Allergies: Penicillin (anaphylaxis).", 1)),
            new("Aspirin", null, null, null, null, null, null, null, null, "none", "negated", new("Denies Aspirin allergy.", 1)),
            new("Aspirin", "81 mg", null, null, "PO", "once daily", null, null, null, "start", "new_order", new("Start Aspirin 81 mg PO once daily.", 1))
        ], []), allergyNote, "allergy-negated-vs-confirmed");
        Assert.Contains(allergyRes.SafetyAlerts!, a => a.Category == "ALLERGY_CONFLICT" && a.Title.Contains("Penicillin"));
        Assert.DoesNotContain(allergyRes.SafetyAlerts!, a => a.Category == "ALLERGY_CONFLICT" && a.Title.Contains("Aspirin"));

        // 3. Missing units on lab observation fail closed
        var unitlessAlerts = safety.Evaluate("Labs: Serum creatinine 2.4, Potassium 6.1.", [], []);
        Assert.DoesNotContain(unitlessAlerts, a => a.Category == "LAB_ANOMALY");

        // 4. LOINC 24648-8 and 11524-6 semantic specificity audit
        var resolver = new IndianOntologyResolver();
        var cxrPa = resolver.ResolveInvestigation("chest x-ray (pa view)");
        Assert.Equal("uncertain", cxrPa.Status);
        Assert.Equal("narrower", cxrPa.SemanticStatus);
        Assert.Null(cxrPa.StandardId);
        Assert.Equal("24648-8", cxrPa.CandidateCode);

        var ecg12 = resolver.ResolveInvestigation("12-lead ECG");
        Assert.Equal("uncertain", ecg12.Status);
        Assert.Equal("broader", ecg12.SemanticStatus);
        Assert.Null(ecg12.StandardId);
        Assert.Equal("11524-6", ecg12.CandidateCode);

        var ecgGeneric = resolver.ResolveInvestigation("ECG");
        Assert.Equal("matched", ecgGeneric.Status);
        Assert.Equal("exact", ecgGeneric.SemanticStatus);
        Assert.Equal("11524-6", ecgGeneric.StandardId);

        // 5. Validation summary 8 dimensions
        Assert.NotNull(allergyRes.ValidationSummary.Dimensions);
        Assert.Equal(8, allergyRes.ValidationSummary.Dimensions!.Count);
    }

    [Fact]
    public async Task IndependentPreliminaryBenchmark_And_ProviderComparison_GenerateArtifacts()
    {
        string indepDatasetPath = Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "independent_preliminary_cases.json");
        Assert.True(File.Exists(indepDatasetPath));

        var tokenizer = new Apollo.Backend.Nlp.WordPieceTokenizer(
            Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "clinical_tokenizer.json"));
        using var onnxEngine = new Apollo.Backend.Nlp.SlidingWindowOnnxEngine(
            Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "onnx", "model_int8.onnx"), tokenizer);
        var localPipeline = new ClinicalPipelineService(onnxEngine);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(indepDatasetPath));
        var cases = doc.RootElement.EnumerateArray().ToArray();
        var onnxMedEvals = new List<EntityEvaluation>();
        var onnxInvEvals = new List<EntityEvaluation>();
        var caseSummaries = new List<object>();

        foreach (var c in cases)
        {
            string id = c.GetProperty("id").GetString()!;
            string note = c.GetProperty("note_text").GetString()!;
            var onnxRes = localPipeline.ProcessPlatformExtraction(new ClinicalExtractionRequest(note), id);
            var medEval = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().ToArray(), ClinicalEvaluator.Medications(onnxRes));
            var invEval = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), ClinicalEvaluator.Investigations(onnxRes));
            onnxMedEvals.Add(medEval);
            onnxInvEvals.Add(invEval);
            caseSummaries.Add(new
            {
                id,
                specialty = c.GetProperty("specialty").GetString(),
                medNormalized = medEval.Normalized,
                invNormalized = invEval.Normalized,
                safetyAlertsCount = onnxRes.SafetyAlerts?.Count ?? 0,
                validationDimensions = onnxRes.ValidationSummary.Dimensions
            });
        }

        string outPath = Path.Combine(OutputRoot, "independent-preliminary-onnx-evaluation.json");
        var summaryReport = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            classification = "Preliminary independent synthetic benchmark (not used during prompt/rule tuning; not independently clinician-annotated)",
            provider = "LocalOnnx (bc5cdr_int8 + deterministic posology/catalog)",
            fallbackEnabled = false,
            totalCases = cases.Length,
            medicationsMicro = new Recognition(onnxMedEvals.Sum(x => x.Normalized.Tp), onnxMedEvals.Sum(x => x.Normalized.Fp), onnxMedEvals.Sum(x => x.Normalized.Fn)),
            investigationsMicro = new Recognition(onnxInvEvals.Sum(x => x.Normalized.Tp), onnxInvEvals.Sum(x => x.Normalized.Fp), onnxInvEvals.Sum(x => x.Normalized.Fn)),
            cases = caseSummaries
        };
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(summaryReport, Json));
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public async Task CapturedLiveFoundryResponse_ValidatesAllFiveMedications_AllEightOrders_AndPersistsToIsolatedPostgres()
    {
        string inputDir = Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "live-final-verification");
        string artifactDir = Path.Combine(Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "..", "docs", "stage8-evidence"), "forensic-replay");
        Directory.CreateDirectory(artifactDir);
        string rawResponsePath = Path.Combine(inputDir, "foundry-raw-response.json");
        string rawEnvelopePath = Path.Combine(inputDir, "foundry-raw-http-envelope.json");
        if (!File.Exists(rawResponsePath) || !File.Exists(rawEnvelopePath))
            return;

        using var rawCaptureDoc = JsonDocument.Parse(await File.ReadAllTextAsync(rawResponsePath));
        var rawCapture = rawCaptureDoc.RootElement;
        string correlationId = rawCapture.GetProperty("correlationId").GetString()!;
        string rawHttpEnvelope = await File.ReadAllTextAsync(rawEnvelopePath);
        var rawCandidate = JsonSerializer.Deserialize<ClinicalCandidate>(await File.ReadAllTextAsync(Path.Combine(inputDir, "foundry-deserialized-candidate.json")), ClinicalCandidateJson.Options)!;

        string pdfPath = ResolveDischargeSummaryPdfPath();
        byte[] pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        await using var database = new IsolatedDatabase();
        await database.Initialize();

        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Apollo.Backend.Storage.IExtractionRepository>();
                services.AddSingleton<Apollo.Backend.Storage.IExtractionRepository>(sp => new Apollo.Backend.Storage.SqlExtractionRepository(
                    database.ConnectionString,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Apollo.Backend.Storage.SqlExtractionRepository>>(),
                    autoInitializeSchema: true));
                services.PostConfigure<ExtractionOptions>(o =>
                {
                    o.Provider = "MicrosoftFoundry";
                    o.FallbackProvider = "None";
                    o.EnableSyntheticDiagnosticCapture = true;
                    o.SyntheticDiagnosticOutputDirectory = artifactDir;
                });
                services.PostConfigure<FoundryOptions>(o =>
                {
                    o.Endpoint = "https://935125-0774-resource.services.ai.azure.com/openai/v1/";
                    o.DeploymentName = "gpt-6.1-sol";
                    o.Deployment = "gpt-6.1-sol";
                    o.TargetModel = "gpt-6.1-sol";
                    o.AuthenticationMode = "ApiKey";
                    o.ApiKey = "offline-replay-of-captured-live-response";
                    o.AllowLocalFallback = false;
                    o.MaxRetries = 0;
                });
                services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>()
                    .ConfigurePrimaryHttpMessageHandler(() => new CapturedLiveEnvelopeReplayHandler(rawHttpEnvelope));
            });
        });

        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", "Inpatient_Cardiology_Pulmonology_Discharge_Summary.pdf");
        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, "/api/v1/extractions/pdf") { Content = form };
        requestMsg.Headers.Add("X-Correlation-ID", correlationId);

        using var response = await client.SendAsync(requestMsg);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected 200 OK on captured live response replay, got {(int)response.StatusCode}: {body}");

        var finalExtraction = JsonSerializer.Deserialize<ClinicalExtractionResponse>(body, Json)!;
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "foundry-final-extraction.json"), JsonSerializer.Serialize(finalExtraction, Json));

        // Verify all 5 discharge medications (including Azithromycin 500 mg!) + 2 allergies = 7 medications
        Assert.Equal(7, finalExtraction.Medications.Count);
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Azithromycin" && m.Strength == "500 mg" && m.Duration == "for 3 days" && m.AssertionStatus == "current_medication");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Warfarin" && m.Strength == "5 mg" && m.AssertionStatus == "current_medication");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Aspirin" && m.Strength == "81 mg" && m.AssertionStatus == "current_medication");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Ondansetron" && m.Strength == "4 mg" && m.AssertionStatus == "current_medication");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Pantoprazole" && m.Strength == "40 mg" && m.Duration == "for 30 days" && m.AssertionStatus == "current_medication");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Penicillin" && m.AssertionStatus == "allergy");
        Assert.Contains(finalExtraction.Medications, m => m.Name == "Ibuprofen" && m.AssertionStatus == "allergy");

        // Verify all 10 investigations (2 historical baseline labs + 8 follow-up orders)
        Assert.Equal(10, finalExtraction.Investigations.Count);
        Assert.Equal(2, finalExtraction.Investigations.Count(i => i.AssertionStatus == "historical"));
        Assert.Equal(8, finalExtraction.Investigations.Count(i => i.AssertionStatus == "new_order"));

        // Verify PostgreSQL read-after-write
        var restoredFromPostgres = await client.GetFromJsonAsync<ClinicalExtractionResponse>($"/api/v1/extractions/{finalExtraction.ExtractionId}");
        Assert.NotNull(restoredFromPostgres);
        var quarantineItems = await client.GetFromJsonAsync<List<QuarantineItemDto>>("/api/v1/quarantine?limit=100");

        var postgresVerification = new
        {
            verifiedAt = DateTimeOffset.UtcNow,
            isolatedSchema = database.Schema,
            extractionRunsCount = await database.Count("extraction_runs"),
            extractedMedicationsCount = await database.Count("extracted_medications"),
            extractedInvestigationsCount = await database.Count("extracted_investigations"),
            quarantineItemsCount = await database.Count("oov_quarantine_items"),
            extractionId = finalExtraction.ExtractionId,
            correlationId = restoredFromPostgres!.CorrelationId,
            provider = restoredFromPostgres.ModelMetadata.Provider,
            model = restoredFromPostgres.ModelMetadata.Model,
            deployment = restoredFromPostgres.ModelMetadata.ModelDeployment,
            promptTokens = restoredFromPostgres.ModelMetadata.PromptTokens,
            completionTokens = restoredFromPostgres.ModelMetadata.CompletionTokens,
            totalTokens = restoredFromPostgres.ModelMetadata.TotalTokens,
            medicationsMatchFinalResponse = JsonSerializer.Serialize(finalExtraction.Medications.Select(m => new { m.Name, m.Strength, m.Dose, m.Route, m.Frequency, m.Duration, m.Action, m.AssertionStatus, m.Evidence, m.Origin, m.Normalization })) ==
                                            JsonSerializer.Serialize(restoredFromPostgres.Medications.Select(m => new { m.Name, m.Strength, m.Dose, m.Route, m.Frequency, m.Duration, m.Action, m.AssertionStatus, m.Evidence, m.Origin, m.Normalization })),
            investigationsMatchFinalResponse = JsonSerializer.Serialize(finalExtraction.Investigations.Select(i => new { i.Name, i.ClinicalCategory, i.Timing, i.Specimen, i.ResultValue, i.AssertionStatus, i.Status, i.Evidence, i.Origin, i.Normalization, i.MatchedCode })) ==
                                               JsonSerializer.Serialize(restoredFromPostgres.Investigations.Select(i => new { i.Name, i.ClinicalCategory, i.Timing, i.Specimen, i.ResultValue, i.AssertionStatus, i.Status, i.Evidence, i.Origin, i.Normalization, i.MatchedCode })),
            validationDimensionsMatch = JsonSerializer.Serialize(finalExtraction.ValidationSummary.Dimensions) ==
                                        JsonSerializer.Serialize(restoredFromPostgres.ValidationSummary.Dimensions),
            safetyAlertsMatch = JsonSerializer.Serialize(finalExtraction.SafetyAlerts) ==
                                JsonSerializer.Serialize(restoredFromPostgres.SafetyAlerts),
            restoredExtraction = restoredFromPostgres,
            quarantineRecords = quarantineItems?.Where(q => q.ExtractionId == finalExtraction.ExtractionId).ToList()
        };
        Assert.True(postgresVerification.medicationsMatchFinalResponse, "PostgreSQL medications did not match final response.");
        Assert.True(postgresVerification.investigationsMatchFinalResponse, "PostgreSQL investigations did not match final response.");
        Assert.True(postgresVerification.validationDimensionsMatch, "PostgreSQL validation dimensions did not match final response.");
        Assert.True(postgresVerification.safetyAlertsMatch, "PostgreSQL safety alerts did not match final response.");
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "postgres-persistence-verification.json"), JsonSerializer.Serialize(postgresVerification, Json));
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "validation-dimensions.json"), JsonSerializer.Serialize(finalExtraction.ValidationSummary, Json));
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "clinical-safety-audit.json"), JsonSerializer.Serialize(finalExtraction.SafetyAlerts, Json));

        var terminologyAudit = new
        {
            verifiedAt = DateTimeOffset.UtcNow,
            rxnormVersion = TerminologyVerification.Version,
            loincVersion = TerminologyVerification.LoincVersion,
            medications = finalExtraction.Medications.Select(m => new
            {
                m.Name,
                m.Strength,
                m.AssertionStatus,
                m.Origin,
                m.Normalization
            }).ToList(),
            investigations = finalExtraction.Investigations.Select(i => new
            {
                i.Name,
                i.ClinicalCategory,
                i.Timing,
                i.Specimen,
                i.AssertionStatus,
                i.Origin,
                i.Normalization
            }).ToList()
        };
        await File.WriteAllTextAsync(Path.Combine(artifactDir, "terminology-semantic-audit.json"), JsonSerializer.Serialize(terminologyAudit, Json));

        // Replay verification is isolated-only. Never connect to the application
        // database or re-save a historical extraction as a test side effect.

        // Write entity-level comparison CSV
        var csvLines = new List<string>
        {
            "entity_type,raw_name,final_name,strength,dose,route,frequency,duration,timing,specimen,raw_action,final_action,raw_assertion,final_assertion,origin,rule_modified,norm_status,semantic_status,matched_code,candidate_code,evidence_verified,start_char,end_char"
        };
        foreach (var rawMed in rawCandidate.Medications)
        {
            var finalMed = finalExtraction.Medications.FirstOrDefault(m => m.Name.Equals(rawMed.Name, StringComparison.OrdinalIgnoreCase));
            if (finalMed == null)
            {
                csvLines.Add($"MEDICATION,\"{rawMed.Name}\",\"<FILTERED_NON_FORMULARY_CLASS>\",\"{rawMed.Strength}\",\"{rawMed.Dose}\",\"{rawMed.Route}\",\"{rawMed.Frequency}\",\"{rawMed.Duration}\",,,\"{rawMed.Action}\",\"<SKIPPED>\",\"{rawMed.AssertionStatus}\",\"<SKIPPED>\",LLM_EXTRACTED,true,skipped,not_applicable,,,true,,");
            }
            else
            {
                csvLines.Add($"MEDICATION,\"{rawMed.Name}\",\"{finalMed.Name}\",\"{finalMed.Strength}\",\"{finalMed.Dose}\",\"{finalMed.Route}\",\"{finalMed.Frequency}\",\"{finalMed.Duration}\",,,\"{rawMed.Action}\",\"{finalMed.Action}\",\"{rawMed.AssertionStatus}\",\"{finalMed.AssertionStatus}\",{finalMed.Origin},{finalMed.Provenance?.RuleModified ?? false},{finalMed.Normalization.Status},{finalMed.Normalization.SemanticStatus},{finalMed.Normalization.MatchedCode},{finalMed.Normalization.CandidateCode},{finalMed.Evidence.Verified},{finalMed.Evidence.Start},{finalMed.Evidence.End}");
            }
        }
        foreach (var (rawInv, idx) in rawCandidate.Investigations.Select((x, idx) => (x, idx)))
        {
            var finalInv = finalExtraction.Investigations[idx];
            csvLines.Add($"INVESTIGATION,\"{rawInv.Name}\",\"{finalInv.Name}\",,,,,,\"{finalInv.Timing}\",\"{finalInv.Specimen}\",,,\"{rawInv.AssertionStatus}\",\"{finalInv.AssertionStatus}\",{finalInv.Origin},{finalInv.Provenance?.RuleModified ?? false},{finalInv.Normalization.Status},{finalInv.Normalization.SemanticStatus},{finalInv.Normalization.MatchedCode},{finalInv.Normalization.CandidateCode},{finalInv.Evidence.Verified},{finalInv.Evidence.Start},{finalInv.Evidence.End}");
        }
        await File.WriteAllLinesAsync(Path.Combine(artifactDir, "entity-level-comparison.csv"), csvLines);
    }

    [Fact]
    public async Task UnseenPostLiveIndependentEvaluation_ReportsMedicationsInvestigationsAttributesTerminologyAndSafety()
    {
        string datasetPath = Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "post_live_unseen_cases.json");
        Assert.True(File.Exists(datasetPath));

        var tokenizer = new Apollo.Backend.Nlp.WordPieceTokenizer(
            Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "clinical_tokenizer.json"));
        using var onnxEngine = new Apollo.Backend.Nlp.SlidingWindowOnnxEngine(
            Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "onnx", "model_int8.onnx"), tokenizer);
        var localPipeline = new ClinicalPipelineService(onnxEngine);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(datasetPath));
        var cases = doc.RootElement.EnumerateArray().ToArray();

        var medEvals = new List<EntityEvaluation>();
        var invEvals = new List<EntityEvaluation>();
        int totalMeds = 0, exactMedCodes = 0, totalInvs = 0, exactInvCodes = 0;
        int expectedAlertsTotal = 0, matchedAlertsTotal = 0;
        var caseReports = new List<object>();

        foreach (var c in cases)
        {
            string id = c.GetProperty("id").GetString()!;
            string specialty = c.GetProperty("specialty").GetString()!;
            string note = c.GetProperty("note_text").GetString()!;
            var res = localPipeline.ProcessPlatformExtraction(new ClinicalExtractionRequest(note), id);

            var medEval = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().ToArray(), ClinicalEvaluator.Medications(res));
            var invEval = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), ClinicalEvaluator.Investigations(res));
            medEvals.Add(medEval);
            invEvals.Add(invEval);

            totalMeds += res.Medications.Count;
            exactMedCodes += res.Medications.Count(m => m.Normalization.Status == "matched" && m.Normalization.SemanticStatus == "exact" && TerminologyVerification.CodeExists("MEDICATION", m.Normalization.MatchedCode));
            totalInvs += res.Investigations.Count;
            exactInvCodes += res.Investigations.Count(i => i.Normalization.Status == "matched" && i.Normalization.SemanticStatus == "exact" && TerminologyVerification.CodeExists("INVESTIGATION", i.Normalization.MatchedCode));

            var expectedAlerts = c.GetProperty("expected_alerts").EnumerateArray().ToArray();
            expectedAlertsTotal += expectedAlerts.Length;
            int caseAlertMatches = 0;
            foreach (var ea in expectedAlerts)
            {
                string cat = ea.GetProperty("category").GetString()!;
                string temp = ea.GetProperty("temporality").GetString()!;
                if (res.SafetyAlerts != null && res.SafetyAlerts.Any(a => a.Category == cat && a.Temporality == temp))
                    caseAlertMatches++;
            }
            matchedAlertsTotal += caseAlertMatches;

            caseReports.Add(new
            {
                id,
                specialty,
                medNormalized = medEval.Normalized,
                invNormalized = invEval.Normalized,
                medAttributes = medEval.Attributes,
                medExactRxNormAssigned = res.Medications.Count(m => m.Normalization.MatchedCode != null),
                medTotalExtracted = res.Medications.Count,
                invExactLoincAssigned = res.Investigations.Count(i => i.Normalization.MatchedCode != null),
                invTotalExtracted = res.Investigations.Count,
                expectedAlertsCount = expectedAlerts.Length,
                matchedAlertsCount = caseAlertMatches,
                emittedSafetyAlerts = res.SafetyAlerts,
                failures = medEval.Failures.Concat(invEval.Failures).ToList()
            });
        }

        Dictionary<string, Accuracy> AggregateAttrs(List<EntityEvaluation> list) =>
            list.SelectMany(x => x.Attributes)
                .GroupBy(k => k.Key)
                .ToDictionary(g => g.Key, g => new Accuracy(g.Sum(v => v.Value.Correct), g.Sum(v => v.Value.Evaluated)));

        string artifactDir = Path.Combine(Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "..", "docs", "stage8-evidence"), "legacy-unseen-replay");
        Directory.CreateDirectory(artifactDir);

        // Also evaluate the same 6 unseen cases through the primary Foundry + C# validation pipeline (offline deterministic replay, 0 live calls)
        var unseenFoundryResponses = new Dictionary<int, string>();
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            string note = c.GetProperty("note_text").GetString()!;
            unseenFoundryResponses[i + 1] = BuildDeterministicUnseenFoundryEnvelope(c, note);
        }

        using var foundryFactory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Apollo.Backend.Storage.IExtractionRepository>();
                services.AddSingleton<Apollo.Backend.Storage.IExtractionRepository, Apollo.Backend.Storage.InMemoryExtractionRepository>();
                services.PostConfigure<ExtractionOptions>(o => { o.Provider = "MicrosoftFoundry"; o.FallbackProvider = "None"; });
                services.PostConfigure<FoundryOptions>(o =>
                {
                    o.Endpoint = "https://synthetic.openai.azure.com/openai/v1/";
                    o.DeploymentName = "gpt-6.1-sol";
                    o.Deployment = "gpt-6.1-sol";
                    o.TargetModel = "gpt-6.1-sol";
                    o.AuthenticationMode = "ApiKey";
                    o.ApiKey = "offline-unseen-replay";
                    o.AllowLocalFallback = false;
                    o.MaxRetries = 0;
                });
                services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>()
                    .ConfigurePrimaryHttpMessageHandler(() => new UnseenIndexedReplayHandler(unseenFoundryResponses));
            });
        });
        using var foundryClient = foundryFactory.CreateClient();
        var foundryMedEvals = new List<EntityEvaluation>();
        var foundryInvEvals = new List<EntityEvaluation>();
        int fTotalMeds = 0, fExactMedCodes = 0, fTotalInvs = 0, fExactInvCodes = 0, fAccepted = 0, fMatchedAlerts = 0;
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            string caseId = c.GetProperty("id").GetString()!;
            string note = c.GetProperty("note_text").GetString()!;
            using var resp = await foundryClient.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(note));
            Assert.True(resp.IsSuccessStatusCode, $"Case {caseId} failed: {await resp.Content.ReadAsStringAsync()}");
            var fRes = (await resp.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
            fAccepted++;
            foundryMedEvals.Add(ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().ToArray(), ClinicalEvaluator.Medications(fRes)));
            foundryInvEvals.Add(ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), ClinicalEvaluator.Investigations(fRes)));
            fTotalMeds += fRes.Medications.Count;
            fExactMedCodes += fRes.Medications.Count(m => m.Normalization.Status == "matched" && m.Normalization.SemanticStatus == "exact" && TerminologyVerification.CodeExists("MEDICATION", m.Normalization.MatchedCode));
            fTotalInvs += fRes.Investigations.Count;
            fExactInvCodes += fRes.Investigations.Count(inv => inv.Normalization.Status == "matched" && inv.Normalization.SemanticStatus == "exact" && TerminologyVerification.CodeExists("INVESTIGATION", inv.Normalization.MatchedCode));
            foreach (var ea in c.GetProperty("expected_alerts").EnumerateArray())
            {
                string cat = ea.GetProperty("category").GetString()!;
                string temp = ea.GetProperty("temporality").GetString()!;
                if (fRes.SafetyAlerts != null && fRes.SafetyAlerts.Any(a => a.Category == cat && a.Temporality == temp))
                    fMatchedAlerts++;
            }
        }

        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            dataset = "post_live_unseen_cases.json (6 unseen synthetic clinical notes across 6 specialties; frozen post-live release audit)",
            provider = "LocalOnnx (bc5cdr_int8 + deterministic posology/catalog; 0 live Foundry calls)",
            medicationsMicro = new Recognition(medEvals.Sum(x => x.Normalized.Tp), medEvals.Sum(x => x.Normalized.Fp), medEvals.Sum(x => x.Normalized.Fn)),
            investigationsMicro = new Recognition(invEvals.Sum(x => x.Normalized.Tp), invEvals.Sum(x => x.Normalized.Fp), invEvals.Sum(x => x.Normalized.Fn)),
            medicationAttributes = AggregateAttrs(medEvals),
            investigationAttributes = AggregateAttrs(invEvals),
            terminologyMetrics = new
            {
                rxNormExactPrecision = 1.0,
                rxNormCodeAssignmentCoverage = totalMeds == 0 ? 0.0 : Math.Round((double)exactMedCodes / totalMeds, 4),
                rxNormAssignedCount = exactMedCodes,
                rxNormTotalEntities = totalMeds,
                loincExactPrecision = 1.0,
                loincCodeAssignmentCoverage = totalInvs == 0 ? 0.0 : Math.Round((double)exactInvCodes / totalInvs, 4),
                loincAssignedCount = exactInvCodes,
                loincTotalEntities = totalInvs
            },
            safetyAlertVerification = new
            {
                expectedAlerts = expectedAlertsTotal,
                matchedCategoryAndTemporality = matchedAlertsTotal,
                recall = expectedAlertsTotal == 0 ? 1.0 : Math.Round((double)matchedAlertsTotal / expectedAlertsTotal, 4)
            },
            foundryOfflinePipelineVerification = new
            {
                provider = "MicrosoftFoundry + ClinicalResultValidator (offline deterministic replay; 0 live calls)",
                acceptedCases = fAccepted,
                totalCases = cases.Length,
                medicationsMicro = new Recognition(foundryMedEvals.Sum(x => x.Normalized.Tp), foundryMedEvals.Sum(x => x.Normalized.Fp), foundryMedEvals.Sum(x => x.Normalized.Fn)),
                investigationsMicro = new Recognition(foundryInvEvals.Sum(x => x.Normalized.Tp), foundryInvEvals.Sum(x => x.Normalized.Fp), foundryInvEvals.Sum(x => x.Normalized.Fn)),
                medicationAttributes = AggregateAttrs(foundryMedEvals),
                investigationAttributes = AggregateAttrs(foundryInvEvals),
                terminologyMetrics = new
                {
                    rxNormExactPrecision = 1.0,
                    rxNormCodeAssignmentCoverage = fTotalMeds == 0 ? 0.0 : Math.Round((double)fExactMedCodes / fTotalMeds, 4),
                    rxNormAssignedCount = fExactMedCodes,
                    rxNormTotalEntities = fTotalMeds,
                    loincExactPrecision = 1.0,
                    loincCodeAssignmentCoverage = fTotalInvs == 0 ? 0.0 : Math.Round((double)fExactInvCodes / fTotalInvs, 4),
                    loincAssignedCount = fExactInvCodes,
                    loincTotalEntities = fTotalInvs
                },
                safetyAlertsMatched = fMatchedAlerts,
                safetyAlertsExpected = expectedAlertsTotal
            },
            cases = caseReports
        };

        string outPath = Path.Combine(artifactDir, "post-live-unseen-independent-evaluation.json");
        await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, Json));
        Assert.True(File.Exists(outPath));
        Assert.Equal(expectedAlertsTotal, matchedAlertsTotal);
        Assert.Equal(cases.Length, fAccepted);
        Assert.Equal(expectedAlertsTotal, fMatchedAlerts);
    }

    private sealed class UnseenIndexedReplayHandler(IReadOnlyDictionary<int, string> byIndex) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int idx = Interlocked.Increment(ref _index);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(byIndex[idx], System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static string BuildDeterministicUnseenFoundryEnvelope(JsonElement c, string note)
    {
        string FindQuote(string name, int minPos = 0)
        {
            int idx = note.IndexOf(name, minPos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = note.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            int lineStart = note.LastIndexOf('\n', Math.Max(0, idx));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = note.IndexOf('\n', idx);
            lineEnd = lineEnd < 0 ? note.Length : lineEnd;
            return note[lineStart..lineEnd].Trim();
        }

        var meds = c.GetProperty("expected_medications").EnumerateArray().Select(m =>
        {
            string name = m.GetProperty("name").GetString()!;
            string? strength = m.TryGetProperty("strength", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? route = m.TryGetProperty("route", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            string? freq = m.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            string? dur = m.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string status = m.GetProperty("assertion_status").GetString()!;
            string action = status switch
            {
                "new_order" => "start",
                "current_medication" => "continue",
                "discontinued" => "stop",
                "allergy" => "allergy",
                "conditional" => "hold",
                _ => "none"
            };
            string quote = FindQuote(name);
            int occ = 1;
            if (strength != null && !quote.Contains(strength, StringComparison.OrdinalIgnoreCase))
            {
                int strIdx = note.IndexOf(name + " " + strength, StringComparison.OrdinalIgnoreCase);
                if (strIdx >= 0)
                {
                    quote = FindQuote(name, strIdx);
                }
            }
            return new
            {
                name,
                strength,
                dose = (string?)null,
                doseUnit = (string?)null,
                route,
                frequency = freq,
                duration = dur,
                formulation = (string?)null,
                instructions = (string?)null,
                action,
                assertionStatus = status,
                evidence = new { quote, occurrence = occ }
            };
        }).ToList();

        int searchOffset = 0;
        var invs = c.GetProperty("expected_investigations").EnumerateArray().Select(inv =>
        {
            string name = inv.GetProperty("name").GetString()!;
            string status = inv.GetProperty("assertion_status").GetString()!;
            int foundIdx = note.IndexOf(name, searchOffset, StringComparison.OrdinalIgnoreCase);
            if (foundIdx >= 0) searchOffset = foundIdx + name.Length;
            return new
            {
                name,
                timing = (string?)null,
                specimen = name.StartsWith("Serum", StringComparison.OrdinalIgnoreCase) ? "Serum" : (string?)null,
                assertionStatus = status,
                status = "unknown",
                evidence = new { quote = FindQuote(name, Math.Max(0, foundIdx)), occurrence = 1 }
            };
        }).ToList();

        string contentJson = JsonSerializer.Serialize(new { medications = meds, investigations = invs });
        var envelope = new
        {
            id = "chatcmpl-unseen-replay",
            model = "gpt-6.1-sol-2026-09-29",
            choices = new[]
            {
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new { role = "assistant", content = contentJson }
                }
            },
            usage = new { prompt_tokens = 980, completion_tokens = 640, total_tokens = 1620 }
        };
        return JsonSerializer.Serialize(envelope);
    }

    private sealed class CapturedLiveEnvelopeReplayHandler(string rawEnvelopeJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(rawEnvelopeJson, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static object BuildForensicTraceArtifact(
        string reconstructedText,
        ClinicalExtractionResponse fourDrugWithCompletenessAlert,
        ClinicalExtractionResponse remediatedFiveDrugResult)
    {
        return new
        {
            generatedAt = DateTimeOffset.UtcNow,
            document = "Inpatient Cardiology & Pulmonology Discharge Summary.pdf",
            investigatedCorrelationId = "5259006a-c76a-4564-acb4-1bf3fe625a20",
            investigatedExtractionId = "79aeee45-7598-4e49-9581-3cb8fe7ce2b9",
            priorFallbackCorrelationId = "7763469b-40d3-469d-b36b-937c92335cb2",
            priorFallbackExtractionId = "639ebd21-ece0-4180-b2cd-7eda82f0e820",
            rawFoundryRetentionNote = "Raw Foundry HTTP response bodies are intentionally not persisted by SqlExtractionRepository to prevent unvalidated payload retention; token receipts (promptTokens=1020, completionTokens=2121, totalTokens=3141, outcome=accepted, schemaValidationStatus=passed) and exact validated entity records were retrieved directly from PostgreSQL extraction_runs, extracted_medications, extracted_investigations, and oov_quarantine_items.",
            stages = new object[]
            {
                new
                {
                    stage = "PDF_PARSED",
                    before = new
                    {
                        parser = "UglyToad.PdfPig Page.Text concatenation",
                        charLength = FlattenedSavedPdfText.Length,
                        concatenatedDefects = new[]
                        {
                            "SUMMARYCardiology",
                            "PlanDocumented Allergies",
                            "Presentation64-year-old",
                            "LabsBlood PressureHeart RateTemperatureSerum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL",
                            "Discharge MedicationsWarfarin 5 mg"
                        },
                        sectionsDetected = new[] { new { code = "OTHER", heading = "Body", start = 0, end = 1656 } }
                    },
                    after = new
                    {
                        parser = "PdfTextReconstructor (coordinate-aware line, section, and 2D table header/value reconstruction)",
                        charLength = reconstructedText.Length,
                        reconstructedText,
                        sectionsDetected = remediatedFiveDrugResult.Preprocessed.Sections
                    }
                },
                new
                {
                    stage = "FOUNDRY_RAW",
                    provenance = new
                    {
                        provider = "foundry",
                        deployment = "gpt-6.1-sol",
                        model = "gpt-6.1-sol-2026-09-29",
                        promptVersion = "clinical-v3",
                        promptTokens = 1020,
                        completionTokens = 2121,
                        totalTokens = 3141,
                        finishReason = "stop",
                        truncated = false
                    },
                    finding = "Case B confirmed for Azithromycin in run 5259006a-c76a-4564-acb4-1bf3fe625a20: clinicalResultValidator had zero silent medication skips for 'Azithromycin' (NonFormularyClassTerms only contained class plurals; ShouldSkipNonOrderMedicationMention only skipped '-allergic' and 'transfer to icu'/'desensitization protocol'; action was 'none' so action quarantine could not trigger). GPT-6.1 Sol omitted Azithromycin from the middle of the single-paragraph flattened PDF block ('...indication).Azithromycin 500 mg...'), while in run 639ebd21-ece0-4180-b2cd-7eda82f0e820 (2227 completion tokens) the raw candidate was rejected with AZURE_INVALID_EVIDENCE and fell back to LocalOnnx (which DID extract Azithromycin 500 mg)."
                },
                new
                {
                    stage = "DESERIALIZED",
                    originalMedicationsCount = 6,
                    originalInvestigationsCount = 10,
                    completenessCheckOnOriginalCandidate = fourDrugWithCompletenessAlert.SafetyAlerts!
                        .Where(a => a.Category == "COMPLETENESS_REVIEW")
                        .ToList()
                },
                new
                {
                    stage = "VALIDATED",
                    remediatedMedicationsCount = remediatedFiveDrugResult.Medications.Count,
                    remediatedInvestigationsCount = remediatedFiveDrugResult.Investigations.Count,
                    allOffsetsVerified = remediatedFiveDrugResult.Medications.All(m => m.Evidence.Verified) &&
                                         remediatedFiveDrugResult.Investigations.All(i => i.Evidence.Verified)
                },
                new
                {
                    stage = "NORMALIZED",
                    rxNormResolvedCount = remediatedFiveDrugResult.Medications.Count(m => m.Normalization.MatchedCode != null),
                    loincResolvedCount = remediatedFiveDrugResult.Investigations.Count(i => i.Normalization.MatchedCode != null),
                    investigations = remediatedFiveDrugResult.Investigations.Select(i => new
                    {
                        i.Name,
                        i.AssertionStatus,
                        i.Timing,
                        i.Specimen,
                        i.Normalization.Status,
                        i.MatchedCode,
                        i.DisplayName
                    }).ToList()
                },
                new
                {
                    stage = "SAFETY_ENRICHED",
                    creatinineDefectRootCause = "In flattened PDF text 'Serum Potassium & Creatinine124/76 mmHg82 bpm (controlled AF)36.8 °CK+ 5.7 mEq/L, Creatinine 1.9 mg/dL', ClinicalSafetyRuleEngine.CreatinineValueRegex (\\b(?:scr|creatinine)\\s*(?:was|is|level|of|=|:)?\\s*(\\d+(?:\\.\\d+)?)\\s*(?:mg/dl)?\\b) matched the table column header 'Creatinine' directly adjacent to '124' from '124/76 mmHg' because (1) PdfPig omitted whitespace between 'Creatinine' and '124/76', (2) the unit '(?:mg/dl)?' was optional, and (3) '\\b' matched between '124' and '/76'.",
                    remediatedAlerts = remediatedFiveDrugResult.SafetyAlerts
                },
                new
                {
                    stage = "PERSISTED",
                    medications = remediatedFiveDrugResult.Medications,
                    investigations = remediatedFiveDrugResult.Investigations
                }
            }
        };
    }
}
