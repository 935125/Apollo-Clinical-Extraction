using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Microsoft.Extensions.DependencyInjection;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class ReliabilityMeasurementTests
{
    [PostgresFact]
    public async Task RepeatedLocalRequests_RecordLatencyErrorsAndMemoryWithoutScalabilityClaims()
    {
        await using var database = new IsolatedDatabase(); await database.Initialize();
        using var factory = new FoundryFactory { PrimaryProvider = "LocalOnnx", DatabaseConnection = database.ConnectionString };
        using var client = factory.CreateClient();
        var engine = factory.Services.GetRequiredService<SlidingWindowOnnxEngine>();
        var samples = new ConcurrentBag<(int Index, string Phase, double RequestMs, double? InferenceMs, string? Error)>();
        var memory = new List<object>(); var process = Process.GetCurrentProcess(); var elapsed = Stopwatch.StartNew();
        void Memory(int completed)
        {
            process.Refresh(); memory.Add(new { completed, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64, managedBytes = GC.GetTotalMemory(false) });
        }
        async Task Request(int index, string phase)
        {
            var watch = Stopwatch.StartNew(); string? error = null; double? inference = null;
            try
            {
                using var response = await FoundryWorkflowTests.Submit(client, false);
                if (!response.IsSuccessStatusCode) error = "HTTP_" + (int)response.StatusCode;
                else
                {
                    var result = (await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
                    if (result.ModelMetadata.Provider != "onnx" || !result.Saved) error = "PROVENANCE_OR_SAVE";
                    inference = result.ModelMetadata.Attempts?.Single().InferenceDurationMs;
                }
            }
            catch (Exception ex) { error = ex.GetType().Name; }
            samples.Add((index, phase, watch.Elapsed.TotalMilliseconds, inference, error));
        }
        Memory(0);
        for (int n = 1; n <= 30; n++) { await Request(n, "sequential"); if (n % 5 == 0) Memory(n); }
        for (int batch = 0; batch < 3; batch++)
        {
            await Task.WhenAll(Enumerable.Range(31 + batch * 5, 5).Select(n => Request(n, "concurrent-five"))); Memory(35 + batch * 5);
        }
        object Metrics(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return new { sampleSize = sorted.Length, p50Ms = sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], p95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] };
        }
        string dir = Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "audit-remediation"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "reliability.json"), JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow, environment = "in-process test HTTP server, real singleton ONNX session, isolated real PostgreSQL; no cloud",
            limitation = "Short 45-request observation in a test-host process. Memory includes tests and GC. Does not establish leak freedom, capacity, production throughput or model speedup.",
            samples = samples.OrderBy(s => s.Index).Select(s => new { s.Index, s.Phase, s.RequestMs, s.InferenceMs, s.Error }), memory,
            fullRequest = Metrics(samples.Select(s => s.RequestMs)), sessionRunOnly = Metrics(samples.Where(s => s.InferenceMs.HasValue).Select(s => s.InferenceMs!.Value)),
            errors = samples.Count(s => s.Error != null), errorRate = samples.Count(s => s.Error != null) / 45.0,
            singletonReused = ReferenceEquals(engine, factory.Services.GetRequiredService<SlidingWindowOnnxEngine>())
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(0, factory.Azure.Calls); Assert.Equal(45, samples.Count); Assert.All(samples, s => Assert.Null(s.Error));
        Assert.Equal(45, await database.Count("extraction_runs"));
    }
}
