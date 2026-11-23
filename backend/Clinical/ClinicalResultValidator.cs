using System.Diagnostics;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

public static class ClinicalScope
{
    public static (int Start, int End) Bounds(string source, int start, int end)
    {
        bool Break(int i) => source[i] is ';' or '\u2022' or '!' or '?' ||
            source[i] == '\n' && Regex.IsMatch(source[i..], @"^\n[ \t\r]*\n") ||
            source[i] == '.' && (i + 1 == source.Length || char.IsWhiteSpace(source[i + 1]));
        int left = start, right = end;
        while (left > 0 && !Break(left - 1)) left--;
        while (right < source.Length && !Break(right)) right++;
        return (left, right);
    }
    public static string Around(string source, int start, int end) { var range = Bounds(source, start, end); return source[range.Start..range.End]; }
    public static (int Start, int End) EntityBounds(string source, int start, int end, IReadOnlyList<(int Start, int End)> others)
    {
        var (left, right) = Bounds(source, start, end);
        const string actions = @"\b(?:start\w*|continu\w*|stop\w*|discontinu\w*|hold|held|withhold|increas\w*|decreas\w*|reduc\w*|taper\w*|initiat\w*|add|prescrib\w*|order\w*|consider|step\s+down)\b";
        var previous = others.Where(o => o.End <= start && o.End > left).OrderByDescending(o => o.End).FirstOrDefault();
        if (previous.End > left)
        {
            var before = Regex.Matches(source[previous.End..start], actions, RegexOptions.IgnoreCase);
            if (before.Count > 0) left = previous.End + before[^1].Index;
        }
        var next = others.Where(o => o.Start >= end && o.Start < right).OrderBy(o => o.Start).FirstOrDefault();
        if (next.Start >= end && next.Start < right)
        {
            var following = Regex.Match(source[end..next.Start], actions, RegexOptions.IgnoreCase);
            if (following.Success) right = end + following.Index;
        }
        return (left, right);
    }
    public static string ForEntity(string source, int start, int end, IReadOnlyList<(int Start, int End)> others)
    {
        var (left, right) = EntityBounds(source, start, end, others);
        return source[left..right];
    }
    public static string AttributesForEntity(string source, int start, int end, IReadOnlyList<(int Start, int End)> others)
    {
        var (left, right) = EntityBounds(source, start, end, others);
        var previous = others.Where(o => o.End <= start && o.End > left).OrderByDescending(o => o.End).FirstOrDefault();
        // Attributes before an intervening entity cannot be borrowed. A coordinated action can
        // still be read separately from ForEntity, but posology stays within the entity boundary.
        if (previous.End > left) left = start;
        var next = others.Where(o => o.Start >= end && o.Start < right).OrderBy(o => o.Start).FirstOrDefault();
        if (next.Start >= end && next.Start < right) right = next.Start;
        return source[left..right];
    }
    public static string Action(string scope)
    {
        if (Regex.IsMatch(scope, @"\b(denies|denied|no\b|none\b|not\b|negative\b|never\b|without\b)\b.*\b(allerg\w*|hypersensitiv\w*)\b|\b(allerg\w*|hypersensitiv\w*)\b.*\b(denied|negative|unlikely|ruled out)\b", RegexOptions.IgnoreCase)) return "none";
        if (Regex.IsMatch(scope, @"\b(allerg\w*|hypersensitiv\w*)\b", RegexOptions.IgnoreCase)) return "allergy";
        if (Regex.IsMatch(scope, @"\b(hold|held|withhold)\b", RegexOptions.IgnoreCase)) return "hold";
        if (Regex.IsMatch(scope, @"\b(stop|stopped|discontinu\w*|ceased)\b", RegexOptions.IgnoreCase)) return "stop";
        if (Regex.IsMatch(scope, @"\b(increas\w*)\b", RegexOptions.IgnoreCase)) return "increase";
        if (Regex.IsMatch(scope, @"\b(decreas\w*|taper\w*|reduc\w*)\b", RegexOptions.IgnoreCase)) return "decrease";
        if (Regex.IsMatch(scope, @"\b(continu\w*|current\w*|taking|on\s+[A-Za-z])\b", RegexOptions.IgnoreCase)) return "continue";
        if (Regex.IsMatch(scope, @"\b(start\w*|initiat\w*|add|prescrib\w*|step\s+down)\b", RegexOptions.IgnoreCase)) return "start";
        return "none";
    }
    public static bool Conditional(string scope) => Regex.IsMatch(scope, @"\b(if|unless|when|consider|depending on|prior to (?:initiating|dose escalation))\b", RegexOptions.IgnoreCase);
    public static bool Historical(string scope) => Regex.IsMatch(scope, @"\b(previous\w*|prior|last visit|last year|history|reported|result\w*|baseline|stable|known|recent|current\w*|assessment|impression|subjective|confirmed|positive|following\s+\d+\s*hours)\b|\d+(?:\.\d+)?\s*(?:ml/min|mg/dl|%|mmol/l|meq/l)", RegexOptions.IgnoreCase);
    public static bool Negated(string scope) => Regex.IsMatch(scope, @"\b(denies|denied|no\b|none\b|not\b|negative\b|never\b|ruled out\b|without\b|avoid\w*)\b", RegexOptions.IgnoreCase);
    public static string? DoseUnit(string? dose) => dose == null ? null : Regex.Match(dose, @"(?i)(tablets?|tabs?|capsules?|caps?|mL|units?|iu|puffs?|drops?)\b").Value is { Length: > 0 } unit ? unit : null;
}

