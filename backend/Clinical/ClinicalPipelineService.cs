using System.Diagnostics;
using System.Text.RegularExpressions;
using Apollo.Backend.Fhir;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Apollo.Backend.Security;

namespace Apollo.Backend.Clinical;

public partial class ClinicalPipelineService
{
    private readonly SlidingWindowOnnxEngine _onnxEngine;
    private readonly ContextEngine _contextEngine = new();
    private readonly PosologyBinder _posologyBinder = new();
    private readonly IndianOntologyResolver _ontologyResolver = new();
    private readonly ClinicalSafetyRuleEngine _safetyEngine = new();

    public ClinicalPipelineService(SlidingWindowOnnxEngine onnxEngine)
    {
        _onnxEngine = onnxEngine;
    }

    private List<RawSpan> CollectAndFilterSpans(string text, IReadOnlyList<ClinicalSection> sections, OnnxExecutionReceipt? receipt = null)
    {
        var rawSpans = _onnxEngine.InferSpans(text, receipt);
        var occupiedRanges = new List<(int Start, int End)>();

        bool IsRangeFree(int start, int end)
        {
            foreach (var (os, oe) in occupiedRanges)
            {
                if (end > os && start < oe)
                    return false;
            }
            return true;
        }

        // 1. Deterministic Section-Aware Investigation & Lab Extraction (Longest-match first)
        var labSpans = new List<RawSpan>();
        foreach (var term in IndianOntologyResolver.KnownLabTerms)
        {
            var pattern = $@"(?<![A-Za-z0-9-]){Regex.Escape(term)}(?![A-Za-z0-9-])";
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                int ms = m.Index;
                int me = m.Index + m.Length;
                if (!IsRangeFree(ms, me))
                    continue;

                // Avoid matching generic "CT" or "US" inside non-clinical contexts unless in LABS section or order context
                string sec = _contextEngine.DetermineSection(ms, sections);
                string line = GetLineContaining(text, ms, me);
                bool isShortAcronym = term.Length <= 3 && (term.Equals("ct", StringComparison.OrdinalIgnoreCase));
                if (isShortAcronym && sec != "LABS" && !LabOrderCueRegex().IsMatch(line))
                    continue;

                string scope = ClinicalScope.Around(text, ms, me);
                if (InvestigationMentionRules.IsThreshold(scope)) continue;
                var qualified = InvestigationMentionRules.Qualify(text, new RawSpan("INVESTIGATION", ms, me, m.Value, 0.98, "RULE_EXTRACTED"));
                if (!IsRangeFree(qualified.StartChar, qualified.EndChar)) continue;
                occupiedRanges.Add((qualified.StartChar, qualified.EndChar));
                labSpans.Add(qualified);
            }
        }

        foreach (var recovered in InvestigationMentionRules.ProductiveMentions(text))
        {
            if (!IsRangeFree(recovered.StartChar, recovered.EndChar)) continue;
            occupiedRanges.Add((recovered.StartChar, recovered.EndChar));
            labSpans.Add(recovered);
        }

