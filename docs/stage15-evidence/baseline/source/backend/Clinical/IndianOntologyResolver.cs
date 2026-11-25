using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Apollo.Backend.Security;

namespace Apollo.Backend.Clinical;

public record OntologyMatch(
    string Status,
    string? Code,
    string? DisplayName,
    string? StandardId, // RxNorm CUI or LOINC
    double Confidence,
    string? Specimen = null,
    string SemanticStatus = "unresolved",
    string? CandidateCode = null,
    string? TerminologySource = null,
    string? TerminologyVersion = null,
    string? MappingNote = null
);

public record OovQuarantineEntry(
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("mention")] string Mention,
    [property: JsonPropertyName("span")] int[] Span,
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("normalized")] string Normalized
);

public class IndianOntologyResolver
{
    private static readonly Lock FileLock = new();

    private record DrugMapping(string StandardName, string CatalogCode, string RxNormCui);
    private record LabMapping(string StandardName, string CatalogCode, string Loinc, string? Specimen = null);

    private static readonly FrozenDictionary<string, DrugMapping> DrugMaster = new Dictionary<string, DrugMapping>(StringComparer.OrdinalIgnoreCase)
    {
        ["atorvastatin"] = new("Atorvastatin", "RX-ATO-20", "83367"),
        ["atorva"] = new("Atorvastatin", "RX-ATO-20", "83367"),
        ["atorva 10"] = new("Atorvastatin", "RX-ATO-10", "617314"),
        ["atorva 20"] = new("Atorvastatin", "RX-ATO-20", "617318"),
        ["atorva 40"] = new("Atorvastatin", "RX-ATO-40", "617320"),
        ["lipitor"] = new("Atorvastatin", "RX-ATO-20", "83367"),

        ["thyronorm"] = new("Levothyroxine", "RX-LEV-50", "10582"),
        ["eltroxin"] = new("Levothyroxine", "RX-LEV-50", "10582"),
        ["levothyroxine"] = new("Levothyroxine", "RX-LEV-50", "10582"),
        ["synthroid"] = new("Levothyroxine", "RX-LEV-50", "10582"),

        ["nitrofurantoin"] = new("Nitrofurantoin", "RX-NIT-100", "7454"),
        ["niftas"] = new("Nitrofurantoin", "RX-NIT-100", "7454"),
        ["furadantin"] = new("Nitrofurantoin", "RX-NIT-100", "7454"),
        ["macrobid"] = new("Nitrofurantoin", "RX-NIT-100", "7454"),

        ["metformin"] = new("Metformin", "RX-MET-500", "6809"),
        ["glycomet"] = new("Metformin", "RX-MET-500", "6809"),
        ["glycomet 500"] = new("Metformin", "RX-MET-500", "6809"),
        ["glycomet gp"] = new("Metformin", "RX-MET-500", "6809"),
        ["glucophage"] = new("Metformin", "RX-MET-500", "6809"),

        ["telma"] = new("Telmisartan", "RX-TEL-40", "73494"),
        ["telmisartan"] = new("Telmisartan", "RX-TEL-40", "73494"),
        ["telma 40"] = new("Telmisartan", "RX-TEL-40", "73494"),
        ["telma 80"] = new("Telmisartan", "RX-TEL-80", "73494"),
        ["micardis"] = new("Telmisartan", "RX-TEL-40", "73494"),

        ["ecosprin"] = new("Aspirin", "RX-ASP-75", "1191"),
        ["aspirin"] = new("Aspirin", "RX-ASP-100", "1191"),
        ["disprin"] = new("Aspirin", "RX-ASP-100", "1191"),
        ["ecotrin"] = new("Aspirin", "RX-ASP-100", "1191"),

        ["paracetamol"] = new("Paracetamol", "RX-PCM-650", "161"),
        ["dolo 650"] = new("Paracetamol", "RX-PCM-650", "161"),
        ["dolo"] = new("Paracetamol", "RX-PCM-650", "161"),
        ["crocin"] = new("Paracetamol", "RX-PCM-500", "161"),
        ["calpol"] = new("Paracetamol", "RX-PCM-500", "161"),
        ["acetaminophen"] = new("Paracetamol", "RX-PCM-650", "161"),

        ["pan 40"] = new("Pantoprazole", "RX-PAN-40", "40790"),
        ["pantocid"] = new("Pantoprazole", "RX-PAN-40", "40790"),
        ["pantoprazole"] = new("Pantoprazole", "RX-PAN-40", "40790"),

        ["amoxicillin-clavulanate"] = new("Amoxicillin/Clavulanate", "RX-AMX-CLV-875", "19711"),
        ["amoxicillin/clavulanate"] = new("Amoxicillin/Clavulanate", "RX-AMX-CLV-875", "19711"),
        ["amoxicillin clavulanate"] = new("Amoxicillin/Clavulanate", "RX-AMX-CLV-500", "19711"),
        ["co-amoxiclav"] = new("Amoxicillin/Clavulanate", "RX-AMX-CLV-500", "19711"),
        ["augmentin"] = new("Amoxicillin/Clavulanate", "RX-AMX-CLV-500", "19711"),
        ["amoxicillin"] = new("Amoxicillin", "RX-AMX-500", "723"),
        ["azithromycin"] = new("Azithromycin", "RX-AZI-500", "18631"),
        ["azee 500"] = new("Azithromycin", "RX-AZI-500", "18631"),

        ["tamsulosin"] = new("Tamsulosin", "RX-TAM-04", "77492"),
        ["combodart"] = new("Tamsulosin/Dutasteride", "RX-TAM-DUT", "1001472"),
        ["warfarin"] = new("Warfarin", "RX-WAR-5", "11289"),
        ["lisinopril"] = new("Lisinopril", "RX-LIS-10", "29046"),
        ["omeprazole"] = new("Omeprazole", "RX-OME-20", "7646"),
        ["allopurinol"] = new("Allopurinol", "RX-ALP-100", "519"),
        ["ibuprofen"] = new("Ibuprofen", "RX-IBU-400", "5640"),
        ["ciprofloxacin"] = new("Ciprofloxacin", "RX-CIP-500", "2551"),
        ["losartan"] = new("Losartan", "RX-LOS-50", "52175"),
        ["amlodipine"] = new("Amlodipine", "RX-AML-5", "17767"),
        ["salbutamol"] = new("Salbutamol", "RX-SAL-100", "435"),
        ["albuterol"] = new("Salbutamol", "RX-SAL-100", "435"),
        ["prednisolone acetate"] = new("Prednisolone", "RX-PRED-AC", "8638"),
        ["prednisolone"] = new("Prednisolone", "RX-PRED-40", "8638"),
        ["clopidogrel"] = new("Clopidogrel", "RX-CLO-75", "32968"),
        ["ondansetron"] = new("Ondansetron", "RX-OND-4", "26225"),
        ["cisplatin"] = new("Cisplatin", "RX-CIS-50", "2555"),
        ["pemetrexed"] = new("Pemetrexed", "RX-PEM-500", "68446"),

        // Additional clinical formularies for comprehensive evaluation
        ["empagliflozin"] = new("Empagliflozin", "RX-EMP-10", "1545653"),
        ["linagliptin"] = new("Linagliptin", "RX-LIN-5", "1100699"),
        ["methotrexate"] = new("Methotrexate", "RX-MTX-15", "6851"),
        ["folic acid"] = new("Folic Acid", "RX-FOL-5", "4511"),
        ["clarithromycin"] = new("Clarithromycin", "RX-CLA-500", "21212"),
        ["cephalexin"] = new("Cephalexin", "RX-CEP-500", "2231"),
        ["levetiracetam"] = new("Levetiracetam", "RX-LEV-500", "114477"),
        ["sodium bicarbonate"] = new("Sodium Bicarbonate", "RX-BIC-650", "36676"),
        ["dexamethasone"] = new("Dexamethasone", "RX-DEX-4", "3264"),
        ["metoclopramide"] = new("Metoclopramide", "RX-MET-10", "6915"),
        ["sacubitril/valsartan"] = new("Sacubitril/Valsartan", "RX-SAC-VAL", "1656339"),
        ["sacubitril"] = new("Sacubitril", "RX-SAC", "1656328"),
        ["valsartan"] = new("Valsartan", "RX-VAL", "69749"),
        ["dapagliflozin"] = new("Dapagliflozin", "RX-DAP-10", "1488564"),
        ["bisoprolol"] = new("Bisoprolol", "RX-BIS-5", "19484"),
        ["spironolactone"] = new("Spironolactone", "RX-SPI-25", "9997"),
        ["betamethasone"] = new("Betamethasone", "RX-BET-05", "1514"),
        ["adalimumab"] = new("Adalimumab", "RX-ADA-40", "327361"),
        ["mirtazapine"] = new("Mirtazapine", "RX-MIR-15", "15996"),
        ["sertraline"] = new("Sertraline", "RX-SER-50", "36437"),
        ["gabapentin"] = new("Gabapentin", "RX-GAB-300", "25480"),
        ["finasteride"] = new("Finasteride", "RX-FIN-5", "25025"),
        ["moxifloxacin"] = new("Moxifloxacin", "RX-MOX-05", "139462"),
        ["penicillin g"] = new("Penicillin G", "RX-PENG", "7980"),
        ["penicillin v"] = new("Penicillin V", "RX-PENV", "7984")
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, LabMapping> LabMaster = new Dictionary<string, LabMapping>(StringComparer.OrdinalIgnoreCase)
    {
        ["cue"] = new("Complete Urine Examination", "LAB-URINE", "24356-8", "Urine"),
        ["complete urine examination"] = new("Complete Urine Examination", "LAB-URINE", "24356-8", "Urine"),
        ["urinalysis"] = new("Urinalysis", "LAB-URINE", "24356-8", "Urine"),
        ["urine routine"] = new("Urinalysis", "LAB-URINE", "24356-8", "Urine"),
        ["routine urine examination"] = new("Urinalysis", "LAB-URINE", "24356-8", "Urine"),

        ["urine culture & sensitivity"] = new("Urine Culture and Sensitivity", "LAB-URINE-CS", "630-4", "Urine"),
        ["urine culture and sensitivity"] = new("Urine Culture and Sensitivity", "LAB-URINE-CS", "630-4", "Urine"),
        ["urine culture"] = new("Urine Culture", "LAB-URINE-CS", "630-4", "Urine"),
        ["urine c/s"] = new("Urine Culture and Sensitivity", "LAB-URINE-CS", "630-4", "Urine"),

        ["rft"] = new("Renal Function Test", "LAB-RFT", "24362-6", "Blood"),
        ["renal function test"] = new("Renal Function Test", "LAB-RFT", "24362-6", "Blood"),
        ["renal function tests"] = new("Renal Function Test", "LAB-RFT", "24362-6", "Blood"),
        ["kft"] = new("Kidney Function Test", "LAB-RFT", "24362-6", "Blood"),
        ["kidney function test"] = new("Kidney Function Test", "LAB-RFT", "24362-6", "Blood"),
        ["u&e"] = new("Urea and Electrolytes", "LAB-RFT", "24362-6", "Blood"),
        ["urea and electrolytes"] = new("Urea and Electrolytes", "LAB-RFT", "24362-6", "Blood"),
        ["serum creatinine"] = new("Serum Creatinine", "LAB-CREAT", "2160-0", "Blood"),
        ["plasma creatinine"] = new("Serum Creatinine", "LAB-CREAT", "2160-0", "Blood"),
        ["creatinine"] = new("Serum Creatinine", "LAB-CREAT", "2160-0", "Blood"),

        ["fasting lipid profile"] = new("Lipid Profile", "LAB-LIPID", "24331-1", "Blood"),
        ["lipid profile"] = new("Lipid Profile", "LAB-LIPID", "24331-1", "Blood"),
        ["lipid panel"] = new("Lipid Profile", "LAB-LIPID", "24331-1", "Blood"),
        ["lipids"] = new("Lipid Profile", "LAB-LIPID", "24331-1", "Blood"),

        ["complete blood count (cbc)"] = new("Complete Blood Count", "LAB-CBC", "58410-2", "Blood"),
        ["complete blood count"] = new("Complete Blood Count", "LAB-CBC", "58410-2", "Blood"),
        ["cbc"] = new("Complete Blood Count", "LAB-CBC", "58410-2", "Blood"),
        ["hemogram"] = new("Complete Blood Count", "LAB-CBC", "58410-2", "Blood"),

        ["liver function tests"] = new("Liver Function Tests", "LAB-LFT", "24325-3", "Blood"),
        ["liver function test"] = new("Liver Function Tests", "LAB-LFT", "24325-3", "Blood"),
        ["lft"] = new("Liver Function Tests", "LAB-LFT", "24325-3", "Blood"),
        ["liver panel"] = new("Liver Function Tests", "LAB-LFT", "24325-3", "Blood"),

        ["hba1c"] = new("Hemoglobin A1c", "LAB-HBA1C", "4548-4", "Blood"),
        ["glycated hemoglobin"] = new("Hemoglobin A1c", "LAB-HBA1C", "4548-4", "Blood"),
        ["hemoglobin a1c"] = new("Hemoglobin A1c", "LAB-HBA1C", "4548-4", "Blood"),
        ["a1c"] = new("Hemoglobin A1c", "LAB-HBA1C", "4548-4", "Blood"),

        ["thyroid stimulating hormone"] = new("Thyroid Stimulating Hormone", "LAB-TSH", "3016-3", "Blood"),
        ["serum tsh"] = new("Thyroid Stimulating Hormone", "LAB-TSH", "3016-3", "Blood"),
        ["tsh"] = new("Thyroid Stimulating Hormone", "LAB-TSH", "3016-3", "Blood"),
        ["free t4"] = new("Free T4", "LAB-FT4", "3024-7", "Blood"),
        ["serum free t4"] = new("Free T4", "LAB-FT4", "3024-7", "Blood"),

        ["c-reactive protein (crp)"] = new("C-Reactive Protein", "LAB-CRP", "1988-5", "Blood"),
        ["c-reactive protein"] = new("C-Reactive Protein", "LAB-CRP", "1988-5", "Blood"),
        ["crp"] = new("C-Reactive Protein", "LAB-CRP", "1988-5", "Blood"),

        ["erythrocyte sedimentation rate"] = new("Erythrocyte Sedimentation Rate", "LAB-ESR", "4537-7", "Blood"),
        ["esr"] = new("Erythrocyte Sedimentation Rate", "LAB-ESR", "4537-7", "Blood"),

        ["12-lead ecg"] = new("Electrocardiogram", "IMG-ECG", "11524-6", null),
        ["12 lead ecg"] = new("Electrocardiogram", "IMG-ECG", "11524-6", null),
        ["electrocardiogram"] = new("Electrocardiogram", "IMG-ECG", "11524-6", null),
        ["ecg"] = new("Electrocardiogram", "IMG-ECG", "11524-6", null),
        ["ekg"] = new("Electrocardiogram", "IMG-ECG", "11524-6", null),

        ["chest x-ray (pa view)"] = new("Chest X-Ray PA", "IMG-CXR-PA", "24648-8", null),
        ["follow-up chest x-ray (pa view)"] = new("Chest X-Ray PA", "IMG-CXR-PA", "24648-8", null),
        ["chest x-ray pa view"] = new("Chest X-Ray PA", "IMG-CXR-PA", "24648-8", null),
        ["chest x-ray"] = new("Chest X-Ray", "IMG-CXR", "36643-5", null),
        ["chest radiograph"] = new("Chest X-Ray", "IMG-CXR", "36643-5", null),
        ["x-ray chest"] = new("Chest X-Ray", "IMG-CXR", "36643-5", null),
        ["cxr"] = new("Chest X-Ray", "IMG-CXR", "36643-5", null),
        ["x-ray"] = new("Radiologic Examination", "IMG-XR", "43468-8", null),

        ["2d echocardiogram"] = new("2D Echocardiogram", "IMG-ECHO-2D", "34552-0", null),
        ["2d echo"] = new("2D Echocardiogram", "IMG-ECHO-2D", "34552-0", null),
        ["echocardiogram"] = new("2D Echocardiogram", "IMG-ECHO-2D", "34552-0", null),
        ["echo"] = new("2D Echocardiogram", "IMG-ECHO-2D", "34552-0", null),

        ["ultrasound abdomen"] = new("Ultrasound Abdomen", "IMG-USG-ABD", "24558-9", null),
        ["usg abdomen"] = new("Ultrasound Abdomen", "IMG-USG-ABD", "24558-9", null),
        ["ultrasound renal"] = new("Ultrasound Renal", "IMG-USG-RENAL", "24604-1", null),
        ["usg renal"] = new("Ultrasound Renal", "IMG-USG-RENAL", "24604-1", null),
        ["ultrasound"] = new("Ultrasound", "IMG-USG", "25061-3", null),
        ["usg"] = new("Ultrasound", "IMG-USG", "25061-3", null),

        ["ct scan"] = new("CT Scan", "IMG-CT", "25045-6", null),
        ["ncct"] = new("CT Scan", "IMG-CT", "25045-6", null),
        ["cect"] = new("CT Scan", "IMG-CT", "25045-6", null),
        ["ct"] = new("CT Scan", "IMG-CT", "25045-6", null),

        ["mri scan"] = new("MRI Study", "IMG-MRI", "25056-3", null),
        ["mri"] = new("MRI Study", "IMG-MRI", "25056-3", null),

        ["troponin-i"] = new("Troponin", "LAB-TROP", "6598-7", "Blood"),
        ["troponin t"] = new("Troponin", "LAB-TROP", "6598-7", "Blood"),
        ["trop-i"] = new("Troponin", "LAB-TROP", "6598-7", "Blood"),
        ["troponin"] = new("Troponin", "LAB-TROP", "6598-7", "Blood"),

        ["d-dimer"] = new("Fibrin D-dimer", "LAB-DDIMER", "48065-7", "Blood"),
        ["d dimer"] = new("Fibrin D-dimer", "LAB-DDIMER", "48065-7", "Blood"),

        ["serum electrolytes"] = new("Serum Electrolytes", "LAB-ELECT", "24326-1", "Blood"),
        ["electrolytes"] = new("Serum Electrolytes", "LAB-ELECT", "24326-1", "Blood"),

        ["serum uric acid"] = new("Serum Uric Acid", "LAB-URIC", "3084-1", "Blood"),
        ["uric acid"] = new("Serum Uric Acid", "LAB-URIC", "3084-1", "Blood"),

        ["prothrombin time and inr (pt/inr)"] = new("International Normalized Ratio", "LAB-INR", "6301-6", "Blood"),
        ["prothrombin time and inr"] = new("International Normalized Ratio", "LAB-INR", "6301-6", "Blood"),
        ["pt/inr"] = new("International Normalized Ratio", "LAB-INR", "6301-6", "Blood"),
        ["inr"] = new("International Normalized Ratio", "LAB-INR", "6301-6", "Blood"),

        ["serum potassium"] = new("Serum Potassium", "LAB-K", "2823-3", "Blood"),
        ["plasma potassium"] = new("Serum Potassium", "LAB-K", "2823-3", "Blood"),
        ["serum k+"] = new("Serum Potassium", "LAB-K", "2823-3", "Blood"),
        ["potassium"] = new("Serum Potassium", "LAB-K", "2823-3", "Blood"),
        ["k+"] = new("Serum Potassium", "LAB-K", "2823-3", "Blood"),

        ["serum sodium"] = new("Serum Sodium", "LAB-NA", "2951-2", "Blood"),
        ["plasma sodium"] = new("Serum Sodium", "LAB-NA", "2951-2", "Blood"),
        ["serum na+"] = new("Serum Sodium", "LAB-NA", "2951-2", "Blood"),
        ["sodium"] = new("Serum Sodium", "LAB-NA", "2951-2", "Blood"),
        ["na+"] = new("Serum Sodium", "LAB-NA", "2951-2", "Blood"),

        ["serum psa"] = new("Serum PSA", "LAB-PSA", "2857-1", "Blood"),
        ["psa"] = new("Serum PSA", "LAB-PSA", "2857-1", "Blood"),

        ["quantiferon-tb gold"] = new("Quantiferon-TB Gold", "LAB-QFTB", "71447-7", "Blood"),
        ["hepatitis b surface antigen"] = new("Hepatitis B Surface Antigen", "LAB-HBSAG", "5196-1", "Blood"),
        ["hepatitis c antibody"] = new("Hepatitis C Antibody", "LAB-HCVAB", "5198-7", "Blood"),

        ["eeg"] = new("Electroencephalogram", "IMG-EEG", "11502-2", null),

        ["blood culture & sensitivity"] = new("Blood Culture", "LAB-BLD-CS", "600-7", "Blood"),
        ["blood culture"] = new("Blood Culture", "LAB-BLD-CS", "600-7", "Blood"),

        ["pulmonary function test"] = new("Pulmonary Function Test", "LAB-PFT", "81459-0", null),
        ["pft"] = new("Pulmonary Function Test", "LAB-PFT", "81459-0", null)
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DrugMapping> LearnedDrugs =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, LabMapping> LearnedLabs =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> KnownLabTerms =>
        LabMaster.Keys.Concat(LearnedLabs.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(k => k.Length).ToArray();

    public static IReadOnlyList<string> KnownDrugTerms =>
        DrugMaster.Keys.Concat(LearnedDrugs.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(k => k.Length).ToArray();

    public static int LearnedDrugCount => LearnedDrugs.Count;
    public static int LearnedLabCount => LearnedLabs.Count;

    public static void RegisterLearnedMedication(string mention, string standardName, string catalogCode, string rxNormCui)
    {
        string clean = CleanText(mention);
        if (!string.IsNullOrWhiteSpace(clean))
        {
            LearnedDrugs[clean] = new DrugMapping(standardName, catalogCode, rxNormCui);
        }
    }

    public static void RegisterLearnedInvestigation(string mention, string standardName, string catalogCode, string loinc, string? specimen = null)
    {
        string clean = CleanText(mention);
        if (!string.IsNullOrWhiteSpace(clean))
        {
            LearnedLabs[clean] = new LabMapping(standardName, catalogCode, loinc, specimen);
        }
    }

    public OntologyMatch ResolveMedication(string name, string? strength = null)
    {
        string clean = CleanText(name);
        if (!DrugMaster.TryGetValue(clean, out var match) && !LearnedDrugs.TryGetValue(clean, out match))
            return new("unmatched", null, null, null, 0, null, "unresolved", null, "RxNorm", TerminologyVerification.Version, "Unlisted medication mention; routed to OOV quarantine.");
        // Ingredient codes do not encode product strength or formulation. Catalog suffixes are
        // not a clinical strength standard. Brands and product-specific concepts need separate review.
        bool supported = TerminologyVerification.IngredientSupported(clean, match.StandardName, match.RxNormCui);
        var (semanticStatus, mappingNote) = TerminologyVerification.ClassifyMedicationSemantic(clean, match.StandardName, match.RxNormCui, strength);
        return new(supported ? "matched" : "uncertain", match.CatalogCode, match.StandardName,
            supported ? match.RxNormCui : null, supported ? 0.98 : 0.5, null,
            semanticStatus, match.RxNormCui, "RxNorm", TerminologyVerification.Version, mappingNote);
    }

    public OntologyMatch ResolveInvestigation(string name)
    {
        string clean = CleanText(name);
        if (!LabMaster.TryGetValue(clean, out var match) && !LearnedLabs.TryGetValue(clean, out match))
            return new("unmatched", null, null, null, 0, null, "unresolved", null, "LOINC", TerminologyVerification.LoincVersion, "Unlisted investigation mention; routed to OOV quarantine.");
        bool supported = TerminologyVerification.InvestigationSupported(clean, match.Loinc);
        var (semanticStatus, mappingNote) = TerminologyVerification.ClassifyInvestigationSemantic(clean, match.Loinc);
        return new(supported ? "matched" : "uncertain", match.CatalogCode, match.StandardName,
            supported ? match.Loinc : null, supported ? 0.98 : 0.5, match.Specimen,
            semanticStatus, match.Loinc, "LOINC", TerminologyVerification.LoincVersion, mappingNote);
    }

    public void QuarantineOov(string category, string mention, int start, int end, string fullNote, string? quarantineLogPath = null)
    {
        try
        {
            string projectDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
            string backendDir = Path.GetFileName(projectDir).Equals("tests", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(Path.Combine(projectDir, ".."))
                : projectDir;
            quarantineLogPath ??= Path.Combine(backendDir, "logs", "oov_quarantine.jsonl");
            string fullPath = Path.GetFullPath(quarantineLogPath);

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Extract context around the mention (100 chars window)
            int ctxStart = Math.Max(0, start - 50);
            int ctxEnd = Math.Min(fullNote.Length, end + 50);
            string rawContext = fullNote[ctxStart..ctxEnd];

            // Scrub PII before logging to disk
            string scrubbedContext = ZeroLeakageValidator.Scrub(rawContext);

            var entry = new OovQuarantineEntry(
                Timestamp: DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                Reason: "fail_closed_oov",
                Category: category,
                Mention: mention,
                Span: [start, end],
                Context: scrubbedContext,
                Normalized: mention.Trim().ToLowerInvariant()
            );

            string line = JsonSerializer.Serialize(entry);

            lock (FileLock)
            {
                File.AppendAllText(fullPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // Fail safe on logging errors, never crash extraction pipeline
        }
    }

    private static string CleanText(string text) =>
        text.Trim().ToLowerInvariant();
}
