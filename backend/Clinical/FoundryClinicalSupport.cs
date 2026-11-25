using System.Text.RegularExpressions;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

/// <summary>Source-span support checks for provider candidates. Never generates missing attributes.</summary>
internal static class FoundryClinicalSupport
{
    internal static List<LocalEntitySpan> Boundaries(string source, IReadOnlyList<(int Start, int End)> spans)
        => spans.Select(s => new LocalEntitySpan("ENTITY", s.Start, s.End, source[s.Start..s.End], null, "provider"))
            .Concat(VersionedClinicalTerminology.Recognize(source, "MEDICATION").Where(e =>
                Regex.IsMatch(source[e.End..Math.Min(source.Length, e.End + 45)], @"^\s*(?:\d+(?:\.\d+)?\s*(?:mg|mcg|g|mL|units?)\b|tablets?\b|capsules?\b)", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(source[Math.Max(0, e.Start - 35)..e.Start], @"\b(?:start|continue|stop|hold|prescribe|add)\s*$", RegexOptions.IgnoreCase)))
            .DistinctBy(s => (s.Start, s.End)).OrderBy(s => s.Start).ToList();

    internal static (int Start, int End) AttributeBounds(string source, LocalEntitySpan entity,
        IReadOnlyList<LocalEntitySpan> entities, EvidenceDto evidence)
    {
        var (left, right) = LocalClinicalScope.Attributes(source, entity, entities);
        // A route prefix following a coordinator belongs to the next medication (only when another entity follows).
        if (entities.Any(e => e.Start >= right))
        {
            var nextPrefix = Regex.Match(source[entity.End..right], @"(?:[,+]|\band\b)\s*(?:IV|IM|SC|SQ|PO|oral|intravenous)\s*$", RegexOptions.IgnoreCase);
            if (nextPrefix.Success) right = entity.End + nextPrefix.Index;
        }
        // Preserve explicitly quoted PDF continuation lines only when they begin with attributes,
        // contain no other entity, and are not a heading or an independent prescription.
        for (int line = 0; line < 4 && right < source.Length && right < evidence.End; line++)
        {
            if (source[right] is not ('\r' or '\n')) break;
            int start = right; while (start < source.Length && source[start] is '\r' or '\n') start++;
            int end = source.IndexOf('\n', start); if (end < 0) end = source.Length;
            end = Math.Min(end, evidence.End!.Value);
            if (start >= end || entities.Any(e => e.Start >= start && e.Start < end)) break;
            if (!Regex.IsMatch(source[start..end], @"^\s*(?:\d+(?:\.\d+)?\s*(?:mg|mcg|g|mL|tablets?|capsules?|units?)\b|(?:one|two|three|half)\s+(?:tablets?|capsules?)\b|(?:by mouth|orally|PO|IV|twice daily|once daily|for\s+\d+)\b)", RegexOptions.IgnoreCase)) break;
            right = end;
        }
        return (left, right);
    }

    internal static bool RelativeDose(string text) => Regex.IsMatch(text.Replace("m²", "m2", StringComparison.Ordinal), @"\b(?:mg|mcg|g)\s*/\s*(?:m\s*2|kg)\b", RegexOptions.IgnoreCase);

    internal static bool TruncatesRelativeDose(string? value, string attributes)
    {
        if (value == null || RelativeDose(value)) return false;
        // A claimed absolute numerator cannot stand in for an explicitly relative dose.
        return Regex.Matches(attributes, @"\b\d+(?:\.\d+)?\s*(?:mg|mcg|g)\s*/\s*(?:m(?:2|²)|kg)\b", RegexOptions.IgnoreCase)
            .Any(m => Regex.IsMatch(value.Trim(), "^" + Regex.Escape(Regex.Replace(m.Value, @"\s*/.*$", "")) + "$", RegexOptions.IgnoreCase));
    }

    internal static bool SupportsRoute(string? proposed, string attributes)
    {
        if (proposed == null) return true;
        static string Route(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ") switch
        {
            "iv" or "intravenous" or "intravenously" => "iv",
            "po" or "oral" or "orally" or "by mouth" or "per os" => "po",
            "im" or "intramuscular" => "im", "sc" or "sq" or "subcutaneous" => "sc", var x => x
        };
        string requested = Route(proposed);
        if (requested is "iv" or "po" or "im" or "sc")
        {
            string pattern = requested switch
            {
                "iv" => @"\b(?:IV|intravenous|intravenously)\b",
                "po" => @"\b(?:PO|oral|orally|by mouth|per os)\b",
                "im" => @"\b(?:IM|intramuscular)\b", _ => @"\b(?:SC|SQ|subcutaneous)\b"
            };
            return Regex.IsMatch(attributes, pattern, RegexOptions.IgnoreCase);
        }
        if (requested is "iv push" or "intravenous push") return Regex.IsMatch(attributes, @"\b(?:IV|intravenous)\s+push\b", RegexOptions.IgnoreCase);
        if (requested is "continuous iv infusion" or "continuous intravenous infusion") return Regex.IsMatch(attributes, @"\bcontinuous\s+(?:IV|intravenous)\s+infusion\b", RegexOptions.IgnoreCase);
        if (requested is "iv infusion" or "intravenous infusion" or "infusion") return Regex.IsMatch(attributes, @"\b(?:IV|intravenous)\s+infusion\b", RegexOptions.IgnoreCase);
        if (requested is "oromucosal" or "oral rinse" or "mouthwash" or "topical" or "oral") return Regex.IsMatch(attributes, @"\b(?:mouthwash|oral rinse)\b", RegexOptions.IgnoreCase);
        return false;
    }

    internal static bool SupportsContinuousFrequency(string? value, string attributes)
        => value != null && Regex.IsMatch(value.Trim(), @"^continuous(?:\s+(?:IV|intravenous))?(?:\s+infusion)?$", RegexOptions.IgnoreCase)
            && Regex.IsMatch(attributes, @"\bcontinuous\s+(?:(?:IV|intravenous)\s+)?infusion\b", RegexOptions.IgnoreCase);

    internal static bool HasResult(string source, LocalEntitySpan entity, IReadOnlyList<LocalEntitySpan> entities)
    {
        var range = LocalClinicalScope.Attributes(source, entity, entities);
        string after = source[entity.End..range.End];
        // Numbers must follow this test. An appointment time before CBC is never its result.
        return Regex.IsMatch(after, @"^\s*(?:(?:was|is|were|of|at|recovered\s+to|rose\s+to|dropped\s+to|fell\s+to|increased\s+to|decreased\s+to|measured(?:\s+at)?|noted\s+at|elevated\s+at|remained(?:\s+at)?)\s+|[:=]\s*)?\d+(?:[.,]\d+)*(?:\s*(?:/uL|/µL|g/dL|mg/dL|mmol/L|mEq/L|%|U/L))?\b|\b(?:showed|shows|grew|confirmed|revealed|reported|result(?:ed)?|no growth|not detected|negative|positive|normal karyotype)\b", RegexOptions.IgnoreCase);
    }

    internal static LocalAssertion InvestigationAssertion(string source, LocalEntitySpan entity,
        IReadOnlyList<LocalEntitySpan> entities, IReadOnlyList<ClinicalSection> sections)
    {
        var assertion = ClinicalAssertionClassifier.Classify(source, entity with { Category = "INVESTIGATION" }, entities, sections);
        if (assertion.LegacyStatus is "negated" or "conditional") return assertion;
        if (Regex.IsMatch(LocalClinicalScope.AssertionScope(source, entity, entities), @"\b(?:unclear|uncertain|possibly|may have|cannot confirm)\b", RegexOptions.IgnoreCase)) return assertion;
        if (HasResult(source, entity, entities)) return new("result_documented", "historical", "past", "none");
        return assertion;
    }

    internal static string? DocumentedResult(string source, LocalEntitySpan entity, IReadOnlyList<LocalEntitySpan> entities)
    {
        var range = LocalClinicalScope.Attributes(source, entity, entities);
        string after = source[entity.End..range.End];
        var karyotype = Regex.Match(after, @"\b\d{2},[XY]{1,3}\[\d+\]", RegexOptions.IgnoreCase);
        if (karyotype.Success) return karyotype.Value;
        var numeric = Regex.Match(after, @"\b\d+(?:[.,]\d+)*\s*(?:mg/dL|mEq/L|mmol/L|g/dL|/uL|/µL|%|U/L|IU/L|ng/mL|pg/mL|mIU/L|mL/min)(?!\w)", RegexOptions.IgnoreCase);
        if (numeric.Success) return numeric.Value.Trim();
        var qualitative = Regex.Match(after, @"\b(?:grew|showed|revealed|confirmed|no growth|negative|positive|not detected)\b[^.;\r\n]*", RegexOptions.IgnoreCase);
        return qualitative.Success ? qualitative.Value.Trim() : null;
    }

    internal static ClinicalEntityContextDto Context(string source, LocalEntitySpan entity, LocalAssertion assertion,
        IReadOnlyList<ClinicalSection> sections, List<AttributeEvidenceDto>? attributes = null)
        => new(assertion.Assertion, assertion.Temporality, new ContextEngine().DetermineSection(entity.Start, sections),
            entity.Start, entity.End, "foundry_source_validated", null, "accepted_as_mention", attributes ?? []);
}
