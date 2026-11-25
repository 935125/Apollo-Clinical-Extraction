using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Apollo.Backend.Tests.Evaluation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Apollo.Backend.Tests;

public sealed class Stage15ModelFixture : IDisposable
{
    public WordPieceTokenizer Tokenizer { get; } = new(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/clinical_tokenizer.json"));
    public SlidingWindowOnnxEngine Engine { get; }
    public ClinicalPipelineService Pipeline { get; }
    public Stage15ModelFixture()
    {
        Engine=new(Path.Combine(HeldOutBenchmarkRunner.Backend,"artifacts/bc5cdr_latest/onnx/model_int8.onnx"),Tokenizer);
        Pipeline=new(Engine,Options.Create(new LocalClinicalPipelineOptions{PipelineVersion="CandidateV2"}));
    }
    public void Dispose()=>Engine.Dispose();
}

public class Stage15ClinicalPipelineTests(Stage15ModelFixture fixture) : IClassFixture<Stage15ModelFixture>
{
    public static IEnumerable<object[]> FrozenCases()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(HeldOutBenchmarkRunner.Backend,"tests/Evaluation/stage15_frozen_cases.json")));
        return doc.RootElement.EnumerateArray().Select(c=>new object[]{c.GetProperty("id").GetString()!,c.GetRawText()}).ToArray();
    }
    internal static Dictionary<string,string?> Fields(ExtractedMedicationDto m)=>new()
    {
        ["strength"]=m.Strength,["dose"]=m.Dose,["dose_unit"]=m.DoseUnit,["route"]=m.Route,["frequency"]=m.Frequency,["duration"]=m.Duration,
        ["assertion_status"]=m.AssertionStatus,["detailed_assertion"]=m.Provenance?.ClinicalContext?.Assertion,["administration"]=m.Provenance?.ClinicalContext?.Administration,
        ["normalization_status"]=m.Normalization.Status,["start_timing"]=m.Provenance?.ClinicalContext?.StartTiming,
        ["stop_timing"]=m.Provenance?.ClinicalContext?.StopTiming,["prn_indication"]=m.Provenance?.ClinicalContext?.PrnIndication
    };
    internal static Dictionary<string,string?> Fields(ExtractedInvestigationDto i)=>new()
    {
        ["assertion_status"]=i.AssertionStatus,["detailed_assertion"]=i.Provenance?.ClinicalContext?.Assertion,["timing"]=i.Timing,
        ["specimen"]=i.Specimen,["clinical_category"]=i.ClinicalCategory,["normalization_status"]=i.Normalization.Status
    };

    [Theory]
    [MemberData(nameof(FrozenCases))]
    public void RealOnnx_FrozenSourceAuthoredCases(string id,string annotation)
    {
        using var doc=JsonDocument.Parse(annotation); var c=doc.RootElement;
        string note=c.GetProperty("note_text").GetString()!;
        var receipt=new OnnxExecutionReceipt();
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(note),id,receipt);
        Assert.True(receipt.WindowsExecuted>0);
        Assert.Equal("CandidateV2",result.ModelMetadata.LocalPipelineVersion);
        Check(c.GetProperty("expected_medications"),result.Medications.Select(m=>(m.Name,Fields(m),m.Provenance?.ClinicalContext)).ToList());
        Check(c.GetProperty("expected_investigations"),result.Investigations.Select(i=>(i.Name,Fields(i),i.Provenance?.ClinicalContext)).ToList());
        LocalEvidenceValidator.Validate(result,note);
        void Check(JsonElement expected,List<(string Name,Dictionary<string,string?> Fields,ClinicalEntityContextDto? Context)> actual)
        {
            Assert.Equal(expected.GetArrayLength(),actual.Count);
            foreach(var gold in expected.EnumerateArray())
            {
                string name=gold.GetProperty("name").GetString()!; int start=gold.GetProperty("mention_start").GetInt32();
                var found=Assert.Single(actual,a=>a.Name.Equals(name,StringComparison.OrdinalIgnoreCase) && a.Context?.SourceStart==start);
                foreach(var property in gold.EnumerateObject())
                {
                    if(property.Name is "name" or "mention_start" or "mention_end") continue;
                    string? target=property.Value.ValueKind==JsonValueKind.Null ? null : property.Value.GetString();
                    string? value=found.Fields.GetValueOrDefault(property.Name);
                    Assert.True(ClinicalEvaluator.Same(property.Name,target,value),$"{id}: {name}.{property.Name} expected '{target ?? "NULL"}' actual '{value ?? "NULL"}'");
                }
            }
        }
    }

    [Fact]
    public void EvidenceGateRejectsMutatedQuotesAndOffsets()
    {
        var result=fixture.Pipeline.ProcessPlatformExtraction(new("Start Aspirin 75 mg PO once daily."),"tamper");
        var med=Assert.Single(result.Medications);
        Assert.Throws<InvalidClinicalEvidenceException>(()=>LocalEvidenceValidator.Validate(result with{Medications=[med with{Evidence=med.Evidence with{Quote="Invented evidence"}}]},result.NoteText!));
        var provenance=med.Provenance! with{ClinicalContext=med.Provenance!.ClinicalContext! with{SourceStart=0}};
        Assert.Throws<InvalidClinicalEvidenceException>(()=>LocalEvidenceValidator.Validate(result with{Medications=[med with{Provenance=provenance}]},result.NoteText!));
    }

    [Fact]
    public void UnknownTerminologyDoesNotDropDoseSupportedCandidateOrInventCodes()
    {
        var receipt=new OnnxExecutionReceipt();
        var result=fixture.Pipeline.ProcessPlatformExtraction(new("Medication Orders:\nZorvaximab 2 mg/kg IV weekly."),"unknown",receipt);
        var med=Assert.Single(result.Medications); Assert.Null(med.Normalization.MatchedCode);
        Assert.Contains(receipt.CandidateTraces,t=>t.Text=="Zorvaximab" && t.CatalogMatch=="unresolved" &&
            (t.DetectionSource=="PRESCRIPTION_PHRASE_GRAMMAR" && t.NerScore==null || t.DetectionSource=="ONNX_CHEMICAL" && t.NerScore!=null));
        Assert.Equal("2 mg/kg",med.Dose); Assert.Equal("mg/kg",med.DoseUnit);
    }

    [Fact]
    public async Task ConcurrentNotesNeverMixEntitiesOrEvidence()
    {
        var results=await Task.WhenAll(Enumerable.Range(0,6).Select(i=>Task.Run(()=>fixture.Pipeline.ProcessPlatformExtraction(
            new(i%2==0 ? "Continue Aspirin 75 mg PO daily." : "Stop Warfarin 5 mg."),"isolated-"+i))));
        for(int i=0;i<results.Length;i++)
        {
            Assert.Equal("isolated-"+i,results[i].CorrelationId);
            Assert.Equal(i%2==0 ? "Aspirin" : "Warfarin",Assert.Single(results[i].Medications).Name);
            LocalEvidenceValidator.Validate(results[i],results[i].NoteText!);
        }
    }

    [Fact]
    public void CancellationAndInvalidPipelineVersionFailClosed()
    {
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>fixture.Pipeline.ProcessPlatformExtraction(new("Start Aspirin 75 mg."),"cancel",cancellationToken:cancellation.Token));
        Assert.Throws<InvalidOperationException>(()=>new ClinicalPipelineService(fixture.Engine,Options.Create(new LocalClinicalPipelineOptions{PipelineVersion="unknown"})));
    }

    [Fact]
    public void UnicodeAndEveryWindowRetainOriginalOffsets()
    {
        string source="Observation 🩺 café. "+string.Concat(Enumerable.Repeat("Routine observation. ",400))+"\nMedication Orders:\nCytarabine 100 mg/m² continuous IV infusion.\nFollow-Up Plan:\nCBC next week.";
        var receipt=new OnnxExecutionReceipt();
        var result=fixture.Pipeline.ProcessPlatformExtraction(new(source),"unicode",receipt);
        Assert.True(receipt.WindowsExecuted>1);
        Assert.Equal(fixture.Tokenizer.Tokenize(source).InputIds.Count-2,receipt.TokensEvaluated);
        Assert.Equal(source.IndexOf("Cytarabine",StringComparison.Ordinal),Assert.Single(result.Medications).Provenance!.ClinicalContext!.SourceStart);
        Assert.Equal("100 mg/m²",Assert.Single(result.Medications).Dose);
        LocalEvidenceValidator.Validate(result,source);
    }

    [Fact]
    public async Task ActualTimeoutIsDistinctFromCancellationAndDoesNotPoisonTheSharedSession()
    {
        var services=new ServiceCollection().AddSingleton(fixture.Engine).AddSingleton(fixture.Tokenizer)
            .AddSingleton(fixture.Pipeline).AddSingleton<IOptions<LocalClinicalPipelineOptions>>(
                Options.Create(new LocalClinicalPipelineOptions{PipelineVersion="CandidateV2",TimeoutSeconds=0.001})).BuildServiceProvider();
        string note=string.Concat(Enumerable.Repeat("Routine observation. ",2500))+"\nDischarge Medications:\nAcyclovir 400 mg PO twice daily.";
        var error=await Assert.ThrowsAsync<ClinicalExtractionException>(()=>new OnnxClinicalExtractor(services).ExtractAsync(note,"timeout",CancellationToken.None));
        Assert.Equal("ONNX_TIMEOUT",error.Code);
        Assert.Single(fixture.Pipeline.ProcessPlatformExtraction(new("Continue Aspirin 75 mg PO daily."),"after-timeout").Medications);
    }
}
