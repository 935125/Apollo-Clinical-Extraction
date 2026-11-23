using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Tests.Evaluation;

public class Stage7DevelopmentBenchmarkTests
{
    [Fact]
    public async Task Stage7_RealModel_Rules_AndFinalApi_AreMeasuredSeparately()
    {
        string backend = HeldOutBenchmarkRunner.Backend;
        var tokenizer = new WordPieceTokenizer(Path.Combine(backend, "artifacts", "bc5cdr_latest", "clinical_tokenizer.json"));
        using var model = new SlidingWindowOnnxEngine(Path.Combine(backend, "artifacts", "bc5cdr_latest", "onnx", "model_int8.onnx"), tokenizer);
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx" }; using var client = factory.CreateClient();
        using var dataset = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backend, "tests", "Evaluation", "stage5_independent_benchmark_35cases.json")));
        var rows = new List<object>();
        var rawScores = new List<EntityEvaluation>(); var ruleMedScores = new List<EntityEvaluation>(); var ruleInvScores = new List<EntityEvaluation>();
        var finalMedScores = new List<EntityEvaluation>(); var finalInvScores = new List<EntityEvaluation>();
        foreach (var c in dataset.RootElement.EnumerateArray())
        {
            string source = c.GetProperty("note_text").GetString()!;
            var receipt = new OnnxExecutionReceipt(); var raw = model.InferSpans(source, receipt); Assert.True(receipt.WindowsExecuted > 0);
            using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(source)); response.EnsureSuccessStatusCode();
            var final = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
            Assert.True(final.ModelMetadata.InferenceExecuted); Assert.Equal("NEURAL_ONNX", final.ModelMetadata.ExecutionMode);
            Assert.All(final.ModelMetadata.Attempts!, a => Assert.True(a.OnnxWindows > 0));
            var ruleOnly = final with
            {
                Medications = final.Medications.Where(m => m.Origin is "RULE_EXTRACTED" or "ONNX_AND_RULE_CONFIRMED").ToList(),
                Investigations = final.Investigations.Where(i => i.Origin == "RULE_EXTRACTED").ToList()
            };
            var goldM = c.GetProperty("expected_medications").EnumerateArray().ToArray(); var goldI = c.GetProperty("expected_investigations").EnumerateArray().ToArray();
            List<ClinicalEvaluator.Prediction> Inv(ClinicalExtractionResponse r) => r.Investigations.Select(i => new ClinicalEvaluator.Prediction(i.Name,
                new() { ["timing"] = i.Timing, ["specimen"] = i.Specimen, ["clinical_category"] = i.ClinicalCategory, ["assertion_status"] = i.AssertionStatus }, i.Evidence)).ToList();
            var rm = ClinicalEvaluator.Evaluate(source, goldM, raw.Where(s => s.Category == "MEDICATION").Select(s => new ClinicalEvaluator.Prediction(s.Text, new(), new(s.Text, true, s.StartChar, s.EndChar))).ToList());
            var dm = ClinicalEvaluator.Evaluate(source, goldM, ClinicalEvaluator.Medications(ruleOnly)); var di = ClinicalEvaluator.Evaluate(source, goldI, Inv(ruleOnly));
            var fm = ClinicalEvaluator.Evaluate(source, goldM, ClinicalEvaluator.Medications(final)); var fi = ClinicalEvaluator.Evaluate(source, goldI, Inv(final));
            rawScores.Add(rm); ruleMedScores.Add(dm); ruleInvScores.Add(di); finalMedScores.Add(fm); finalInvScores.Add(fi);
            rows.Add(new { id = c.GetProperty("id").GetString(), source, goldMedications = goldM, goldInvestigations = goldI,
                rawModelSpans = raw, receipt, deterministicEntityRecovery = ruleOnly, finalApiResult = final,
                medicationEvaluation = fm, investigationEvaluation = fi });
        }
        Recognition Sum(List<EntityEvaluation> scores) => new(scores.Sum(s => s.Normalized.Tp), scores.Sum(s => s.Normalized.Fp), scores.Sum(s => s.Normalized.Fn));
        Dictionary<string, Accuracy> Attributes(List<EntityEvaluation> scores) => scores.SelectMany(s => s.Attributes).GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => new Accuracy(g.Sum(p => p.Value.Correct), g.Sum(p => p.Value.Evaluated)));
        string directory = Path.Combine(backend, "..", "docs", "stage7-evidence"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "stage7-development-results.json"), JsonSerializer.Serialize(new
        {
            classification = "Development regression; not independently clinically validated", caseCount = rows.Count, billableFoundryCalls = factory.Azure.Calls,
            modelSha256 = OnnxArtifactIntegrity.ModelSha256, tokenizerSha256 = OnnxArtifactIntegrity.TokenizerSha256,
            rawModelMedications = Sum(rawScores), rawModelInvestigations = "UNSUPPORTED: no investigation label",
            deterministicMedicationRecovery = Sum(ruleMedScores), deterministicInvestigations = Sum(ruleInvScores),
            deterministicMeasurement = "Subset with catalog/rule origin, including model-confirmed overlaps; not an independent additive or neural-free attribute experiment",
            finalMedications = Sum(finalMedScores), finalInvestigations = Sum(finalInvScores),
            medicationAttributes = Attributes(finalMedScores), investigationAttributes = Attributes(finalInvScores), cases = rows
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Assert.Equal(0, factory.Azure.Calls); Assert.Equal(35, rows.Count);
    }
}
