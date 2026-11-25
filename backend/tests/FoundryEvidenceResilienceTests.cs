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
}
