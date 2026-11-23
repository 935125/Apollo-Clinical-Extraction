using System.Globalization;
using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public sealed record GroundedLabMeasurement(
    string Analyte,
    double Value,
    string Unit,
    string Quote,
    int StartChar,
    int EndChar
);

public partial class ClinicalSafetyRuleEngine
{
    public List<ClinicalSafetyAlertDto> Evaluate(
        string noteText,
        IReadOnlyList<ExtractedMedicationDto> medications,
        IReadOnlyList<ExtractedInvestigationDto> investigations)
    {
        var alerts = new List<ClinicalSafetyAlertDto>();
        if (string.IsNullOrWhiteSpace(noteText))
            return alerts;

        string textLower = noteText.ToLowerInvariant();

        var activeMeds = medications.Where(m => m.AssertionStatus is "new_order" or "current_medication").ToList();

        bool HasActiveDrug(params string[] keywords) =>
            activeMeds.Any(m =>
                keywords.Any(k =>
                    m.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    (m.Normalization.DisplayName != null && m.Normalization.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase))));

        bool HasAnyDrugMention(params string[] keywords) =>
            medications.Any(m =>
                keywords.Any(k =>
                    m.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    (m.Normalization.DisplayName != null && m.Normalization.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase))));

        var sections = new ContextEngine().SegmentSections(noteText);
        string DetermineAlertTemporality(int? startChar, string quote)
        {
            if (startChar.HasValue)
            {
                var sec = sections.FirstOrDefault(s => startChar.Value >= s.Start && startChar.Value < s.End);
                if (sec != null)
                {
                    if (sec.Heading.Contains("Baseline", StringComparison.OrdinalIgnoreCase))
                        return "BASELINE_MEASUREMENT";
                    if (sec.Code is "CHIEF_COMPLAINT" or "PAST_MEDICAL_HISTORY" ||
                        sec.Heading.Contains("Hospital Course", StringComparison.OrdinalIgnoreCase) ||
                        sec.Heading.Contains("Presentation", StringComparison.OrdinalIgnoreCase))
                        return "HISTORICAL_ADMISSION_COURSE";
                }
            }
            if (Regex.IsMatch(quote, @"\b(admitted\s+with|prior\s+to\s+discharge|on\s+admission|hospital\s+course)\b", RegexOptions.IgnoreCase))
                return "HISTORICAL_ADMISSION_COURSE";
            return "CURRENT_ACTIVE";
        }

        // 1. High-Risk Drug-Drug Interactions (DDIs) & High-Risk Posology
        var warfarinMed = activeMeds.FirstOrDefault(m => m.Name.Contains("warfarin", StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains("warfarin", StringComparison.OrdinalIgnoreCase) ?? false));
        var antiplateletMed = activeMeds.FirstOrDefault(m =>
            new[] { "aspirin", "ecosprin", "clopidogrel", "ibuprofen" }.Any(k =>
                m.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)));
        if (warfarinMed != null && antiplateletMed != null)
        {
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"DDI-{Guid.NewGuid():N}"[..12],
                Severity: "HIGH",
                Category: "DRUG_INTERACTION",
                Title: "Major Bleeding Risk: Warfarin + Antiplatelet/NSAID",
                Description: "Derived interpretation: Concurrent active documentation of Warfarin with Aspirin, Clopidogrel, or NSAIDs significantly increases the risk of major gastrointestinal and intracranial hemorrhage.",
                InvolvedEntities: [warfarinMed.Name, antiplateletMed.Name],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "INTERACTION_RISK_REVIEW",
                Temporality: "CURRENT_ACTIVE",
                ThresholdCriterion: "Active co-prescription of Vitamin K antagonist (Warfarin) + Antiplatelet/NSAID",
                ObservedFact: $"Active co-prescription of {warfarinMed.Name} ({warfarinMed.Strength ?? "strength unstated"}) and {antiplateletMed.Name} ({antiplateletMed.Strength ?? "strength unstated"}).",
                EvidenceQuote: $"{warfarinMed.Evidence.Quote} | {antiplateletMed.Evidence.Quote}",
                StartChar: warfarinMed.Evidence.Start,
                EndChar: antiplateletMed.Evidence.End
            ));
        }

        var qtAntimicrobial = activeMeds.FirstOrDefault(m =>
            new[] { "azithromycin", "ciprofloxacin" }.Any(k =>
                m.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)));
        var ondansetronMed = activeMeds.FirstOrDefault(m =>
            m.Name.Contains("ondansetron", StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains("ondansetron", StringComparison.OrdinalIgnoreCase) ?? false));
        if (qtAntimicrobial != null && ondansetronMed != null)
        {
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"DDI-{Guid.NewGuid():N}"[..12],
                Severity: "MODERATE",
                Category: "DRUG_INTERACTION",
                Title: "QTc Prolongation Risk: Macrolide/Fluoroquinolone + Ondansetron",
                Description: "Derived interpretation: Co-administration of QT-prolonging antimicrobials with Ondansetron increases the risk of ventricular arrhythmia / Torsades de Pointes.",
                InvolvedEntities: [qtAntimicrobial.Name, ondansetronMed.Name],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "INTERACTION_RISK_REVIEW",
                Temporality: "CURRENT_ACTIVE",
                ThresholdCriterion: "Active co-prescription of QT-prolonging antimicrobial (Macrolide/Fluoroquinolone) + 5-HT3 antagonist (Ondansetron)",
                ObservedFact: $"Active co-prescription of {qtAntimicrobial.Name} and {ondansetronMed.Name}.",
                EvidenceQuote: $"{qtAntimicrobial.Evidence.Quote} | {ondansetronMed.Evidence.Quote}",
                StartChar: qtAntimicrobial.Evidence.Start,
                EndChar: ondansetronMed.Evidence.End
            ));
        }

        // High-Risk Methotrexate Dosing Check (Daily vs Weekly)
        foreach (var mtx in activeMeds.Where(m => m.Name.Contains("methotrexate", StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains("methotrexate", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            string freqText = (mtx.Frequency ?? mtx.Instructions ?? mtx.Evidence.Quote).ToLowerInvariant();
            bool isDaily = Regex.IsMatch(freqText, @"\b(once\s+daily|twice\s+daily|daily|every\s+day|od|qd|bd|bid|tid|tds|qid)\b") &&
                           !Regex.IsMatch(freqText, @"\b(weekly|once\s+weekly|every\s+week|never\s+(?:take\s+)?daily)\b");
            if (isDaily)
            {
                alerts.Add(new ClinicalSafetyAlertDto(
                    AlertId: $"POS-{Guid.NewGuid():N}"[..12],
                    Severity: "CRITICAL",
                    Category: "HIGH_RISK_POSOLOGY",
                    Title: "High-Risk Posology: Methotrexate Daily Dosing Detected",
                    Description: "Derived interpretation: Methotrexate is documented with a daily frequency rather than weekly dosing. Non-oncologic daily Methotrexate administration carries severe risk of fatal pancytopenia and hepatotoxicity.",
                    InvolvedEntities: [mtx.Name],
                    Recommendation: "Verify intended weekly versus daily dosing schedule immediately against the original prescriber order.",
                    AlertType: "DERIVED_CLINICAL_INFERENCE",
                    ObservedFact: $"Methotrexate frequency documented as '{mtx.Frequency ?? "daily"}' in '{mtx.Evidence.Quote}'.",
                    EvidenceQuote: mtx.Evidence.Quote,
                    StartChar: mtx.Evidence.Start,
                    EndChar: mtx.Evidence.End
                ));
            }
        }

        // Pediatric / Weight-Based Dosing Review Alert
        foreach (var med in activeMeds.Where(m => m.QualityFlags.Contains("COMPLEX_REGIMEN_REVIEW") && Regex.IsMatch(m.Evidence.Quote, @"\bmg\s*/\s*kg\b", RegexOptions.IgnoreCase)))
        {
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"POS-WT-{Guid.NewGuid():N}"[..12],
                Severity: "MODERATE",
                Category: "WEIGHT_BASED_POSOLOGY",
                Title: $"Weight-Based Regimen Requires Manual Calculation Check: {med.Name}",
                Description: $"Observed data: '{med.Evidence.Quote}' specifies a weight-based dose expression (mg/kg). Automated scalar strength/dose fields are withheld to prevent miscalculation.",
                InvolvedEntities: [med.Name],
                Recommendation: "Verify patient weight in kg and confirm total daily and per-administration dose against the source note.",
                AlertType: "OBSERVED_FACT_WARNING",
                ObservedFact: med.Evidence.Quote,
                EvidenceQuote: med.Evidence.Quote,
                StartChar: med.Evidence.Start,
                EndChar: med.Evidence.End
            ));
        }

        // 2. Contraindications & Drug-Disease / Allergy Conflicts
        // Only active or non-discontinued/non-negated ACEi/ARB triggers hyperkalemia contraindication
        var activeAceArb = activeMeds.FirstOrDefault(m =>
            new[] { "lisinopril", "telmisartan", "telma", "losartan", "valsartan" }.Any(k =>
                m.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)));
        if (textLower.Contains("hyperkalemia") && activeAceArb != null)
        {
            int hkIdx = textLower.IndexOf("hyperkalemia", StringComparison.Ordinal);
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"CI-{Guid.NewGuid():N}"[..12],
                Severity: "CRITICAL",
                Category: "CONTRAINDICATION",
                Title: "Electrolyte Contraindication: ACEi/ARB in Hyperkalemia",
                Description: "Derived interpretation: Renin-angiotensin system blockers (Lisinopril/ARB) exacerbate serum potassium retention in patients with documented hyperkalemia.",
                InvolvedEntities: [activeAceArb.Name, "Hyperkalemia"],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "DERIVED_CLINICAL_INFERENCE",
                ObservedFact: $"Active {activeAceArb.Name} order with 'hyperkalemia' documented at [{hkIdx}..{hkIdx + 12}].",
                EvidenceQuote: activeAceArb.Evidence.Quote,
                StartChar: activeAceArb.Evidence.Start,
                EndChar: activeAceArb.Evidence.End
            ));
        }

        if ((textLower.Contains("prolonged prothrombin") || textLower.Contains("supratherapeutic inr")) && HasAnyDrugMention("warfarin"))
        {
            var wMed = medications.First(m => m.Name.Contains("warfarin", StringComparison.OrdinalIgnoreCase) || (m.Normalization.DisplayName?.Contains("warfarin", StringComparison.OrdinalIgnoreCase) ?? false));
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"CI-{Guid.NewGuid():N}"[..12],
                Severity: "HIGH",
                Category: "CONTRAINDICATION",
                Title: "Coagulopathy Alert: Warfarin with Prolonged Prothrombin Time",
                Description: "Derived interpretation: Prolonged prothrombin time / elevated INR detected in patient with Warfarin exposure.",
                InvolvedEntities: [wMed.Name, "Prolonged Prothrombin Time"],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "DERIVED_CLINICAL_INFERENCE",
                ObservedFact: wMed.Evidence.Quote,
                EvidenceQuote: wMed.Evidence.Quote,
                StartChar: wMed.Evidence.Start,
                EndChar: wMed.Evidence.End
            ));
        }

        // Confirmed allergies only (exclude negated allergy mentions like "Denies Aspirin allergy")
        var allergies = medications.Where(m => m.AssertionStatus == "allergy" && !ClinicalScope.Negated(m.Evidence.Quote)).ToList();
        foreach (var allergy in allergies)
        {
            string allergen = allergy.Name;
            if ((allergen.Contains("aspirin", StringComparison.OrdinalIgnoreCase) && HasActiveDrug("aspirin", "ecosprin")) ||
                (allergen.Contains("penicillin", StringComparison.OrdinalIgnoreCase) && HasActiveDrug("penicillin", "amoxicillin", "augmentin")) ||
                (allergen.Contains("amoxicillin", StringComparison.OrdinalIgnoreCase) && HasActiveDrug("amoxicillin", "augmentin", "penicillin")) ||
                (allergen.Contains("ibuprofen", StringComparison.OrdinalIgnoreCase) && HasActiveDrug("ibuprofen")))
            {
                alerts.Add(new ClinicalSafetyAlertDto(
                    AlertId: $"ALG-{Guid.NewGuid():N}"[..12],
                    Severity: "CRITICAL",
                    Category: "ALLERGY_CONFLICT",
                    Title: $"Drug-Allergy Contraindication: Active Order Matches Documented {allergen} Allergy",
                    Description: $"Derived interpretation: Patient has a documented allergy to {allergen}, and a cross-reactive or identical medication is active in the treatment plan.",
                    InvolvedEntities: [allergen],
                    Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                    AlertType: "DERIVED_CLINICAL_INFERENCE",
                    ObservedFact: allergy.Evidence.Quote,
                    EvidenceQuote: allergy.Evidence.Quote,
                    StartChar: allergy.Evidence.Start,
                    EndChar: allergy.Evidence.End
                ));
            }
            else
            {
                alerts.Add(new ClinicalSafetyAlertDto(
                    AlertId: $"ALG-{Guid.NewGuid():N}"[..12],
                    Severity: "MODERATE",
                    Category: "ALLERGY_CONFLICT",
                    Title: $"Documented Drug Allergy Vigilance: {allergen}",
                    Description: $"Observed data: Patient note documents allergy/reaction to {allergen} (evidence: \"{allergy.Evidence.Quote}\"). Ensure all newly prescribed agents avoid cross-reactivity.",
                    InvolvedEntities: [allergen],
                    Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                    AlertType: "OBSERVED_FACT_WARNING",
                    ObservedFact: allergy.Evidence.Quote,
                    EvidenceQuote: allergy.Evidence.Quote,
                    StartChar: allergy.Evidence.Start,
                    EndChar: allergy.Evidence.End
                ));
            }
        }

        // 3. Source-Grounded Lab & Vital Sign Anomalies (Fail-Closed on Ambiguity or Blood Pressure Contamination)
        var (kMeasurement, kConflict) = TryBindMeasurement(noteText, PotassiumGroundedRegex(), "Potassium", "mEq/L", minPlausible: 1.0, maxPlausible: 12.0);
        if (kConflict)
        {
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"QA-{Guid.NewGuid():N}"[..12],
                Severity: "WARNING",
                Category: "DATA_QUALITY_WARNING",
                Title: "Ambiguous or Conflicting Potassium Measurement",
                Description: "Multiple conflicting serum potassium values were detected in the note; automated threshold alerting was suppressed pending clinician verification.",
                InvolvedEntities: ["Potassium"],
                Recommendation: "Verify serum potassium directly in the source laboratory report.",
                AlertType: "OBSERVED_FACT_WARNING"
            ));
        }
        else if (kMeasurement != null && (kMeasurement.Value > 5.5 || kMeasurement.Value < 3.0))
        {
            string valStr = kMeasurement.Value.ToString("0.##", CultureInfo.InvariantCulture);
            string kTemp = DetermineAlertTemporality(kMeasurement.StartChar, kMeasurement.Quote);
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"LAB-{Guid.NewGuid():N}"[..12],
                Severity: "CRITICAL",
                Category: "LAB_ANOMALY",
                Title: $"Critical Potassium Value ({valStr} {kMeasurement.Unit})",
                Description: $"Observed data ({kTemp}): {kMeasurement.Analyte} = {valStr} {kMeasurement.Unit} (evidence: \"{kMeasurement.Quote}\" at [{kMeasurement.StartChar}..{kMeasurement.EndChar}]). Derived interpretation (configured institutional threshold: K+ > 5.5 or < 3.0 mEq/L; reference range 3.5–5.1 mEq/L): Exceeds hyperkalemia threshold (> 5.5 mEq/L) and carries arrhythmia risk.",
                InvolvedEntities: [$"Potassium: {valStr} {kMeasurement.Unit}"],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "DERIVED_CLINICAL_INFERENCE",
                Temporality: kTemp,
                ThresholdCriterion: "Configured rule threshold: Serum Potassium > 5.5 mEq/L or < 3.0 mEq/L (reference interval 3.5–5.1 mEq/L)",
                ObservedFact: $"{kMeasurement.Analyte} = {valStr} {kMeasurement.Unit}",
                EvidenceQuote: kMeasurement.Quote,
                StartChar: kMeasurement.StartChar,
                EndChar: kMeasurement.EndChar
            ));
        }

        var (crMeasurement, crConflict) = TryBindMeasurement(noteText, CreatinineGroundedRegex(), "Serum Creatinine", "mg/dL", minPlausible: 0.1, maxPlausible: 25.0);
        if (crConflict)
        {
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"QA-{Guid.NewGuid():N}"[..12],
                Severity: "WARNING",
                Category: "DATA_QUALITY_WARNING",
                Title: "Ambiguous or Conflicting Serum Creatinine Measurement",
                Description: "Multiple conflicting serum creatinine measurements were detected in the note; automated renal impairment alerting was suppressed pending clinician verification.",
                InvolvedEntities: ["Serum Creatinine"],
                Recommendation: "Verify serum creatinine directly in the source laboratory report.",
                AlertType: "OBSERVED_FACT_WARNING"
            ));
        }
        else if (crMeasurement != null && crMeasurement.Value >= 1.8)
        {
            string valStr = crMeasurement.Value.ToString("0.##", CultureInfo.InvariantCulture);
            string crTemp = DetermineAlertTemporality(crMeasurement.StartChar, crMeasurement.Quote);
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"LAB-{Guid.NewGuid():N}"[..12],
                Severity: "HIGH",
                Category: "LAB_ANOMALY",
                Title: $"Elevated Serum Creatinine ({valStr} {crMeasurement.Unit})",
                Description: $"Observed data ({crTemp}): {crMeasurement.Analyte} = {valStr} {crMeasurement.Unit} (evidence: \"{crMeasurement.Quote}\" at [{crMeasurement.StartChar}..{crMeasurement.EndChar}]). Derived interpretation (configured threshold: SCr >= 1.8 mg/dL): Potentially elevated serum creatinine requiring clinical interpretation and renal dose review.",
                InvolvedEntities: [$"Serum Creatinine: {valStr} {crMeasurement.Unit}"],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "DERIVED_CLINICAL_INFERENCE",
                Temporality: crTemp,
                ThresholdCriterion: "Configured rule threshold: Serum Creatinine >= 1.8 mg/dL",
                ObservedFact: $"{crMeasurement.Analyte} = {valStr} {crMeasurement.Unit}",
                EvidenceQuote: crMeasurement.Quote,
                StartChar: crMeasurement.StartChar,
                EndChar: crMeasurement.EndChar
            ));
        }

        var tempMatch = HighFeverRegex().Match(noteText);
        if (tempMatch.Success && double.TryParse(tempMatch.Groups["val"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double tempC) && tempC >= 38.5)
        {
            string tempStr = tempC.ToString("0.#", CultureInfo.InvariantCulture);
            string pyrexiaTemp = DetermineAlertTemporality(tempMatch.Index, noteText[Math.Max(0, tempMatch.Index - 80)..Math.Min(noteText.Length, tempMatch.Index + tempMatch.Length + 80)]);
            string titlePrefix = pyrexiaTemp == "HISTORICAL_ADMISSION_COURSE" ? "Historical Admission Finding — " : "";
            alerts.Add(new ClinicalSafetyAlertDto(
                AlertId: $"VIT-{Guid.NewGuid():N}"[..12],
                Severity: "MODERATE",
                Category: "LAB_ANOMALY",
                Title: $"{titlePrefix}Significant Pyrexia ({tempStr}°C)",
                Description: $"Observed data ({pyrexiaTemp}): Temperature = {tempStr}°C (evidence: \"{tempMatch.Value.Trim()}\" at [{tempMatch.Index}..{tempMatch.Index + tempMatch.Length}]). Derived interpretation (configured threshold: Temp >= 38.5°C): High fever documented in {pyrexiaTemp.ToLowerInvariant().Replace('_', ' ')} context.",
                InvolvedEntities: [$"Temperature: {tempStr}°C"],
                Recommendation: "Review the source note and reconcile this finding with a qualified clinician. This extraction does not recommend treatment changes.",
                AlertType: "DERIVED_CLINICAL_INFERENCE",
                Temporality: pyrexiaTemp,
                ThresholdCriterion: "Configured rule threshold: Temperature >= 38.5°C",
                ObservedFact: $"Temperature = {tempStr}°C",
                EvidenceQuote: tempMatch.Value.Trim(),
                StartChar: tempMatch.Index,
                EndChar: tempMatch.Index + tempMatch.Length
            ));
        }

        return alerts;
    }

    /// <summary>
    /// Binds an analyte to a directly associated numeric value and compatible clinical unit with exact
    /// source offsets. Rejects blood-pressure ratios (e.g. 124/76 mmHg), duration phrases (e.g. in 48 hours),
    /// and incompatible units. Fails closed if multiple distinct values conflict.
    /// </summary>
    public static (GroundedLabMeasurement? Measurement, bool HasConflict) TryBindMeasurement(
        string noteText,
        Regex pattern,
        string canonicalAnalyte,
        string defaultUnit,
        double minPlausible,
        double maxPlausible)
    {
        var grounded = new List<GroundedLabMeasurement>();

        foreach (Match match in pattern.Matches(noteText))
        {
            var valGroup = match.Groups["val"];
            var unitGroup = match.Groups["unit"];
            if (!valGroup.Success || !double.TryParse(valGroup.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                continue;

            // Guard against blood pressure ratio (e.g. "124/76") or date/ratio where number is followed or preceded by '/'
            int valEnd = valGroup.Index + valGroup.Length;
            if (valEnd < noteText.Length && noteText[valEnd] == '/')
                continue;
            if (valGroup.Index > 0 && noteText[valGroup.Index - 1] == '/')
                continue;

            // Check if the number is immediately followed by an incompatible unit (e.g., mmHg, bpm, hours, days, weeks, mg of a drug)
            string tail = noteText[valEnd..Math.Min(noteText.Length, valEnd + 18)];
            if (IncompatibleTrailingUnitRegex().IsMatch(tail))
                continue;

            // Require an explicit compatible unit (e.g. mg/dL, mEq/L, mmol/L) so unitless numbers are never accepted as lab results
            bool hasExplicitUnit = unitGroup.Success && unitGroup.Length > 0;
            if (!hasExplicitUnit)
                continue;

            if (value < minPlausible || value > maxPlausible)
                continue;

            string unit = hasExplicitUnit ? CanonicalizeUnit(unitGroup.Value, defaultUnit) : defaultUnit;
            int start = match.Index;
            int end = match.Index + match.Length;
            grounded.Add(new GroundedLabMeasurement(canonicalAnalyte, value, unit, noteText[start..end].Trim(), start, end));
        }

        if (grounded.Count == 0)
            return (null, false);

        var distinctValues = grounded.Select(g => Math.Round(g.Value, 2)).Distinct().ToList();
        if (distinctValues.Count > 1)
            return (null, true); // Conflicting measurements for the same analyte -> fail closed

        return (grounded[0], false);
    }

    private static string CanonicalizeUnit(string rawUnit, string fallback) =>
        rawUnit.Trim().ToLowerInvariant() switch
        {
            "mg/dl" => "mg/dL",
            "meq/l" => "mEq/L",
            "mmol/l" => "mmol/L",
            "umol/l" or "µmol/l" => "µmol/L",
            _ => fallback
        };

    // Requires explicit analyte name ("K+", "K", "Potassium", "Serum potassium"), optional connector,
    // numeric value NOT part of a ratio, and either compatible unit (mEq/L, mmol/L) or explicit verb/colon connector.
    // Note: °CK+ in flattened PDF text has 'C' before 'K+', so allow 'K+' even after '°C'.
    [GeneratedRegex(@"(?i)(?:(?<![A-Za-z])(?:serum\s+potassium|potassium|k)(?![A-Za-z])|(?<![A-BD-Za-bd-z])k\+)(?:\s+(?<conn>was|is|level|of)|(?<conn>\s*[=:]\s*)|\s+)(?<val>\d+(?:\.\d+)?)(?!\s*/)(?:\s*(?<unit>mEq/L|mmol/L)\b)?", RegexOptions.Compiled)]
    private static partial Regex PotassiumGroundedRegex();

    // Requires explicit analyte name ("Serum creatinine", "Creatinine", "SCr"), optional connector,
    // numeric value NOT part of a ratio (e.g., 124/76), and either compatible unit (mg/dL) or explicit verb/colon connector.
    [GeneratedRegex(@"(?i)\b(?:serum\s+creatinine|creatinine|scr)\b(?:\s+(?<conn>was|is|level|of)|(?<conn>\s*[=:]\s*)|\s+)(?<val>\d+(?:\.\d+)?)(?!\s*/)(?:\s*(?<unit>mg/dL)\b)?", RegexOptions.Compiled)]
    private static partial Regex CreatinineGroundedRegex();

    [GeneratedRegex(@"^\s*(?:mmHg|bpm|hours?|hrs?|days?|weeks?|months?|years?|%|°C|C\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IncompatibleTrailingUnitRegex();

    [GeneratedRegex(@"(?i)\bfever\s*(?:>|of|is|was|:)?\s*(?<val>\d{2}(?:\.\d+)?)\s*°?\s*c\b", RegexOptions.Compiled)]
    private static partial Regex HighFeverRegex();
}
