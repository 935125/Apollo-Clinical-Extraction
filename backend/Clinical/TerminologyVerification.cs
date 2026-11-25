using System.Text.Json;
using System.Text.RegularExpressions;

namespace Apollo.Backend.Clinical;

// Pinned lookup evidence is separate from semantic approval. No network request occurs during extraction.
public static class TerminologyVerification
{
    private static readonly JsonDocument Snapshot = Load();
    private static JsonDocument Load()
    {
        using var stream = typeof(TerminologyVerification).Assembly.GetManifestResourceStream("Apollo.Backend.Clinical.terminology-verification.json")!;
        return JsonDocument.Parse(stream);
    }
    public static string Version => Snapshot.RootElement.GetProperty("rxnormVersion").GetString()!;
    public static string RxNormSource => Snapshot.RootElement.GetProperty("rxnormSource").GetString()!;
    public static string LoincSource => Snapshot.RootElement.GetProperty("loincSource").GetString()!;
    public static string LoincVersion => "2.83";
    private static string Ingredient(string value) => string.Join("/", value.ToLowerInvariant().Split('/')
        .Select(v => Regex.Replace(v.Trim(), @"\s+", " ").Replace("paracetamol", "acetaminophen").Replace("salbutamol", "albuterol"))
        .Order(StringComparer.Ordinal));
    public static bool CodeExists(string category, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        string rootProp = category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase) ? "medications" : "investigations";
        if (!Snapshot.RootElement.GetProperty(rootProp).TryGetProperty(code, out var term)) return false;
        return rootProp == "medications"
            ? term.GetProperty("active").GetBoolean()
            : term.GetProperty("exists").GetBoolean();
    }
    public static string ClassifyMedication(string mention, string? standardName, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(standardName)) return "unresolved";
        if (!CodeExists("MEDICATION", code)) return "unresolved";
        if (IngredientSupported(mention, standardName, code)) return "exact_clinical_match";
        string clean = mention.Trim().ToLowerInvariant();
        if (clean.Contains('/') || clean.Contains(" gp", StringComparison.Ordinal)) return "ambiguous_match";
        return "partial_match";
    }
    /// <summary>
    /// Standardized 5-tier semantic specificity classification for medications:
    /// exact, broader, narrower, ambiguous, unresolved.
    /// </summary>
    public static (string SemanticStatus, string MappingNote) ClassifyMedicationSemantic(string mention, string? standardName, string? code, string? documentedStrength = null)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(standardName) || !CodeExists("MEDICATION", code))
            return ("unresolved", "No active RxNorm concept found in verified snapshot.");
        string clean = mention.Trim().ToLowerInvariant();
        if (clean.Contains(" gp", StringComparison.Ordinal))
            return ("ambiguous", "Brand mention 'Glycomet GP' implies a multi-ingredient glimepiride/metformin product, whereas candidate RxCUI encodes single-ingredient metformin.");
        if (!Snapshot.RootElement.GetProperty("medications").TryGetProperty(code, out var term))
            return ("unresolved", "RxCUI not present in verified snapshot.");
        string tty = term.GetProperty("termType").GetString() ?? "";
        if (tty is "SBD" or "SCD")
            return ("narrower", $"Candidate RxCUI {code} ({tty}) encodes a specific brand, salt, or strength not proven by the source mention '{mention}'.");
        if (IngredientSupported(mention, standardName, code))
        {
            string note = string.IsNullOrWhiteSpace(documentedStrength)
                ? $"Exact RxNorm ingredient ({tty}) match for '{standardName}' (RxCUI {code})."
                : $"Exact RxNorm ingredient ({tty}) match for '{standardName}' (RxCUI {code}); product strength '{documentedStrength}' is preserved separately in the strength field.";
            return ("exact", note);
        }
        return ("broader", $"Candidate RxCUI {code} is broader or differs in brand/form specificity from mention '{mention}'.");
    }
    public static string ClassifyInvestigation(string mention, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !CodeExists("INVESTIGATION", code)) return "unresolved";
        if (InvestigationSupported(mention, code)) return "exact_clinical_match";
        string clean = mention.Trim().ToLowerInvariant();
        if (clean is "ultrasound" or "usg" or "ct" or "ct scan" or "mri" or "mri scan" or "x-ray" or "echocardiogram" or "echo")
            return "ambiguous_match";
        return "partial_match";
    }
    /// <summary>
    /// Standardized 5-tier semantic specificity classification for LOINC investigations:
    /// exact, broader, narrower, ambiguous, unresolved.
    /// </summary>
    public static (string SemanticStatus, string MappingNote) ClassifyInvestigationSemantic(string mention, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !CodeExists("INVESTIGATION", code))
            return ("unresolved", "No verified LOINC concept found in snapshot.");
        string clean = mention.Trim().ToLowerInvariant();
        if (InvestigationSupported(clean, code))
            return ("exact", $"Verified LOINC {code} matches component, system/specimen, method, and view/lead specificity of '{mention}'.");
        if (clean is "ultrasound" or "usg" or "ct" or "ct scan" or "mri" or "mri scan" or "x-ray")
            return ("ambiguous", $"Mention '{mention}' lacks anatomical region or modality view; candidate LOINC {code} is underspecified.");
        return (code, clean) switch
        {
            ("24648-8", "chest x-ray pa view" or "chest x-ray (pa view)" or "follow-up chest x-ray (pa view)") =>
                ("narrower", "LOINC 24648-8 ('XR Chest PA upright') specifies an upright patient posture not stated in 'chest X-ray (PA view)'; withheld from exact match code."),
            ("36643-5", _) =>
                ("narrower", "LOINC 36643-5 ('XR Chest 2 Views') specifies a 2-view study not stated in generic 'Chest X-ray'; withheld from exact match code."),
            ("11524-6", "12-lead ecg" or "12 lead ecg") =>
                ("broader", "LOINC 11524-6 ('EKG study') is a general electrocardiogram concept and does not encode the 12-lead configuration stated in '12-lead ECG' (vs LOINC 34534-8); withheld from exact match code."),
            ("2823-3", "k+" or "potassium") =>
                ("narrower", "LOINC 2823-3 specifies Serum or Plasma system, whereas shorthand 'K+'/'potassium' does not state specimen (could be serum, plasma, whole blood, or urine)."),
            ("2160-0", "creatinine") =>
                ("narrower", "LOINC 2160-0 specifies Serum or Plasma system, whereas unqualified 'Creatinine' does not state specimen (serum vs urine)."),
            ("2951-2", "na+" or "sodium") =>
                ("narrower", "LOINC 2951-2 specifies Serum or Plasma system, whereas shorthand 'Na+'/'sodium' does not state specimen."),
            ("6301-6", _) =>
                ("narrower", "LOINC 6301-6 ('INR in Platelet poor plasma by Coagulation assay') encodes only INR (omitting Prothrombin Time seconds) and assumes platelet-poor plasma coagulation assay."),
            ("58410-2", _) =>
                ("narrower", "LOINC 58410-2 ('CBC panel - Blood by Automated count') assumes an automated count method not stated in generic 'CBC'."),
            ("1988-5", _) =>
                ("narrower", "LOINC 1988-5 ('C reactive protein [Mass/volume] in Serum or Plasma') assumes Serum/Plasma mass concentration not stated in unqualified 'CRP'."),
            ("630-4", _) =>
                ("broader", "LOINC 630-4 ('Bacteria identified in Urine by Culture') encodes culture identification only and does not include antibiotic susceptibility (& Sensitivity)."),
            _ => ("narrower", $"Candidate LOINC {code} carries additional specimen, method, panel-year, or view assumptions beyond '{mention}'.")
        };
    }
    public static bool IngredientSupported(string mention, string standardName, string code)
    {
        if (!Snapshot.RootElement.GetProperty("medications").TryGetProperty(code, out var term) ||
            !term.GetProperty("active").GetBoolean() || term.GetProperty("termType").GetString() is not ("IN" or "MIN")) return false;
        string cleanMention = Regex.Replace(mention.Trim().ToLowerInvariant(), @"\s+acetate$", "");
        return Ingredient(cleanMention) == Ingredient(standardName) && Ingredient(standardName) == Ingredient(term.GetProperty("name").GetString()!);
    }
    /// <summary>V2 identity gate: a documented salt/ester cannot disappear to approve a base ingredient code.</summary>
    public static bool DocumentedIngredientMatches(string mention, string? code)
    {
        return code != null && Snapshot.RootElement.GetProperty("medications").TryGetProperty(code, out var term) &&
            term.GetProperty("active").GetBoolean() && term.GetProperty("termType").GetString() is "IN" or "MIN" &&
            Ingredient(mention) == Ingredient(term.GetProperty("name").GetString()!);
    }
    public static bool InvestigationSupported(string mention, string code)
    {
        if (!Snapshot.RootElement.GetProperty("investigations").TryGetProperty(code, out var term) || !term.GetProperty("exists").GetBoolean()) return false;
        // Only these reviewed concept/mention pairs carry no extra method, view, posture, lead-count, analyte, or system assumption.
        return (code, mention.Trim().ToLowerInvariant()) switch
        {
            ("24558-9", "ultrasound abdomen" or "usg abdomen") => true,
            ("11524-6", "ecg" or "ekg" or "electrocardiogram") => true,
            ("34552-0", "2d echo" or "2d echocardiogram" or "echocardiogram") => true,
            ("600-7", "blood culture" or "blood culture & sensitivity") => true,
            ("24356-8", "complete urine examination" or "cue" or "urinalysis") => true,
            ("2160-0", "serum creatinine" or "plasma creatinine") => true,
            ("2823-3", "serum potassium" or "plasma potassium" or "serum k+") => true,
            ("2951-2", "serum sodium" or "plasma sodium" or "serum na+") => true,
            ("24648-8", "chest x-ray pa upright" or "chest x-ray (pa upright)") => true,
            ("4548-4", "hba1c" or "hemoglobin a1c" or "glycated hemoglobin") => true,
            ("24326-1", "serum electrolytes" or "electrolytes") => true,
            ("3084-1", "serum uric acid") => true,
            ("2857-1", "serum psa") => true,
            ("3016-3", "serum tsh") => true,
            ("3024-7", "serum free t4") => true,
            ("58410-2", "cbc panel - blood by automated count" or "complete blood count automated" or "automated cbc" or "cbc automated") => true,
            _ => false
        };
    }
}
