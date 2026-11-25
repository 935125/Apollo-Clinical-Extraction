using System.Diagnostics;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

/// <summary>Source-preserving local mention extraction. No model output is promoted directly to an active order.</summary>
public sealed class UpgradedClinicalPipeline(SlidingWindowOnnxEngine engine)
{
    public ClinicalExtractionResponse Process(ClinicalExtractionRequest request,string correlationId,OnnxExecutionReceipt receipt,
        CancellationToken ct=default,LocalPipelineFeatures? features=null,ClinicalExtractionResponse? baseline=null)
    {
        var watch=Stopwatch.StartNew();
        features ??= new();
        string source=request.NoteText;
        ct.ThrowIfCancellationRequested();
        var raw=engine.InferSpans(source,receipt,contextualWindows:features.ContextualWindows,cancellationToken:ct);
        receipt.ModelSpans.AddRange(raw);
        var sections=features.Sections ? ClinicalSectionDetector.Detect(source) : new ContextEngine().SegmentSections(source);
        for(int i=0;i<receipt.CandidateTraces.Count;i++)
        {
            var trace=receipt.CandidateTraces[i];
            receipt.CandidateTraces[i]=trace with{Section=new ContextEngine().DetermineSection(trace.Start,sections)};
        }
        if((!features.MedicationCandidates || !features.Investigations || !features.Assertions) && baseline==null)
            throw new ArgumentException("Offline ablations require the frozen legacy prediction for this exact source",nameof(baseline));
        List<LocalEntitySpan> LegacySpans(bool medication) => (medication
            ? baseline!.Medications.Select(m=>(m.Name,m.Evidence)) : baseline!.Investigations.Select(i=>(i.Name,i.Evidence)))
            .Select(x=>{ int start=x.Evidence.Start!.Value+x.Evidence.Quote.IndexOf(x.Name,StringComparison.OrdinalIgnoreCase);
                return new LocalEntitySpan(medication ? "MEDICATION" : "INVESTIGATION",start,start+x.Name.Length,x.Name,null,"LEGACY_ABLATION"); }).ToList();
        var invs=features.Investigations ? InvestigationRecognizer.Recognize(source,sections,receipt) : LegacySpans(false);
        var meds=features.MedicationCandidates ? MedicationCandidateClassifier.Recognize(source,raw,invs,sections,receipt) : LegacySpans(true);
        var all=meds.Concat(invs).OrderBy(e=>e.Start).ToList();
        var medications=new List<ExtractedMedicationDto>();
        var investigations=new List<ExtractedInvestigationDto>();
        var ontology=new IndianOntologyResolver();
        NormalizationDto Normalize(LocalEntitySpan e,string? strength=null)
        {
            if(features.Terminology) return VersionedClinicalTerminology.Resolve(e.Category,e.Text,strength);
            var n=e.Category=="MEDICATION" ? ontology.ResolveMedication(e.Text,strength) : ontology.ResolveInvestigation(e.Text);
            return new(n.Status,n.StandardId,n.DisplayName,n.Confidence,n.SemanticStatus,n.CandidateCode,n.TerminologySource,n.TerminologyVersion,n.MappingNote);
        }
        LocalAssertion Assertion(LocalEntitySpan e)
        {
            if(features.Assertions) return ClinicalAssertionClassifier.Classify(source,e,all,sections);
            var old=e.Category=="MEDICATION" ? baseline!.Medications.FirstOrDefault(m=>m.Name.Equals(e.Text,StringComparison.OrdinalIgnoreCase) && Contains(m.Evidence,e))?.AssertionStatus :
                baseline!.Investigations.FirstOrDefault(i=>i.Name.Equals(e.Text,StringComparison.OrdinalIgnoreCase) && Contains(i.Evidence,e))?.AssertionStatus;
            var assertion=new ContextEngine().Classify(source,e.Start,e.End,sections);
            string scope=ClinicalScope.ForEntity(source,e.Start,e.End,all.Select(s=>(s.Start,s.End)).ToList());
            scope=string.Join(" ",scope.Split('\n').Where(l=>l.Contains(e.Text,StringComparison.OrdinalIgnoreCase)));
            old ??= e.Category=="MEDICATION" ? ClinicalPipelineService.DetermineMedicationAssertionStatus(assertion,scope) :
                ClinicalScope.Negated(scope.Replace("rule out","",StringComparison.OrdinalIgnoreCase)) ? "negated" :
                assertion.Temporality==ClinicalTemporality.Historical || Regex.IsMatch(scope,@"[:=]\s*\d+") ? "historical" : ClinicalScope.Conditional(scope) ? "conditional" : "new_order";
            return new(old,old,assertion.Temporality.ToString().ToLowerInvariant(),old=="negated" ? "none" : ClinicalScope.Action(scope));
        }
        static bool Contains(EvidenceDto ev,LocalEntitySpan e)=>ev.Start<=e.Start && ev.End>=e.End;
        EvidenceDto Evidence(LocalEntitySpan e)
        {
            var (left,right)=LocalClinicalScope.Sentence(source,e.Start,e.End);
            while(left<right && char.IsWhiteSpace(source[left])) left++;
            while(right>left && char.IsWhiteSpace(source[right-1])) right--;
            return new(source[left..right],true,left,right);
        }
        string Section(LocalEntitySpan e)=>new ContextEngine().DetermineSection(e.Start,sections);
        EntityProvenanceDto Provenance(LocalEntitySpan e,LocalAssertion assertion,NormalizationDto n,List<AttributeEvidenceDto> attributes,LocalMedicationAttributes? a=null)
        {
            string origin=e.DetectionSource.StartsWith("ONNX",StringComparison.Ordinal) ? e.DetectionSource.Contains("CATALOG",StringComparison.Ordinal) ? "ONNX_AND_RULE_CONFIRMED" : "ONNX_EXTRACTED" : "RULE_EXTRACTED";
            bool action=assertion.LegacyStatus is "new_order" or "current_medication" or "discontinued" or "held";
            string state=action ? "linked_to_clinical_action" : n.MatchType=="ambiguous" ? "ambiguous" : n.Status=="unmatched" ? "unresolved" : "accepted_as_mention";
            var context=new ClinicalEntityContextDto(assertion.Assertion,assertion.Temporality,Section(e),e.Start,e.End,e.DetectionSource,e.NerScore,state,
                attributes,a?.Administration,a?.StartTiming,a?.StopTiming,a?.PrnIndication,a?.Schedule);
            var contextBounds=LocalClinicalScope.Sentence(source,e.Start,e.End);
            receipt.CandidateTraces.Add(new(e.Text,e.Category,Section(e),e.Start,e.End,e.NerScore,e.DetectionSource,e.CatalogMatch,
                [],source[contextBounds.Start..contextBounds.End],state,assertion.Assertion,"MENTION_CLASSIFIED_WITH_EXACT_SOURCE_EVIDENCE",["detected","accepted_as_mention",state]));
            return new(origin,"onnx",true,["Source-local span linking", "Clinical assertion and terminology evaluated independently"],
                n.Status=="matched" ? null : n.MappingNote,n.Status!="matched",ClinicalContext:context);
        }
        (List<string>,List<FindingDto>) Findings(NormalizationDto n,LocalEntitySpan e,bool missingDose)
        {
            var flags=new List<string>(); var findings=new List<FindingDto>();
            if(n.Status!="matched") { flags.Add("TERMINOLOGY_UNRESOLVED"); findings.Add(new("TERMINOLOGY_UNRESOLVED","warning","name",n.MappingNote ?? "Standard code unresolved; source mention retained.")); }
            if(missingDose) { flags.Add("DOSE_UNIT_MISSING"); findings.Add(new("DOSE_UNIT_MISSING","warning","dose","Administered dose is not documented in this medication's attribute scope.")); }
            return (flags,findings);
        }
        foreach(var e in meds)
        {
            ct.ThrowIfCancellationRequested();
            var a=MedicationAttributeExtractor.Extract(source,e,all);
            if(!features.Attributes)
            {
                var old=new PosologyBinder().Bind(source,e.Start,e.End,all.Select(s=>(s.Start,s.End)).ToList());
                a=new(old.Strength,old.Dose,ClinicalScope.DoseUnit(old.Dose),old.Route,old.Frequency,old.Duration,old.Formulation,null,null,null,null,[]);
            }
            var assertion=Assertion(e);
            var norm=Normalize(e,a.Strength);
            var (flags,findings)=Findings(norm,e,a.Dose==null && assertion.LegacyStatus is "new_order" or "current_medication");
            var provenance=Provenance(e,assertion,norm,a.Evidence,a);
            var evidence=Evidence(e);
            medications.Add(new(e.Text,a.Strength,a.Dose,a.Route,a.Frequency,a.Duration,a.Formulation,
                a.Administration!=null || a.Schedule!=null || assertion.LegacyStatus=="conditional" || a.StartTiming!=null || a.StopTiming!=null ? evidence.Quote : null,
                assertion.LegacyStatus,evidence,flags,findings,norm,a.DoseUnit,assertion.Action,provenance.Origin,provenance));
        }
        foreach(var e in invs)
        {
            ct.ThrowIfCancellationRequested();
            var assertion=Assertion(e); var norm=Normalize(e);
            var (flags,findings)=Findings(norm,e,false);
            var evidence=Evidence(e);
            var range=LocalClinicalScope.Attributes(source,e,all);
            string scope=source[range.Start..range.End];
            var attributes=new List<AttributeEvidenceDto>();
            var date=Regex.Match(evidence.Quote,@"\b\d{1,2}/\d{1,2}/\d{4}(?:\s+at\s+\d{1,2}:\d{2}\s*(?:AM|PM)?)?\b",RegexOptions.IgnoreCase);
            string? timing=null;
            if(date.Success)
            {
                timing=date.Value;
                int start=evidence.Start!.Value+date.Index;
                attributes.Add(new("timing",timing,new(date.Value,true,start,start+date.Length)));
            }
            else
            {
                timing=LocalClinicalAttributes.Timing(source,e.Start,e.End,all.Select(s=>(s.Start,s.End)).ToList());
                if(timing!=null)
                {
                    int index=evidence.Quote.IndexOf(timing,StringComparison.Ordinal);
                    if(index>=0) attributes.Add(new("timing",timing,new(timing,true,evidence.Start+index,evidence.Start+index+timing.Length)));
                    else timing=null;
                }
            }
            string? specimen=LocalClinicalAttributes.Specimen(e.Text,scope);
            if(specimen!=null)
            {
                int index=evidence.Quote.IndexOf(specimen,StringComparison.OrdinalIgnoreCase);
                if(index>=0) attributes.Add(new("specimen",specimen,new(evidence.Quote.Substring(index,specimen.Length),true,evidence.Start+index,evidence.Start+index+specimen.Length)));
            }
            string after=source[e.End..range.End];
            var value=Regex.Match(after,@"^\s*[:=]?\s*(?<value>\d+(?:[.,]\d+)*\s*(?:g/dL|mg/dL|mmol/L|mEq/L|/uL|%|U/L))\b",RegexOptions.IgnoreCase);
            string? resultValue=assertion.Assertion=="result_documented" && value.Success ? value.Groups["value"].Value : null;
            var provenance=Provenance(e,assertion,norm,attributes);
            investigations.Add(new(e.Text,timing,specimen,assertion.LegacyStatus,evidence,flags,findings,norm,norm.MatchedCode,norm.DisplayName,
                assertion.Assertion=="result_documented" ? "final" : "preliminary",ClinicalResultValidator.ClassifyInvestigationCategory(e.Text),
                ResultValue:resultValue,Origin:provenance.Origin,Provenance:provenance));
        }
        receipt.ModelMedicationMentions=raw.Count(s=>s.Category=="MEDICATION");
        receipt.RuleMedicationMentions=meds.Count(s=>s.DetectionSource!="ONNX_CHEMICAL");
        receipt.RuleInvestigationMentions=invs.Count;
        var safety=new ClinicalSafetyRuleEngine().Evaluate(source,medications,investigations);
        var response=new ClinicalExtractionResponse("1.0.0",Guid.NewGuid().ToString("D"),correlationId,DateTimeOffset.UtcNow.ToString("O"),watch.Elapsed.TotalMilliseconds,
            new("onnx_neurosymbolic_int8","bc5cdr_int8","clinical-local-v15",Provider:"onnx",Model:"bc5cdr_int8",InferenceExecuted:receipt.WindowsExecuted>0,
                ExecutionMode:receipt.ExecutionMode,LocalPipelineVersion:"CandidateV2",RecognitionCatalogVersion:VersionedClinicalTerminology.Version),
            new(source.Length,sections.Select(s=>new SectionSpanDto(s.Code,s.Heading,s.Start,s.End)).ToList()),medications,investigations,
            new(false,0,medications.Sum(m=>m.Findings.Count)+investigations.Sum(i=>i.Findings.Count)),safety,ClinicalEmbeddingGenerator.GenerateEmbedding(source),source);
        LocalEvidenceValidator.Validate(response,source);
        return response;
    }
}
