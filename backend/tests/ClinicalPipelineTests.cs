using Apollo.Backend.Clinical;
using Apollo.Backend.Fhir;
using Apollo.Backend.Models;

namespace Apollo.Backend.Tests;

public class ClinicalPipelineTests
{
    private readonly ContextEngine _contextEngine = new();
    private readonly PosologyBinder _posologyBinder = new();
    private readonly IndianOntologyResolver _ontologyResolver = new();

    [Fact]
    public void ContextEngine_LongRangeBackwardNegation_IdentifiesNegation()
    {
        string note = "Respiratory: Shortness of breath, hemoptysis, pleuritic pain, and nocturnal dyspnea were all explicitly denied.";
        var sections = _contextEngine.SegmentSections(note);

        // Find position of "shortness of breath"
        int sobStart = note.IndexOf("Shortness of breath", StringComparison.OrdinalIgnoreCase);
        int sobEnd = sobStart + "Shortness of breath".Length;

        var assertion = _contextEngine.Classify(note, sobStart, sobEnd, sections);
        Assert.Equal(ClinicalCertainty.Negated, assertion.Certainty);
        Assert.Equal(ClinicalExperiencer.Patient, assertion.Experiencer);
    }

    [Fact]
    public void ContextEngine_PseudoNegation_DoesNotNegate()
    {
        string note = "Chest examination revealed no change in murmur intensity, patient continues with chronic heart failure.";
        var sections = _contextEngine.SegmentSections(note);

        int chfStart = note.IndexOf("chronic heart failure", StringComparison.OrdinalIgnoreCase);
        int chfEnd = chfStart + "chronic heart failure".Length;

        var assertion = _contextEngine.Classify(note, chfStart, chfEnd, sections);
        Assert.Equal(ClinicalCertainty.Positive, assertion.Certainty);
    }

    [Fact]
    public void ContextEngine_FamilyHistorySection_AssignsFamilyExperiencer()
    {
        string note = "FAMILY HISTORY:\nMother had severe hypertension and breast cancer.";
        var sections = _contextEngine.SegmentSections(note);

        int htStart = note.IndexOf("hypertension", StringComparison.OrdinalIgnoreCase);
        int htEnd = htStart + "hypertension".Length;

        var assertion = _contextEngine.Classify(note, htStart, htEnd, sections);
        Assert.Equal(ClinicalExperiencer.Family, assertion.Experiencer);
    }

    [Fact]
    public void ContextEngine_FamilyCue_AssignsFamilyExperiencer()
    {
        string note = "Patient is healthy, but father was diagnosed with Parkinson disease at age 55.";
        var sections = _contextEngine.SegmentSections(note);

        int pdStart = note.IndexOf("Parkinson disease", StringComparison.OrdinalIgnoreCase);
        int pdEnd = pdStart + "Parkinson disease".Length;

        var assertion = _contextEngine.Classify(note, pdStart, pdEnd, sections);
        Assert.Equal(ClinicalExperiencer.Family, assertion.Experiencer);
    }

    [Fact]
    public void PosologyBinder_BindsAttributes_WithoutCrossingInterveningDrug()
    {
        string note = "1. Atorva 20 mg PO once daily for 30 days.\n2. Metformin 500 mg oral tablet BID with meals.";

        int atorvaStart = note.IndexOf("Atorva", StringComparison.OrdinalIgnoreCase);
        int atorvaEnd = atorvaStart + "Atorva".Length;

        int metStart = note.IndexOf("Metformin", StringComparison.OrdinalIgnoreCase);
        int metEnd = metStart + "Metformin".Length;

        var otherDrugs = new List<(int, int)> { (metStart, metEnd) };

        var bound = _posologyBinder.Bind(note, atorvaStart, atorvaEnd, otherDrugs);
        Assert.Equal("20 mg", bound.Strength);
        Assert.Null(bound.Dose);
        Assert.Equal("PO", bound.Route);
        Assert.Equal("QD", bound.Frequency);
        Assert.Equal("for 30 days", bound.Duration);
    }

