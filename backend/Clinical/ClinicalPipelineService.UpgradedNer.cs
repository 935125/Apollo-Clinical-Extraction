using Apollo.Backend.Fhir;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

public partial class ClinicalPipelineService
{
    private ClinicalNERResponse ProcessUpgradedNer(ClinicalNERRequest request)
    {
        var receipt=new OnnxExecutionReceipt();
        var result=ProcessPlatformExtraction(new(request.Text),Guid.NewGuid().ToString("D"),receipt);
        var entities=new List<ClinicalEntityResponse>();
        var structured=new List<StructuredMedicationResponse>();
        foreach(var medication in result.Medications)
        {
            var context=medication.Provenance!.ClinicalContext!;
            string certainty=request.ApplyAssertion ? medication.AssertionStatus=="negated" ? "Negated" : medication.AssertionStatus is "uncertain" or "conditional" ? "Possible" : "Positive" : "Positive";
            string? code=request.EnableGrounding && medication.Normalization.MatchedCode!=null ? "RxNorm:"+medication.Normalization.MatchedCode : null;
            entities.Add(new("MEDICATION",medication.Name,context.SourceStart,context.SourceEnd,context.RawNerScore ?? 0,
                Assertion:request.ApplyAssertion ? context.Assertion : "PRESENT",Certainty:certainty,
                Temporality:request.ApplyAssertion ? context.Temporality : "current",Experiencer:context.Assertion=="other_experiencer" ? "Other" : "Patient",Section:context.Section,
                ConceptCode:code,CanonicalName:request.EnableGrounding ? medication.Normalization.DisplayName : null));
            structured.Add(new(medication.Name,[context.SourceStart,context.SourceEnd],medication.Dose,medication.Dose,medication.Frequency,
                medication.Route,medication.Duration,certainty,context.Assertion=="other_experiencer" ? "Other" : "Patient",code));
        }
        foreach(var investigation in result.Investigations)
        {
            var context=investigation.Provenance!.ClinicalContext!;
            entities.Add(new("INVESTIGATION",investigation.Name,context.SourceStart,context.SourceEnd,0,
                Assertion:request.ApplyAssertion ? context.Assertion : "PRESENT",Certainty:investigation.AssertionStatus=="negated" && request.ApplyAssertion ? "Negated" : "Positive",
                Temporality:request.ApplyAssertion ? context.Temporality : "current",Experiencer:"Patient",Section:context.Section,
                ConceptCode:request.EnableGrounding && investigation.MatchedCode!=null ? "LOINC:"+investigation.MatchedCode : null,
                CanonicalName:request.EnableGrounding ? investigation.Normalization.DisplayName : null));
        }
        var sections=ClinicalSectionDetector.Detect(request.Text);
        foreach(var span in receipt.ModelSpans.Where(s=>s.Category=="CONDITION"))
        {
            var context=_contextEngine.Classify(request.Text,span.StartChar,span.EndChar,sections);
            entities.Add(new(span.Category,span.Text,span.StartChar,span.EndChar,span.Confidence,
                Certainty:request.ApplyAssertion ? context.Certainty.ToString() : "Positive",
                Temporality:request.ApplyAssertion ? context.Temporality.ToString() : "Current",Experiencer:context.Experiencer.ToString(),Section:context.Section));
        }
        entities.Sort((a,b)=>a.StartChar.CompareTo(b.StartChar));
        return new(entities,structured,MicroCardBuilder.Build(request.Text,entities,structured),
            request.IncludeFhir ? FhirR4Mapper.ToFhirBundle(request.Text,entities,structured) : new Dictionary<string,object?>(),
            result.ExecutionDurationMs,"clinical-local-v15-bc5cdr-int8",request.IncludeText ? request.Text : null);
    }
}
