using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

public record BoundPosology(
    string? Strength,
    string? Dose,
    string? Route,
    string? Frequency,
    string? Duration,
    string? Formulation
);

public partial class PosologyBinder
{
    private static readonly (Regex Regex, string StandardRoute)[] RouteMatchers =
    [
        (RoutePoRegex(), "PO"),
        (RouteIvRegex(), "IV"),
        (RouteImRegex(), "IM"),
        (RouteScRegex(), "SC"),
        (RouteInhalationRegex(), "Inhalation"),
        (RouteOphthalmicRegex(), "Ophthalmic"),
        (RouteTopicalRegex(), "Topical"),
        (RouteSublingualRegex(), "SL")
    ];

    private static readonly (Regex Regex, string StandardFreq)[] FrequencyMatchers =
    [
        (FreqBidRegex(), "BID"),
        (FreqTidRegex(), "TID"),
        (FreqQidRegex(), "QID"),
        (FreqOdRegex(), "QD"),
        (FreqPrnRegex(), "PRN"),
        (FreqStatRegex(), "STAT"),
        (FreqQ8hRegex(), "Q8H"),
        (FreqQ12hRegex(), "Q12H"),
        (FreqBedtimeRegex(), "QHS")
    ];

    public BoundPosology Bind(string noteText, int drugStart, int drugEnd, IReadOnlyList<(int Start, int End)> otherDrugSpans)
    {
        string clauseText = ClinicalScope.AttributesForEntity(noteText, drugStart, drugEnd, otherDrugSpans);
        int relativeDrugStart = clauseText.IndexOf(noteText[drugStart..drugEnd], StringComparison.Ordinal);
        int relativeDrugEnd = relativeDrugStart + drugEnd - drugStart;

        // An unrecognized adjacent drug must still terminate attribute scope. Accepted
        // entity spans alone cannot guard against borrowing from an omitted medication.
        var nextPrescription = Regex.Match(clauseText[relativeDrugEnd..],
            @"\b(?:and\s+)?(?:start|continue|prescribe|add|stop|discontinue)\s+[A-Za-z]", RegexOptions.IgnoreCase);
        if (nextPrescription.Success) clauseText = clauseText[..(relativeDrugEnd + nextPrescription.Index)];

        string? strength = ExtractNearest(StrengthRegex(), clauseText, relativeDrugStart, relativeDrugEnd);
        string? dose = ExtractNearest(AdministeredDoseRegex(), clauseText, relativeDrugStart, relativeDrugEnd);
        string? route = ExtractRoute(clauseText);
        string? frequency = ExtractFrequency(clauseText);
        string? duration = ExtractDuration(clauseText);
        string? formulation = ExtractFormulation(clauseText);
        var qualifiedForm = Regex.Match(clauseText, @"\b(?:oral|ophthalmic)\s+(?:suspension|solution)\b", RegexOptions.IgnoreCase);
        if (qualifiedForm.Success) formulation = qualifiedForm.Value;

        return new BoundPosology(strength, dose, route, frequency, duration, formulation);
    }

    private static string? ExtractNearest(Regex regex, string text, int drugStart, int drugEnd)
    {
        var matches = regex.Matches(text);
        string? bestDose = null;
        int minDistance = int.MaxValue;

        foreach (Match m in matches)
        {
            if (regex == AdministeredDoseRegex() && Regex.IsMatch(text[(m.Index + m.Length)..], @"^\s*(?:/|per\s+)\s*(?:m[lL]|[lL])\b", RegexOptions.IgnoreCase)) continue;
            int dist = m.Index >= drugEnd ? (m.Index - drugEnd) : (drugStart - (m.Index + m.Length));
            if (dist < minDistance && dist <= 60)
            {
                minDistance = dist;
                bestDose = m.Value.Trim();
            }
        }

        return bestDose;
    }

    private static string? ExtractRoute(string text)
    {
        foreach (var (regex, std) in RouteMatchers)
        {
            if (regex.IsMatch(text))
                return std;
        }
        return null;
    }

    private static string? ExtractFrequency(string text)
    {
        var documented = ClinicalAttributeGrounding.Frequencies(text);
        if (documented.Contains("prn"))
        {
            var literal = Regex.Match(text, @"\b(?:every\s+\d+\s+hours|q\d+h)\s+(?:as needed|prn|sos)\b", RegexOptions.IgnoreCase);
            if (literal.Success) return literal.Value;
        }
        if (documented.Count == 1) return documented[0].ToUpperInvariant();
        var nonPrn = documented.Where(d => d != "prn").ToList();
        if (documented.Contains("prn") && nonPrn.Count == 1) return nonPrn[0].ToUpperInvariant();
        return null;
    }

