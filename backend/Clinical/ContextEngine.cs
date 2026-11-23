using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

public enum ClinicalCertainty
{
    Positive,
    Negated,
    Possible
}

public enum ClinicalTemporality
{
    Current,
    Historical,
    Hypothetical
}

public enum ClinicalExperiencer
{
    Patient,
    Family,
    Other
}

public record ClinicalAssertion(
    ClinicalCertainty Certainty,
    ClinicalTemporality Temporality,
    ClinicalExperiencer Experiencer,
    string Section
);

public record ClinicalSection(
    string Code,
    string Heading,
    int Start,
    int End
);

public partial class ContextEngine
{
    private static readonly (Regex Pattern, string Code)[] SectionPatterns =
    [
        (SectionHpiRegex(), "CHIEF_COMPLAINT"),
        (SectionPmhRegex(), "PAST_MEDICAL_HISTORY"),
        (SectionFhRegex(), "FAMILY_HISTORY"),
        (SectionAllergiesRegex(), "ALLERGIES"),
        (SectionMedsRegex(), "MEDICATIONS"),
        (SectionLabsRegex(), "LABS"),
        (SectionDiagnosesRegex(), "DIAGNOSES"),
        (SectionPlanRegex(), "ASSESSMENT_PLAN"),
        (SectionExamRegex(), "EXAMINATION")
    ];