    [Fact]
    public void IndianOntology_ResolvesIndianFormularyCrosswalk()
    {
        var atorva = _ontologyResolver.ResolveMedication("Atorva 20");
        Assert.Equal("uncertain", atorva.Status); // Atorva is not the Lipitor brand encoded by 617318.
        Assert.Equal("Atorvastatin", atorva.DisplayName);
        Assert.Null(atorva.StandardId);

        var thyronorm = _ontologyResolver.ResolveMedication("Thyronorm");
        Assert.Equal("uncertain", thyronorm.Status);
        Assert.Equal("Levothyroxine", thyronorm.DisplayName);
        Assert.Null(thyronorm.StandardId); // Brand name alone does not document a 125 mcg oral tablet.

        var niftas = _ontologyResolver.ResolveMedication("Niftas");
        Assert.Equal("uncertain", niftas.Status);
        Assert.Equal("Nitrofurantoin", niftas.DisplayName);
        Assert.Null(niftas.StandardId);

        var glycomet = _ontologyResolver.ResolveMedication("Glycomet");
        Assert.Equal("uncertain", glycomet.Status);
        Assert.Equal("Metformin", glycomet.DisplayName);
        Assert.Null(glycomet.StandardId); // Release form is not documented.

        var dolo = _ontologyResolver.ResolveMedication("Dolo 650");
        Assert.Equal("uncertain", dolo.Status);
        Assert.Equal("Paracetamol", dolo.DisplayName);
        Assert.Null(dolo.StandardId); // 198440 encodes 500 mg, not Dolo 650.
    }

    [Fact]
    public void IndianOntology_ResolvesDiagnosticPanels()
    {
        var cue = _ontologyResolver.ResolveInvestigation("CUE");
        Assert.Equal("matched", cue.Status);
        Assert.Equal("24356-8", cue.StandardId);

        var urineCs = _ontologyResolver.ResolveInvestigation("Urine Culture & Sensitivity");
        Assert.Equal("uncertain", urineCs.Status);
        Assert.Null(urineCs.StandardId); // Culture identification alone does not encode susceptibility.

        var rft = _ontologyResolver.ResolveInvestigation("RFT");
        Assert.Equal("uncertain", rft.Status);
        Assert.Null(rft.StandardId);

        var lipid = _ontologyResolver.ResolveInvestigation("Fasting Lipid Profile");
        Assert.Equal("uncertain", lipid.Status);
        Assert.Null(lipid.StandardId);
    }

    [Fact]
    public void MicroCardBuilder_ConstructsExpectedCompressedFormat()
    {
        string note = "58 yo male with Hypertension. Prescribed Metformin 500 mg PO BID. Ordered CUE, RFT.";
        var entities = new List<ClinicalEntityResponse>
        {
            new("CONDITION", "Hypertension", 16, 28, 0.95, "PRESENT", "Positive", "Current", "Patient", "BODY"),
            new("MEDICATION", "Metformin", 41, 50, 0.98, "PRESENT", "Positive", "Current", "Patient", "BODY"),
            new("LAB", "CUE", 74, 77, 0.92, "PRESENT", "Positive", "Current", "Patient", "BODY", "LOINC:24356-8"),
            new("LAB", "RFT", 79, 82, 0.94, "PRESENT", "Positive", "Current", "Patient", "BODY", "LOINC:24362-6")
        };

        var structuredMeds = new List<StructuredMedicationResponse>
        {
            new("Metformin", [41, 50], "500 mg", "500 mg", "BID", "PO", null, "Positive", "Patient")
        };

        string card = MicroCardBuilder.Build(note, entities, structuredMeds);
        Assert.StartsWith("[PT 58M]", card);
        Assert.Contains("DX:Hypertension", card);
        Assert.Contains("RX:Metformin 500 mg", card);
        Assert.Contains("LAB:CUE, RFT", card);
    }