    private static string? ExtractDuration(string text)
    {
        var match = DurationRegex().Match(text);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string? ExtractFormulation(string text)
    {
        var match = FormulationRegex().Match(text);
        return match.Success ? match.Value.Trim().ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"(?i)\b\d+(?:\.\d+)?(?:\s*/\s*\d+(?:\.\d+)?)?\s*(?:mg|mcg|µg|ug|gm|g|mEq)(?:\s*/\s*\d+(?:\.\d+)?\s*(?:mL|L))?\b|\b\d+(?:\.\d+)?\s*%", RegexOptions.Compiled)]
    private static partial Regex StrengthRegex();

    [GeneratedRegex(@"(?i)\b(?:\d+(?:\.\d+)?|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:mL|units?|iu|tablets?|tabs?|capsules?|caps?|puffs?|drops?)\b", RegexOptions.Compiled)]
    private static partial Regex AdministeredDoseRegex();

    [GeneratedRegex(@"(?i)\b(po|oral|orally|by mouth|per os|p\.o\.)\b", RegexOptions.Compiled)]
    private static partial Regex RoutePoRegex();

    [GeneratedRegex(@"(?i)\b(iv|intravenous|intravenously|i\.v\.)\b", RegexOptions.Compiled)]
    private static partial Regex RouteIvRegex();

    [GeneratedRegex(@"(?i)\b(im|intramuscular|intramuscularly|i\.m\.)\b", RegexOptions.Compiled)]
    private static partial Regex RouteImRegex();

    [GeneratedRegex(@"(?i)\b(sc|sq|subcutaneous|subcutaneously|sub-q|s\.c\.)\b", RegexOptions.Compiled)]
    private static partial Regex RouteScRegex();

    [GeneratedRegex(@"(?i)\b(inhalation|inhaled|nebulized|via inhaler)\b", RegexOptions.Compiled)]
    private static partial Regex RouteInhalationRegex();

    [GeneratedRegex(@"(?i)\b(ophthalmic|eye\s*drops?|(?:right|left|each|both)\s+eyes?)\b", RegexOptions.Compiled)]
    private static partial Regex RouteOphthalmicRegex();

    [GeneratedRegex(@"(?i)\b(topical|topically|apply)\b", RegexOptions.Compiled)]
    private static partial Regex RouteTopicalRegex();

    [GeneratedRegex(@"(?i)\b(sublingual|sl|under the tongue)\b", RegexOptions.Compiled)]
    private static partial Regex RouteSublingualRegex();

    [GeneratedRegex(@"(?i)\b(bid|bd|twice daily|twice a day|2 times daily|b\.i\.d\.|b\.d\.|1-0-1)\b", RegexOptions.Compiled)]
    private static partial Regex FreqBidRegex();

    [GeneratedRegex(@"(?i)\b(tid|tds|three times daily|three times a day|thrice daily|3 times daily|t\.i\.d\.|t\.d\.s\.|1-1-1)\b", RegexOptions.Compiled)]
    private static partial Regex FreqTidRegex();

    [GeneratedRegex(@"(?i)\b(qid|qds|four times daily|four times a day|4 times daily|q\.i\.d\.|1-1-1-1)\b", RegexOptions.Compiled)]
    private static partial Regex FreqQidRegex();

    [GeneratedRegex(@"(?i)\b(qd|od|once daily|once a day|daily|every day|q\.d\.|o\.d\.|1-0-0|0-1-0)\b", RegexOptions.Compiled)]
    private static partial Regex FreqOdRegex();

    [GeneratedRegex(@"(?i)\b(prn|as needed|sos|when required|p\.r\.n\.)\b", RegexOptions.Compiled)]
    private static partial Regex FreqPrnRegex();

    [GeneratedRegex(@"(?i)\b(stat|immediately)\b", RegexOptions.Compiled)]
    private static partial Regex FreqStatRegex();

    [GeneratedRegex(@"(?i)\b(q8h|every 8 hours|q 8 hours)\b", RegexOptions.Compiled)]
    private static partial Regex FreqQ8hRegex();

    [GeneratedRegex(@"(?i)\b(q12h|every 12 hours|q 12 hours)\b", RegexOptions.Compiled)]
    private static partial Regex FreqQ12hRegex();

    [GeneratedRegex(@"(?i)\b(qhs|hs|at bedtime|before sleep|bedtime|nightly|0-0-1)\b", RegexOptions.Compiled)]
    private static partial Regex FreqBedtimeRegex();

    [GeneratedRegex(@"(?i)\b(for\s+\d+\s*(?:more\s+)?(days?|weeks?|months?)|x\s*\d+\s*(days?|weeks?|d))\b", RegexOptions.Compiled)]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"(?i)\b(tablets?|tabs?|capsules?|caps?|syrup|syp|suspension|susp|inhaler|injections?|inj|drops?|cream|ointment|gel)\b", RegexOptions.Compiled)]
    private static partial Regex FormulationRegex();
}
