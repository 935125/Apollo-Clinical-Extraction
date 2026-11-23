using System.Text.RegularExpressions;

namespace Apollo.Backend.Nlp;

public partial class ClinicalEmbeddingGenerator
{
    public const int EmbeddingDimension = 64;

    private static readonly (int Dimension, string[] Concepts)[] DomainClusters =
    [
        (0, ["pneumonia", "respiratory", "cough", "dyspnea", "shortness", "breath", "consolidation", "cxr", "chest", "sputum", "bronchospasm", "salbutamol", "inhaler"]),
        (1, ["amoxicillin", "clavulanate", "augmentin", "azithromycin", "ciprofloxacin", "nitrofurantoin", "niftas", "antibiotic", "infection", "uti", "culture"]),
        (2, ["diabetes", "diabetic", "glycemia", "glycemic", "glucose", "sugar", "hba1c", "a1c", "metformin", "glycomet", "insulin", "endocrine"]),
        (3, ["hypertension", "blood pressure", "bp", "systolic", "diastolic", "amlodipine", "lisinopril", "telmisartan", "telma", "losartan", "cardiology"]),
        (4, ["dyslipidemia", "hyperlipidemia", "cholesterol", "lipid", "triglycerides", "atorvastatin", "atorva", "lipitor", "statin"]),
        (5, ["anticoagulation", "warfarin", "aspirin", "ecosprin", "clopidogrel", "prothrombin", "inr", "bleeding", "coagulopathy", "thrombosis"]),
        (6, ["renal", "kidney", "creatinine", "rft", "kft", "electrolytes", "potassium", "hyperkalemia", "sodium", "u&e", "urea", "nephrology"]),
        (7, ["urinary", "urine", "cue", "urinalysis", "dysuria", "frequency", "cystitis", "pyelonephritis"]),
        (8, ["thyroid", "hypothyroidism", "hyperthyroidism", "tsh", "levothyroxine", "thyronorm", "eltroxin"]),
        (9, ["fever", "febrile", "pyrexia", "pain", "headache", "paracetamol", "dolo", "crocin", "acetaminophen", "ibuprofen", "analgesic"]),
        (10, ["gout", "uric", "allopurinol", "joint", "arthritis", "rheumatology"]),
        (11, ["cardiac", "heart", "ecg", "ekg", "echocardiogram", "echo", "troponin", "stemi", "infarction", "angina", "chest pain"]),
        (12, ["gastrointestinal", "gerd", "acidity", "omeprazole", "pantoprazole", "pan", "pantocid", "liver", "lft", "hepatic", "abdomen", "usg"]),
        (13, ["hematology", "cbc", "hemogram", "hemoglobin", "wbc", "platelets", "esr", "crp", "d-dimer", "anemia"]),
        (14, ["oncology", "cancer", "tumor", "chemotherapy", "cisplatin", "pemetrexed", "ondansetron", "malignancy"]),
        (15, ["prostate", "bph", "tamsulosin", "finasteride", "combodart", "urology"])
    ];

    public static float[] GenerateEmbedding(string? text)
    {
        var vec = new float[EmbeddingDimension];
        if (string.IsNullOrWhiteSpace(text))
            return vec;

        string normalized = text.ToLowerInvariant();
        var wordMatches = TokenRegex().Matches(normalized);
        var words = new List<string>(wordMatches.Count);
        foreach (Match m in wordMatches)
        {
            if (m.Value.Length >= 2)
                words.Add(m.Value);
        }

        // 1. Clinical semantic cluster activations (Dimensions 0..15)
        foreach (var (dim, concepts) in DomainClusters)
        {
            float score = 0f;
            foreach (var concept in concepts)
            {
                if (normalized.Contains(concept, StringComparison.Ordinal))
                {
                    score += 1.5f;
                }
            }
            vec[dim] = score;
        }

        // 2. Signed feature hashing over word unigrams, bigrams, and character 3-grams (Dimensions 16..63)
        const int hashBuckets = EmbeddingDimension - 16;
        for (int i = 0; i < words.Count; i++)
        {
            string w = words[i];
            AccumulateHash(vec, w, 1.0f, hashBuckets);

            if (i + 1 < words.Count)
            {
                AccumulateHash(vec, $"{w}_{words[i + 1]}", 1.25f, hashBuckets);
            }

            if (w.Length >= 4)
            {
                for (int c = 0; c <= w.Length - 3; c++)
                {
                    AccumulateHash(vec, w.Substring(c, 3), 0.35f, hashBuckets);
                }
            }
        }

        // 3. L2 Normalization for exact cosine similarity via dot product
        float norm = MathF.Sqrt(DotProduct(vec, vec));
        if (norm > 1e-6f)
        {
            for (int i = 0; i < EmbeddingDimension; i++)
            {
                vec[i] = MathF.Round(vec[i] / norm, 6);
            }
        }

        return vec;
    }

    public static double ComputeCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0.0;

        float dot = DotProduct(a, b);
        float normA = MathF.Sqrt(DotProduct(a, a));
        float normB = MathF.Sqrt(DotProduct(b, b));

        if (normA <= 1e-6f || normB <= 1e-6f)
            return 0.0;

        return Math.Round(Math.Clamp(dot / (normA * normB), -1.0f, 1.0f), 4);
    }

    private static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float sum = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    public static string FormatPgVectorLiteral(ReadOnlySpan<float> embedding)
    {
        return "[" + string.Join(",", embedding.ToArray().Select(v => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }

    private static void AccumulateHash(float[] vec, string feature, float weight, int buckets)
    {
        uint h = Fnv1aHash(feature);
        int idx = 16 + (int)(h % (uint)buckets);
        float sign = ((h >> 31) & 1) == 0 ? 1.0f : -1.0f;
        vec[idx] += sign * weight;
    }

    private static uint Fnv1aHash(string s)
    {
        uint hash = 2166136261u;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    [GeneratedRegex(@"[a-z0-9&/-]+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();
}
