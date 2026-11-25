using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public sealed record ClinicalTerm(string Category, string Canonical, string[] Aliases, string Mapping, string Provenance);

/// <summary>Immutable embedded recognition snapshot. Recognition never implies standard-code approval.</summary>
public static class VersionedClinicalTerminology
{
    private sealed record Catalog(string Version, string License, ClinicalTerm[] Entries);
    private static readonly Catalog Snapshot = Load();
    private static Catalog Load()
    {
        using var stream = typeof(VersionedClinicalTerminology).Assembly.GetManifestResourceStream(
            "Apollo.Backend.Clinical.Terminology.clinical-recognition-v1.json")
            ?? throw new InvalidOperationException("Clinical recognition catalog is missing");
        return JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private static readonly FrozenDictionary<string, ClinicalTerm[]> Index = Snapshot.Entries
        .SelectMany(t => t.Aliases.Select(a => (Key: t.Category+":"+Clean(a), Term: t)))
        .GroupBy(x => x.Key).ToFrozenDictionary(g => g.Key, g => g.Select(x => x.Term).ToArray());
    private static readonly FrozenDictionary<string, Regex> Matchers = new[] { "MEDICATION", "INVESTIGATION" }
        .ToFrozenDictionary(c => c, c => new Regex(@"(?<![\p{L}\p{N}-])(?:"+
            string.Join("|", Snapshot.Entries.Where(t => t.Category == c).SelectMany(t => t.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a => a.Length).Select(Regex.Escape))+
            @")(?![\p{L}\p{N}-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2)));
    public static string Version => Snapshot.Version;
    private static string Clean(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
    public static IEnumerable<LocalEntitySpan> Recognize(string source, string category)
    {
        foreach (Match m in Matchers[category].Matches(source))
            yield return new(category, m.Index, m.Index+m.Length, m.Value, null, "VERSIONED_CATALOG", MatchType(category,m.Value));
    }
    public static string MatchType(string category, string name)
    {
        if (!Index.TryGetValue(category+":"+Clean(name), out var terms)) return "unresolved";
        if (name.Contains(" gp",StringComparison.OrdinalIgnoreCase) || terms.Select(t => t.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1)
            return "ambiguous";
        return terms.Any(t => Clean(t.Canonical)==Clean(name)) ? "exact" : "synonym";
    }
    public static NormalizationDto Resolve(string category, string name, string? strength = null)
    {
        var legacy = new IndianOntologyResolver();
        var original = category=="MEDICATION" ? legacy.ResolveMedication(name,strength) : legacy.ResolveInvestigation(name);
        string type=MatchType(category,name);
        if (original.Status != "unmatched")
            return new(original.Status, original.StandardId, original.DisplayName, original.Confidence,
                original.SemanticStatus, original.CandidateCode, original.TerminologySource, original.TerminologyVersion,
                original.MappingNote, type);
        var terms=Index.GetValueOrDefault(category+":"+Clean(name));
        var term=terms?.FirstOrDefault();
        // Only reviewed identity synonyms may inherit a legacy ingredient/code; local recognition-only
        // extensions carry no code. Broad investigations do not inherit narrower LOINC assignments.
        return new(type=="ambiguous" ? "uncertain" : "unmatched", null, type=="ambiguous" ? null : term?.Canonical,
            0, type=="ambiguous" ? "ambiguous" : "unresolved", null, "Apollo recognition catalog", Version,
            (term?.Provenance ?? "Source-grounded entity outside the recognition catalog")+"; standard terminology unresolved.",type);
    }
}
