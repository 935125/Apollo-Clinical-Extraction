using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class FinalEvaluationFactAttribute : FactAttribute
{
    public FinalEvaluationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FINAL_EVALUATION_MANIFEST") == null)
            Skip = "Final assessment requires a separately sealed dataset and implementation freeze manifest. No independent clinical annotation is available.";
    }
}

// Historical class name retained for tooling. The original cases are development/regression data.
public sealed class HeldOutBenchmarkRunner(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string Backend => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    [Fact]
    public Task Benchmark_LocalOnnx_DevelopmentCases() => Run("LocalOnnx", Path.Combine(Backend, "tests", "Evaluation", "held_out_cases.json"), false);
    [FoundryLiveFact]
    public Task Benchmark_MicrosoftFoundry_DevelopmentCases() => Run("MicrosoftFoundry", Path.Combine(Backend, "tests", "Evaluation", "held_out_cases.json"), false);
    [Fact]
    public Task Replay_SavedFoundry_DevelopmentCases() => ReplaySavedFoundry(Path.Combine(Backend, "tests", "Evaluation", "held_out_cases.json"));
    [FinalEvaluationFact]
    public async Task SealedFinalEvaluation_OnceAfterImplementationFreeze()
    {
        string manifestPath = Environment.GetEnvironmentVariable("FINAL_EVALUATION_MANIFEST")!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)); var m = manifest.RootElement;
        string dataset = m.GetProperty("dataset").GetString()!;
        Assert.Equal(m.GetProperty("datasetSha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataset))));
        foreach (var file in m.GetProperty("implementation").EnumerateArray())
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.GetProperty("path").GetString()!))));
        await using (var marker = new FileStream(manifestPath + ".consumed", FileMode.CreateNew))
            await marker.WriteAsync(System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
        await Run("LocalOnnx", dataset, true);
    }
    private async Task Run(string provider, string dataset, bool final)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(dataset)); var cases = data.RootElement.EnumerateArray().ToArray();
        if (provider == "MicrosoftFoundry")
            Assert.True(int.TryParse(Environment.GetEnvironmentVariable("CLINICAL_LIVE_MAX_REQUESTS"), out int maxRequests) && maxRequests >= cases.Length,
                "Set CLINICAL_LIVE_MAX_REQUESTS before live evaluation. Retries and fallback are disabled.");
        using var budget = provider == "MicrosoftFoundry" ? new FoundryEvaluationBudget(Path.Combine(Backend, "artifacts", "audit-remediation", "foundry-development-ledger.jsonl")) : null;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtractionRepository>(); services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
            services.PostConfigure<ExtractionOptions>(o => { o.Provider = provider; o.FallbackProvider = "None"; });
            services.PostConfigure<FoundryOptions>(o => { o.AllowLocalFallback = false; o.MaxRetries = 0; if (budget != null) o.MaxCompletionTokens = 2000; });
            if (budget != null) services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>().AddHttpMessageHandler(() => new BudgetedFoundryHandler(budget));
        }));
        using var client = factory.CreateClient(); client.Timeout = TimeSpan.FromMinutes(6);
        var rows = new List<object>(); var medicationMetrics = new List<EntityEvaluation>(); var investigationMetrics = new List<EntityEvaluation>();
        var rawMedMetrics = new List<EntityEvaluation>(); var rawInvMetrics = new List<EntityEvaluation>();
        var latencies = new List<double>(); int errors = 0;
        foreach (var c in cases)
        {
            string id = c.GetProperty("id").GetString()!, source = c.GetProperty("note_text").GetString()!;
            var watch = Stopwatch.StartNew(); ClinicalExtractionResponse? actual = null; string? failure = null;
            int callsBefore = budget?.Calls ?? 0;
            try
            {
                if (budget?.Stopped == true) throw new InvalidOperationException("Evaluation stopped before dispatch");
                using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(source));
                if (response.IsSuccessStatusCode)
                {
                    actual = await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
                    string expected = provider == "LocalOnnx" ? "onnx" : "foundry";
                    if (actual?.ModelMetadata.Provider != expected || actual.ModelMetadata.InferenceExecuted != true ||
                        actual.ModelMetadata.FallbackReason != null || actual.ModelMetadata.Attempts?.Count != 1 ||
                        actual.ModelMetadata.Attempts[0].Provider != expected || actual.ModelMetadata.Attempts[0].InferenceExecuted != true)
                    { failure = "PROVENANCE_REJECTED"; actual = null; }
                }
                else failure = "HTTP_" + (int)response.StatusCode;
            }
            catch (Exception ex) { failure = ex.GetType().Name; }
            watch.Stop(); latencies.Add(watch.Elapsed.TotalMilliseconds); if (failure != null) errors++;
            var med = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_medications").EnumerateArray().ToArray(), actual == null ? [] : ClinicalEvaluator.Medications(actual));
            var inv = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), actual == null ? [] : ClinicalEvaluator.Investigations(actual));
            medicationMetrics.Add(med); investigationMetrics.Add(inv);
            EntityEvaluation? rawMed = null, rawInv = null;
            if (budget != null)
            {
                ClinicalCandidate? raw = null;
                try
                {
                    using var envelope = JsonDocument.Parse(budget.Calls > callsBefore ? budget.LastRawResponse ?? "{}" : "{}");
                    raw = JsonSerializer.Deserialize<ClinicalCandidate>(envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!, ClinicalCandidateJson.Options);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { }
                rawMed = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_medications").EnumerateArray().ToArray(), raw == null ? [] : ClinicalEvaluator.RawMedications(raw, source));
                rawInv = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), raw == null ? [] : ClinicalEvaluator.RawInvestigations(raw, source));
                rawMedMetrics.Add(rawMed); rawInvMetrics.Add(rawInv);
            }
            rows.Add(new { id, requestedProvider = provider, failure, latencyMs = watch.Elapsed.TotalMilliseconds,
                provenance = actual?.ModelMetadata, medication = med, investigation = inv, actual, rawMedication = rawMed, rawInvestigation = rawInv });
        }
        object Summary(List<EntityEvaluation> items)
        {
            object Recognize(Func<EntityEvaluation, Recognition> get)
            {
                var values = items.Select(get).ToArray();
                return new { micro = new Recognition(values.Sum(v => v.Tp), values.Sum(v => v.Fp), values.Sum(v => v.Fn)),
                    macro = new { precision = values.Average(v => v.Precision), recall = values.Average(v => v.Recall), f1 = values.Average(v => v.F1) } };
            }
            Dictionary<string, Accuracy> Aggregate(Func<EntityEvaluation, Dictionary<string, Accuracy>> get) => items.SelectMany(i => get(i)).GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => new Accuracy(g.Sum(v => v.Value.Correct), g.Sum(v => v.Value.Evaluated)));
            Accuracy Sum(Func<EntityEvaluation, Accuracy> get) => new(items.Sum(i => get(i).Correct), items.Sum(i => get(i).Evaluated));
            var full = items.Where(i => i.FullPrescription != null).Select(i => i.FullPrescription!).ToArray();
            return new { exact = Recognize(i => i.Exact), normalized = Recognize(i => i.Normalized),
                fullPrescription = full.Length == 0 ? null : new Recognition(full.Sum(v => v.Tp), full.Sum(v => v.Fp), full.Sum(v => v.Fn)),
                fullPrescriptionEvaluableCases = full.Length,
                attributes = Aggregate(i => i.Attributes), documentedAttributes = Aggregate(i => i.DocumentedAttributes), missingAttributes = Aggregate(i => i.MissingAttributes),
                attributeMacro = items.SelectMany(i => i.Attributes).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Average(v => v.Value.Rate)),
                evidenceQuote = Sum(i => i.EvidenceQuote), characterOffset = Sum(i => i.CharacterOffset), unsupportedEntityRate = Sum(i => i.UnsupportedEntity),
                conditionalOrder = Sum(i => i.ConditionalOrder), ambiguousGoldMentions = items.Sum(i => i.AmbiguousGoldMentions) };
        }
        double Percentile(double p) => latencies.Order().ElementAt(Math.Clamp((int)Math.Ceiling(p * latencies.Count) - 1, 0, latencies.Count - 1));
        string dir = Path.Combine(Backend, "artifacts", "audit-remediation", final ? "final" : "development"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, provider.ToLowerInvariant() + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
        var report = new { generatedAt = DateTimeOffset.UtcNow, classification = final ? "synthetic preliminary, single-use post-freeze assessment; not independently clinically annotated" : "synthetic development/regression; original gold preserved with known omissions",
            datasetSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataset))), evaluatorVersion = "audit-v1", provider,
            assemblyMvid = typeof(ClinicalResultValidator).Assembly.ManifestModule.ModuleVersionId, promptVersion = "clinical-v3", fallback = false, retries = 0,
            totalCases = cases.Length, errors, latency = new { measurement = "in-process full API request including persistence stub; not pure inference", sampleSize = cases.Length, p50Ms = Percentile(.5), p95Ms = Percentile(.95), errorRate = (double)errors / cases.Length },
            liveBudget = budget == null ? null : new { budget.Calls, budget.ChargedOrReservedUsd, budget.Stopped, budget.Receipts },
            rawFoundry = budget == null ? null : new { medications = Summary(rawMedMetrics), investigations = Summary(rawInvMetrics) },
            medications = Summary(medicationMetrics), investigations = Summary(investigationMetrics), cases = rows };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));
        output.WriteLine("Evaluation retained every case: " + path);
        Assert.Equal(0, errors); // Engineering execution check, not clinical-quality certification.
    }

    private sealed class SavedFoundryReplayHandler(IReadOnlyDictionary<int, string> responsesByIndex) : HttpMessageHandler
    {
        private int callIndex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            int idx = Interlocked.Increment(ref callIndex);
            if (!responsesByIndex.TryGetValue(idx, out var rawJson))
                throw new InvalidOperationException($"Missing saved Foundry response for replay index {idx}.");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(rawJson, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private async Task ReplaySavedFoundry(string dataset)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(dataset));
        var cases = data.RootElement.EnumerateArray().ToArray();
        string ledgerPath = Path.Combine(Backend, "artifacts", "audit-remediation", "foundry-development-ledger.jsonl");
        Assert.True(File.Exists(ledgerPath), "Saved Foundry ledger not found: " + ledgerPath);

        var responsesByIndex = new Dictionary<int, string>();
        var receipts = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(ledgerPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("syntheticRawResponse", out var rawProp) && root.TryGetProperty("index", out var idxProp))
            {
                int idx = idxProp.GetInt32();
                responsesByIndex[idx] = rawProp.GetString()!;
                receipts.Add(root.Clone());
            }
        }
        Assert.Equal(cases.Length, responsesByIndex.Count);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IExtractionRepository>();
            services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
            services.PostConfigure<ExtractionOptions>(o => { o.Provider = "MicrosoftFoundry"; o.FallbackProvider = "None"; });
            services.PostConfigure<FoundryOptions>(o =>
            {
                o.Endpoint = "https://synthetic.openai.azure.com/openai/v1/";
                o.DeploymentName = "gpt-6.1-sol";
                o.Deployment = "gpt-6.1-sol";
                o.TargetModel = "gpt-6.1-sol";
                o.AuthenticationMode = "ApiKey";
                o.ApiKey = "offline-replay-key";
                o.AllowLocalFallback = false;
                o.MaxRetries = 0;
            });
            services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>()
                .ConfigurePrimaryHttpMessageHandler(() => new SavedFoundryReplayHandler(responsesByIndex));
        }));

        using var client = factory.CreateClient();
        var rows = new List<object>();
        var medicationMetrics = new List<EntityEvaluation>();
        var investigationMetrics = new List<EntityEvaluation>();
        var rawMedMetrics = new List<EntityEvaluation>();
        var rawInvMetrics = new List<EntityEvaluation>();
        var latencies = new List<double>();
        var csvLines = new List<string>
        {
            "case_id,specialty,status,med_exact_tp,med_exact_fp,med_exact_fn,med_norm_tp,med_norm_fp,med_norm_fn,inv_exact_tp,inv_exact_fp,inv_exact_fn,inv_norm_tp,inv_norm_fp,inv_norm_fn,strength_correct,strength_eval,route_correct,route_eval,freq_correct,freq_eval,dur_correct,dur_eval,assert_correct,assert_eval"
        };
        int errors = 0;

        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            int idx = i + 1;
            string id = c.GetProperty("id").GetString()!;
            string specialty = c.GetProperty("specialty").GetString()!;
            string source = c.GetProperty("note_text").GetString()!;
            var watch = Stopwatch.StartNew();
            ClinicalExtractionResponse? actual = null;
            string? failure = null;
            try
            {
                using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest(source));
                if (response.IsSuccessStatusCode)
                {
                    actual = await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
                    if (actual?.ModelMetadata.Provider != "foundry" || actual.ModelMetadata.InferenceExecuted != true ||
                        actual.ModelMetadata.FallbackReason != null)
                    {
                        failure = "PROVENANCE_REJECTED";
                        actual = null;
                    }
                }
                else
                {
                    failure = "HTTP_" + (int)response.StatusCode;
                }
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name;
            }
            watch.Stop();
            latencies.Add(watch.Elapsed.TotalMilliseconds);
            if (failure != null) errors++;

            var med = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_medications").EnumerateArray().ToArray(), actual == null ? [] : ClinicalEvaluator.Medications(actual));
            var inv = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), actual == null ? [] : ClinicalEvaluator.Investigations(actual));
            medicationMetrics.Add(med);
            investigationMetrics.Add(inv);

            ClinicalCandidate? raw = null;
            try
            {
                using var envelope = JsonDocument.Parse(responsesByIndex[idx]);
                raw = JsonSerializer.Deserialize<ClinicalCandidate>(envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!, ClinicalCandidateJson.Options);
            }
            catch { }
            var rawMed = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_medications").EnumerateArray().ToArray(), raw == null ? [] : ClinicalEvaluator.RawMedications(raw, source));
            var rawInv = ClinicalEvaluator.Evaluate(source, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), raw == null ? [] : ClinicalEvaluator.RawInvestigations(raw, source));
            rawMedMetrics.Add(rawMed);
            rawInvMetrics.Add(rawInv);

            Accuracy Attr(Dictionary<string, Accuracy> dict, string key) => dict.GetValueOrDefault(key, new(0, 0));
            var strAcc = Attr(med.Attributes, "strength");
            var routeAcc = Attr(med.Attributes, "route");
            var freqAcc = Attr(med.Attributes, "frequency");
            var durAcc = Attr(med.Attributes, "duration");
            var assertAcc = Attr(med.Attributes, "assertion_status");

            csvLines.Add($"{id},\"{specialty}\",{failure ?? "ACCEPTED"},{med.Exact.Tp},{med.Exact.Fp},{med.Exact.Fn},{med.Normalized.Tp},{med.Normalized.Fp},{med.Normalized.Fn},{inv.Exact.Tp},{inv.Exact.Fp},{inv.Exact.Fn},{inv.Normalized.Tp},{inv.Normalized.Fp},{inv.Normalized.Fn},{strAcc.Correct},{strAcc.Evaluated},{routeAcc.Correct},{routeAcc.Evaluated},{freqAcc.Correct},{freqAcc.Evaluated},{durAcc.Correct},{durAcc.Evaluated},{assertAcc.Correct},{assertAcc.Evaluated}");

            rows.Add(new
            {
                id,
                requestedProvider = "MicrosoftFoundry",
                failure,
                latencyMs = watch.Elapsed.TotalMilliseconds,
                provenance = actual?.ModelMetadata,
                medication = med,
                investigation = inv,
                actual,
                rawMedication = rawMed,
                rawInvestigation = rawInv
            });
        }

        object Summary(List<EntityEvaluation> items)
        {
            object Recognize(Func<EntityEvaluation, Recognition> get)
            {
                var values = items.Select(get).ToArray();
                return new
                {
                    micro = new Recognition(values.Sum(v => v.Tp), values.Sum(v => v.Fp), values.Sum(v => v.Fn)),
                    macro = new { precision = values.Average(v => v.Precision), recall = values.Average(v => v.Recall), f1 = values.Average(v => v.F1) }
                };
            }
            Dictionary<string, Accuracy> Aggregate(Func<EntityEvaluation, Dictionary<string, Accuracy>> get) => items.SelectMany(i => get(i)).GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => new Accuracy(g.Sum(v => v.Value.Correct), g.Sum(v => v.Value.Evaluated)));
            Accuracy Sum(Func<EntityEvaluation, Accuracy> get) => new(items.Sum(i => get(i).Correct), items.Sum(i => get(i).Evaluated));
            var full = items.Where(i => i.FullPrescription != null).Select(i => i.FullPrescription!).ToArray();
            return new
            {
                exact = Recognize(i => i.Exact),
                normalized = Recognize(i => i.Normalized),
                fullPrescription = full.Length == 0 ? null : new Recognition(full.Sum(v => v.Tp), full.Sum(v => v.Fp), full.Sum(v => v.Fn)),
                fullPrescriptionEvaluableCases = full.Length,
                attributes = Aggregate(i => i.Attributes),
                documentedAttributes = Aggregate(i => i.DocumentedAttributes),
                missingAttributes = Aggregate(i => i.MissingAttributes),
                attributeMacro = items.SelectMany(i => i.Attributes).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Average(v => v.Value.Rate)),
                evidenceQuote = Sum(i => i.EvidenceQuote),
                characterOffset = Sum(i => i.CharacterOffset),
                unsupportedEntityRate = Sum(i => i.UnsupportedEntity),
                conditionalOrder = Sum(i => i.ConditionalOrder),
                ambiguousGoldMentions = items.Sum(i => i.AmbiguousGoldMentions)
            };
        }

        double Percentile(double p) => latencies.Order().ElementAt(Math.Clamp((int)Math.Ceiling(p * latencies.Count) - 1, 0, latencies.Count - 1));
        string dir = Path.Combine(Backend, "artifacts", "audit-remediation", "development");
        Directory.CreateDirectory(dir);
        string jsonPath = Path.Combine(dir, "microsoftfoundry-remediated-replay.json");
        string csvPath = Path.Combine(Backend, "artifacts", "audit-remediation", "foundry-remediated-case-metrics.csv");
        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            classification = "offline replay of saved 20-call Microsoft Foundry development responses; 0 new live API calls",
            datasetSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dataset))),
            evaluatorVersion = "audit-v2-remediated",
            provider = "MicrosoftFoundry",
            assemblyMvid = typeof(ClinicalResultValidator).Assembly.ManifestModule.ModuleVersionId,
            promptVersion = "clinical-v3",
            fallback = false,
            retries = 0,
            totalCases = cases.Length,
            errors,
            latency = new { measurement = "offline replay through full ASP.NET Core pipeline", sampleSize = cases.Length, p50Ms = Percentile(.5), p95Ms = Percentile(.95), errorRate = (double)errors / cases.Length },
            liveBudget = new { Calls = 0, ReplayedSavedCalls = receipts.Count, OriginalSpendUsd = 0.294738m },
            rawFoundry = new { medications = Summary(rawMedMetrics), investigations = Summary(rawInvMetrics) },
            medications = Summary(medicationMetrics),
            investigations = Summary(investigationMetrics),
            cases = rows
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, Json));
        await File.WriteAllLinesAsync(csvPath, csvLines);
        output.WriteLine("Offline Foundry replay completed: " + jsonPath);
        Assert.Equal(0, errors);
    }
}
