using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Tests.Evaluation;

namespace Apollo.Backend.Tests;

public class FoundryValidatorOncologyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string Root => Path.GetFullPath(Path.Combine(HeldOutBenchmarkRunner.Backend, ".."));
    internal static string AmlSource()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(HeldOutBenchmarkRunner.Backend, "tests/Evaluation/validator_fix_aml_source.json")));
        string source = fixture.RootElement.GetProperty("source").GetString()!;
        Assert.Equal(fixture.RootElement.GetProperty("sourceHash").GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))));
        Assert.Equal(35, fixture.RootElement.GetProperty("version").GetInt32());
        return source;
    }

    internal static CandidateMedication Med(string name, string quote, string? dose = null, string? unit = null,
        string? route = null, string? frequency = null, string? duration = null, string? formulation = null,
        string assertion = "historical", string action = "none", string? strength = null, string? instructions = null)
        => new(name, strength, dose, unit, route, frequency, duration, formulation, instructions, action, assertion, new(quote, 1));

    internal static ClinicalCandidate AmlCandidate(string source)
    {
        string Line(string text) => source.Split('\n').First(l => l.Contains(text, StringComparison.OrdinalIgnoreCase));
        string chemo = Line("Chemotherapy Administered");
        string follow = Line("Report to Day Care");
        return new([
            Med("Cytarabine", chemo, "100 mg/m2", "mg/m2", "IV", "continuous IV infusion", "Days 1-7"),
            Med("Daunorubicin", chemo, "60 mg/m2", "mg/m2", "IV", duration: "Days 1-3", instructions: "IV push (Days 1-3)"),
            Med("Midostaurin", chemo, route: "PO", frequency: "BID", duration: "Days 8-21", strength: "50 mg"),
            Med("Meropenem", Line("Complications"), "1 g", "g", "IV", "Q8H", "10 days"),
            Med("benzydamine", Line("Complications"), formulation: "mouthwash"),
            Med("Posaconazole", Line("Posaconazole"), route: "PO", frequency: "QD", formulation: "tablets", assertion: "current_medication", strength: "300 mg"),
            Med("Acyclovir", Line("Acyclovir"), route: "PO", frequency: "BID", assertion: "current_medication", strength: "400 mg"),
            Med("Levofloxacin", Line("Levofloxacin"), route: "PO", frequency: "QD", assertion: "current_medication", strength: "500 mg"),
            Med("Ondansetron", Line("Ondansetron"), route: "PO", frequency: "SOS", assertion: "current_medication", strength: "8 mg"),
            Med("Cytarabine", follow, assertion: "conditional")
        ], [
            new("bone marrow aspirate", null, null, "historical", "final", new(Line("Baseline bone marrow"), 1)),
            new("Cytogenetics", null, null, "historical", "final", new(Line("Cytogenetics:"), 1)),
            new("Blood cultures", null, "Blood", "historical", "final", new(Line("Complications"), 1)),
            new("Platelets", null, null, "historical", "final", new(Line("Count Recovery"), 1)),
            new("Hemoglobin", null, null, "historical", "final", new(Line("Count Recovery"), 1)),
            new("bone marrow biopsy", null, null, "historical", "final", new(Line("Count Recovery"), 1)),
            new("CBC", "01/12/2026 at 09:00 AM", null, "new_order", "preliminary", new(follow, 1)),
            new("renal/liver function panel", "01/12/2026 at 09:00 AM", null, "new_order", "preliminary", new(follow, 1))
        ]);
    }

    internal static IEnumerable<(string Id, string Source, ClinicalCandidate Candidate)> GroundedFixtures()
    {
        string source = AmlSource();
        yield return ("exact-source-representative-aml", source, AmlCandidate(source));
        foreach (var m in AmlCandidate(source).Medications)
            yield return ("aml-medication-" + m.Name + "-" + m.AssertionStatus, source, new([m], []));
        foreach (var i in AmlCandidate(source).Investigations)
            yield return ("aml-investigation-" + i.Name, source, new([], [i]));
        const string mouthwash = "Previously received benzydamine oral mouthwash SOS for mucositis.";
        yield return ("oral-mouthwash", mouthwash, new([Med("benzydamine", mouthwash, formulation: "oral mouthwash", frequency: "SOS")], []));
    }

    [Fact]
    public void OfflineReplay_ExactImmutableSource_AndRepresentativeOncologyCandidates()
    {
        bool baseline = Environment.GetEnvironmentVariable("APOLLO_VALIDATOR_BASELINE_CAPTURE") == "1";
        var results = new List<object>(); var failures = new List<string>();
        foreach (var fixture in GroundedFixtures())
        {
            try
            {
                var result = new ClinicalResultValidator().Map(fixture.Candidate, fixture.Source, "offline-representative");
                Assert.False(result.ValidationSummary.HasErrors);
                foreach (var evidence in result.Medications.Select(m => m.Evidence).Concat(result.Investigations.Select(i => i.Evidence)))
                    Assert.Equal(evidence.Quote, fixture.Source[evidence.Start!.Value..evidence.End!.Value]);
                results.Add(new { fixture.Id, status = "PASS", medications = result.Medications.Count, investigations = result.Investigations.Count });
            }
            catch (InvalidClinicalEvidenceException rejection)
            {
                failures.Add(fixture.Id + ": " + rejection.Diagnostic.RuleId);
                results.Add(new { fixture.Id, status = "REJECTED", diagnostic = rejection.Diagnostic });
            }
        }
        string directory = Path.Combine(Root, "docs/validator-fix-evidence"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, baseline ? "baseline/representative-replay.json" : "representative-replay-after.json");
        if (baseline && File.Exists(path)) throw new InvalidOperationException("Do not overwrite original replay evidence.");
        File.WriteAllText(path, JsonSerializer.Serialize(new { classification = "Synthetic representative candidates; not the unavailable original rejected GPT response", conditions = baseline ? "original validator conditions with diagnostic IDs only" : "upgraded validator", liveFoundryCalls = 0, results }, Json));
        if (baseline) Assert.NotEmpty(failures);
        else Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
