using System.Text.RegularExpressions;
using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public partial class MicroCardBuilder
{
    public static string Build(
        string noteText,
        IReadOnlyList<ClinicalEntityResponse> entities,
        IReadOnlyList<StructuredMedicationResponse> structuredMeds,
        string demographics = "PT")
    {
        string textLower = noteText.ToLowerInvariant();

        // 1. Diagnoses routing
        var dxItems = new List<string>();
        var diffItems = new List<string>();
        var hxItems = new List<string>();
        var seenDx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDiff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenHx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entities)
        {
            if (e.Category.Equals("CONDITION", StringComparison.OrdinalIgnoreCase) ||
                e.Category.Equals("DISEASE", StringComparison.OrdinalIgnoreCase))
            {
                string name = e.Text.Trim();
                bool isPositive = (e.Certainty != null && e.Certainty.Equals("Positive", StringComparison.OrdinalIgnoreCase)) ||
                                  (e.Assertion != null && e.Assertion.Equals("PRESENT", StringComparison.OrdinalIgnoreCase));
                bool isPatient = e.Experiencer == null || e.Experiencer.Equals("Patient", StringComparison.OrdinalIgnoreCase);

                if (isPositive && isPatient)
                {
                    if (e.Temporality != null && e.Temporality.Equals("Historical", StringComparison.OrdinalIgnoreCase))
                    {
                        if (seenHx.Add(name)) hxItems.Add(name);
                    }
                    else
                    {
                        if (seenDx.Add(name)) dxItems.Add(name);
                    }
                }
                else if (e.Certainty != null && e.Certainty.Equals("Possible", StringComparison.OrdinalIgnoreCase))
                {
                    if (seenDiff.Add(name)) diffItems.Add(name);
                }
            }
        }

        string dxStr = dxItems.Count > 0 ? string.Join("+", dxItems.Take(2)) : "NONE";

        // 2. Affirmative Medications & Posology
        var rxItems = new List<string>();
        var seenRx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (structuredMeds.Count > 0)
        {
            foreach (var sm in structuredMeds)
            {
                bool isPositive = sm.Certainty == null || sm.Certainty.Equals("Positive", StringComparison.OrdinalIgnoreCase);
                bool isPatient = sm.Experiencer == null || sm.Experiencer.Equals("Patient", StringComparison.OrdinalIgnoreCase);

                if (isPositive && isPatient)
                {
                    string? doseStr = sm.Dose ?? sm.Dosage;
                    string dosePart = !string.IsNullOrWhiteSpace(doseStr) ? $" {doseStr}" : "";
                    string entry = $"{sm.Drug}{dosePart}".Trim();
                    if (seenRx.Add(entry))
                    {
                        rxItems.Add(entry);
                    }
                }
            }
        }
        else
        {
            foreach (var e in entities)
            {
                if (e.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase))
                {
                    bool isPositive = (e.Certainty != null && e.Certainty.Equals("Positive", StringComparison.OrdinalIgnoreCase)) ||
                                      (e.Assertion != null && e.Assertion.Equals("PRESENT", StringComparison.OrdinalIgnoreCase));
                    bool isPatient = e.Experiencer == null || e.Experiencer.Equals("Patient", StringComparison.OrdinalIgnoreCase);

                    if (isPositive && isPatient)
                    {
                        string entry = e.Text.Trim();
                        if (seenRx.Add(entry))
                        {
                            rxItems.Add(entry);
                        }
                    }
                }
            }
        }

        string rxStr = rxItems.Count > 0 ? string.Join(", ", rxItems.Take(3)) : "NONE";

        // 3. Allergies
        string algStr = "NKDA";
        var algMatch = AllergyRegex().Match(textLower);
        if (algMatch.Success)
        {
            string val = algMatch.Groups[1].Value.Trim();
            if (val.Contains("no known") || val.Contains("nkda") || val.Contains("none") || val.Contains("denies"))
            {
                algStr = "NKDA";
            }
            else if (!string.IsNullOrEmpty(val))
            {
                algStr = char.ToUpperInvariant(val[0]) + val[1..];
            }
        }

        // 4. Critical / Actionable Labs
        var labItems = new List<string>();
        var seenLabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Check Panic labs in note
        var kMatch = PotassiumRegex().Match(textLower);
        if (kMatch.Success && double.TryParse(kMatch.Groups[1].Value, out double kval) && (kval > 5.5 || kval < 3.5))
        {
            labItems.Add($"K:{kval}!!");
        }

        var scrMatch = CreatinineRegex().Match(textLower);
        if (scrMatch.Success && double.TryParse(scrMatch.Groups[1].Value, out double scrVal) && scrVal > 1.3)
        {
            labItems.Add($"sCr:{scrVal}^");
        }

        // Add Lab and Investigation entities (including Indian panels like CUE, RFT, Lipid Profile)
        foreach (var e in entities)
        {
            bool isLab = e.Category.Equals("LAB", StringComparison.OrdinalIgnoreCase) ||
                         e.Category.Equals("LAB_TEST", StringComparison.OrdinalIgnoreCase) ||
                         e.Category.Equals("INVESTIGATION", StringComparison.OrdinalIgnoreCase) ||
                         (e.ConceptCode != null && e.ConceptCode.StartsWith("LOINC:"));

            bool isNegated = (e.Certainty != null && e.Certainty.Equals("Negated", StringComparison.OrdinalIgnoreCase)) ||
                             (e.Assertion != null && e.Assertion.Equals("ABSENT", StringComparison.OrdinalIgnoreCase));

            if (isLab && !isNegated)
            {
                string lbl = e.Text.Trim();
                if (!string.IsNullOrEmpty(lbl) && seenLabs.Add(lbl))
                {
                    labItems.Add(lbl);
                }
            }
        }

        string labStr = labItems.Count > 0 ? string.Join(", ", labItems.Take(6)) : "NONE";

        // 5. Vitals
        var vitItems = new List<string>();
        var bpMatch = BpRegex().Match(textLower);
        if (bpMatch.Success)
        {
            var parts = bpMatch.Groups[1].Value.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int sys) && int.TryParse(parts[1], out int dia))
            {
                if (sys >= 140 || sys < 90 || dia >= 90 || dia < 60)
                {
                    vitItems.Add($"BP:{bpMatch.Groups[1].Value}");
                }
            }
        }

        var hrMatch = HrRegex().Match(textLower);
        if (hrMatch.Success && int.TryParse(hrMatch.Groups[1].Value, out int hr) && (hr > 100 || hr < 50))
        {
            vitItems.Add($"HR:{hr}");
        }

        string vitStr = vitItems.Count > 0 ? string.Join(", ", vitItems.Take(2)) : "NONE";

        // 6. Demographics
        string demoStr = demographics;
        var demoMatch = DemoRegex().Match(textLower);
        if (demoMatch.Success)
        {
            string age = demoMatch.Groups[1].Value;
            string g = demoMatch.Groups[2].Value.StartsWith("m") ? "M" : "F";
            demoStr = $"PT {age}{g}";
        }

        string card = $"[{demoStr}] DX:{dxStr} | RX:{rxStr} | ALG:{algStr} | LAB:{labStr} | VIT:{vitStr}";
        if (diffItems.Count > 0)
        {
            card += $" | DIFF:{string.Join("+", diffItems.Take(2))}";
        }
        if (hxItems.Count > 0)
        {
            card += $" | HX:{string.Join("+", hxItems.Take(2))}";
        }

        return card;
    }

    [GeneratedRegex(@"\b(?:allergic to|allergy(?:\s+to)?|allergies)\s*[:]?\s*([A-Za-z0-9,\s]+?)(?=[.;]|$)", RegexOptions.Compiled)]
    private static partial Regex AllergyRegex();

    [GeneratedRegex(@"\b(?:k|potassium)(?:\s+(?:was|is|level|serum|of))?\s*[:=]?\s*([0-9.]+)\b", RegexOptions.Compiled)]
    private static partial Regex PotassiumRegex();

    [GeneratedRegex(@"\b(?:scr|creatinine)(?:\s+(?:was|is|level|serum|of))?\s*[:=]?\s*([0-9.]+)\b", RegexOptions.Compiled)]
    private static partial Regex CreatinineRegex();

    [GeneratedRegex(@"\b(?:bp|blood pressure)?\s*[:=]?\s*(\d{2,3}/\d{2,3})\b", RegexOptions.Compiled)]
    private static partial Regex BpRegex();

    [GeneratedRegex(@"\b(?:hr|heart rate|pulse)(?:\s+(?:was|is|of))?\s*[:=]?\s*(\d{2,3})\b", RegexOptions.Compiled)]
    private static partial Regex HrRegex();

    [GeneratedRegex(@"\b(\d{1,2})\s*(?:yo|year old|year-old|-year-old)\s*(male|female|man|woman|m|f)\b", RegexOptions.Compiled)]
    private static partial Regex DemoRegex();
}