        // 2. Deterministic Formulary Medication Recovery (handles hyphenated compounds like amoxicillin-clavulanate)
        var recoveredMedSpans = new List<RawSpan>();
        foreach (var drugTerm in IndianOntologyResolver.KnownDrugTerms)
        {
            var pattern = $@"(?<![A-Za-z0-9-]){Regex.Escape(drugTerm)}(?![A-Za-z0-9-])";
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                int ms = m.Index;
                int me = m.Index + m.Length;
                if (!IsRangeFree(ms, me))
                    continue;

                string sec = _contextEngine.DetermineSection(ms, sections);
                string line = GetLineContaining(text, ms, me);

                // Prevent tokens in LABS section from being classified as MEDICATION unless preceded by explicit Rx verb/prefix
                if (sec == "LABS" && !ExplicitRxPrefixRegex().IsMatch(line))
                    continue;

                bool alsoMatchedByOnnx = rawSpans.Any(s =>
                    s.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase) &&
                    s.EndChar > ms && s.StartChar < me);
                occupiedRanges.Add((ms, me));
                recoveredMedSpans.Add(new RawSpan("MEDICATION", ms, me, m.Value, 0.98)
                {
                    Origin = alsoMatchedByOnnx ? "ONNX_AND_RULE_CONFIRMED" : "RULE_EXTRACTED"
                });
            }
        }

        // Retain explicitly documented allergy mentions even if they are absent from the formulary.
        foreach (Match match in Regex.Matches(text, @"(?i)\b(?<name>[a-z][a-z-]+)\s+is\s+(?:listed\s+as\s+)?an?\s+allergy\b|\ballerg(?:y|ies)\s*:\s*(?<name>[a-z][a-z-]+)"))
        {
            var name = match.Groups["name"];
            if (name.Value.ToLowerInvariant() is "patient" or "denies" or "denied" or "nkda" or "nka" or "none" or "no" or "unknown") continue;
            if (IsRangeFree(name.Index, name.Index + name.Length))
            {
                occupiedRanges.Add((name.Index, name.Index + name.Length));
                recoveredMedSpans.Add(new RawSpan("MEDICATION", name.Index, name.Index + name.Length, name.Value, 1.0)
                {
                    Origin = "RULE_EXTRACTED"
                });
            }
        }

        // 3. Filter ONNX / Fallback Raw Spans (Stopwords, LABS section guard, Fail-Closed >= 0.85 gate)
        var filteredSpans = new List<RawSpan>();
        foreach (var span in rawSpans)
        {
            string clean = span.Text.Trim();
            if (clean.Length <= 1 || SlidingWindowOnnxEngine.FalsePositiveStoplist.Contains(clean))
                continue;

            if (PurePunctuationOrDigitsRegex().IsMatch(clean))
                continue;

            // Skip if already covered by deterministic lab or formulary span
            if (!IsRangeFree(span.StartChar, span.EndChar))
                continue;

            if (span.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
            {
                if (!SlidingWindowOnnxEngine.IsValidMedicationSpan(clean))
                    continue;

                string sec = _contextEngine.DetermineSection(span.StartChar, sections);
                string line = GetLineContaining(text, span.StartChar, span.EndChar);
                if (InvestigationMentionRules.IsThreshold(ClinicalScope.Around(text, span.StartChar, span.EndChar)) &&
                    IndianOntologyResolver.KnownLabTerms.Any(term => term.Equals(clean, StringComparison.OrdinalIgnoreCase) || term.EndsWith(" " + clean, StringComparison.OrdinalIgnoreCase)))
                    continue; // A diagnostic threshold cannot become a drug when its investigation mention is suppressed.

                // Prevent tokens in LABS/Investigations section or preceded by "serum"/"plasma"/"urine" from leaking into MEDICATION
                int lookbackStart = Math.Max(0, span.StartChar - 16);
                string precedingTokenWindow = text[lookbackStart..span.StartChar];
                if ((sec == "LABS" && !ExplicitRxPrefixRegex().IsMatch(line)) ||
                    Regex.IsMatch(precedingTokenWindow, @"(?i)\b(?:serum|plasma|urine|blood|repeat)\s+$"))
                    continue;

                var ontMatch = _ontologyResolver.ResolveMedication(clean);
                if (ontMatch.Status != "matched")
                {
                    // Fail-closed confidence threshold (>= 0.85) AND require explicit prescription context for unknown drugs
                    if (span.Confidence < SlidingWindowOnnxEngine.FailClosedConfidenceThreshold || !ExplicitRxPrefixRegex().IsMatch(line))
                    {
                        string reason = span.Confidence < SlidingWindowOnnxEngine.FailClosedConfidenceThreshold
                            ? "BELOW_NEURAL_CONFIDENCE_THRESHOLD" : "NO_EXPLICIT_PRESCRIPTION_PREFIX";
                        receipt?.ReviewCandidates.Add(new SourceReviewCandidateDto(clean, "MEDICATION", span.Confidence,
                            new EvidenceDto(text[span.StartChar..span.EndChar], true, span.StartChar, span.EndChar), reason));
                        continue;
                    }
                }
            }

            occupiedRanges.Add((span.StartChar, span.EndChar));
            filteredSpans.Add(span);
        }

        if (receipt != null)
        {
            // Raw model detections, before filtering or catalog overlap. Rule counts
            // overlap this count and must not be added to it as independent mentions.
            receipt.ModelMedicationMentions = rawSpans.Count(s => s.Category == "MEDICATION");
            receipt.RuleMedicationMentions = recoveredMedSpans.Count;
            receipt.RuleInvestigationMentions = labSpans.Count;
        }
        filteredSpans.AddRange(recoveredMedSpans);
        filteredSpans.AddRange(labSpans);
        filteredSpans.Sort((a, b) => a.StartChar.CompareTo(b.StartChar));
        // A long form immediately followed by its catalog abbreviation is one mention.
        var distinct = new List<RawSpan>();
        foreach (var span in filteredSpans)
        {
            var prior = distinct.LastOrDefault();
            bool alias = prior != null && prior.Category == "INVESTIGATION" && span.Category == "INVESTIGATION" &&
                Regex.IsMatch(text[prior.EndChar..span.StartChar], @"^\s*\(\s*$") &&
                span.EndChar < text.Length && text[span.EndChar] == ')' &&
                _ontologyResolver.ResolveInvestigation(prior.Text).DisplayName is { } display &&
                display == _ontologyResolver.ResolveInvestigation(span.Text).DisplayName;
            if (!alias) distinct.Add(span);
        }
        return distinct;
    }

    public ClinicalNERResponse ProcessApolloNer(ClinicalNERRequest request)
    {
        var sw = Stopwatch.StartNew();

        // 1. Zero-Leakage PII Validation (Fail closed with PIILeakageException)
        ZeroLeakageValidator.Validate(request.Text);

        // 2. Section Segmentation
        var sections = _contextEngine.SegmentSections(request.Text);

        // 3. Collect & Filter Spans (Stopword filtering + Fail-closed >= 0.85 + Section-aware Labs)
        var validSpans = CollectAndFilterSpans(request.Text, sections);

        // 4. PosologyBinder runs strictly AFTER stopword/confidence filtering
        var clinicalEntities = new List<ClinicalEntityResponse>();
        var otherDrugSpans = validSpans
            .Where(s => s.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.StartChar, s.EndChar))
            .ToList();

        var structuredMeds = new List<StructuredMedicationResponse>();

        foreach (var span in validSpans)
        {
            var assertion = request.ApplyAssertion
                ? _contextEngine.Classify(request.Text, span.StartChar, span.EndChar, sections)
                : new ClinicalAssertion(ClinicalCertainty.Positive, ClinicalTemporality.Current, ClinicalExperiencer.Patient, "OTHER");

            string? conceptCode = null;
            string? canonicalName = null;

            if (request.EnableGrounding)
            {
                if (span.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
                {
                    var match = _ontologyResolver.ResolveMedication(span.Text);
                    if (match.Status == "matched")
                    {
                        conceptCode = match.StandardId != null ? $"RxNorm:{match.StandardId}" : match.Code;
                        canonicalName = match.DisplayName;
                    }
                    else
                    {
                    }
                }
                else if (span.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                         span.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase) ||
                         span.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase))
                {
                    var match = _ontologyResolver.ResolveInvestigation(span.Text);
                    if (match.Status == "matched")
                    {
                        conceptCode = match.StandardId != null ? $"LOINC:{match.StandardId}" : match.Code;
                        canonicalName = match.DisplayName;
                    }
                    else
                    {
                    }
                }
            }

            clinicalEntities.Add(new ClinicalEntityResponse(
                Text: span.Text,
                Category: span.Category,
                StartChar: span.StartChar,
                EndChar: span.EndChar,
                Confidence: Math.Round(span.Confidence, 4),
                Certainty: assertion.Certainty.ToString(),
                Temporality: assertion.Temporality.ToString(),
                Experiencer: assertion.Experiencer.ToString(),
                Section: assertion.Section,
                ConceptCode: conceptCode,
                CanonicalName: canonicalName
            ));

            if (span.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
            {
                var bound = _posologyBinder.Bind(request.Text, span.StartChar, span.EndChar, otherDrugSpans);
                structuredMeds.Add(new StructuredMedicationResponse(
                    Drug: span.Text,
                    DrugSpan: [span.StartChar, span.EndChar],
                    Dosage: bound.Dose,
                    Dose: bound.Dose,
                    Route: bound.Route,
                    Frequency: bound.Frequency,
                    Duration: bound.Duration,
                    Certainty: assertion.Certainty.ToString(),
                    Experiencer: assertion.Experiencer.ToString(),
                    ConceptCode: conceptCode
                ));
            }
        }

        // 5. Dense Token-Compressed Micro-Card
        string microCard = MicroCardBuilder.Build(request.Text, clinicalEntities, structuredMeds);

        // 6. HL7 FHIR R4 Bundle
        var fhirBundle = request.IncludeFhir
            ? FhirR4Mapper.ToFhirBundle(request.Text, clinicalEntities, structuredMeds)
            : new Dictionary<string, object?>();

        sw.Stop();

        return new ClinicalNERResponse(
            Entities: clinicalEntities,
            StructuredMedications: structuredMeds,
            MicroCard: microCard,
            FhirBundle: fhirBundle,
            LatencyMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            ModelVersion: _onnxEngine.HasOnnxModel ? "2.1.0-onnx-int8" : "deterministic-test-scoring-no-neural-inference",
            Text: request.IncludeText ? request.Text : null
        );
    }

    public ClinicalExtractionResponse ProcessPlatformExtraction(ClinicalExtractionRequest request, string correlationId, OnnxExecutionReceipt? receipt = null)
    {
        var sw = Stopwatch.StartNew();
        receipt ??= new OnnxExecutionReceipt();

        // 1. Zero-Leakage PII Validation
        ZeroLeakageValidator.Validate(request.NoteText);

        // 2. Sections
        var sections = _contextEngine.SegmentSections(request.NoteText);
        var sectionDtos = sections.Select(s => new SectionSpanDto(s.Code, s.Heading, s.Start, s.End)).ToList();

        // 3. Collect & Filter Spans (Stopword filtering + Fail-closed >= 0.85 + Section-aware Labs)
        var validSpans = CollectAndFilterSpans(request.NoteText, sections, receipt);

        // 4. PosologyBinder runs strictly AFTER stopword/confidence filtering
        var allEntitySpans = validSpans.Where(s => s.Category is "MEDICATION" or "INVESTIGATION" or "LAB" or "LAB_TEST").Select(s => (Start: s.StartChar, End: s.EndChar)).ToList();
        var otherDrugSpans = validSpans
            .Where(s => s.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.StartChar, s.EndChar))
            .ToList();

        var medications = new List<ExtractedMedicationDto>();
        var investigations = new List<ExtractedInvestigationDto>();
        int errorCount = 0;
        int warningCount = 0;

        foreach (var span in validSpans)
        {
            var assertion = _contextEngine.Classify(request.NoteText, span.StartChar, span.EndChar, sections);

            if (span.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
            {
                var bound = _posologyBinder.Bind(request.NoteText, span.StartChar, span.EndChar, allEntitySpans);
                var normMatch = _ontologyResolver.ResolveMedication(span.Text, bound.Strength);

                var qualityFlags = new List<string>();
                var findings = new List<FindingDto>();

                if (bound.Strength == null && bound.Dose == null && assertion.Certainty != ClinicalCertainty.Negated)
                {
                    qualityFlags.Add("DOSE_UNIT_MISSING");
                    findings.Add(new FindingDto("DOSE_UNIT_MISSING", "warning", "dose", "No dosage could be isolated for this medication order."));
                    warningCount++;
                }

                if (normMatch.Status is "unmatched" or "uncertain")
                {
                    string flag = normMatch.Status == "uncertain" ? "TERMINOLOGY_UNCERTAIN" : "TERMINOLOGY_UNMATCHED";
                    qualityFlags.Add(flag);
                    findings.Add(new FindingDto(flag, "warning", "name", normMatch.Status == "uncertain"
                        ? "Medication name matched, but the documented strength did not match the formulary concept."
                        : "Medication mention could not be matched against standard formulary."));
                    warningCount++;
                }

                string line = ClinicalScope.ForEntity(request.NoteText, span.StartChar, span.EndChar, allEntitySpans);
                line = string.Join(" ", line.Split('\n').Where(l => l.Contains(span.Text, StringComparison.OrdinalIgnoreCase)));
                var evidence = GetLineEvidence(request.NoteText, span.StartChar, span.EndChar);
                string assertionStatus = DetermineMedicationAssertionStatus(assertion, line);
                string medOrigin = span.Origin ?? "ONNX_EXTRACTED";

                medications.Add(new ExtractedMedicationDto(
                    Name: span.Text,
                    Strength: bound.Strength,
                    Dose: bound.Dose,
                    Route: bound.Route,
                    Frequency: bound.Frequency,
                    Duration: bound.Duration,
                    Formulation: bound.Formulation,
                    Instructions: ClinicalScope.Action(line) is "increase" or "decrease" || Regex.IsMatch(line, @"\bthen\b|\bday\s*\d|\bas needed\b|\bprn\b|\bsos\b|\bweekly\b", RegexOptions.IgnoreCase) ? line.Trim() : null,
                    AssertionStatus: assertionStatus,
                    Evidence: evidence,
                    QualityFlags: qualityFlags,
                    Findings: findings,
                    Normalization: new NormalizationDto(
                        Status: normMatch.Status,
                        MatchedCode: normMatch.StandardId,
                        DisplayName: normMatch.DisplayName,
                        Confidence: normMatch.Confidence,
                        SemanticStatus: normMatch.SemanticStatus,
                        CandidateCode: normMatch.CandidateCode,
                        TerminologySource: normMatch.TerminologySource,
                        TerminologyVersion: normMatch.TerminologyVersion,
                        MappingNote: normMatch.MappingNote
                    ),
                    DoseUnit: ClinicalScope.DoseUnit(bound.Dose),
                    Action: assertionStatus == "negated" ? "none" : ClinicalScope.Action(line),
                    Origin: medOrigin,
                    Provenance: new EntityProvenanceDto(
                        Origin: medOrigin,
                        Provider: "onnx",
                        RuleModified: true,
                        Modifications: ["Posology bound deterministically via PosologyBinder"],
                        UncertaintyReason: normMatch.Status != "matched" ? normMatch.MappingNote : null,
                        Quarantined: normMatch.Status != "matched")
                ));
            }
            else if (span.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                     span.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase) ||
                     span.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase))
            {
                var normMatch = _ontologyResolver.ResolveInvestigation(span.Text);
                var qualityFlags = new List<string>();
                var findings = new List<FindingDto>();

                if (normMatch.Status != "matched")
                {
                    qualityFlags.Add("TERMINOLOGY_UNMATCHED");
                    findings.Add(new FindingDto("TERMINOLOGY_UNMATCHED", "warning", "name", "A terminology lookup is not an exact semantic match. The standard code is unresolved pending review."));
                    warningCount++;
                }

                string line = ClinicalScope.ForEntity(request.NoteText, span.StartChar, span.EndChar, allEntitySpans);
                line = string.Join(" ", line.Split('\n').Where(l => l.Contains(span.Text, StringComparison.OrdinalIgnoreCase)));
                bool hasResultValue = LabNumericResultRegex().IsMatch(line);
                string obsStatus = hasResultValue ? "final" : "preliminary";

                string assertionStatus = ClinicalScope.Negated(line.Replace("rule out", "", StringComparison.OrdinalIgnoreCase)) ? "negated" :
                    assertion.Temporality == ClinicalTemporality.Historical || (hasResultValue && !Regex.IsMatch(line, @"\b(?:order|obtain|check|send|advise)\b", RegexOptions.IgnoreCase)) ? "historical" :
                    ClinicalScope.Conditional(line) ? "conditional" : "new_order";

                string? loincOrCode = normMatch.StandardId;
                string invOrigin = span.Origin ?? "RULE_EXTRACTED";

                investigations.Add(new ExtractedInvestigationDto(
                    Name: span.Text,
                    Timing: LocalClinicalAttributes.Timing(request.NoteText, span.StartChar, span.EndChar, allEntitySpans),
                    Specimen: LocalClinicalAttributes.Specimen(span.Text, ClinicalScope.AttributesForEntity(request.NoteText, span.StartChar, span.EndChar, allEntitySpans)),
                    AssertionStatus: assertionStatus,
                    Evidence: GetLineEvidence(request.NoteText, span.StartChar, span.EndChar),
                    QualityFlags: qualityFlags,
                    Findings: findings,
                    Normalization: new NormalizationDto(
                        Status: normMatch.Status,
                        MatchedCode: loincOrCode,
                        DisplayName: normMatch.DisplayName,
                        Confidence: normMatch.Confidence,
                        SemanticStatus: normMatch.SemanticStatus,
                        CandidateCode: normMatch.CandidateCode,
                        TerminologySource: normMatch.TerminologySource,
                        TerminologyVersion: normMatch.TerminologyVersion,
                        MappingNote: normMatch.MappingNote
                    ),
                    MatchedCode: loincOrCode,
                    DisplayName: normMatch.DisplayName,
                    Status: obsStatus,
                    ClinicalCategory: ClinicalResultValidator.ClassifyInvestigationCategory(span.Text),
                    Origin: invOrigin,
                    Provenance: new EntityProvenanceDto(
                        Origin: invOrigin,
                        Provider: "onnx",
                        RuleModified: true,
                        Modifications: ["Investigation entity, timing, explicit specimen and category derived by C# rules"],
                        UncertaintyReason: normMatch.Status != "matched" ? normMatch.MappingNote : null,
                        Quarantined: normMatch.Status != "matched")
                ));
            }
        }

        // 5. Evaluate Clinical Safety & Drug Interaction Rules
        var safetyAlerts = _safetyEngine.Evaluate(request.NoteText, medications, investigations);

        // 6. Generate 64-dimensional L2-normalized semantic vector embedding for pgvector retrieval
        var embedding = ClinicalEmbeddingGenerator.GenerateEmbedding(request.NoteText);

        sw.Stop();

        return new ClinicalExtractionResponse(
            SchemaVersion: "1.0.0",
            ExtractionId: Guid.NewGuid().ToString("D"),
            CorrelationId: correlationId,
            ExtractedAt: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            ExecutionDurationMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            ModelMetadata: new ModelMetadataDto(
                AdapterType: _onnxEngine.HasOnnxModel ? "onnx_neurosymbolic_int8" : "deterministic_test_scoring",
                ModelDeployment: _onnxEngine.HasOnnxModel ? "bc5cdr_int8" : "no_neural_model",
                PromptVersion: "v2.1.0",
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                EstimatedCostUsd: null,
                Provider: _onnxEngine.HasOnnxModel ? "onnx" : "deterministic_test",
                InferenceExecuted: receipt.WindowsExecuted > 0,
                ExecutionMode: receipt.ExecutionMode,
                ReviewCandidates: receipt.ReviewCandidates
            ),
            Preprocessed: new PreprocessedDto(
                CharLength: request.NoteText.Length,
                Sections: sectionDtos
            ),
            Medications: medications,
            Investigations: investigations,
            ValidationSummary: new ValidationSummaryDto(
                HasErrors: errorCount > 0,
                ErrorCount: errorCount,
                WarningCount: warningCount
            ),
            SafetyAlerts: safetyAlerts,
            Embedding: embedding,
            NoteText: request.NoteText
        );
    }

    private static string GetLineContaining(string text, int start, int end)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
        lineStart = lineStart == -1 ? 0 : lineStart + 1;

        int lineEnd = text.IndexOf('\n', Math.Min(text.Length, end));
        if (lineEnd == -1) lineEnd = text.Length;

        return text[lineStart..lineEnd];
    }

    private static EvidenceDto GetLineEvidence(string text, int start, int end)
    {
        var (left, right) = ClinicalScope.Bounds(text, start, end);
        while (left < right && char.IsWhiteSpace(text[left])) left++;
        while (right > left && char.IsWhiteSpace(text[right - 1])) right--;
        return new EvidenceDto(text[left..right], true, left, right);
    }

    private static string DetermineMedicationAssertionStatus(ClinicalAssertion assertion, string line)
    {
        if (ClinicalScope.Negated(line))
            return "negated";
        if (ClinicalScope.Action(line) == "allergy") return "allergy";
        if (ClinicalScope.Conditional(line)) return "conditional";
        if (ClinicalScope.Action(line) == "hold") return "held";
        if (Regex.IsMatch(line, @"\b(previously\s+took|last\s+year|past\s+meds?)\b", RegexOptions.IgnoreCase))
            return "historical";
        if (DiscontinuedVerbRegex().IsMatch(line) || Regex.IsMatch(line, @"\b(then\s+cease|then\s+stop|then\s+discontinue)\b", RegexOptions.IgnoreCase))
            return "discontinued";
        if (assertion.Temporality == ClinicalTemporality.Historical)
            return "historical";
        if (ConditionalCueRegex().IsMatch(line))
            return "conditional";
        if (ClinicalScope.Action(line) is "increase" or "decrease" || NewOrderVerbRegex().IsMatch(line))
            return "new_order";
        return "current_medication";
    }

    [GeneratedRegex(@"^[\d\s.,;:\-_/\\#*•()]+$", RegexOptions.Compiled)]
    private static partial Regex PurePunctuationOrDigitsRegex();

    [GeneratedRegex(@"(?i)\b(tab(?:let)?s?|cap(?:sule)?s?|inj(?:ection)?s?|syp|syrup|start|continue|prescribe|add)\b", RegexOptions.Compiled)]
    private static partial Regex ExplicitRxPrefixRegex();

    [GeneratedRegex(@"(?i)\b(order|ordered|advise|advised|obtain|check|send|repeat|rule out|evaluate|workup|investigations?|labs?)\b", RegexOptions.Compiled)]
    private static partial Regex LabOrderCueRegex();

    [GeneratedRegex(@"(?i)(?:[:=]\s*\d+(?:\.\d+)?|\b\d+(?:\.\d+)?\s*(?:mg/dL|mmol/L|mEq/L|g/dL|%|U/L|IU/L|ng/mL|pg/mL|mIU/L)\b)", RegexOptions.Compiled)]
    private static partial Regex LabNumericResultRegex();

    [GeneratedRegex(@"(?i)\b(discontinue|discontinued|stop|stopped|ceased|hold|held)\b", RegexOptions.Compiled)]
    private static partial Regex DiscontinuedVerbRegex();

    [GeneratedRegex(@"(?i)\b(start|started|initiate|add|added|prescribe|prescribed)\b", RegexOptions.Compiled)]
    private static partial Regex NewOrderVerbRegex();

    [GeneratedRegex(@"(?i)\b(if|when|unless|should|in case of|depending on|rule out)\b", RegexOptions.Compiled)]
    private static partial Regex ConditionalCueRegex();
}
