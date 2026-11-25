using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Tests.Evaluation;

public sealed class Stage15EvaluationFactAttribute : FactAttribute
{
    public Stage15EvaluationFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("APOLLO_STAGE15_EVALUATE")!="1") Skip="Explicit offline benchmark run only";
    }
}

public class Stage15AblationTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    [Stage15EvaluationFact]
    public async Task RealModel_AblationsAndPerformance()
    {
        string root=Stage15BenchmarkTests.Root;
        string dir=Path.Combine(root,"docs/stage15-evidence/evaluation"); Directory.CreateDirectory(dir);
        var watch=Stopwatch.StartNew();
        var tokenizer=new WordPieceTokenizer(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/clinical_tokenizer.json"));
        using var engine=new SlidingWindowOnnxEngine(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/onnx/model_int8.onnx"),tokenizer);
        double init=watch.Elapsed.TotalMilliseconds;
        using var frozen=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"docs/stage15-evidence/baseline/real-local-outputs.json")));
        var sources=frozen.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        var variants=new Dictionary<string,LocalPipelineFeatures>
        {
            ["candidates-only"]=new(true,false,false,false,false,false,false),
            ["investigations-only"]=new(false,true,false,false,false,false,false),
            ["assertions-only"]=new(false,false,true,false,false,false,false),
            ["terminology-only"]=new(false,false,false,true,false,false,false),
            ["attributes-only"]=new(false,false,false,false,true,false,false),
            ["sections-only"]=new(false,false,false,false,false,true,false),
            ["integrated"]=new()
        };
        var integrated=new Dictionary<string,ClinicalExtractionResponse>();
        double coldMs=0; var performance=new List<object>();
        foreach(var variant in variants)
        {
            var rows=new List<object>();
            foreach(var original in sources)
            {
                string id=original.GetProperty("id").GetString()!;
                var baseline=original.GetProperty("result").Deserialize<ClinicalExtractionResponse>(Json)!;
                string note=baseline.NoteText!;
                var receipt=new OnnxExecutionReceipt();
                watch.Restart();
                var cpu=Process.GetCurrentProcess().TotalProcessorTime;
                var result=new UpgradedClinicalPipeline(engine).Process(new(note),id,receipt,features:variant.Value,baseline:baseline);
                double elapsed=watch.Elapsed.TotalMilliseconds;
                double cpuMs=(Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds;
                if(coldMs==0) coldMs=elapsed;
                rows.Add(new{id,dataset=original.GetProperty("dataset").GetString(),latencyMs=elapsed,cpuMs,receipt.WindowsExecuted,
                    receipt.InferenceDurationMs,receipt.TokensEvaluated,receipt.UnknownTokens,receipt.CandidateTraces,result});
                if(variant.Key=="integrated") integrated[id]=result;
            }
            await File.WriteAllTextAsync(Path.Combine(dir,variant.Key+".json"),JsonSerializer.Serialize(new{variant=variant.Key,
                features=variant.Value,liveFoundryCalls=0,cases=rows},Json));
        }
        foreach(string version in new[]{"Legacy","CandidateV2"})
        {
            var services=new ServiceCollection().AddSingleton(engine).AddSingleton(tokenizer)
                .AddSingleton<IOptions<LocalClinicalPipelineOptions>>(Options.Create(new LocalClinicalPipelineOptions{PipelineVersion=version}))
                .AddSingleton<ClinicalPipelineService>().AddSingleton<ClinicalResultValidator>().BuildServiceProvider();
            var provider=new OnnxClinicalExtractor(services);
            var rows=new List<object>();
            foreach(var original in sources)
            {
                string id=original.GetProperty("id").GetString()!;
                string note=original.GetProperty("result").GetProperty("noteText").GetString()!;
                watch.Restart();
                var result=await provider.ExtractAsync(note,id,id=="P100234-ENC-100567-v33",CancellationToken.None);
                rows.Add(new{id,dataset=original.GetProperty("dataset").GetString(),latencyMs=watch.Elapsed.TotalMilliseconds,result});
            }
            await File.WriteAllTextAsync(Path.Combine(dir,version=="Legacy" ? "legacy-adapter.json" : "integrated-adapter.json"),
                JsonSerializer.Serialize(new{variant=version,liveFoundryCalls=0,cases=rows},Json));
        }
        var target=integrated["P100234-ENC-100567-v33"];
        foreach(var mode in new[]{"Legacy","CandidateV2"})
        {
            var pipeline=new ClinicalPipelineService(engine,Microsoft.Extensions.Options.Options.Create(new LocalClinicalPipelineOptions{PipelineVersion=mode}));
            var times=new List<double>(); var inference=new List<double>();
            var before=Process.GetCurrentProcess().TotalProcessorTime; watch.Restart();
            for(int i=0;i<30;i++)
            {
                var one=Stopwatch.StartNew(); var receipt=new OnnxExecutionReceipt();
                var result=pipeline.ProcessPlatformExtraction(new(target.NoteText!),"latency-"+i,receipt,skipPrivacyValidation:true);
                times.Add(one.Elapsed.TotalMilliseconds); inference.Add(receipt.InferenceDurationMs);
                LocalEvidenceValidator.Validate(result,target.NoteText!);
            }
            double wall=watch.Elapsed.TotalMilliseconds;
            double cpu=(Process.GetCurrentProcess().TotalProcessorTime-before).TotalMilliseconds;
            var ordered=times.Order().ToArray();
            performance.Add(new {mode,notes=times.Count,meanMs=times.Average(),p50Ms=ordered[14],p95Ms=ordered[28],maxMs=ordered[^1],
                meanInferenceMs=inference.Average(),cpuMs=cpu,wallMs=wall,cpuUtilizationAllLogicalProcessors=cpu/wall/Environment.ProcessorCount,
                workingSetBytes=Process.GetCurrentProcess().WorkingSet64,peakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64});
        }
        var concurrentWatch=Stopwatch.StartNew();
        var concurrent=await Task.WhenAll(Enumerable.Range(0,8).Select(i=>Task.Run(()=>
            new UpgradedClinicalPipeline(engine).Process(new(i%2==0 ? "Continue Aspirin 75 mg PO daily." : "Stop Warfarin 5 mg."),"parallel-"+i,new()))));
        double concurrentMs=concurrentWatch.Elapsed.TotalMilliseconds;
        Assert.All(concurrent,r=>Assert.Single(r.Medications));
        var coldRuns=new List<object>();
        var longRuns=new List<object>();
        foreach(var mode in new[]{"Legacy","CandidateV2"})
        {
            watch.Restart();
            var coldTokenizer=new WordPieceTokenizer(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/clinical_tokenizer.json"));
            using var coldEngine=new SlidingWindowOnnxEngine(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/onnx/model_int8.onnx"),coldTokenizer);
            double loadMs=watch.Elapsed.TotalMilliseconds;
            var coldPipeline=new ClinicalPipelineService(coldEngine,Options.Create(new LocalClinicalPipelineOptions{PipelineVersion=mode}));
            var receipt=new OnnxExecutionReceipt();watch.Restart();
            coldPipeline.ProcessPlatformExtraction(new(target.NoteText!),"cold-"+mode,receipt,skipPrivacyValidation:true);
            coldRuns.Add(new{mode,initializationMs=loadMs,firstExtractionMs=watch.Elapsed.TotalMilliseconds,inferenceMs=receipt.InferenceDurationMs,
                receipt.WindowsExecuted,scope="New ONNX session, OS file cache not cleared; pipeline only, no persistence"});
            string longSource=integrated["long-note"].NoteText!;
            watch.Restart();
            for(int i=0;i<5;i++) coldPipeline.ProcessPlatformExtraction(new(longSource),"long-"+i,new());
            double elapsed=watch.Elapsed.TotalMilliseconds;
            longRuns.Add(new{mode,notes=5,charLength=longSource.Length,tokens=coldTokenizer.Tokenize(longSource).InputIds.Count-2,
                elapsedMs=elapsed,meanMs=elapsed/5,observedSequentialNotesPerSecond=5000/elapsed});
        }
        await File.WriteAllTextAsync(Path.Combine(dir,"performance.json"),JsonSerializer.Serialize(new{
            scope="Fresh xUnit testhost; process peak includes framework, managed pipeline, and native ONNX memory. No GPU used.",
            os=RuntimeInformation.OSDescription,processorCount=Environment.ProcessorCount,initializationMs=init,
            firstColdExtractionMs=coldMs,coldVariant="candidates-only",artifactBytes=new FileInfo(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/onnx/model_int8.onnx")).Length,
            tokenizerBytes=new FileInfo(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/clinical_tokenizer.json")).Length,
            concurrency=new{requests=8,elapsedMs=concurrentMs,isolated=concurrent.Select(r=>r.CorrelationId).Distinct().Count()==8},
            measurements=performance,coldSessionMeasurements=coldRuns,longNoteMeasurements=longRuns,
            overallPeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64},Json));
        await File.WriteAllTextAsync(Path.Combine(dir,"dataset-manifest.json"),JsonSerializer.Serialize(new{
            classification="Draft engineering annotations; the 35-case historical labels are incomplete for mention-level evaluation; no independent clinician adjudication",
            datasets=new[]{"stage5_independent_benchmark_35cases.json","stage15_frozen_cases.json"}.Select(f=>new{
                name=f,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(HeldOutBenchmarkRunner.Backend,"tests/Evaluation",f))))})},Json));
    }
}
