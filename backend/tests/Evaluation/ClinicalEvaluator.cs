using System.Text.Json;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;
using Apollo.Backend.Clinical;

namespace Apollo.Backend.Tests.Evaluation;

public record Recognition(int Tp, int Fp, int Fn)
{
    public double? Precision => Tp + Fp == 0 ? null : (double)Tp / (Tp + Fp);
    public double? Recall => Tp + Fn == 0 ? null : (double)Tp / (Tp + Fn);
    public double? F1 => 2 * Tp + Fp + Fn == 0 ? null : 2.0 * Tp / (2 * Tp + Fp + Fn);
}
public record Accuracy(int Correct, int Evaluated)
{
    public double? Rate => Evaluated == 0 ? null : (double)Correct / Evaluated;
}
public record EvaluationFailure(string Kind, string Category, string Entity, string Field, string? Expected, string? Actual);
public record EntityEvaluation(Recognition Exact, Recognition Normalized, Recognition? FullPrescription,
    Dictionary<string, Accuracy> Attributes, Dictionary<string, Accuracy> DocumentedAttributes,
    Dictionary<string, Accuracy> MissingAttributes, Accuracy EvidenceQuote, Accuracy CharacterOffset,
    Accuracy UnsupportedEntity, Accuracy ConditionalOrder, int AmbiguousGoldMentions, List<EvaluationFailure> Failures);

