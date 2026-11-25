using System.Text.RegularExpressions;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

public static class MedicationCandidateClassifier
{
    private static readonly Regex Prescription = new(@"(?<![\p{L}\p{N}])(?<name>[A-Za-z][A-Za-z-]{3,})[ \t]+\d+(?:\.\d+)?[ \t]*(?:mg|mcg|ug|g|units?)(?:[ \t]*/[ \t]*(?:kg|m2|m²|mL))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,TimeSpan.FromSeconds(1));
    public static List<LocalEntitySpan> Recognize(string source,IReadOnlyList<RawSpan> neural,IReadOnlyList<LocalEntitySpan> investigations,
        IReadOnlyList<ClinicalSection> sections,OnnxExecutionReceipt receipt)
    {
        var catalog=VersionedClinicalTerminology.Recognize(source,"MEDICATION").ToList();
        var candidates=catalog.Concat(neural.Where(s=>s.Category=="MEDICATION").Select(s=>
            new LocalEntitySpan("MEDICATION",s.StartChar,s.EndChar,s.Text,s.Confidence,"ONNX_CHEMICAL",VersionedClinicalTerminology.MatchType("MEDICATION",s.Text)))).ToList();
        foreach(Match match in Prescription.Matches(source))
        {
            var name=match.Groups["name"];
            if(candidates.Any(c=>c.Start<name.Index+name.Length && c.End>name.Index)) continue;
            var bounds=LocalClinicalScope.Sentence(source,name.Index,name.Index+name.Length);
            string scope=source[bounds.Start..bounds.End];
            // Unknown lexical candidates require two independent clinical supports (dose + route/action/medication heading).
            string section=new ContextEngine().DetermineSection(name.Index,sections);
            if(LocalClinicalScope.Has(scope,LocalClinicalScope.Actions+@"|\b(?:PO|oral|IV|IM|SC|subcutaneous|inhaled|topical)\b") ||
                section is "MEDICATIONS" or "CHEMOTHERAPY" or "CURRENT_TREATMENT")
                candidates.Add(new("MEDICATION",name.Index,name.Index+name.Length,name.Value,null,"PRESCRIPTION_PHRASE_GRAMMAR"));
        }
        var result=new List<LocalEntitySpan>();
        foreach(var initial in candidates.OrderBy(c=>c.Start).ThenByDescending(c=>c.End-c.Start))
        {
            var entity=initial;
            var model=neural.Where(s=>s.Category=="MEDICATION" && s.StartChar<entity.End && s.EndChar>entity.Start).MaxBy(s=>s.Confidence);
            if(entity.DetectionSource=="VERSIONED_CATALOG" && model!=null)
                entity=entity with {NerScore=model.Confidence,DetectionSource="ONNX_AND_VERSIONED_CATALOG"};
            string section=new ContextEngine().DetermineSection(entity.Start,sections);
            var bounds=LocalClinicalScope.Sentence(source,entity.Start,entity.End);
            string scope=source[bounds.Start..bounds.End];
            var signals=new List<string>();
            bool catalogMatch=entity.CatalogMatch!="unresolved";
            if(catalogMatch) signals.Add("catalog_"+entity.CatalogMatch);
            if(entity.NerScore!=null) signals.Add("onnx_chemical_detection");
            if(LocalClinicalScope.Has(scope,@"\b\d+(?:\.\d+)?\s*(?:mg|mcg|ug|g|units?|mL)(?:\s*/\s*(?:m2|m²|kg|mL))?\b")) signals.Add("documented_dose_or_strength");
            if(LocalClinicalScope.Has(scope,@"\b(?:PO|oral|IV|IM|SC|subcutaneous|inhaled|topical|ophthalmic|intranasal)\b")) signals.Add("route");
            if(LocalClinicalScope.Has(scope,@"\b(?:daily|weekly|BID|BD|q\d+h|every \d+ hours|SOS|PRN|infusion)\b")) signals.Add("schedule");
            if(section is "MEDICATIONS" or "CHEMOTHERAPY" or "CURRENT_TREATMENT" or "MEDICATION_HISTORY" or "DISCONTINUED_MEDICATIONS" or "ALLERGIES") signals.Add("medication_section");
            if(LocalClinicalScope.Has(scope,LocalClinicalScope.Actions)) signals.Add("medication_action");
            bool assertionContext=LocalClinicalScope.Has(scope,@"\b(?:allerg\w*|not taking|denies|previously|received|completed|education|consider|unclear)\b");
            if(assertionContext) signals.Add("mention_assertion");
            string? reject=!SlidingWindowOnnxEngine.IsValidMedicationSpan(entity.Text) ? "LEXICALLY_INVALID_MEDICATION" :
                investigations.Any(i=>i.Start<entity.End && i.End>entity.Start) ? "INVESTIGATION_NOT_MEDICATION" :
                LocalClinicalScope.Has(scope,@"\b(?:occupational exposure|industrial|solvent|reagent|technician|postal|district)\b") && !catalogMatch ? "NONTHERAPEUTIC_CHEMICAL_CONTEXT" :
                LocalClinicalScope.Has(source[Math.Max(bounds.Start,entity.Start-18)..entity.Start],@"\b(?:serum|plasma|urine|blood)\s+$") && !LocalClinicalScope.Has(scope,@"\b(?:give|administer|start|infuse)\b") ? "SPECIMEN_ANALYTE_CONTEXT" :
                !catalogMatch && !signals.Any(s=>s is "documented_dose_or_strength" or "medication_section" or "medication_action" or "mention_assertion") ? "CHEMICAL_WITHOUT_MEDICATION_SUPPORT" : null;
            bool duplicate=result.Any(c=>c.Start<entity.End && c.End>entity.Start);
            if(reject==null && !duplicate) result.Add(entity);
            string status=reject!=null ? "rejected_false_positive" : entity.CatalogMatch=="ambiguous" ? "ambiguous" :
                entity.CatalogMatch=="unresolved" ? "unresolved" : "accepted_as_mention";
            receipt.CandidateTraces.Add(new(entity.Text,entity.Category,section,entity.Start,entity.End,entity.NerScore,entity.DetectionSource,
                entity.CatalogMatch,signals,assertionContext ? "explicit_assertion_context" : "no_explicit_assertion",status,null,
                reject ?? (duplicate ? "MERGED_WITH_OVERLAPPING_ACCEPTED_MENTION" : "SOURCE_GROUNDED_MENTION_RETAINED_WITHOUT_CONFIDENCE_CUTOFF"),["detected",status]));
        }
        return result;
    }
}
