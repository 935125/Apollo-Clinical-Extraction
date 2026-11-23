using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Security;

public class PIILeakageException : Exception
{
    public string ErrorCode { get; } = "ERR_PII_LEAKAGE_DETECTED";
    public List<FlaggedPiiItem> Flagged { get; }
    public List<FlaggedPiiItem> FlaggedIdentifiers => Flagged;

    public PIILeakageException(string message, List<FlaggedPiiItem> flagged)
        : base(message)
    {
        Flagged = flagged;
    }
}

public partial class ZeroLeakageValidator
{
    public static readonly FrozenSet<string> ClinicalEponyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "parkinson", "parkinson's", "alzheimer", "alzheimer's",
        "st. jude", "hashimoto", "bell's palsy", "whipple",
        "crohn's", "kaposi", "hodgkin", "non-hodgkin",
        "raynaud", "barrett", "meniere", "graves"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ========================================================================
    // Generated Regular Expressions for High-Performance Scanning
    // ========================================================================

    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|\b(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(@"\b\d{4}[\s-]\d{4}[\s-]\d{4}\b(?!\s*\d)", RegexOptions.CultureInvariant)]
    private static partial Regex AadhaarRegex();

    [GeneratedRegex(@"\b\d{2}-\d{4}-\d{4}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AbhaRegex();

    [GeneratedRegex(@"\b[A-Z]{5}\d{4}[A-Z]\b", RegexOptions.CultureInvariant)]
    private static partial Regex PanRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b(?:mrn|uhid|ip\s?(?:no|number|id)|op\s?(?:no|number|id)|patient\s+id|medical\s+record(?:\s+(?:no|number|#))?)\b\.?\s*[:#\-]?\s*[A-Za-z0-9/\-]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MrnRegex();

    [GeneratedRegex(@"(?<![\w.])(?:\+?91[\s-]?)?[6-9]\d{4}[\s-]?\d{5}(?!\d)|(?<![\w.])\(?[2-9]\d{2}\)?[\s.\-]?[2-9]\d{2}[\s.\-]?\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:pin(?:\s?code)?|zip(?:\s?code)?|postal\s+code)\s*[:\-]?\s*\d{5,6}(?:-\d{4})?\b|\b\d{1,5}[A-Z]?[,/\- ]+(?:[A-Za-z0-9.']+\s+){0,5}(?:road|street|lane|marg|nagar|colony|avenue|sector|layout|apartments?|society|chowk|bazaar|enclave)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"\b(?:patient(?:'s)?\s+name|pt\.?\s+name|name\s+of\s+(?:the\s+)?patient|full\s+name|first\s+name|last\s+name)\s*[:=]\s*[A-Za-z'’\-]+(?:\s+[A-Za-z'’\-]+){0,3}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatientNameLabelRegex();

    [GeneratedRegex(@"\b(?:mr|mrs|ms|miss|master|shri|smt|dr|prof)\b\.?\s+[A-Z][a-zA-Z'’\-]+(?:\s+[A-Z][a-zA-Z'’\-]+){0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex SalutationNameRegex();

    [GeneratedRegex(@"\b(?<n>\d{2,3})\s*(?:years?[- ]old|years?|yrs?|y/?o|yo|y\.o\.?)\b|\baged?\s*[:\-]?\s*(?<n>\d{2,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeRegex();

    // ========================================================================
    // Scan & Validate
    // ========================================================================

    public static void Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Record(string category, int count)
        {
            if (count > 0)
            {
                counts[category] = counts.GetValueOrDefault(category, 0) + count;
            }
        }

        Record("AADHAAR", AadhaarRegex().Matches(text).Count);
        Record("ABHA", AbhaRegex().Matches(text).Count);
        Record("PAN", PanRegex().Matches(text).Count);
        Record("SSN", SsnRegex().Matches(text).Count);
        Record("EMAIL", EmailRegex().Matches(text).Count);
        Record("IP_ADDRESS", IpAddressRegex().Matches(text).Count);
        Record("URL", UrlRegex().Matches(text).Count);
        Record("MRN", MrnRegex().Matches(text).Count);
        Record("PHONE", PhoneRegex().Matches(text).Count);
        Record("GEOGRAPHIC", AddressRegex().Matches(text).Count);

        // Check for labelled patient name
        int nameMatches = PatientNameLabelRegex().Matches(text).Count;

        // Check salutation names outside eponym allowlist
        foreach (Match match in SalutationNameRegex().Matches(text))
        {
            string val = match.Value.ToLowerInvariant();
            bool isEponym = false;
            foreach (var ep in ClinicalEponyms)
            {
                if (val.Contains(ep))
                {
                    isEponym = true;
                    break;
                }
            }
            if (!isEponym)
            {
                nameMatches++;
            }
        }
        Record("NAME", nameMatches);

        // Check age > 89
        int overAgeMatches = 0;
        foreach (Match m in AgeRegex().Matches(text))
        {
            if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out int age) && age > 89)
            {
                overAgeMatches++;
            }
        }
        Record("AGE>89", overAgeMatches);

        if (counts.Count > 0)
        {
            var flagged = counts.Select(kv => new FlaggedPiiItem(kv.Key, $"[{kv.Key}]", kv.Value)).ToList();
            int totalViolations = counts.Values.Sum();
            throw new PIILeakageException(
                $"Direct patient identifiers detected in clinical narrative: {totalViolations} violation(s).",
                flagged
            );
        }
    }

    public static string Scrub(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        string scrubbed = AadhaarRegex().Replace(text, "[AADHAAR]");
        scrubbed = AbhaRegex().Replace(scrubbed, "[ABHA]");
        scrubbed = PanRegex().Replace(scrubbed, "[PAN]");
        scrubbed = SsnRegex().Replace(scrubbed, "[SSN]");
        scrubbed = EmailRegex().Replace(scrubbed, "[EMAIL]");
        scrubbed = IpAddressRegex().Replace(scrubbed, "[IP_ADDRESS]");
        scrubbed = UrlRegex().Replace(scrubbed, "[URL]");
        scrubbed = MrnRegex().Replace(scrubbed, "[MRN]");
        scrubbed = PhoneRegex().Replace(scrubbed, "[PHONE]");
        scrubbed = AddressRegex().Replace(scrubbed, "[GEOGRAPHIC]");
        scrubbed = PatientNameLabelRegex().Replace(scrubbed, "[NAME]");

        scrubbed = SalutationNameRegex().Replace(scrubbed, m =>
        {
            string val = m.Value.ToLowerInvariant();
            foreach (var ep in ClinicalEponyms)
            {
                if (val.Contains(ep)) return m.Value;
            }
            return "[NAME]";
        });

        scrubbed = AgeRegex().Replace(scrubbed, m =>
        {
            if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out int age) && age > 89)
            {
                return "[AGE>89]";
            }
            return m.Value;
        });

        return scrubbed;
    }
}
