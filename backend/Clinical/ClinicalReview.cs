using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public static class ClinicalReview
{
    public static bool IsComplex(string scope) => Regex.IsMatch(scope,
        @"\b(taper\w*|titrate\w*|restart\w*|then|conflicting|unclear)\b|\bmg\s*/\s*kg\b|\b(?:increase|decrease|reduce)\b.*\bfrom\b.*\bto\b", RegexOptions.IgnoreCase);

    public static ClinicalExtractionResponse Apply(ClinicalExtractionResponse result, bool reducedCoverage = false)
    {
        var medications = result.Medications.Select(m =>
        {
            var flags = m.QualityFlags.ToList(); var findings = m.Findings.ToList();
            void Flag(string code, string field, string message)
            {
                if (!flags.Contains(code)) { flags.Add(code); findings.Add(new(code, "warning", field, message)); }
            }
            var revised = m;
            if (IsComplex(m.Evidence.Quote))
            {
                Flag("COMPLEX_REGIMEN_REVIEW", "instructions", "A changing or weight-based regimen cannot be represented reliably as a single prescription. Review the complete source instructions.");
                bool mustNullifyPosology = Regex.IsMatch(m.Evidence.Quote,
                    @"\bmg\s*/\s*kg\b|\brestart\w*\b|\b(?:conflicting|unclear)\b|\b(?:increase|decrease|reduce)\b.*\bfrom\b.*\bto\b",
                    RegexOptions.IgnoreCase);
                revised = mustNullifyPosology
                    ? revised with { Strength = null, Dose = null, DoseUnit = null, Frequency = null, Duration = null, Instructions = m.Evidence.Quote }
                    : revised with { Instructions = m.Instructions ?? m.Evidence.Quote };
            }
            if (m.AssertionStatus is "new_order" or "current_medication" &&
                (revised.Dose == null || revised.Frequency == null || revised.Route == null))
            {
                var missingFields = new List<string>();
                if (revised.Dose == null) missingFields.Add("dose (administered form quantity; product strength alone does not specify administered dose)");
                if (revised.Route == null) missingFields.Add("route");
                if (revised.Frequency == null) missingFields.Add("frequency");
                Flag("INCOMPLETE_PRESCRIPTION", "dose", $"Unresolved prescription attributes: {string.Join(", ", missingFields)}. Human review is required.");
            }
            if (result.Medications.Count(other => other.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)) > 1)
                Flag("MULTIPLE_MENTIONS_REVIEW", "instructions", "Multiple mentions of this medication are retained separately. Review their timing and any conflicting directions.");
            if (reducedCoverage) Flag("LOCAL_COVERAGE_REDUCED", "name", "LocalOnnx combines BC5CDR with catalog rules and has reduced extraction coverage. Review the source for missing entities and attributes.");
            return revised with { QualityFlags = flags, Findings = findings };
        }).ToList();
        var investigations = result.Investigations.Select(i =>
        {
            if (!reducedCoverage) return i;
            if (i.Timing == null && i.AssertionStatus == "new_order")
                i = i with { QualityFlags = [.. i.QualityFlags, "TIMING_NOT_DOCUMENTED"], Findings = [.. i.Findings,
                    new("TIMING_NOT_DOCUMENTED", "warning", "timing", "No supported timing was extracted. Review the source; no schedule has been assumed.")] };
            if (i.Specimen == null && i.ClinicalCategory == "laboratory")
                i = i with { QualityFlags = [.. i.QualityFlags, "SPECIMEN_NOT_DOCUMENTED"], Findings = [.. i.Findings,
                    new("SPECIMEN_NOT_DOCUMENTED", "info", "specimen", "Specimen is not explicit in this test's source evidence; no default specimen has been assigned.")] };
            return i with { QualityFlags = [.. i.QualityFlags, "LOCAL_COVERAGE_REDUCED"], Findings = [.. i.Findings,
                new("LOCAL_COVERAGE_REDUCED", "warning", "name", "Local investigation extraction uses catalog rules and requires review for missing or incomplete findings.")] };
        }).ToList();
        var all = medications.SelectMany(m => m.Findings).Concat(investigations.SelectMany(i => i.Findings)).ToList();
        var updatedDimensions = ClinicalResultValidator.BuildValidationDimensions(
            medications,
            investigations,
            [],
            [],
            result.SafetyAlerts ?? []);
        if (result.ValidationSummary.Dimensions is { Count: > 0 } existingDims)
        {
            var completenessDim = existingDims.FirstOrDefault(d => d.Dimension == "EntityCompleteness");
            if (completenessDim != null)
            {
                updatedDimensions = updatedDimensions.Select(d => d.Dimension == "EntityCompleteness" ? completenessDim : d).ToList();
            }
        }
        return result with { Medications = medications, Investigations = investigations,
            ValidationSummary = new(all.Any(f => f.Severity == "error"), all.Count(f => f.Severity == "error"), all.Count(f => f.Severity == "warning"), updatedDimensions) };
    }
}
