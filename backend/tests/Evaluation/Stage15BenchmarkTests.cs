using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Tests.Evaluation;

/// <summary>Real model baseline capture. Does not create services, dispatch messages or call Foundry.</summary>
public class Stage15BenchmarkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static string Root => Path.GetFullPath(Path.Combine(HeldOutBenchmarkRunner.Backend, ".."));

    [Fact]
    public async Task CaptureRealLocalOutputs()
    {
        string phase = Environment.GetEnvironmentVariable("APOLLO_STAGE15_PHASE") ?? "baseline";
        string output = Path.Combine(Root, "docs", "stage15-evidence", phase);
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "real-local-outputs.json");
        // Frozen baseline must never be silently replaced by a subsequent test run.
        if (File.Exists(path) && phase == "baseline") return;
        var watch = Stopwatch.StartNew();
        var tokenizer = new WordPieceTokenizer(Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts/bc5cdr_latest/clinical_tokenizer.json"));
        using var engine = new SlidingWindowOnnxEngine(Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts/bc5cdr_latest/onnx/model_int8.onnx"), tokenizer);
        double initializationMs = watch.Elapsed.TotalMilliseconds;
        var pipeline = new ClinicalPipelineService(engine, Options.Create(new LocalClinicalPipelineOptions { PipelineVersion = phase == "baseline" ? "Legacy" : "CandidateV2" }));
        var rows = new List<object>();
        foreach (string dataset in new[] { "stage5_independent_benchmark_35cases.json", "stage15_frozen_cases.json" })
        {
            string source = await File.ReadAllTextAsync(Path.Combine(HeldOutBenchmarkRunner.Backend, "tests/Evaluation", dataset));
            using var doc = JsonDocument.Parse(source);
            foreach (var c in doc.RootElement.EnumerateArray())
                rows.Add(Capture(c.GetProperty("id").GetString()!, c.GetProperty("note_text").GetString()!, dataset));
        }
        string amlPath = Path.Combine(Root,"docs/stage15-evidence/baseline/aml-v33-historical.json");
        if (File.Exists(amlPath))
        {
            using var aml = JsonDocument.Parse(await File.ReadAllTextAsync(amlPath));
            Assert.True(aml.RootElement.GetProperty("synthetic").GetBoolean());
            rows.Add(Capture("P100234-ENC-100567-v33", aml.RootElement.GetProperty("note_text").GetString()!, "original-synthetic-aml"));
        }
        object Capture(string id, string note, string dataset)
        {
            var rawReceipt = new OnnxExecutionReceipt();
            var raw = engine.InferSpans(note, rawReceipt);
            var receipt = new OnnxExecutionReceipt();
            watch.Restart();
            // Historical synthetic original is confirmed by the read-only export, not an API-supplied flag.
            var result = pipeline.ProcessPlatformExtraction(new(note), "stage15-"+id, receipt,
                skipPrivacyValidation: dataset == "original-synthetic-aml");
            return new { id, dataset, sourceSha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(note))),
                raw, receipt.WindowsExecuted, receipt.InferenceDurationMs, receipt.ReviewCandidates, receipt.CandidateTraces,
                receipt.TokensEvaluated, receipt.UnknownTokens,
                latencyMs=watch.Elapsed.TotalMilliseconds, result };
        }
        using var stream = new FileStream(path, phase == "baseline" ? FileMode.CreateNew : FileMode.Create);
        await JsonSerializer.SerializeAsync(stream, new { phase, initializationMs, liveFoundryCalls=0,
            modelSha256=OnnxArtifactIntegrity.ModelSha256, tokenizerSha256=OnnxArtifactIntegrity.TokenizerSha256,
            peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64, cases=rows }, Json);
        Assert.True(engine.HasOnnxModel);
    }
}
