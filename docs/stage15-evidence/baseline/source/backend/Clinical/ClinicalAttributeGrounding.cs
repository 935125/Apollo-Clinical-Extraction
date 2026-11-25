using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

// Conservative lexical grounding, not dose advice or clinical interpretation.
public static class ClinicalAttributeGrounding
{
    private const string Pattern = @"\b(?:twice\s+(?:daily|a day|weekly)|three times\s+(?:daily|a day)|four times\s+(?:daily|a day)|once\s+(?:daily|weekly|a day)|every\s+\d+\s+hours|every\s+morning|in\s+the\s+morning|at\s+night|daily|weekly|bid|bd|tid|tds|qid|qds|qd|od|qhs|hs|q6h|q8h|q12h|prn|sos|as needed|at bedtime|nightly)\b";
    public static string Canonical(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ") switch
    {
        "twice daily" or "twice a day" or "bd" or "bid" => "bid",
        "daily" or "once daily" or "once a day" or "qd" or "od" or "every morning" or "in the morning" => "qd",
        "three times daily" or "three times a day" or "tds" or "tid" => "tid",
        "four times daily" or "four times a day" or "qds" or "qid" => "qid",
        "once weekly" or "weekly" => "weekly",
        "at bedtime" or "nightly" or "at night" or "hs" or "qhs" => "qhs",
        "as needed" or "prn" or "sos" => "prn",
        "every 6 hours" or "q6h" => "q6h",
        "every 8 hours" or "q8h" => "q8h",
        "every 12 hours" or "q12h" => "q12h",
        var other => other
    };
    public static List<string> Frequencies(string text) => Regex.Matches(text, Pattern, RegexOptions.IgnoreCase)
        .Where(m => !Regex.IsMatch(text[Math.Max(0, m.Index - 24)..m.Index], @"\b(?:never|not|avoid|no)\b[^.;]*$", RegexOptions.IgnoreCase))
        .Select(m => Canonical(m.Value)).Distinct(StringComparer.Ordinal).ToList();
    public static bool SupportsFrequency(string? value, string scope)
    {
        if (value == null) return true;
        var documented = Frequencies(scope);
        var proposed = Frequencies(value);
        if (documented.Count > 0)
        {
            bool exactSequence = proposed.Count > 0 && documented.Order().SequenceEqual(proposed.Order()) &&
                (Canonical(value) == string.Join(" ", proposed) ||
                 scope.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                 (ClinicalReview.IsComplex(scope) && Regex.Matches(value.ToLowerInvariant(), @"\d+|\p{L}+").Select(m => m.Value).All(t => Regex.IsMatch(scope, @"(?<!\w)" + Regex.Escape(t) + @"(?!\w)", RegexOptions.IgnoreCase))));
            if (exactSequence) return true;
            // Allow single primary frequency when the clause specifies time-of-day or PRN alongside the primary frequency (e.g., "daily at night", "OD in the morning", "every 8 hours as needed")
            if (proposed.Count == 1 && documented.Contains(proposed[0]) &&
                documented.All(d => d == proposed[0] || d is "qd" or "qhs" or "prn") &&
                scope.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        return !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(scope, @"(?<!\w)" + Regex.Escape(value) + @"(?!\w)", RegexOptions.IgnoreCase);
    }
}
