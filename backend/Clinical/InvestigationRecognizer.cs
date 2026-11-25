using System.Text.RegularExpressions;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

/// <summary>Independent of the BC5CDR label space. Catalog aliases plus bounded clinical phrase grammars.</summary>
public static class InvestigationRecognizer
{
    private static readonly Regex Productive = new(@"\b(?:(?:fasting|random|postprandial)\s+(?:blood|plasma|serum)\s+(?:sugar|glucose)|(?:sputum|urine|blood|stool|CSF|wound|throat)\s+(?:cultures?|microscopy|cytology)|(?:platelet|white blood cell|red blood cell|reticulocyte|differential)\s+count|(?:bone marrow|lymph node|skin|liver|renal)\s+(?:aspirat(?:e|ion)|biopsy)|(?:[A-Za-z]+\s+){0,2}(?:mutation testing|antigen testing))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,TimeSpan.FromSeconds(1));

    public static List<LocalEntitySpan> Recognize(string source,IReadOnlyList<ClinicalSection> sections,OnnxExecutionReceipt receipt)
    {
        var candidates=VersionedClinicalTerminology.Recognize(source,"INVESTIGATION").ToList();
        var medicationTerms=VersionedClinicalTerminology.Recognize(source,"MEDICATION").ToList();
        foreach(Match m in Productive.Matches(source))
            if(!candidates.Any(c=>c.Start<m.Index+m.Length && c.End>m.Index))
                candidates.Add(new("INVESTIGATION",m.Index,m.Index+m.Length,m.Value,null,"INVESTIGATION_PHRASE_GRAMMAR"));
        var accepted=new List<LocalEntitySpan>();
        foreach(var initial in candidates.OrderBy(c=>c.Start).ThenByDescending(c=>c.End-c.Start))
        {
            var entity=initial;
            string section=new ContextEngine().DetermineSection(entity.Start,sections);
            var bounds=LocalClinicalScope.Sentence(source,entity.Start,entity.End);
            string scope=source[bounds.Start..bounds.End];
            bool clinical=section is "LABS" or "INVESTIGATIONS_PERFORMED" or "INVESTIGATION_HISTORY" or "INVESTIGATIONS_PLANNED" or "FOLLOW_UP_PLAN" ||
                LocalClinicalScope.Has(scope,@"\b(?:order\w*|obtain|check|send|repeat|report|attend|performed|showed|shows|scan|study|result\w*|grew|confirmed|require\w*|plan\w*)\b");
            string? reject=medicationTerms.Any(m=>m.Start<=entity.Start && m.End>=entity.End) || MedicationCandidateClassifier.IsTherapeuticAdministration(source,entity,sections) ? "MEDICATION_ADMINISTRATION_NOT_DIAGNOSTIC_TEST" :
                InvestigationMentionRules.IsThreshold(scope) ? "DIAGNOSTIC_THRESHOLD_WITHOUT_TEST_ACTION" :
                entity.Text.Length<=3 && entity.Text.Equals("CT",StringComparison.OrdinalIgnoreCase) && !clinical ? "AMBIGUOUS_ABBREVIATION_WITHOUT_DIAGNOSTIC_CONTEXT" :
                LocalClinicalScope.Has(scope,@"\b(?:initials|technician|district|postal)\b") && !clinical ? "NONCLINICAL_ABBREVIATION" : null;
            if(reject!=null)
            {
                receipt.CandidateTraces.Add(new(entity.Text,entity.Category,section,entity.Start,entity.End,null,entity.DetectionSource,
                    entity.CatalogMatch,[],scope,"rejected_false_positive",null,reject,["detected","rejected_false_positive"]));
                continue;
            }
            // Imaging expansion uses a bounded anatomical vocabulary; it cannot consume narrative prose.
            if(LocalClinicalScope.Has(entity.Text,@"^(?:CT|CT scan|MRI|MRI scan|ultrasound|USG)$"))
            {
                var qualifier=Regex.Match(source[entity.End..],@"^[ \t]+(?:(?:of[ \t]+(?:the[ \t]+)?)?)(?:brain|head|chest|abdomen|pelvis|spine|neck|thorax|knee|renal)(?:[ \t]+(?:and[ \t]+)?(?:pelvis|abdomen))?\b",RegexOptions.IgnoreCase);
                if(qualifier.Success) entity=entity with{End=entity.End+qualifier.Length,Text=source[entity.Start..(entity.End+qualifier.Length)]};
                var prefix=Regex.Match(source[Math.Max(0,entity.Start-40)..entity.Start],@"\b(?:high[- ]resolution|low[- ]dose|non[- ]contrast|contrast[- ]enhanced)[ \t]+$",RegexOptions.IgnoreCase);
                if(prefix.Success) entity=entity with{Start=entity.Start-prefix.Length,Text=source[(entity.Start-prefix.Length)..entity.End]};
            }
            var qualified=InvestigationMentionRules.Qualify(source,new("INVESTIGATION",entity.Start,entity.End,entity.Text,0));
            // Retain only the reviewed Troponin subtype extension from the legacy broad qualifier.
            if(entity.Text.Equals("Troponin",StringComparison.OrdinalIgnoreCase))
                entity=entity with{End=qualified.EndChar,Text=qualified.Text};
            if(accepted.Any(c=>c.Start<entity.End && c.End>entity.Start)) continue;
            accepted.Add(entity);
        }
        return accepted;
    }
}