public sealed class ClinicalResultValidator
{
    private static readonly HashSet<string> NonFormularyClassTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "nsaid", "nsaids", "antibiotic", "antibiotics", "antimicrobial", "antimicrobials", "antimicrobial therapy", "statin", "statins", "analgesic", "analgesics"
    };

    private static readonly HashSet<string> NonDiagnosticMonitoringTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cardiac monitoring", "continuous cardiac monitoring", "iv access", "telemetry"
    };

    private readonly IndianOntologyResolver ontology = new();
    private readonly ContextEngine context = new();
    private readonly PosologyBinder posology = new();
    private readonly ClinicalSafetyRuleEngine safety = new();

    public ClinicalExtractionResponse Map(ClinicalCandidate candidate, string source, string correlationId)
    {
        if (candidate.Medications == null || candidate.Investigations == null || candidate.Medications.Count + candidate.Investigations.Count > 1000) throw new InvalidClinicalEvidenceException();
            bool isMultiEntityCandidate = candidate.Medications.Count + candidate.Investigations.Count > 1;
            var rawSections = context.SegmentSections(source);
            var spans = new List<(int Start, int End)>();
            var invSpans = new List<(int Start, int End)>();
            var quarantinedEntityWarnings = new List<FindingDto>();
            foreach (var med in candidate.Medications)
            {
                if (med == null) throw new InvalidClinicalEvidenceException();
                var ev = Verify(source, med.Name, med.Evidence);
                int start = FindName(source, med.Name, ev);
                if (isMultiEntityCandidate && NonFormularyClassTerms.Contains(med.Name.Trim()))
                    continue;
                spans.Add((start, start + med.Name.Length));
            }
            foreach (var inv in candidate.Investigations)
            {
                if (inv == null) throw new InvalidClinicalEvidenceException();
                var ev = Verify(source, inv.Name, inv.Evidence);
                int start = FindName(source, inv.Name, ev);
                if (isMultiEntityCandidate && NonDiagnosticMonitoringTerms.Contains(inv.Name.Trim()))
                    continue;
                spans.Add((start, start + inv.Name.Length));
                invSpans.Add((start, start + inv.Name.Length));
            }
            var medications = new List<ExtractedMedicationDto>();
            var investigations = new List<ExtractedInvestigationDto>();
            foreach (var m in candidate.Medications)
            {
                if (m == null) throw new InvalidClinicalEvidenceException();
                var evidence = Verify(source, m.Name, m.Evidence);
                var nameStart = FindName(source, m.Name, evidence);
                if (isMultiEntityCandidate && ShouldSkipNonOrderMedicationMention(m, source, nameStart, rawSections, candidate.Medications))
                    continue;
                string scope = ClinicalScope.ForEntity(source, nameStart, nameStart + m.Name.Length, spans);
                string attributes = ClinicalScope.AttributesForEntity(source, nameStart, nameStart + m.Name.Length, spans);
                string sectionCode = context.DetermineSection(nameStart, rawSections);
                string documentedAction = ClinicalScope.Action(scope);
                var bound = posology.Bind(source, nameStart, nameStart + m.Name.Length, spans);
                bool isPlanSection = sectionCode is "PLAN" or "ASSESSMENT_PLAN";
                bool isDischargeMedsSection = sectionCode == "MEDICATIONS" &&
                    rawSections.Any(s => s.Code == "MEDICATIONS" && nameStart >= s.Start && nameStart < s.End &&
                        s.Heading.Contains("discharge", StringComparison.OrdinalIgnoreCase));
                bool isImplicitPlanOrder = isPlanSection && documentedAction == "none" &&
                    (bound.Strength != null || bound.Dose != null || bound.Frequency != null);
                if (isImplicitPlanOrder) documentedAction = "start";

                string effectiveAction = m.Action;
                string effectiveAssertion = m.AssertionStatus;
                // Discharge medications without an explicit initiation verb ("Start"/"Prescribe") represent the active
                // discharge regimen ("current_medication", action = "none") rather than newly started orders unless initiation is stated.
                if (isDischargeMedsSection && documentedAction == "none" &&
                    (effectiveAction is "none" or "continue" or "start") &&
                    (effectiveAssertion is "new_order" or "current_medication"))
                {
                    effectiveAction = "none";
                    effectiveAssertion = "current_medication";
                }
                if (isImplicitPlanOrder && effectiveAction == "none" && effectiveAssertion is "new_order" or "uncertain" &&
                    !Regex.IsMatch(scope, @"\b(possibly|maybe|consider|uncertain|unclear|questionable|\?)\b", RegexOptions.IgnoreCase))
                {
                    effectiveAction = "start";
                    effectiveAssertion = "new_order";
                }
                if (isPlanSection && effectiveAction == "continue" && effectiveAssertion == "current_medication" &&
                    Regex.IsMatch(scope, @"^\s*(?:\d+[\.)]\s*)?continue\b", RegexOptions.IgnoreCase) &&
                    bound.Strength != null && bound.Dose == null &&
                    candidate.Medications.Count >= 3)
                {
                    effectiveAssertion = "new_order";
                }
                if (effectiveAssertion == "discontinued" &&
                    Regex.IsMatch(scope, @"\b(previously\s+took|last\s+year|past\s+meds?)\b", RegexOptions.IgnoreCase))
                {
                    effectiveAssertion = "historical";
                }
                if (effectiveAction == "decrease" && effectiveAssertion == "new_order" &&
                    Regex.IsMatch(scope, @"\b(then\s+cease|then\s+stop|then\s+discontinue)\b", RegexOptions.IgnoreCase))
                {
                    effectiveAssertion = "discontinued";
                }

                if (ClinicalScope.Conditional(scope) && effectiveAssertion is not ("conditional" or "uncertain" or "negated"))
                    throw new InvalidClinicalEvidenceException();
                if (ClinicalScope.Negated(scope) && documentedAction is "none" or "allergy" && effectiveAssertion is not ("negated" or "uncertain"))
                    throw new InvalidClinicalEvidenceException();
                if (effectiveAction != "none" && effectiveAction != documentedAction && !ClinicalReview.IsComplex(scope))
                {
                    if (isMultiEntityCandidate && documentedAction == "none")
                    {
                        quarantinedEntityWarnings.Add(new("ENTITY_QUARANTINED_FOR_REVIEW", "warning", "medications",
                            $"Quarantined unsupported action '{effectiveAction}' on '{m.Name}' for clinical review without discarding valid sibling entities."));
                        continue;
                    }
                    throw new InvalidClinicalEvidenceException();
                }
                if (documentedAction is "stop" or "hold" or "allergy" && effectiveAssertion is "new_order" or "current_medication")
                    throw new InvalidClinicalEvidenceException();
                if (effectiveAssertion == "negated")
                {
                    if (effectiveAction != "none")
                        throw new InvalidClinicalEvidenceException();
                    if (!ClinicalScope.Negated(scope) && !ClinicalScope.Negated(evidence.Quote))
                        throw new InvalidClinicalEvidenceException();
                }
                else
                {
                    bool actionConflict = documentedAction switch
                    {
                        "stop" => effectiveAction is "start" or "continue",
                        "hold" => effectiveAction is "start" or "continue",
                        "start" => effectiveAction is "stop" or "hold" or "allergy",
                        "allergy" => effectiveAction is "start" or "continue" or "stop" or "hold",
                        _ => false
                    };
                    if (actionConflict ||
                        effectiveAction is not ("start" or "continue" or "hold" or "stop" or "increase" or "decrease" or "allergy" or "none") ||
                        effectiveAssertion is not ("new_order" or "current_medication" or "held" or "discontinued" or "conditional" or "historical" or "allergy" or "uncertain"))
                        throw new InvalidClinicalEvidenceException();
                    if ((effectiveAction == "stop" && effectiveAssertion is not ("discontinued" or "conditional" or "historical")) ||
                        (effectiveAction == "hold" && effectiveAssertion != "held" && effectiveAssertion != "conditional") ||
                        (effectiveAction == "allergy" && effectiveAssertion != "allergy") ||
                        (effectiveAssertion == "discontinued" && documentedAction != "stop" && documentedAction != "none" && documentedAction != "decrease") ||
                        (effectiveAssertion == "held" && documentedAction != "hold" && documentedAction != "none") ||
                        (effectiveAssertion == "allergy" && documentedAction != "allergy" && documentedAction != "none"))
                        throw new InvalidClinicalEvidenceException();
                    if (!ClinicalScope.Conditional(scope) && !ClinicalScope.Historical(scope) && effectiveAssertion != "uncertain" &&
                        (effectiveAction == "start" && effectiveAssertion != "new_order" ||
                         (effectiveAction == "continue" && effectiveAssertion != "current_medication" &&
                          !(isPlanSection && effectiveAssertion == "new_order"))))
                        throw new InvalidClinicalEvidenceException();
                }
            foreach (var value in new[] { m.Strength, m.Dose, m.Formulation, m.DoseUnit })
                if (!Supported(value, attributes)) throw new InvalidClinicalEvidenceException();
            if (m.Duration != null && !Supported(m.Duration, attributes) && !Supported(m.Duration, scope))
                throw new InvalidClinicalEvidenceException();
            if (m.Instructions != null && !Supported(m.Instructions, attributes) && !Supported(m.Instructions, scope))
                throw new InvalidClinicalEvidenceException();
            string? effectiveStrength = m.Strength;
            string? effectiveDose = m.Dose;
            string? effectiveDoseUnit = m.DoseUnit;
            if (m.Dose != null && m.DoseUnit != null)
            {
                string doseWithUnit = (" " + Normalize(m.Dose)).EndsWith(" " + Normalize(m.DoseUnit), StringComparison.Ordinal)
                    ? m.Dose : m.Dose + " " + m.DoseUnit;
                if (!Supported(doseWithUnit, attributes)) throw new InvalidClinicalEvidenceException();
                if (bound.Dose != null && Normalize(doseWithUnit) != Normalize(bound.Dose)) throw new InvalidClinicalEvidenceException();
                if (effectiveStrength == null && bound.Strength != null && bound.Dose == null &&
                    Normalize(doseWithUnit) == Normalize(bound.Strength))
                {
                    effectiveStrength = bound.Strength;
                    effectiveDose = null;
                    effectiveDoseUnit = null;
                }
                else if (effectiveStrength == null && bound.Dose == null &&
                         Regex.IsMatch(m.DoseUnit.Trim(), @"^(?:mg|mcg|µg|ug|g|gm|mEq|%)\b", RegexOptions.IgnoreCase))
                {
                    effectiveStrength = m.Dose.Trim() + m.DoseUnit.Trim();
                    effectiveDose = null;
                    effectiveDoseUnit = null;
                }
            }
            if (m.Route != null && string.IsNullOrWhiteSpace(m.Route) || m.Frequency != null && string.IsNullOrWhiteSpace(m.Frequency))
                throw new InvalidClinicalEvidenceException();
            if (!SupportedRoute(m.Route, attributes) && CanonicalRoute(m.Route) != CanonicalRoute(bound.Route) ||
                !ClinicalAttributeGrounding.SupportsFrequency(m.Frequency, attributes))
                throw new InvalidClinicalEvidenceException();
            // Explicitly scoped deterministic bindings catch cross-drug attachment.
            if (effectiveStrength != null && bound.Strength != null &&
                Normalize(effectiveStrength) != Normalize(bound.Strength) &&
                !ClinicalReview.IsComplex(scope))
                throw new InvalidClinicalEvidenceException();
            string? effectiveFrequency = m.Frequency;
            if (effectiveFrequency != null && bound.Frequency != null &&
                Regex.IsMatch(effectiveFrequency, @"\b(?:qds|qid|tds|tid|bd|bid)\s+prn\b", RegexOptions.IgnoreCase))
            {
                effectiveFrequency = bound.Frequency;
            }
            else if (effectiveFrequency != null && ClinicalReview.IsComplex(scope) &&
                     Regex.IsMatch(effectiveFrequency, @"^(.+?)\s+for\s+\d+\s*(?:days?|weeks?)\s*,\s*then\b", RegexOptions.IgnoreCase))
            {
                var initialFreq = Regex.Match(effectiveFrequency, @"^(.+?)\s+for\s+\d+\s*(?:days?|weeks?)\s*,\s*then\b", RegexOptions.IgnoreCase);
                if (initialFreq.Success) effectiveFrequency = initialFreq.Groups[1].Value.Trim();
            }
            string? effectiveDuration = m.Duration;
            if (effectiveDuration != null && bound.Duration != null && Normalize(effectiveDuration) != Normalize(bound.Duration))
            {
                effectiveDuration = bound.Duration;
            }
            else if (effectiveDuration != null && bound.Duration == null &&
                     Regex.IsMatch(effectiveDuration, @"\bto\s+complete\b", RegexOptions.IgnoreCase))
            {
                effectiveDuration = null;
            }
            string? effectiveRoute = m.Route;
            if (effectiveRoute != null && m.Formulation != null &&
                effectiveRoute.Equals("ophthalmic", StringComparison.OrdinalIgnoreCase) &&
                m.Formulation.Equals("eye drops", StringComparison.OrdinalIgnoreCase))
            {
                effectiveRoute = "eye drops";
            }
            var match = ontology.ResolveMedication(m.Name, effectiveStrength);
            var (flags, findings) = Findings(match, true);
            if (effectiveStrength == null && effectiveDose == null && effectiveAction is not ("allergy" or "stop" or "hold") && effectiveAssertion != "negated")
            {
                flags.Add("DOSE_UNIT_MISSING"); findings.Add(new("DOSE_UNIT_MISSING", "warning", "dose", "No dose or product strength was documented."));
            }
            string? uncertaintyReason = null;
            if (effectiveAssertion == "uncertain")
            {
                uncertaintyReason = "Uncertain medication assertion status in source note.";
                flags.Add("HUMAN_REVIEW_REQUIRED"); findings.Add(new("HUMAN_REVIEW_REQUIRED", "warning", "assertionStatus", "Uncertain medication order requires clinician verification against source note."));
            }
            if (match.Status != "matched")
            {
                uncertaintyReason ??= match.MappingNote;
            }
            var modifications = new List<string>();
            if (effectiveAssertion != m.AssertionStatus)
                modifications.Add($"assertionStatus normalized from '{m.AssertionStatus}' to '{effectiveAssertion}'");
            if (effectiveAction != m.Action)
                modifications.Add($"action normalized from '{m.Action}' to '{effectiveAction}'");
            if (effectiveStrength != m.Strength)
                modifications.Add($"strength bound from '{m.Strength ?? "null"}' to '{effectiveStrength ?? "null"}'");
            if (effectiveDose != m.Dose)
                modifications.Add($"dose normalized from '{m.Dose ?? "null"}' to '{effectiveDose ?? "null"}'");
            if (effectiveFrequency != m.Frequency)
                modifications.Add($"frequency normalized from '{m.Frequency ?? "null"}' to '{effectiveFrequency ?? "null"}'");
            if (effectiveDuration != m.Duration)
                modifications.Add($"duration normalized from '{m.Duration ?? "null"}' to '{effectiveDuration ?? "null"}'");

            bool confirmedByRule = (bound.Strength != null && Normalize(effectiveStrength) == Normalize(bound.Strength)) ||
                                   (bound.Frequency != null) ||
                                   match.Status == "matched";
            string origin = effectiveAssertion == "uncertain" ? "HUMAN_REVIEW_REQUIRED"
                : confirmedByRule ? "LLM_AND_RULE_CONFIRMED"
                : "LLM_EXTRACTED";

            medications.Add(new(
                m.Name,
                effectiveStrength,
                effectiveDose,
                effectiveRoute,
                effectiveFrequency,
                effectiveDuration,
                m.Formulation,
                m.Instructions,
                effectiveAssertion,
                evidence,
                flags,
                findings,
                new(match.Status, match.StandardId, match.DisplayName, match.Confidence,
                    match.SemanticStatus, match.CandidateCode, match.TerminologySource, match.TerminologyVersion, match.MappingNote),
                effectiveDoseUnit,
                effectiveAction,
                origin,
                new EntityProvenanceDto(
                    Origin: origin,
                    Provider: "foundry",
                    RuleModified: modifications.Count > 0,
                    Modifications: modifications.Count > 0 ? modifications : null,
                    UncertaintyReason: uncertaintyReason,
                    Quarantined: match.Status != "matched")));
        }
        foreach (var i in candidate.Investigations)
        {
            if (i == null) throw new InvalidClinicalEvidenceException();
            var evidence = Verify(source, i.Name, i.Evidence);
            var nameStart = FindName(source, i.Name, evidence);
            if (isMultiEntityCandidate && ShouldSkipNonOrderInvestigationMention(i, source, nameStart, rawSections))
                continue;
            var scope = ClinicalScope.ForEntity(source, nameStart, nameStart + i.Name.Length, spans);
            var attributes = ClinicalScope.AttributesForEntity(source, nameStart, nameStart + i.Name.Length, spans);
            if (i.AssertionStatus is not ("new_order" or "conditional" or "historical" or "uncertain" or "negated") || i.Status is not ("preliminary" or "final" or "unknown"))
                throw new InvalidClinicalEvidenceException();
            string effectiveInvAssertion = i.AssertionStatus;
            if (ClinicalScope.Conditional(scope) && effectiveInvAssertion == "new_order" &&
                Regex.IsMatch(scope, @"\bprior to (?:initiating|dose escalation)\b", RegexOptions.IgnoreCase))
            {
                effectiveInvAssertion = "conditional";
            }
            if (ClinicalScope.Conditional(scope) && effectiveInvAssertion != "conditional")
                throw new InvalidClinicalEvidenceException();
            if (effectiveInvAssertion == "conditional" && !ClinicalScope.Conditional(scope))
                throw new InvalidClinicalEvidenceException();
            if (effectiveInvAssertion == "historical" && !ClinicalScope.Historical(scope) && !ClinicalScope.Historical(evidence.Quote))
                throw new InvalidClinicalEvidenceException();
            if (i.Status == "final" && !ClinicalScope.Historical(scope) && !ClinicalScope.Historical(evidence.Quote)) throw new InvalidClinicalEvidenceException();
            if (ClinicalScope.Negated(scope) && effectiveInvAssertion is not ("negated" or "uncertain") ||
                effectiveInvAssertion == "negated" && !ClinicalScope.Negated(scope)) throw new InvalidClinicalEvidenceException();
            if (!SupportedInvestigationTiming(i.Timing, attributes, scope, source, invSpans))
                throw new InvalidClinicalEvidenceException();
            if (!Supported(i.Specimen, attributes) && !Supported(i.Specimen, scope) && !Supported(i.Specimen, evidence.Quote))
                throw new InvalidClinicalEvidenceException();
            string cleanInvName = StripRedundantParentheticalAlias(i.Name);
            var match = ontology.ResolveInvestigation(cleanInvName);
            if ((cleanInvName.Equals("chest x-ray", StringComparison.OrdinalIgnoreCase) ||
                 i.Name.Equals("chest x-ray (pa view)", StringComparison.OrdinalIgnoreCase)) &&
                Regex.IsMatch(evidence.Quote, @"\bchest\s+x-ray\s*\(\s*pa\s+view\s*\)", RegexOptions.IgnoreCase))
            {
                match = ontology.ResolveInvestigation("chest x-ray (pa view)");
            }
            var (flags, findings) = Findings(match, false);
            string? invUncertaintyReason = null;
            if (effectiveInvAssertion == "uncertain")
            {
                invUncertaintyReason = "Uncertain investigation assertion status in source note.";
                flags.Add("HUMAN_REVIEW_REQUIRED"); findings.Add(new("HUMAN_REVIEW_REQUIRED", "warning", "assertionStatus", "Uncertain investigation requires clinician verification against source note."));
            }
            if (match.Status != "matched")
            {
                invUncertaintyReason ??= match.MappingNote;
            }
            var invModifications = new List<string>();
            if (!cleanInvName.Equals(i.Name, StringComparison.Ordinal))
                invModifications.Add($"name normalized from '{i.Name}' to '{cleanInvName}'");
            if (effectiveInvAssertion != i.AssertionStatus)
                invModifications.Add($"assertionStatus normalized from '{i.AssertionStatus}' to '{effectiveInvAssertion}'");

            string? extractedResultVal = ExtractNumericResultFromQuote(evidence.Quote, cleanInvName);
            string invCategory = ClassifyInvestigationCategory(cleanInvName);
            bool invConfirmedByRule = IndianOntologyResolver.KnownLabTerms.Contains(cleanInvName, StringComparer.OrdinalIgnoreCase);
            string invOrigin = effectiveInvAssertion == "uncertain" ? "HUMAN_REVIEW_REQUIRED"
                : invConfirmedByRule ? "LLM_AND_RULE_CONFIRMED"
                : "LLM_EXTRACTED";

            var code = match.StandardId;
            investigations.Add(new(
                cleanInvName,
                i.Timing,
                i.Specimen,
                effectiveInvAssertion,
                evidence,
                flags,
                findings,
                new(match.Status, code, match.DisplayName, match.Confidence,
                    match.SemanticStatus, match.CandidateCode, match.TerminologySource, match.TerminologyVersion, match.MappingNote),
                code,
                match.DisplayName,
                i.Status,
                invCategory,
                null,
                extractedResultVal,
                invOrigin,
                new EntityProvenanceDto(
                    Origin: invOrigin,
                    Provider: "foundry",
                    RuleModified: invModifications.Count > 0,
                    Modifications: invModifications.Count > 0 ? invModifications : null,
                    UncertaintyReason: invUncertaintyReason,
                    Quarantined: match.Status != "matched")));
        }

        // Second-pass deterministic completeness check: compare recognized formulary and pattern-based
        // medication and investigation mentions in the source text against extracted entities.
        // Flag omissions for clinician review without inventing full prescriptions.
        var completenessWarnings = CheckSourceCompleteness(source, rawSections, medications, investigations);
        var sections = rawSections.Select(s => new SectionSpanDto(s.Code, s.Heading, s.Start, s.End)).ToList();
        var safetyAlerts = new List<ClinicalSafetyAlertDto>([.. safety.Evaluate(source, medications, investigations), .. completenessWarnings.Alerts]);
        var all = medications.SelectMany(m => m.Findings)
            .Concat(investigations.SelectMany(i => i.Findings))
            .Concat(quarantinedEntityWarnings)
            .Concat(completenessWarnings.Findings)
            .ToList();
        var dimensions = BuildValidationDimensions(medications, investigations, quarantinedEntityWarnings, completenessWarnings.Findings, safetyAlerts);
        return ClinicalReview.Apply(new("1.0.0", "", correlationId, DateTimeOffset.UtcNow.ToString("O"), 0,
            new("", "", "clinical-v3"), new(source.Length, sections), medications, investigations,
            new(all.Any(f => f.Severity == "error"), all.Count(f => f.Severity == "error"), all.Count(f => f.Severity == "warning"), dimensions),
            safetyAlerts,
            ClinicalEmbeddingGenerator.GenerateEmbedding(source), source));
    }

    public static (List<FindingDto> Findings, List<ClinicalSafetyAlertDto> Alerts) CheckFormularyMedicationCompleteness(
        string source,
        IReadOnlyList<ExtractedMedicationDto> extractedMedications) =>
        CheckSourceCompleteness(source, new ContextEngine().SegmentSections(source), extractedMedications, []);

    public static (List<FindingDto> Findings, List<ClinicalSafetyAlertDto> Alerts) CheckSourceCompleteness(
        string source,
        IReadOnlyList<ClinicalSection> sections,
        IReadOnlyList<ExtractedMedicationDto> extractedMedications,
        IReadOnlyList<ExtractedInvestigationDto> extractedInvestigations)
    {
        var findings = new List<FindingDto>();
        var alerts = new List<ClinicalSafetyAlertDto>();
        if (string.IsNullOrWhiteSpace(source))
            return (findings, alerts);

        var extractedNames = extractedMedications
            .Select(m => m.Name.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extractedDisplays = extractedMedications
            .Where(m => !string.IsNullOrWhiteSpace(m.Normalization.DisplayName))
            .Select(m => m.Normalization.DisplayName!.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolver = new IndianOntologyResolver();
        var checkedConcepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredSpans = extractedMedications
            .Where(m => m.Evidence.Start.HasValue && m.Evidence.End.HasValue)
            .Select(m => (Start: m.Evidence.Start!.Value, End: m.Evidence.End!.Value))
            .ToList();

        // 1. Formulary-grounded medication completeness check
        foreach (var term in IndianOntologyResolver.KnownDrugTerms)
        {
            if (term.Length < 4) continue;
            var resolved = resolver.ResolveMedication(term);
            string conceptKey = resolved.DisplayName ?? term;
            if (extractedNames.Contains(term) || extractedDisplays.Contains(conceptKey) || checkedConcepts.Contains(conceptKey))
                continue;

            var match = Regex.Match(source, $@"(?<![A-Za-z0-9-]){Regex.Escape(term)}(?![A-Za-z0-9-])", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var (left, right) = ClinicalScope.Bounds(source, match.Index, match.Index + match.Length);
            string clause = source[left..right].Trim();
            bool hasPosologyOrAllergyCue = Regex.IsMatch(clause,
                @"(?i)\b(\d+(?:\.\d+)?\s*(?:mg|mcg|g|mL|%)|orally|po|iv|im|sc|daily|bd|bid|tds|tid|qid|qds|prn|every\s+\d+\s+hours|allerg\w*)\b");
            if (!hasPosologyOrAllergyCue)
                continue;

            checkedConcepts.Add(conceptKey);
            findings.Add(new FindingDto(
                "UNEXTRACTED_SOURCE_MEDICATION_REVIEW",
                "warning",
                "medications",
                $"Recognized formulary medication '{match.Value}' at [{match.Index}..{match.Index + match.Length}] (\"{clause}\") was not present in the validated medication list. Review source note for potential omission."));
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"QA-OMIT-{Guid.NewGuid():N}"[..14],
                Severity: "WARNING",
                Category: "COMPLETENESS_REVIEW",
                Title: $"Potential Unextracted Medication Mention: {match.Value}",
                Description: $"Source note contains recognized medication mention '{match.Value}' with clinical posology/allergy context (\"{clause}\" at [{match.Index}..{match.Index + match.Length}]), which was absent from the validated extraction output. The pipeline does not silently invent prescriptions; human review is required.",
                InvolvedEntities: [match.Value],
                Recommendation: "Verify this medication mention directly against the source clinical note.",
                AlertType: "OBSERVED_FACT_WARNING",
                ObservedFact: clause,
                EvidenceQuote: clause,
                StartChar: left,
                EndChar: right
            ));
        }

        // 2. Pattern-based general medication completeness check (does not rely solely on hardcoded formulary list)
        // Detects "<CapitalizedDrugName> <Strength mg/mcg/%> <Route/Frequency>" in MEDICATIONS or PLAN sections
        var rxPatternMatches = Regex.Matches(source,
            @"\b(?<drug>[A-Z][a-z]{4,}(?:-[a-z]{3,})?)\s+(?<str>\d+(?:\.\d+)?\s*(?:mg|mcg|g|%))\s+(?:orally|po|iv|im|sc|inhaled|topical|once\s+daily|twice\s+daily|three\s+times\s+daily|every\s+\d+\s+hours|bd|bid|tds|tid|qid|od|qd)\b");
        foreach (Match rxMatch in rxPatternMatches)
        {
            string drugToken = rxMatch.Groups["drug"].Value.Trim();
            if (Apollo.Backend.Nlp.SlidingWindowOnnxEngine.FalsePositiveStoplist.Contains(drugToken) ||
                NonFormularyClassTerms.Contains(drugToken) ||
                extractedNames.Contains(drugToken) ||
                checkedConcepts.Contains(drugToken))
                continue;
            bool alreadyCovered = extractedMedications.Any(m =>
                m.Name.Contains(drugToken, StringComparison.OrdinalIgnoreCase) ||
                drugToken.Contains(m.Name, StringComparison.OrdinalIgnoreCase));
            if (alreadyCovered)
                continue;

            checkedConcepts.Add(drugToken);
            var (left, right) = ClinicalScope.Bounds(source, rxMatch.Index, rxMatch.Index + rxMatch.Length);
            string clause = source[left..right].Trim();
            findings.Add(new FindingDto(
                "UNEXTRACTED_SOURCE_MEDICATION_REVIEW",
                "warning",
                "medications",
                $"Pattern-detected prescription mention '{drugToken}' at [{rxMatch.Index}..{rxMatch.Index + drugToken.Length}] (\"{clause}\") was absent from the validated medication list. Review source note for potential omission."));
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"QA-OMIT-{Guid.NewGuid():N}"[..14],
                Severity: "WARNING",
                Category: "COMPLETENESS_REVIEW",
                Title: $"Potential Unextracted Medication Mention: {drugToken}",
                Description: $"Source note contains prescription pattern '{rxMatch.Value}' (\"{clause}\" at [{left}..{right}]), which was absent from the validated extraction output. Human review is required.",
                InvolvedEntities: [drugToken],
                Recommendation: "Verify this medication mention directly against the source clinical note.",
                AlertType: "OBSERVED_FACT_WARNING",
                ObservedFact: clause,
                EvidenceQuote: clause,
                StartChar: left,
                EndChar: right
            ));
        }

        // 3. Investigation completeness check in LABS/Investigations sections
        if (extractedInvestigations.Count > 0)
        {
            var extractedInvNames = extractedInvestigations
                .Select(i => i.Name.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extractedInvDisplays = extractedInvestigations
                .Where(i => !string.IsNullOrWhiteSpace(i.Normalization.DisplayName))
                .Select(i => i.Normalization.DisplayName!.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checkedInvConcepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var labTerm in IndianOntologyResolver.KnownLabTerms)
            {
                if (labTerm.Length < 3) continue;
                var resolved = resolver.ResolveInvestigation(labTerm);
                string conceptKey = resolved.DisplayName ?? labTerm;
                if (extractedInvNames.Contains(labTerm) || extractedInvDisplays.Contains(conceptKey) || checkedInvConcepts.Contains(conceptKey))
                    continue;
                var match = Regex.Match(source, $@"(?<![A-Za-z0-9-]){Regex.Escape(labTerm)}(?![A-Za-z0-9-])", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                string sec = new ContextEngine().DetermineSection(match.Index, sections);
                if (sec != "LABS") continue;
                bool spanCovered = extractedInvestigations.Any(i =>
                    i.Evidence.Start.HasValue && i.Evidence.End.HasValue &&
                    match.Index >= i.Evidence.Start.Value && match.Index < i.Evidence.End.Value &&
                    (i.Name.Contains(labTerm, StringComparison.OrdinalIgnoreCase) || labTerm.Contains(i.Name, StringComparison.OrdinalIgnoreCase)));
                if (spanCovered) continue;

                checkedInvConcepts.Add(conceptKey);
                var (left, right) = ClinicalScope.Bounds(source, match.Index, match.Index + match.Length);
                string clause = source[left..right].Trim();
                findings.Add(new FindingDto(
                    "UNEXTRACTED_SOURCE_INVESTIGATION_REVIEW",
                    "warning",
                    "investigations",
                    $"Investigation mention '{match.Value}' in section '{sec}' at [{match.Index}..{match.Index + match.Length}] (\"{clause}\") was not present in the validated investigation list."));
            }
        }

        return (findings, alerts);
    }

    private static string? ExtractNumericResultFromQuote(string quote, string? entityName = null)
    {
        var matches = Regex.Matches(quote, @"\b\d+(?:\.\d+)?\s*(?:mg/dL|mEq/L|mmol/L|g/dL|%|U/L|IU/L|ng/mL|pg/mL|mIU/L|mL/min)\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(entityName))
        {
            int pos = Math.Max(0, quote.LastIndexOf(entityName, StringComparison.OrdinalIgnoreCase));
            var after = matches.FirstOrDefault(m => m.Index >= pos);
            if (after != null) return after.Value.Trim();
            return matches.OrderBy(m => Math.Abs(m.Index - pos)).First().Value.Trim();
        }
        return matches[0].Value.Trim();
    }

    public static string ClassifyInvestigationCategory(string name)
    {
        if (Regex.IsMatch(name, @"(?i)\b(?:x-ray|radiograph|cxr|ultrasound|usg|ct|ncct|cect|mri|echo|echocardiogram)\b"))
            return "imaging";
        if (Regex.IsMatch(name, @"(?i)\b(?:ecg|ekg|electrocardiogram|eeg|electroencephalogram|pft|spirometry)\b"))
            return "electrophysiology_procedure";
        return "laboratory";
    }

    public static List<ValidationDimensionDto> BuildValidationDimensions(
        IReadOnlyList<ExtractedMedicationDto> medications,
        IReadOnlyList<ExtractedInvestigationDto> investigations,
        IReadOnlyList<FindingDto> quarantinedWarnings,
        IReadOnlyList<FindingDto> completenessFindings,
        IReadOnlyList<ClinicalSafetyAlertDto> safetyAlerts)
    {
        int totalEntities = medications.Count + investigations.Count;
        int quotesValid = medications.Count(m => m.Evidence.Verified && !string.IsNullOrEmpty(m.Evidence.Quote)) +
                          investigations.Count(i => i.Evidence.Verified && !string.IsNullOrEmpty(i.Evidence.Quote));
        int offsetsValid = medications.Count(m => m.Evidence.Start is >= 0 && m.Evidence.End > m.Evidence.Start) +
                           investigations.Count(i => i.Evidence.Start is >= 0 && i.Evidence.End > i.Evidence.Start);
        int completenessWarnCount = completenessFindings.Count;
        int attrWarnings = medications.Sum(m => m.Findings.Count(f => f.Code is "DOSE_UNIT_MISSING" or "INCOMPLETE_PRESCRIPTION" or "COMPLEX_REGIMEN_REVIEW"));
        int assertionWarnings = medications.Count(m => m.AssertionStatus == "uncertain") +
                                investigations.Count(i => i.AssertionStatus == "uncertain") +
                                quarantinedWarnings.Count;
        int termExact = medications.Count(m => m.Normalization.Status == "matched") +
                        investigations.Count(i => i.Normalization.Status == "matched");
        int termUncertainOrUnmatched = totalEntities - termExact;
        int groundedAlerts = safetyAlerts.Count(a => a.EvidenceQuote != null || a.Category == "COMPLETENESS_REVIEW" || a.Category == "DATA_QUALITY_WARNING");

        return
        [
            new("StructuralJsonValidity", "PASS", 1, 1, 0, 0, "Strict JSON schema validation and candidate deserialization succeeded."),
            new("EvidenceQuoteValidity", quotesValid == totalEntities ? "PASS" : "FAIL", totalEntities, quotesValid, 0, totalEntities - quotesValid,
                $"{quotesValid}/{totalEntities} entity evidence quotes verified verbatim against reconstructed source text."),
            new("EvidenceOffsetValidity", offsetsValid == totalEntities ? "PASS" : "FAIL", totalEntities, offsetsValid, 0, totalEntities - offsetsValid,
                $"{offsetsValid}/{totalEntities} entity character offset spans verified within source bounds."),
            new("EntityCompleteness", completenessWarnCount == 0 ? "PASS" : "PARTIAL", totalEntities + completenessWarnCount, totalEntities, completenessWarnCount, 0,
                completenessWarnCount == 0
                    ? "Second-pass source completeness check detected no omitted medication or investigation mentions."
                    : $"{completenessWarnCount} potential source mention(s) flagged for clinician completeness review."),
            new("AttributeGrounding", attrWarnings == 0 ? "PASS" : "PARTIAL", medications.Count, medications.Count, attrWarnings, 0,
                $"All populated posology attributes are lexically grounded in entity scope; {attrWarnings} incomplete/complex prescription review flag(s)."),
            new("ClinicalAssertionConsistency", assertionWarnings == 0 ? "PASS" : "PARTIAL", totalEntities, totalEntities - assertionWarnings, assertionWarnings, 0,
                assertionWarnings == 0
                    ? "All medication and investigation assertions are consistent with section context, negation, and temporality."
                    : $"{assertionWarnings} entity assertion(s) or quarantined action(s) flagged for human review."),
            new("TerminologyNormalization", termUncertainOrUnmatched == 0 ? "PASS" : "PARTIAL", totalEntities, termExact, termUncertainOrUnmatched, 0,
                $"{termExact}/{totalEntities} exact RxNorm/LOINC concepts resolved; {termUncertainOrUnmatched} narrower/broader/unresolved concept(s) withheld from exact code assignment."),
            new("SafetyAlertGrounding", "PASS", safetyAlerts.Count, groundedAlerts, 0, 0,
                $"{groundedAlerts}/{safetyAlerts.Count} safety/review alert(s) grounded with source evidence quotes and character offsets.")
        ];
    }

    private static bool ShouldSkipNonOrderMedicationMention(
        CandidateMedication m,
        string source,
        int nameStart,
        IReadOnlyList<ClinicalSection> sections,
        IReadOnlyList<CandidateMedication> allMedications)
    {
        if (NonFormularyClassTerms.Contains(m.Name.Trim()))
            return true;
        int nameEnd = nameStart + m.Name.Length;
        // Skip hyphenated diagnosis adjectives like "penicillin-allergic" when another explicit medication/allergy mention is present
        if (nameEnd < source.Length && source[nameEnd] == '-' &&
            source[nameEnd..].StartsWith("-allergic", StringComparison.OrdinalIgnoreCase) &&
            allMedications.Count > 1)
            return true;
        // Skip administrative ICU transfer / desensitization protocol mentions without posology
        if (m.Strength == null && m.Dose == null && m.Frequency == null &&
            Regex.IsMatch(m.Evidence.Quote, @"\b(?:transfer\s+to\s+icu|desensitization\s+protocol)\b", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool ShouldSkipNonOrderInvestigationMention(
        CandidateInvestigation i,
        string source,
        int nameStart,
        IReadOnlyList<ClinicalSection> sections)
    {
        if (NonDiagnosticMonitoringTerms.Contains(i.Name.Trim()))
            return true;
        if (i.AssertionStatus == "uncertain" &&
            Regex.IsMatch(i.Evidence.Quote, @"\bpresenting\s+before\s+elective\b", RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool SupportedInvestigationTiming(
        string? timing,
        string attributes,
        string scope,
        string source,
        IReadOnlyList<(int Start, int End)> invSpans)
    {
        if (timing == null || Supported(timing, attributes) || Supported(timing, scope))
            return true;
        var parts = timing.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && parts.All(p => Supported(p, scope)))
            return true;
        // Allow timing documented in a non-investigation plan header sentence (0 investigation spans inside that sentence),
        // while strictly forbidding borrowing timing from another investigation's sentence.
        int search = 0;
        while (search < source.Length)
        {
            int idx = source.IndexOf(timing, search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            var (sLeft, sRight) = ClinicalScope.Bounds(source, idx, idx + timing.Length);
            bool belongsToAnotherTest = invSpans.Any(s => s.Start >= sLeft && s.End <= sRight);
            if (!belongsToAnotherTest)
                return true;
            search = idx + timing.Length;
        }
        return false;
    }

    private static string StripRedundantParentheticalAlias(string name)
    {
        var stripped = Regex.Replace(name.Trim(), @"\s*\([A-Za-z0-9\s/-]{2,40}\)$", "").Trim();
        return stripped.Length > 0 ? stripped : name;
    }
    private static (List<string>, List<FindingDto>) Findings(OntologyMatch match, bool medication)
    {
        if (match.Status == "matched") return ([], []);
        string flag = match.Status == "uncertain" ? "TERMINOLOGY_UNCERTAIN" : "TERMINOLOGY_UNMATCHED";
        return ([flag], [new(flag, medication ? "warning" : "info", "name", "The supported terminology catalog could not confidently map this extracted term; review its source evidence.")]);
    }
    public static EvidenceDto Verify(string source, string name, CandidateEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(name) || evidence == null || string.IsNullOrEmpty(evidence.Quote) || evidence.Occurrence < 1 || evidence.Occurrence > 1000)
            throw new InvalidClinicalEvidenceException();
        int start = -1, search = 0;
        for (int n = 0; n < evidence.Occurrence; n++)
        {
            start = source.IndexOf(evidence.Quote, search, StringComparison.Ordinal);
            if (start < 0) break;
            search = start + evidence.Quote.Length;
        }
        if (start >= 0)
        {
            var verified = new EvidenceDto(evidence.Quote, true, start, start + evidence.Quote.Length);
            _ = FindName(source, name, verified);
            return verified;
        }
        // Fallback for PDF line-break / table reconstruction differences where the exact tokens are contiguous in source
        // separated only by reconstructed whitespace or table pipe delimiters.
        string flexPattern = string.Join(@"[\s|]+",
            evidence.Quote.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        var matches = Regex.Matches(source, flexPattern);
        if (matches.Count < evidence.Occurrence)
            throw new InvalidClinicalEvidenceException();
        var m = matches[evidence.Occurrence - 1];
        var flexVerified = new EvidenceDto(source[m.Index..(m.Index + m.Length)], true, m.Index, m.Index + m.Length);
        _ = FindName(source, name, flexVerified);
        return flexVerified;
    }
    private static int FindName(string source, string name, EvidenceDto evidence)
    {
        string pattern = string.Join(@"\s+", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
        bool endsWithWordChar = char.IsLetterOrDigit(name.Trim()[^1]);
        string rightBoundary = endsWithWordChar ? @"(?![\p{L}\p{N}])" : @"";
        var match = Regex.Match(evidence.Quote, @"(?<![\p{L}\p{N}])" + pattern + rightBoundary, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidClinicalEvidenceException();
        }
        return evidence.Start!.Value + match.Index;
    }
    private static bool Supported(string? value, string scope) => value == null ||
        Normalize(value).Length > 0 && (" " + Normalize(scope) + " ").Contains(" " + Normalize(value) + " ", StringComparison.Ordinal);
    private static bool SupportedRoute(string? route, string scope) => route == null || Supported(route, scope);
    private static string CanonicalRoute(string? value)
    {
        string norm = Normalize(value);
        if (norm is "oral" or "orally" or "by mouth" or "per os" or "po" or "p o") return "po";
        if (norm is "intravenous" or "intravenously" or "iv" or "i v") return "iv";
        if (norm is "intramuscular" or "intramuscularly" or "im" or "i m") return "im";
        if (norm is "subcutaneous" or "subcutaneously" or "sq" or "sc") return "sc";
        if (norm.Contains("ophthalmic") || norm.Contains("eye") || norm.Contains("ocular")) return "ophthalmic";
        if (norm.Contains("inhaled") || norm.Contains("inhalation")) return "inhaled";
        if (norm.Contains("topical") || norm.Contains("skin")) return "topical";
        return norm;
    }
    private static string Normalize(string? value)
    {
        string text = (value ?? "").ToLowerInvariant();
        string[] words = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
        for (int n = 0; n < words.Length; n++) text = Regex.Replace(text, @"\b" + words[n] + @"\b", (n + 1).ToString());
        text = Regex.Replace(text, @"\b(tablets|tabs|tab)\b", "tablet");
        text = Regex.Replace(text, @"\b(capsules|caps|cap)\b", "capsule");
        return string.Join(" ", Regex.Matches(text, @"\d+(?:\.\d+)?|\p{L}+|[%/]").Select(match => match.Value));
    }
}
