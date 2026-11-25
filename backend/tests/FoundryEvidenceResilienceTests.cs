using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Security;
using Apollo.Backend.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Tests;

public class FoundryEvidenceResilienceTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RepairingFoundryStub(ClinicalCandidate first, ClinicalCandidate repaired) : IFoundryClinicalExtractor
    {
        public int ExtractCalls { get; private set; }
        public int RepairCalls { get; private set; }
        public ClinicalValidationDiagnosticDto? LastDiagnostic { get; private set; }

        public Task<FoundryExtraction> ExtractAsync(string source, CancellationToken ct)
        {
            ExtractCalls++;
            string raw = JsonSerializer.Serialize(first, ClinicalCandidateJson.Options);
            return Task.FromResult(new FoundryExtraction(
                first,
                new ProviderAttemptDto("foundry", "gpt-6.1-sol-2026-09-29", "gpt-6.1-sol", "accepted", null, 25, true, 120, 90, 210, HttpAttempts: 1),
                raw));
        }

        public Task<FoundryExtraction> RepairAsync(
            string source,
            string priorStructuredJson,
            ClinicalValidationDiagnosticDto diagnostic,
            CancellationToken ct)
        {
            RepairCalls++;
            LastDiagnostic = diagnostic;
            string raw = JsonSerializer.Serialize(repaired, ClinicalCandidateJson.Options);
            return Task.FromResult(new FoundryExtraction(
                repaired,
                new ProviderAttemptDto("foundry", "gpt-6.1-sol-2026-09-29", "gpt-6.1-sol", "accepted", null, 20, true, 140, 95, 235, HttpAttempts: 1),
                raw));
        }
    }

    private sealed class FailingLocalStub : ILocalClinicalExtractor
    {
        public string ProviderName => "LocalOnnx";
        public Task<ClinicalExtractionResponse> ExtractAsync(string source, string correlationId, CancellationToken ct)
            => throw new InvalidOperationException("Local ONNX must not be called when Foundry self-repair succeeds.");
    }

    [Fact]
    public void Validator_AcceptsAncRecoveredResult_ThousandsSeparatorVariants_AndMouthwashOrIvInfusionRoutes()
    {
        const string source = """
            HOSPITAL COURSE:
            - Chemotherapy Administered: Cytarabine 100 mg/m2 continuous IV infusion (Days 1-7).
            - Complications: Experienced Grade 2 oral mucositis managed with benzydamine mouthwash.
            - Count Recovery (Day 22): ANC recovered to 1,450/uL; unsupported Platelets 92,000/uL; Hemoglobin 9.6 g/dL.

            DISCHARGE MEDICATIONS:
            1. Levofloxacin 500 mg PO once daily until ANC > 1,500/uL on two consecutive checks.
            """;

        string Line(string needle) => source.Split('\n').First(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase)).Trim();

        var candidate = new ClinicalCandidate(
            [
                new("Cytarabine", null, "100 mg/m2", "mg/m2", "IV infusion", "continuous IV infusion", "Days 1-7", null, null, "none", "historical", new(Line("Cytarabine"), 1)),
                new("benzydamine", null, null, null, "mouthwash", null, null, "mouthwash", null, "none", "historical", new(Line("benzydamine"), 1)),
                new("Levofloxacin", "500 mg", null, null, "PO", "once daily", "until ANC > 1500/uL", null, "until ANC > 1500/uL on two consecutive checks", "none", "current_medication", new(Line("Levofloxacin"), 1)),
            ],
            [
                new("ANC", null, null, "historical", "final", new(Line("ANC recovered"), 1)),
                new("Platelets", null, null, "historical", "final", new(Line("Platelets"), 1)),
                new("Hemoglobin", null, null, "historical", "final", new(Line("Hemoglobin"), 1)),
            ]);

        var result = new ClinicalResultValidator().Map(candidate, source, "resilience-oncology");
        Assert.False(result.ValidationSummary.HasErrors);
        Assert.Equal(3, result.Medications.Count);
        Assert.Equal(3, result.Investigations.Count);
        Assert.Contains(result.Investigations, i => i.Name == "ANC" && i.AssertionStatus == "historical" && i.Status == "final");
    }

    [Fact]
    public async Task ExtractionWorkflow_SelfRepairsFoundryEvidenceMismatch_WithoutFallingBackToOnnx()
    {
        const string source = "DISCHARGE MEDICATIONS:\n1. Posaconazole delayed-release tablets 300 mg PO once daily.\nFOLLOW-UP INSTRUCTIONS:\nReport on 01/12/2026 at 09:00 AM for CBC.";
        string medLine = "1. Posaconazole delayed-release tablets 300 mg PO once daily.";
        string invLine = "Report on 01/12/2026 at 09:00 AM for CBC.";

        // Attempt 1 has an unsupported specimen on CBC ("serum" not in note) -> triggers INVESTIGATION_SPECIMEN_UNSUPPORTED
        var firstAttempt = new ClinicalCandidate(
            [new("Posaconazole", "300 mg", null, null, "PO", "once daily", null, "tablets", null, "none", "current_medication", new(medLine, 1))],
            [new("CBC", "01/12/2026 at 09:00 AM", "serum", "new_order", "preliminary", new(invLine, 1))]);

        // Repair attempt removes the unsupported specimen -> passes strict validator 100%
        var repairedAttempt = new ClinicalCandidate(
            [new("Posaconazole", "300 mg", null, null, "PO", "once daily", null, "tablets", null, "none", "current_medication", new(medLine, 1))],
            [new("CBC", "01/12/2026 at 09:00 AM", null, "new_order", "preliminary", new(invLine, 1))]);

        var stub = new RepairingFoundryStub(firstAttempt, repairedAttempt);
        var repo = new InMemoryExtractionRepository();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:LocalDemoMode"] = "true" }).Build();
        var privacy = new SyntheticDataPrivacyPolicy(new FakeEnv(), cfg, Options.Create(new SyntheticDataPrivacyOptions()));

        var workflow = new ExtractionWorkflow(
            stub,
            new FailingLocalStub(),
            new ClinicalResultValidator(),
            repo,
            Options.Create(new FoundryOptions
            {
                Endpoint = "https://offline-stub.services.ai.azure.com/openai/v1/",
                Deployment = "gpt-6.1-sol",
                DeploymentName = "gpt-6.1-sol",
                TargetModel = "gpt-6.1-sol",
                AuthenticationMode = "ApiKey",
                Authentication = "ApiKey",
                ApiKey = "offline-stub",
                MaxEvidenceRepairRetries = 2
            }),
            Options.Create(new ExtractionOptions { Provider = "MicrosoftFoundry", FallbackProvider = "LocalOnnx" }),
            NullLogger<ExtractionWorkflow>.Instance,
            new FoundryCircuitBreaker(TimeProvider.System),
            privacy);

        var result = await workflow.ExtractAndSaveAsync(source, "corr-self-repair", "text", null, CancellationToken.None);

        Assert.Equal(1, stub.ExtractCalls);
        Assert.Equal(1, stub.RepairCalls);
        Assert.Equal("INVESTIGATION_SPECIMEN_UNSUPPORTED", stub.LastDiagnostic?.RuleId);
        Assert.Equal("foundry", result.ModelMetadata.Provider);
        Assert.Equal("MicrosoftFoundry", result.ModelMetadata.FinalExecutedProvider);
        Assert.False(result.ModelMetadata.FallbackTriggered);
        Assert.Equal(2, result.ModelMetadata.Attempts!.Count);
        Assert.Equal("rejected", result.ModelMetadata.Attempts[0].Outcome);
        Assert.Equal("accepted", result.ModelMetadata.Attempts[1].Outcome);
        Assert.Single(result.Medications);
        Assert.Single(result.Investigations);
    }

    [Fact]
    public void MapResilient_AcceptsComplexGiOncologyNoteV39_WithQ14d_Split5FuDose_AndHistoricalDiagnostics_WhileStillBlockingPureHallucinations()
    {
        const string v39Note = """
            CHIEF COMPLAINT:
            Persistent fatigue, postprandial fullness, and elevated CEA on surveillance after adjuvant capecitabine for stage III colon cancer.

            HISTORY OF PRESENT ILLNESS:
            She completed laparoscopic sigmoid colectomy (1/18 lymph nodes positive), followed by 6 months of adjuvant Capecitabine 1250 mg/m2 PO twice daily (Days 1-14 of a 21-day cycle x 8 cycles), finishing in August 2025.
            Two weeks ago, routine surveillance showed rising CEA at 18.6 ng/mL (prior normal 2.4 ng/mL in May 2026).

            DIAGNOSTICS & IMAGING:
            - Labs (22/11/2026): Hemoglobin 11.4 g/dL, WBC 6,100/uL, Platelets 248,000/uL, Total Bilirubin 0.7 mg/dL, AST 38 U/L, ALT 41 U/L, ALP 162 U/L, Serum Creatinine 0.85 mg/dL.
            - Contrast-Enhanced CT Chest/Abdomen/Pelvis (23/11/2026): Two new hypodense liver lesions in segment IVa (2.4 cm) and segment VII (1.9 cm) with peripheral rim enhancement, suspicious for hepatic metastases.
            - Molecular / NGS Panel (24/11/2026): KRAS G12D mutant detected; NRAS, BRAF V600E, and HER2 amplified wild-type; MSI-H / dMMR absent (MSS / pMMR).

            ASSESSMENT & PLAN:
            2. Discontinue surveillance-only protocol; initiate first-line systemic chemotherapy with mFOLFOX6 + Bevacizumab q14d (Oxaliplatin 85 mg/m2 IV, Leucovorin 400 mg/m2 IV, 5-FU 400 mg/m2 IV bolus then 2400 mg/m2 continuous IV over 46h, Bevacizumab 5 mg/kg IV).
            3. Order baseline DPD (DPYD) genotype/phenotype check if 5-FU toxicity is flagged, and repeat CBC, LFTs, and Serum Creatinine prior to Cycle 1 Day 1.
            """;

        string Line(string s) => v39Note.Split('\n').First(l => l.Contains(s, StringComparison.OrdinalIgnoreCase)).Trim();
        string planChemo = Line("mFOLFOX6 + Bevacizumab");

        var candidate = new ClinicalCandidate(
            [
                new("Capecitabine", null, "1250 mg/m2", "mg/m2", "PO", "twice daily", "6 months", null, "Days 1-14 of a 21-day cycle x 8 cycles", "none", "historical", new(Line("adjuvant Capecitabine"), 1)),
                new("Oxaliplatin", null, "85 mg/m2", "mg/m2", "IV", "q14d", null, null, null, "start", "new_order", new(planChemo, 1)),
                new("Leucovorin", null, "400 mg/m2", "mg/m2", "IV", "q14d", null, null, null, "start", "new_order", new(planChemo, 1)),
                new("5-FU", null, "400 mg/m2 bolus then 2400 mg/m2", "mg/m2", "IV", "continuous IV over 46h", "46h", null, "400 mg/m2 IV bolus then 2400 mg/m2 continuous IV over 46h", "start", "new_order", new(planChemo, 1)),
                new("Bevacizumab", null, "5 mg/kg", "mg/kg", "IV", "q14d", null, null, null, "start", "new_order", new(planChemo, 1)),
            ],
            [
                new("CEA", null, null, "historical", "final", new(Line("elevated CEA on surveillance"), 1)),
                new("Contrast-Enhanced CT Chest/Abdomen/Pelvis", "23/11/2026", null, "historical", "final", new(Line("Contrast-Enhanced CT"), 1)),
                new("Molecular / NGS Panel", "24/11/2026", null, "historical", "final", new(Line("Molecular / NGS Panel"), 1)),
                new("DPD (DPYD) genotype/phenotype check", null, null, "conditional", "preliminary", new(Line("DPD (DPYD)"), 1)),
                new("CBC", "prior to Cycle 1 Day 1", null, "new_order", "preliminary", new(Line("repeat CBC"), 1)),
                new("LFTs", "prior to Cycle 1 Day 1", null, "new_order", "preliminary", new(Line("repeat CBC"), 1)),
                new("Serum Creatinine", "prior to Cycle 1 Day 1", null, "new_order", "preliminary", new(Line("repeat CBC"), 1)),
            ]);

        var validator = new ClinicalResultValidator();
        var result = validator.MapResilient(candidate, v39Note, "v39-gi-oncology");
        Assert.False(result.ValidationSummary.HasErrors);
        Assert.Equal(5, result.Medications.Count);
        Assert.Equal(7, result.Investigations.Count);
        Assert.Contains(result.Medications, m => m.Name == "Capecitabine" && m.Dose == "1250 mg/m2" && m.Frequency == "twice daily");
        Assert.Contains(result.Medications, m => m.Name == "Oxaliplatin" && m.Route == "IV" && m.QualityFlags.Contains("COMPLEX_REGIMEN_REVIEW"));
        Assert.Contains(result.Medications, m => m.Name == "Bevacizumab" && m.Route == "IV" && m.QualityFlags.Contains("COMPLEX_REGIMEN_REVIEW"));
        Assert.Contains(result.Investigations, i => i.Name == "CEA" && i.AssertionStatus == "historical");

        // Pure hallucination (non-verbatim quote) MUST still fail closed with InvalidClinicalEvidenceException
        var pureHallucination = new ClinicalCandidate(
            [new("Metformin", "500 mg", null, null, "PO", "daily", null, null, null, "start", "new_order", new("Invented quote never in note", 1))],
            []);
        Assert.Throws<InvalidClinicalEvidenceException>(() => validator.MapResilient(pureHallucination, v39Note, "pure-hallucination"));
    }
}
