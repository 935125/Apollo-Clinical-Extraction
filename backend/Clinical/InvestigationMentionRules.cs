using System.Text.RegularExpressions;
using Apollo.Backend.Nlp;

namespace Apollo.Backend.Clinical;

/// <summary>Productive source grammars, not code assignment or model predictions.</summary>
public static class InvestigationMentionRules
{
    public static bool IsThreshold(string scope) => Regex.IsMatch(scope,
        @"\b(?:if|when|unless)\b[^.;\n]*\b(?:exceeds?|remains?|falls?|rises?|above|below|greater|less)\b", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(scope, @"\b(?:order|obtain|check|send|measure|repeat)\b", RegexOptions.IgnoreCase);

    public static RawSpan Qualify(string source, RawSpan span)
    {
        int start = span.StartChar, end = span.EndChar;
        if (Regex.IsMatch(span.Text, @"^(?:CT|MRI|ultrasound)$", RegexOptions.IgnoreCase))
        {
            var prefix = Regex.Match(source[Math.Max(0, start - 45)..start], @"\b(?:high[- ]resolution|low[- ]dose|non[- ]contrast|contrast[- ]enhanced)\s+$", RegexOptions.IgnoreCase);
            if (prefix.Success) start -= prefix.Length;
            int suffixEnd = end;
            foreach (Match token in Regex.Matches(source[end..], @"\s+(?<word>[A-Za-z][A-Za-z-]*)").Take(6))
            {
                if (token.Index != suffixEnd - end || Regex.IsMatch(token.Groups["word"].Value,
                    @"^(?:in|at|on|if|when|unless|today|tomorrow|now|and|or|for|before|after|was|is|showed|shows|normal|to|order|ordered|repeat|obtain)$", RegexOptions.IgnoreCase)) break;
                suffixEnd = end + token.Index + token.Length;
            }
            if (suffixEnd > end) end = suffixEnd;
        }
        if (span.Text.Equals("Troponin", StringComparison.OrdinalIgnoreCase))
        {
            var subtype = Regex.Match(source[end..], @"^\s+[IT]\b", RegexOptions.IgnoreCase);
            if (subtype.Success) end += subtype.Length;
        }
        return span with { StartChar = start, EndChar = end, Text = source[start..end] };
    }

    public static IEnumerable<RawSpan> ProductiveMentions(string source)
    {
        const string pattern = @"\b(?:(?:fasting|random|postprandial)\s+(?:blood|plasma|serum)\s+(?:sugar|glucose)|(?:sputum|urine|blood|stool|CSF|wound|throat)\s+(?:culture|microscopy|cytology)|[A-Za-z]+\s+(?:cell\s+)?count)\b";
        foreach (Match match in Regex.Matches(source, pattern, RegexOptions.IgnoreCase))
        {
            string scope = ClinicalScope.Around(source, match.Index, match.Index + match.Length);
            if (!Regex.IsMatch(scope, @"\b(?:order|obtain|check|send|repeat|result|baseline)\b", RegexOptions.IgnoreCase) || IsThreshold(scope)) continue;
            yield return new("INVESTIGATION", match.Index, match.Index + match.Length, match.Value, 1, "RULE_EXTRACTED");
        }
    }
}
