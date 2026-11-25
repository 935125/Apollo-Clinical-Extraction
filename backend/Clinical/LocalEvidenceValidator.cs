using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

/// <summary>Independent exact-span gate for local extraction. Never repairs or fabricates a quote.</summary>
public static class LocalEvidenceValidator
{
    public static void Validate(ClinicalExtractionResponse result,string source)
    {
        var spans=result.Medications.Select(m=>(m.Name,m.Provenance?.ClinicalContext,"MEDICATION"))
            .Concat(result.Investigations.Select(i=>(i.Name,i.Provenance?.ClinicalContext,"INVESTIGATION")))
            .Where(e=>e.ClinicalContext!=null).Select(e=>new LocalEntitySpan(e.Item3,e.ClinicalContext!.SourceStart,e.ClinicalContext.SourceEnd,e.Name,null,"evidence_validation")).ToList();
        foreach(var entity in result.Medications.Select(m=>(m.Name,m.Evidence,m.Provenance))
            .Concat(result.Investigations.Select(i=>(i.Name,i.Evidence,i.Provenance))))
        {
            Verify(source,entity.Evidence);
            if(!entity.Evidence.Quote.Contains(entity.Name,StringComparison.OrdinalIgnoreCase)) throw new InvalidClinicalEvidenceException();
            if(entity.Provenance?.ClinicalContext is not {} context) continue;
            if(context.SourceStart<0 || context.SourceEnd>source.Length || context.SourceEnd<=context.SourceStart ||
                !source[context.SourceStart..context.SourceEnd].Equals(entity.Name,StringComparison.Ordinal) ||
                context.SourceStart<entity.Evidence.Start || context.SourceEnd>entity.Evidence.End) throw new InvalidClinicalEvidenceException();
            foreach(var attr in context.Attributes)
            {
                Verify(source,attr.Evidence);
                if(attr.Evidence.Start<entity.Evidence.Start || attr.Evidence.End>entity.Evidence.End) throw new InvalidClinicalEvidenceException();
                var medication=spans.FirstOrDefault(s=>s.Category=="MEDICATION" && s.Start==context.SourceStart && s.End==context.SourceEnd);
                if(medication!=null)
                {
                    var own=LocalClinicalScope.Attributes(source,medication,spans);
                    if(attr.Evidence.Start<own.Start || attr.Evidence.End>own.End) throw new InvalidClinicalEvidenceException();
                }
            }
        }
    }
    public static void Verify(string source,EvidenceDto evidence)
    {
        if(evidence.Start is not int start || evidence.End is not int end || start<0 || end>source.Length || end<=start ||
            source[start..end]!=evidence.Quote || !evidence.Verified) throw new InvalidClinicalEvidenceException();
    }
}
