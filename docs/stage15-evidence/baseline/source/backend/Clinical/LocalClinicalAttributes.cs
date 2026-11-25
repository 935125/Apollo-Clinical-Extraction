using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

/// <summary>Lexical recovery only. Values are source substrings; absent information stays null.</summary>
public static class LocalClinicalAttributes
{
    public static string Line(string source, int start, int end)
    {
        int left = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        int right = source.IndexOf('\n', end);
        return source[left..(right < 0 ? source.Length : right)];
    }

    public static string? Specimen(string name, string attributes)
    {
        // Prefer the entity itself; never borrow a neighbouring test's specimen.
        var match = Regex.Match(name, @"\b(serum|plasma|urine|sputum|blood|CSF|stool)\b", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(attributes, @"\b(?:specimen|sample)\s*[:=]\s*(?<value>serum|plasma|urine|sputum|blood|CSF|stool)\b", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups["value"].Success ? match.Groups["value"].Value : match.Value) : null;
    }

    public static string? Timing(string source, int start, int end, IReadOnlyList<(int Start, int End)> entities)
    {
        var (left, right) = ClinicalScope.Bounds(source, start, end);
        int newline = source.IndexOf('\n', end);
        if (newline >= 0) right = Math.Min(right, newline);
        // Each independently timed test retains its own timing. A trailing time can apply
        // to an explicitly coordinated list only when intervening text is punctuation/and.
        int cursor = end;
        foreach (var next in entities.Where(e => e.Start >= end && e.Start < right).OrderBy(e => e.Start))
        {
            string gap = source[cursor..next.Start];
            var own = Time(gap);
            if (own != null) return own;
            if (!Regex.IsMatch(gap, @"^(?:\s|,|and|&|\(|\))*$", RegexOptions.IgnoreCase)) return null;
            cursor = next.End;
        }
        return Time(source[cursor..right]);
    }

    private static string? Time(string text)
    {
        var match = Regex.Match(text, @"\b(?:in\s+\d+\s+(?:hours?|days?|weeks?|months?)|at\s+\d+[- ]weeks?(?:\s+follow-up)?|today|tomorrow|tonight|now|before discharge|next\s+(?:week|month))\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }
}
