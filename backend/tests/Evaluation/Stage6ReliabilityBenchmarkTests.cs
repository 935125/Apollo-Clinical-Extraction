using System.Diagnostics;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Microsoft.Extensions.DependencyInjection;
using Apollo.Backend.Nlp;
using Apollo.Backend.Security;

namespace Apollo.Backend.Tests.Evaluation;

/// <summary>
/// Stage 6 development replay of the frozen Stage 5 engineering set (35 cases).
/// Gold labels were authored from the source text before execution and are NOT derived from model output.
/// Provider evaluated: LocalOnnx (bc5cdr_int8 + deterministic pipeline). Zero Foundry calls.
/// Labels are engineering-authored (not clinician-reviewed) => preliminary synthetic engineering evaluation.
/// </summary>
public sealed class Stage6ReliabilityBenchmarkTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static List<ClinicalEvaluator.Prediction> InvestigationsWithCategory(ClinicalExtractionResponse r) =>
        r.Investigations.Select(i => new ClinicalEvaluator.Prediction(i.Name, new()
        {
            ["assertion_status"] = i.AssertionStatus,
            ["timing"] = i.Timing,
            ["specimen"] = i.Specimen,
            ["status"] = i.Status,
            ["clinical_category"] = i.ClinicalCategory
        }, i.Evidence)).ToList();

    [Fact]
    public async Task Stage6_DevelopmentRegression_CapturesRawAndFinalSeparately()
    {
        string datasetPath = Path.Combine(HeldOutBenchmarkRunner.Backend, "tests", "Evaluation", "stage5_independent_benchmark_35cases.json");
        Assert.True(File.Exists(datasetPath));
        string datasetText = await File.ReadAllTextAsync(datasetPath);
        string datasetSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(datasetText)));

        var tokenizer = new Apollo.Backend.Nlp.WordPieceTokenizer(Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "clinical_tokenizer.json"));
        using var onnx = new Apollo.Backend.Nlp.SlidingWindowOnnxEngine(Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest", "onnx", "model_int8.onnx"), tokenizer);
        var pipeline = new ClinicalPipelineService(onnx);
        using var services = new ServiceCollection().AddSingleton(onnx).AddSingleton(tokenizer).AddSingleton(pipeline).BuildServiceProvider();
        var extractor = new OnnxClinicalExtractor(services);
        string artifacts = Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest");
        string modelSha256 = ArtifactChecksumVerifier.ComputeSha256(Path.Combine(artifacts, "onnx", "model_int8.onnx"));
        string tokenizerSha256 = ArtifactChecksumVerifier.ComputeSha256(Path.Combine(artifacts, "clinical_tokenizer.json"));
        Assert.Equal("59830ca601c97246f068cc265c215d00c1024e2d3ae3549a5d053723f4a65cd0", modelSha256);
        Assert.Equal("00a99d2e62f065e43eb57d570e38ee7e814b3044f27e2da70755e9587ff33c81", tokenizerSha256);

        using var doc = JsonDocument.Parse(datasetText);
        var cases = doc.RootElement.EnumerateArray().ToArray();

        var medEvals = new List<EntityEvaluation>();
        var rawModelEvals = new List<EntityEvaluation>();
        var invEvals = new List<EntityEvaluation>();
        int medTotal = 0, medExact = 0, medExactCatalogValid = 0, medCandidate = 0, medAbstain = 0, medAmbiguous = 0;
        int invTotal = 0, invExact = 0, invExactCatalogValid = 0, invCandidate = 0, invAbstain = 0, invAmbiguous = 0;
        int expectedAlerts = 0, matchedAlerts = 0, emittedAlerts = 0, groundedAlerts = 0, unsupportedAlerts = 0, measurementAlerts = 0, measurementGrounded = 0;
        int evidenceQuoteValid = 0, evidenceOffsetValid = 0, evidenceTotal = 0;
        var latencies = new List<double>();
        var caseRows = new List<object>();
        var matchedMedEvals = new List<EntityEvaluation>();
        var matchedInvEvals = new List<EntityEvaluation>();
        var errorCategories = new Dictionary<string, int>();

        foreach (var c in cases)
        {
            string id = c.GetProperty("id").GetString()!;
            string note = c.GetProperty("note_text").GetString()!;
            var receipt = new Apollo.Backend.Nlp.OnnxExecutionReceipt();
            var raw = onnx.InferSpans(note, receipt);
            Assert.True(receipt.WindowsExecuted > 0);
            var sw = Stopwatch.StartNew();
            var unreviewed = pipeline.ProcessPlatformExtraction(new ClinicalExtractionRequest(note), id);
            var res = await extractor.ExtractAsync(note, id, CancellationToken.None);
            rawModelEvals.Add(ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().ToArray(), raw.Where(s => s.Category == "MEDICATION").Select(s => new ClinicalEvaluator.Prediction(s.Text, new(), new EvidenceDto(s.Text, true, s.StartChar, s.EndChar))).ToList()));
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);

            var me = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().ToArray(), ClinicalEvaluator.Medications(res));
            var ie = ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_investigations").EnumerateArray().ToArray(), InvestigationsWithCategory(res));
            medEvals.Add(me); invEvals.Add(ie);
            matchedMedEvals.Add(ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_medications").EnumerateArray().Where(g => res.Medications.Any(p => ClinicalEvaluator.EntityName(p.Name) == ClinicalEvaluator.EntityName(g.GetProperty("name").GetString()))).ToArray(), ClinicalEvaluator.Medications(res)));
            matchedInvEvals.Add(ClinicalEvaluator.Evaluate(note, c.GetProperty("expected_investigations").EnumerateArray().Where(g => res.Investigations.Any(p => ClinicalEvaluator.EntityName(p.Name) == ClinicalEvaluator.EntityName(g.GetProperty("name").GetString()))).ToArray(), InvestigationsWithCategory(res)));
            foreach (var f in me.Failures.Concat(ie.Failures))
            {
                string key = $"{f.Kind}:{f.Category}" + (f.Kind == "attribute" ? $":{f.Field}" : "");
                errorCategories[key] = errorCategories.GetValueOrDefault(key) + 1;
            }

            foreach (var m in res.Medications)
            {
                medTotal++;
                var n = m.Normalization;
                if (n.MatchedCode != null) { medExact++; if (TerminologyVerification.CodeExists("MEDICATION", n.MatchedCode)) medExactCatalogValid++; }
                else medAbstain++;
                if (n.CandidateCode != null) medCandidate++;
                if (n.SemanticStatus == "ambiguous") medAmbiguous++;
            }
            foreach (var i in res.Investigations)
            {
                invTotal++;
                var n = i.Normalization;
                if (n.MatchedCode != null) { invExact++; if (TerminologyVerification.CodeExists("INVESTIGATION", n.MatchedCode)) invExactCatalogValid++; }
                else invAbstain++;
                if (n.CandidateCode != null) invCandidate++;
                if (n.SemanticStatus == "ambiguous") invAmbiguous++;
            }
            foreach (var e in res.Medications.Select(m => m.Evidence).Concat(res.Investigations.Select(i => i.Evidence)))
            {
                evidenceTotal++;
                if (ClinicalEvaluator.QuoteValid(note, e)) evidenceQuoteValid++;
                if (ClinicalEvaluator.OffsetValid(note, e)) evidenceOffsetValid++;
            }

            var alerts = res.SafetyAlerts ?? [];
            emittedAlerts += alerts.Count;
            foreach (var a in alerts)
            {
                bool grounded = a.EvidenceQuote != null && a.EvidenceQuote.Split(" | ").All(q => note.Contains(q, StringComparison.Ordinal));
                if (a.Category == "DATA_QUALITY_WARNING") grounded = true; // explicitly a suppression notice, not a clinical claim
                if (grounded) groundedAlerts++; else unsupportedAlerts++;
                if (a.Category == "LAB_ANOMALY")
                {
                    measurementAlerts++;
                    if (a.EvidenceQuote != null && a.StartChar is int s && a.EndChar is int en && en <= note.Length && note[s..en] == a.EvidenceQuote) measurementGrounded++;
                }
            }
            var exp = c.GetProperty("expected_alerts").EnumerateArray().ToArray();
            expectedAlerts += exp.Length;
            int caseMatched = exp.Count(ea => alerts.Any(a => a.Category == ea.GetProperty("category").GetString() && a.Temporality == ea.GetProperty("temporality").GetString()));
            matchedAlerts += caseMatched;

            caseRows.Add(new
            {
                id,
                rawOnnxSpans = raw,
                inferenceReceipt = receipt,
                unreviewedPipeline = unreviewed,
                finalPipeline = res,
                reviewedFallback = ClinicalReview.Apply(res, reducedCoverage: true),
                specialty = c.GetProperty("specialty").GetString(),
                challenge = c.GetProperty("challenge").GetString(),
                latencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                medications = me.Normalized,
                investigations = ie.Normalized,
                expectedAlerts = exp.Length,
                matchedAlerts = caseMatched,
                emittedAlerts = alerts.Select(a => new { a.Category, a.Temporality, a.Severity, a.Title }),
                failures = me.Failures.Concat(ie.Failures)
            });
        }

        Dictionary<string, Accuracy> Aggregate(IEnumerable<KeyValuePair<string, Accuracy>> values) => values.GroupBy(k => k.Key)
            .ToDictionary(g => g.Key, g => new Accuracy(g.Sum(v => v.Value.Correct), g.Sum(v => v.Value.Evaluated)));
        Recognition Sum(List<EntityEvaluation> l) => new(l.Sum(x => x.Normalized.Tp), l.Sum(x => x.Normalized.Fp), l.Sum(x => x.Normalized.Fn));
        var sorted = latencies.OrderBy(x => x).ToList();
        double P(double q) => sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(q * sorted.Count) - 1)];

        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            classification = "Development regression only; frozen engineering annotations, no independent clinician adjudication",
            dataset = "stage5_independent_benchmark_35cases.json",
            datasetSha256 = datasetSha,
            caseCount = cases.Length,
            specialties = cases.GroupBy(c => c.GetProperty("specialty").GetString()).ToDictionary(g => g.Key!, g => g.Count()),
            providerThatGeneratedEveryPrediction = "LocalOnnx (bc5cdr_int8 INT8 ONNX + deterministic posology/ontology pipeline)",
            liveFoundryCalls = 0,
            foundryEvaluation = "NOT EVALUATED — no saved GPT-6.1 Sol responses exist for these 35 notes and new billable calls were prohibited.",
            rawModelMedicationDetection = Sum(rawModelEvals),
            modelSha256, tokenizerSha256,
            medicationsMicro = Sum(medEvals),
            medicationAttributes = Aggregate(medEvals.SelectMany(x => x.Attributes)),
            documentedMedicationAttributes = Aggregate(medEvals.SelectMany(x => x.DocumentedAttributes)),
            absentMedicationAttributes = Aggregate(medEvals.SelectMany(x => x.MissingAttributes)),
            matchedMedicationAttributes = Aggregate(matchedMedEvals.SelectMany(x => x.Attributes)),
            investigationsMicro = Sum(invEvals),
            investigationAttributes = Aggregate(invEvals.SelectMany(x => x.Attributes)),
            documentedInvestigationAttributes = Aggregate(invEvals.SelectMany(x => x.DocumentedAttributes)),
            absentInvestigationAttributes = Aggregate(invEvals.SelectMany(x => x.MissingAttributes)),
            matchedInvestigationAttributes = Aggregate(matchedInvEvals.SelectMany(x => x.Attributes)),
            terminology = new
            {
                note = "Gold labels contain no reference codes; exact-code precision against gold is NOT MEASURED. Reported: assignment coverage, catalog validity of assigned codes, candidate coverage and abstention.",
                rxNorm = new { extracted = medTotal, exactAssigned = medExact, exactAssignedCatalogValid = medExactCatalogValid, candidateIdentified = medCandidate, abstained = medAbstain, ambiguousFlagged = medAmbiguous },
                loinc = new { extracted = invTotal, exactAssigned = invExact, exactAssignedCatalogValid = invExactCatalogValid, candidateIdentified = invCandidate, abstained = invAbstain, ambiguousFlagged = invAmbiguous }
            },
            evidence = new { total = evidenceTotal, quoteValid = evidenceQuoteValid, offsetValid = evidenceOffsetValid },
            safety = new
            {
                expectedAlerts, matchedCategoryAndTemporality = matchedAlerts,
                emittedAlerts, groundedAlerts, unsupportedAlerts,
                labAnomalyAlerts = measurementAlerts, labAnomalyAlertsMeasurementGrounded = measurementGrounded,
                clinicianSeverityAgreement = "NOT AVAILABLE (no clinician review)"
            },
            latencyMs = new { mean = Math.Round(latencies.Average(), 2), p50 = Math.Round(P(0.5), 2), p95 = Math.Round(P(0.95), 2), max = Math.Round(sorted[^1], 2) },
            errorCategories = errorCategories.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value),
            cases = caseRows
        };

        string outDir = Environment.GetEnvironmentVariable("APOLLO_EVALUATION_OUTPUT_ROOT") ?? Path.Combine(HeldOutBenchmarkRunner.Backend, "..", "docs", "stage7-evidence");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, Environment.GetEnvironmentVariable("STAGE6_BENCHMARK_OUTPUT") ?? "after.json"), JsonSerializer.Serialize(report, Json));
        Assert.Equal("3D2EE6AFD0AC72157C92BDDEEDC43F94511BA170D7985A1F44177CB78EB9AD84", datasetSha);
        Assert.True(onnx.HasOnnxModel);
        Assert.True(tokenizer.LoadedFromArtifact);
        Assert.Equal(35, cases.Length);
    }
}