public static class ClinicalEvaluator
{
    // Explicit, reviewable equivalences only. No substring, first-word, organ-agnostic or drug-class matching.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["cbc"] = "complete blood count", ["complete blood count (cbc)"] = "complete blood count",
        ["cxr"] = "chest x-ray", ["chest radiograph"] = "chest x-ray", ["chest x-ray pa view"] = "chest x-ray",
        ["ecg"] = "electrocardiogram", ["ekg"] = "electrocardiogram", ["12-lead ecg"] = "electrocardiogram", ["12 lead ecg"] = "electrocardiogram",
        ["lft"] = "liver function tests", ["liver function tests (lft)"] = "liver function tests",
        ["esr"] = "erythrocyte sedimentation rate", ["erythrocyte sedimentation rate (esr)"] = "erythrocyte sedimentation rate",
        ["serum psa (prostate specific antigen)"] = "serum psa",
        ["serum tsh (thyroid stimulating hormone)"] = "serum tsh",
        ["serum free t4"] = "free t4",
        ["fasting lipid profile"] = "lipid profile",
        ["hba1c"] = "hemoglobin a1c", ["paracetamol"] = "acetaminophen", ["salbutamol"] = "albuterol",
        ["prednisolone acetate"] = "prednisolone"
    };
    public static string Text(string? value) => Regex.Replace(value?.Trim().ToLowerInvariant() ?? "", @"\s+", " ");
    public static string EntityName(string? value) { var text = Text(value); return Aliases.GetValueOrDefault(text, text); }
    public static string Attribute(string field, string? value)
    {
        string v = Text(value);
        if (field == "frequency") return v switch
        {
            "bid" or "bd" or "twice daily" or "twice a day" => "bid",
            "od" or "qd" or "once daily" or "daily" or "every morning" or "morning" or "in the morning" => "qd",
            "tid" or "tds" or "three times daily" or "tds prn" => "tid",
            "qid" or "qds" or "four times daily" or "qds prn" => "qid",
            "once weekly" or "weekly" or "once weekly on sundays" or "once weekly on tuesdays" => "weekly",
            "prn" or "sos" or "as needed" or "every 6 hours prn" => "prn",
            "qhs" or "hs" or "bedtime" or "at bedtime" or "at night" or "night" => "qhs", _ => v
        };
        if (field == "route") return v switch
        {
            "po" or "oral" or "orally" or "by mouth" => "oral",
            "iv" or "intravenous" => "iv", "sc" or "sq" or "subcutaneous" => "sc",
            "inhalation" or "inhaled" => "inhaled",
            "ophthalmic" or "eye drops" or "ophthalmic suspension" => "ophthalmic", _ => v
        };
        if (field is "strength" or "dose" or "dose_unit" or "duration")
        {
            if (field == "duration") v = Regex.Replace(v, @"^for\s+", "");
            string[] words = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];
            for (int n = 0; n < words.Length; n++) v = Regex.Replace(v, @"\b" + words[n] + @"\b", (n + 1).ToString());
            v = Regex.Replace(v, @"\b(tablets|tabs|tab)\b", "tablet");
            v = Regex.Replace(v, @"\b(units|capsules|days|weeks|months)\b", m => m.Value[..^1]);
            return Regex.Replace(v, @"\s+", ""); // Numeric values and units must still match exactly.
        }
        return v;
    }
    public static bool Same(string field, string? a, string? b) => Attribute(field, a) == Attribute(field, b);
    public sealed record Prediction(string Name, Dictionary<string, string?> Fields, EvidenceDto Evidence);
    public static List<Prediction> RawMedications(ClinicalCandidate c, string source) => c.Medications.Select(m => new Prediction(m.Name,
        new() { ["strength"] = m.Strength, ["dose"] = m.Dose, ["dose_unit"] = m.DoseUnit, ["frequency"] = m.Frequency,
            ["route"] = m.Route, ["duration"] = m.Duration, ["action"] = m.Action, ["assertion_status"] = m.AssertionStatus,
            ["formulation"] = m.Formulation, ["instructions"] = m.Instructions }, RawEvidence(source, m.Evidence))).ToList();
    public static List<Prediction> RawInvestigations(ClinicalCandidate c, string source) => c.Investigations.Select(i => new Prediction(i.Name,
        new() { ["assertion_status"] = i.AssertionStatus, ["timing"] = i.Timing, ["specimen"] = i.Specimen, ["status"] = i.Status }, RawEvidence(source, i.Evidence))).ToList();
    private static EvidenceDto RawEvidence(string source, CandidateEvidence e)
    {
        int start = -1, search = 0;
        if (string.IsNullOrEmpty(e.Quote) || e.Occurrence < 1 || e.Occurrence > 1000) return new(e.Quote, false, null, null);
        for (int n = 0; n < e.Occurrence; n++) { start = source.IndexOf(e.Quote, search, StringComparison.Ordinal); if (start < 0) return new(e.Quote, false, null, null); search = start + e.Quote.Length; }
        return new(e.Quote, true, start, search);
    }
    public static List<Prediction> Medications(ClinicalExtractionResponse r) => r.Medications.Select(m => new Prediction(m.Name,
        new() { ["strength"] = m.Strength, ["dose"] = m.Dose, ["dose_unit"] = m.DoseUnit, ["frequency"] = m.Frequency,
            ["route"] = m.Route, ["duration"] = m.Duration, ["action"] = m.Action, ["assertion_status"] = m.AssertionStatus,
            ["formulation"] = m.Formulation, ["instructions"] = m.Instructions }, m.Evidence)).ToList();
    public static List<Prediction> Investigations(ClinicalExtractionResponse r) => r.Investigations.Select(i => new Prediction(i.Name,
        new() { ["assertion_status"] = i.AssertionStatus, ["timing"] = i.Timing, ["specimen"] = i.Specimen, ["status"] = i.Status }, i.Evidence)).ToList();
    public static bool QuoteValid(string source, EvidenceDto e) => !string.IsNullOrEmpty(e.Quote) && source.Contains(e.Quote, StringComparison.Ordinal);
    public static bool OffsetValid(string source, EvidenceDto e) => e.Start is >= 0 && e.End >= e.Start && e.End <= source.Length && source[e.Start.Value..e.End.Value] == e.Quote;
    public static EntityEvaluation Evaluate(string source, JsonElement[] gold, List<Prediction> predicted)
    {
        string Name(JsonElement g) => g.GetProperty("name").GetString()!;
        List<(int Gold, int Pred)> Match(bool normalized)
        {
            var pairs = new List<(int, int)>(); var used = new HashSet<int>();
            for (int g = 0; g < gold.Length; g++)
            {
                int bestP = -1;
                int bestScore = -1;
                for (int p = 0; p < predicted.Count; p++)
                {
                    if (used.Contains(p)) continue;
                    bool sameName = normalized ? EntityName(Name(gold[g])) == EntityName(predicted[p].Name) : Text(Name(gold[g])) == Text(predicted[p].Name);
                    if (!sameName) continue;
                    if (gold[g].TryGetProperty("mention_start", out var start) && start.ValueKind == JsonValueKind.Number)
                    {
                        int relative = predicted[p].Evidence.Quote.IndexOf(predicted[p].Name, StringComparison.OrdinalIgnoreCase);
                        int? mentionStart = relative < 0 ? null : predicted[p].Evidence.Start + relative;
                        if (mentionStart != start.GetInt32()) continue;
                        if (!normalized && gold[g].TryGetProperty("mention_end", out var end) && mentionStart + predicted[p].Name.Length != end.GetInt32()) continue;
                    }
                    int score = 0;
                    foreach (var prop in gold[g].EnumerateObject())
                    {
                        if (prop.Name is "name" or "mention_start" or "mention_end" or "evidence_start" or "evidence_end" or "evidence_quote") continue;
                        string? expected = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetString();
                        string? actual = predicted[p].Fields.GetValueOrDefault(prop.Name);
                        if (Same(prop.Name, expected, actual)) score += prop.Name == "assertion_status" ? 4 : 2;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestP = p;
                    }
                }
                if (bestP >= 0)
                {
                    pairs.Add((g, bestP));
                    used.Add(bestP);
                }
            }
            return pairs;
        }
        var exact = Match(false); var normalized = Match(true);
        Recognition Counts(int tp) => new(tp, predicted.Count - tp, gold.Length - tp);
        var attrs = new Dictionary<string, Accuracy>(); var documented = new Dictionary<string, Accuracy>(); var missing = new Dictionary<string, Accuracy>();
        var failures = new List<EvaluationFailure>(); int full = 0, conditionalCount = 0, conditionalCorrect = 0;
        void Add(Dictionary<string, Accuracy> target, string field, bool correct)
        { var prior = target.GetValueOrDefault(field, new(0, 0)); target[field] = new(prior.Correct + (correct ? 1 : 0), prior.Evaluated + 1); }
        for (int g = 0; g < gold.Length; g++)
        {
            var match = normalized.Where(pair => pair.Gold == g).Select(pair => predicted[pair.Pred]).FirstOrDefault();
            bool fullMatch = match != null;
            if (match == null) failures.Add(new("FN", "unrecognized", Name(gold[g]), "name", Name(gold[g]), null));
            foreach (var property in gold[g].EnumerateObject())
            {
                if (property.Name is "name" or "mention_start" or "mention_end" or "evidence_start" or "evidence_end" or "evidence_quote") continue;
                string? expected = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                string? actual = match?.Fields.GetValueOrDefault(property.Name);
                bool correct = match != null && Same(property.Name, expected, actual);
                fullMatch &= correct;
                Add(attrs, property.Name, correct); Add(expected == null ? missing : documented, property.Name, correct);
                if (!correct) failures.Add(new("attribute", "attribute_mismatch", Name(gold[g]), property.Name, expected, actual));
                if (property.Name == "assertion_status" && expected == "conditional") { conditionalCount++; if (correct) conditionalCorrect++; }
            }
            if (gold[g].TryGetProperty("evidence_quote", out var quote))
                Add(attrs, "reference_evidence_quote", match?.Evidence.Quote == quote.GetString());
            if (gold[g].TryGetProperty("evidence_start", out var evStart) && gold[g].TryGetProperty("evidence_end", out var evEnd))
                Add(attrs, "reference_character_offsets", match?.Evidence.Start == evStart.GetInt32() && match?.Evidence.End == evEnd.GetInt32());
            // Missing annotations must never masquerade as a verified complete prescription.
            bool fullyAnnotated = new[] { "strength", "dose", "dose_unit", "frequency", "route", "duration", "action", "assertion_status" }.All(f => gold[g].TryGetProperty(f, out _));
            if (fullMatch && fullyAnnotated) full++;
        }
        foreach (int p in Enumerable.Range(0, predicted.Count).Except(normalized.Select(pair => pair.Pred)))
        {
            var item = predicted[p]; string status = item.Fields.GetValueOrDefault("assertion_status") ?? "unknown";
            string category = !QuoteValid(source, item.Evidence) || !OffsetValid(source, item.Evidence) ? "Evidence-grounding failure" :
                predicted.Take(p).Any(other => EntityName(other.Name) == EntityName(item.Name) && other.Evidence.Start == item.Evidence.Start) ? "Duplicate extraction" :
                status switch { "historical" => "Historical mention", "negated" => "Negated mention", "allergy" => "Allergy mention", "conditional" => "Conditional instruction", _ => "Evaluation matching error / incomplete gold (unadjudicated)" };
            failures.Add(new("FP", category, item.Name, "assertion_status", null, status));
        }
        bool Unsupported(Prediction p) => !QuoteValid(source, p.Evidence) || !OffsetValid(source, p.Evidence) ||
            !Regex.IsMatch(p.Evidence.Quote, @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", p.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
        int ambiguous = gold.GroupBy(g => EntityName(Name(g))).Where(group => group.Count() > 1).Sum(group => group.Count(g => !g.TryGetProperty("mention_start", out _)));
        bool fullGold = gold.Length > 0 && gold.All(g => new[] { "strength", "dose", "dose_unit", "frequency", "route", "duration", "action", "assertion_status" }.All(f => g.TryGetProperty(f, out _)));
        return new(Counts(exact.Count), Counts(normalized.Count), fullGold ? Counts(full) : null, attrs, documented, missing,
            new(predicted.Count(p => QuoteValid(source, p.Evidence)), predicted.Count), new(predicted.Count(p => OffsetValid(source, p.Evidence)), predicted.Count),
            new(predicted.Count(Unsupported), predicted.Count), new(conditionalCorrect, conditionalCount), ambiguous, failures);
    }
}