    [Fact]
    public void FhirR4Mapper_CreatesPreliminaryObservationForUnvaluedLabOrders()
    {
        string note = "Ordered CUE and Fasting Lipid Profile for evaluation.";
        var entities = new List<ClinicalEntityResponse>
        {
            new("LAB", "CUE", 8, 11, 0.95, "PRESENT", "Positive", "Current", "Patient", "LABS", "LOINC:24356-8", "Complete Urine Examination"),
            new("LAB", "Fasting Lipid Profile", 16, 37, 0.95, "PRESENT", "Positive", "Current", "Patient", "LABS", "LOINC:24331-1", "Lipid Profile")
        };

        var bundle = FhirR4Mapper.ToFhirBundle(note, entities, [], "PAT-12345");
        Assert.Equal("Bundle", bundle["resourceType"]);

        var entries = (List<Dictionary<string, object?>>)bundle["entry"]!;
        Assert.Equal(2, entries.Count);

        foreach (var entry in entries)
        {
            var res = (Dictionary<string, object?>)entry["resource"]!;
            Assert.Equal("Observation", res["resourceType"]);
            Assert.Equal("preliminary", res["status"]); // Preliminary for unvalued lab orders
        }
    }

    [Fact]
    public void OovQuarantine_LogsThreadSafeAndRedactsPhi()
    {
        string testLogPath = Path.Combine(Path.GetTempPath(), $"oov_test_{Guid.NewGuid():N}.jsonl");
        try
        {
            string note = "Contact phone: +91 9876543210. Patient took unapproved drug XYZ-Pharma99 for severe illness.";
            int drugStart = note.IndexOf("XYZ-Pharma99");
            int drugEnd = drugStart + "XYZ-Pharma99".Length;

            _ontologyResolver.QuarantineOov("MEDICATION", "XYZ-Pharma99", drugStart, drugEnd, note, testLogPath);

            Assert.True(File.Exists(testLogPath));
            string content = File.ReadAllText(testLogPath);

            Assert.Contains("XYZ-Pharma99", content);
            Assert.Contains("fail_closed_oov", content);
            // PHI (+91 9876543210) MUST be redacted in logged context
            Assert.DoesNotContain("9876543210", content);
            Assert.Contains("[PHONE]", content);
        }
        finally
        {
            if (File.Exists(testLogPath))
            {
                File.Delete(testLogPath);
            }
        }
    }