    private static readonly FrozenSet<string> PseudoNegations = new[]
    {
        "no change", "no increase", "no decrease", "not only", "without delay",
        "no significant change", "not certain", "not clearly"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ForwardNegationCues = new[]
    {
        "no", "not", "none", "denies", "denied", "denying", "negative for", "free of",
        "without", "rule out", "ruled out", "absence of", "unremarkable for",
        "resolved", "no evidence of", "no sign of", "no signs of", "patient denies",
        "fails to reveal", "negative", "clear of", "prevent", "discontinued", "stopped"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> BackwardNegationCues = new[]
    {
        "ruled out", "was ruled out", "unlikely", "negative", "denied", "was denied",
        "is absent", "were absent", "not present", "not detected", "resolved"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> UncertaintyCues = new[]
    {
        "possible", "possibly", "probable", "probably", "suspicious", "suspected",
        "evaluate for", "consider", "differential", "question of", "may have",
        "cannot rule out", "could be", "equivocal", "borderline"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> HistoricalCues = new[]
    {
        "history of", "hx of", "past", "prior", "previous", "previously",
        "status post", "s/p", "in the past", "years ago", "months ago",
        "former", "remote", "childhood", "resolved"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> FamilyCues = new[]
    {
        "mother", "father", "sister", "brother", "parent", "parents",
        "maternal", "paternal", "grandmother", "grandfather", "aunt", "uncle",
        "family history", "fhx", "family member", "cousin", "sibling"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ScopeTerminators = new[]
    {
        "but", "however", "nevertheless", "yet", "although", "except", "aside from",
        "whereas", "nonetheless", "though"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public List<ClinicalSection> SegmentSections(string text)
    {
        var sections = new List<ClinicalSection>();
        if (string.IsNullOrWhiteSpace(text))
            return sections;

        var headerMatches = new List<(int Index, int Length, string Heading, string Code)>();

        foreach (var (regex, code) in SectionPatterns)
        {
            var matches = regex.Matches(text);
            foreach (Match m in matches)
            {
                if (m.Length <= 60)
                {
                    headerMatches.Add((m.Index, m.Length, m.Value.Trim(), code));
                }
            }
        }

        headerMatches.Sort((a, b) => a.Index.CompareTo(b.Index));

        if (headerMatches.Count == 0)
        {
            sections.Add(new ClinicalSection("OTHER", "Body", 0, text.Length));
            return sections;
        }

        if (headerMatches[0].Index > 0)
        {
            sections.Add(new ClinicalSection("OTHER", "Prologue", 0, headerMatches[0].Index));
        }

        for (int i = 0; i < headerMatches.Count; i++)
        {
            int start = headerMatches[i].Index;
            int end = (i + 1 < headerMatches.Count) ? headerMatches[i + 1].Index : text.Length;
            sections.Add(new ClinicalSection(headerMatches[i].Code, headerMatches[i].Heading, start, end));
        }

        return sections;
    }

    public string DetermineSection(int startChar, IReadOnlyList<ClinicalSection> sections)
    {
        foreach (var s in sections)
        {
            if (startChar >= s.Start && startChar < s.End)
                return s.Code;
        }
        return "OTHER";
    }

    public ClinicalAssertion Classify(string noteText, int startChar, int endChar, IReadOnlyList<ClinicalSection> sections)
    {
        string section = DetermineSection(startChar, sections);

        int sentenceStart = FindSentenceBoundaryBackward(noteText, startChar);
        int sentenceEnd = FindSentenceBoundaryForward(noteText, endChar);

        string precedingText = noteText[sentenceStart..startChar];
        string followingText = noteText[endChar..sentenceEnd];

        var precedingWords = TokenizeWords(precedingText);
        var followingWords = TokenizeWords(followingText);

        ClinicalExperiencer experiencer = ClinicalExperiencer.Patient;
        if (section == "FAMILY_HISTORY")
        {
            experiencer = ClinicalExperiencer.Family;
        }
        else
        {
            foreach (var w in precedingWords.TakeLast(8))
            {
                if (FamilyCues.Contains(w))
                {
                    experiencer = ClinicalExperiencer.Family;
                    break;
                }
            }
        }

        ClinicalTemporality temporality = ClinicalTemporality.Current;
        if (section == "PAST_MEDICAL_HISTORY")
        {
            temporality = ClinicalTemporality.Historical;
        }
        else
        {
            foreach (var w in precedingWords.TakeLast(10))
            {
                if (HistoricalCues.Contains(w))
                {
                    temporality = ClinicalTemporality.Historical;
                    break;
                }
            }
        }

        ClinicalCertainty certainty = ClinicalCertainty.Positive;

        // Check Forward Negation (preceding words up to 45 words or until scope terminator)
        bool isNegated = false;
        var relevantPreceding = FilterUntilTerminator(precedingWords.TakeLast(45).Reverse()).Reverse().ToList();
        string precedingWindow = string.Join(" ", relevantPreceding);

        foreach (var pseudo in PseudoNegations)
        {
            if (precedingWindow.Contains(pseudo, StringComparison.OrdinalIgnoreCase))
            {
                // Cancel pseudo-negation
                precedingWindow = precedingWindow.Replace(pseudo, "", StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var cue in ForwardNegationCues)
        {
            if (Regex.IsMatch(precedingWindow, $@"\b{Regex.Escape(cue)}\b", RegexOptions.IgnoreCase))
            {
                isNegated = true;
                break;
            }
        }

        // Check Backward Negation (following words up to 45 words or until scope terminator)
        if (!isNegated)
        {
            var relevantFollowing = FilterUntilTerminator(followingWords.Take(45)).ToList();
            string followingWindow = string.Join(" ", relevantFollowing);

            foreach (var cue in BackwardNegationCues)
            {
                if (Regex.IsMatch(followingWindow, $@"\b{Regex.Escape(cue)}\b", RegexOptions.IgnoreCase))
                {
                    isNegated = true;
                    break;
                }
            }
        }

        if (isNegated)
        {
            certainty = ClinicalCertainty.Negated;
        }
        else
        {
            // Check Uncertainty cues near the entity
            foreach (var w in relevantPreceding.TakeLast(8))
            {
                if (UncertaintyCues.Contains(w))
                {
                    certainty = ClinicalCertainty.Possible;
                    break;
                }
            }
        }

        return new ClinicalAssertion(certainty, temporality, experiencer, section);
    }

    private static IEnumerable<string> FilterUntilTerminator(IEnumerable<string> words)
    {
        foreach (var w in words)
        {
            if (ScopeTerminators.Contains(w))
                yield break;
            yield return w;
        }
    }

    private static int FindSentenceBoundaryBackward(string text, int index)
    {
        int limit = Math.Max(0, index - 400);
        for (int i = index - 1; i >= limit; i--)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == ';' || (c == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])))
            {
                return i + 1;
            }
        }
        return limit;
    }

    private static int FindSentenceBoundaryForward(string text, int index)
    {
        int limit = Math.Min(text.Length, index + 400);
        for (int i = index; i < limit; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == ';' || (c == '.' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]))))
            {
                return i;
            }
        }
        return limit;
    }

    private static List<string> TokenizeWords(string text)
    {
        var matches = WordRegex().Matches(text);
        var list = new List<string>(matches.Count);
        foreach (Match m in matches)
        {
            list.Add(m.Value.ToLowerInvariant());
        }
        return list;
    }

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(history of present illness|chief complaint|presenting complaints?|reasons? for admission|reasons? for visit|hospital course(?:\s*&\s*clinical\s+presentation|\s*&\s*plan)?|clinical presentation|subjective)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionHpiRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(past medical history|pmh|past history|medical history|surgical history)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionPmhRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(family history|family medical history|fhx|fh)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionFhRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(documented allergies|allergies|drug allergies|allergy)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionAllergiesRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(current medications?|Current Therapy|past meds?|medication changes?|medications?|meds|rx|prescriptions?|discharge medications?)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionMedsRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(outpatient follow-up investigations?|follow-up investigations?|baseline labs?|investigations?|labs?|laboratory|imaging|diagnostic results?|test results?|diagnostic workup|workup)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionLabsRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(discharge diagnoses|diagnoses|diagnosis|impression)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionDiagnosesRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(assessment and plan|plan|treatment plan|recommendations?|advice|assessment)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionPlanRegex();

    [GeneratedRegex(@"(?im)^[\s#*•-]*\b(discharge vital signs(?:\s*&\s*baseline\s+labs?)?|physical examination|examination|exam|o/e|vital signs?|vitals?)\b[\s:]*", RegexOptions.Compiled)]
    private static partial Regex SectionExamRegex();

    [GeneratedRegex(@"\b[\w'-]+\b", RegexOptions.Compiled)]
    private static partial Regex WordRegex();
}