    [Fact]
    public void PlatformExtraction_EliminatesStopwords_RetainsFullPosology_AndExtractsInvestigations()
    {
        var tokenizer = new Apollo.Backend.Nlp.WordPieceTokenizer();
        using var engine = new Apollo.Backend.Nlp.SlidingWindowOnnxEngine(tokenizer: tokenizer, allowDeterministicTestScoring: true);
        var pipeline = new ClinicalPipelineService(engine);

        string note = """
            62-year-old male with history of fever and productive cough.
            Plan:
            • Start amoxicillin 125 mg PO BID for 7 days.
            Investigations:
            • Chest X-ray to rule out consolidation.
            • CBC, CRP, RFT and CUE today.
            """;

        var result = pipeline.ProcessPlatformExtraction(new ClinicalExtractionRequest(note), "test-corr-id");

        // 1. Stopwords ("of", "for", "X", "to") are NEVER emitted in "medications"
        string[] forbiddenStopwords = ["of", "for", "X", "to", "in", "on", "at", "by", "with", "and", "or", "the", "a"];
        foreach (var sw in forbiddenStopwords)
        {
            Assert.DoesNotContain(result.Medications, m => m.Name.Equals(sw, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Strength is not duplicated into administered dose, and a strength mismatch is fail-closed.
        var amox = Assert.Single(result.Medications);
        Assert.Equal("amoxicillin", amox.Name, ignoreCase: true);
        Assert.Null(amox.Dose);
        Assert.Equal("125 mg", amox.Strength);
        Assert.Equal("PO", amox.Route);
        Assert.Equal("BID", amox.Frequency);
        Assert.Equal("for 7 days", amox.Duration);
        Assert.Equal("matched", amox.Normalization.Status);
        Assert.Equal("723", amox.Normalization.MatchedCode); // Ingredient only, independent of product strength.
        Assert.Equal(result.Medications.Sum(m => m.Findings.Count(f => f.Severity == "warning")) +
            result.Investigations.Sum(i => i.Findings.Count(f => f.Severity == "warning")), result.ValidationSummary.WarningCount);
        Assert.Equal(0, result.ValidationSummary.ErrorCount);
        Assert.False(result.ValidationSummary.HasErrors);

        // 3. Items under "Investigations:" populate "investigations" with valid LOINC codes and character offsets
        Assert.NotEmpty(result.Investigations);
        Assert.Equal(5, result.Investigations.Count);

        var cxr = Assert.Single(result.Investigations, i => i.Name.Equals("Chest X-ray", StringComparison.OrdinalIgnoreCase));
        Assert.Null(cxr.Normalization.MatchedCode); // Generic chest X-ray does not document two views.
        Assert.Equal("preliminary", cxr.Status);
        Assert.True(cxr.Evidence.Verified);
        Assert.NotNull(cxr.Evidence.Start);
        Assert.NotNull(cxr.Evidence.End);
        Assert.Equal(cxr.Evidence.Quote, note[cxr.Evidence.Start.Value..cxr.Evidence.End.Value]);
        Assert.Contains("Chest X-ray", cxr.Evidence.Quote);
        Assert.Equal("new_order", cxr.AssertionStatus); // "to rule out" is an indication, not a conditional order.

        var cbc = Assert.Single(result.Investigations, i => i.Name.Equals("CBC", StringComparison.OrdinalIgnoreCase));
        Assert.Null(cbc.Normalization.MatchedCode); // Automated count method is not documented.
        Assert.Equal("Complete Blood Count", cbc.Normalization.DisplayName);
        Assert.Equal("preliminary", cbc.Status);

        var crp = Assert.Single(result.Investigations, i => i.Name.Equals("CRP", StringComparison.OrdinalIgnoreCase));
        Assert.Null(crp.Normalization.MatchedCode);

        var rft = Assert.Single(result.Investigations, i => i.Name.Equals("RFT", StringComparison.OrdinalIgnoreCase));
        Assert.Null(rft.Normalization.MatchedCode);

        var cue = Assert.Single(result.Investigations, i => i.Name.Equals("CUE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("24356-8", cue.Normalization.MatchedCode);
    }

    [Fact]
    public void SlidingWindowOnnxEngine_StopwordBlocklist_RejectsAllMandatoryNonDrugTokens()
    {
        string[] mandatoryStopwords =
        [
            "of", "for", "to", "in", "on", "at", "by", "with", "from", "as",
            "is", "was", "were", "are", "be", "been", "and", "or", "but", "not", "no",
            "the", "a", "an", "this", "that", "it", "he", "she", "they", "we", "you",
            "mg", "ml", "mcg", "tab", "cap", "inj", "syp", "po", "iv", "im",
            "od", "bd", "bid", "tid", "qid", "qd", "hs", "prn", "stat",
            "x", "rx", "dx", "hx"
        ];

        foreach (var word in mandatoryStopwords)
        {
            Assert.False(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan(word), $"Expected stopword '{word}' to be rejected.");
            Assert.False(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan(word.ToUpperInvariant()), $"Expected uppercase stopword '{word.ToUpperInvariant()}' to be rejected.");
        }

        Assert.False(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan("A"));
        Assert.False(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan("125"));
        Assert.False(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan("---"));
        Assert.True(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan("amoxicillin"));
        Assert.True(Apollo.Backend.Nlp.SlidingWindowOnnxEngine.IsValidMedicationSpan("amoxicillin-clavulanate"));
    }
}
