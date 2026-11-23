using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Apollo.Backend.Nlp;

public record RawSpan(
    string Category,
    int StartChar,
    int EndChar,
    string Text,
    double Confidence,
    string? Origin = null
);

public sealed class OnnxExecutionReceipt
{
    public string ExecutionMode { get; internal set; } = "NOT_EXECUTED";
    public List<Apollo.Backend.Models.SourceReviewCandidateDto> ReviewCandidates { get; } = [];
    public int WindowsExecuted { get; internal set; }
    public double InferenceDurationMs { get; internal set; }
    public int ModelMedicationMentions { get; internal set; }
    public int RuleMedicationMentions { get; internal set; }
    public int RuleInvestigationMentions { get; internal set; }
}

public class SlidingWindowOnnxEngine : IDisposable
{
    private readonly InferenceSession? _session;
    private readonly WordPieceTokenizer? _tokenizer;
    private readonly double _temperature;
    private readonly bool _hasModel;

    public const int MaxWindowLength = 512;
    public const int WindowStride = 448;
    public const int Overlap = 64; // 512 - 448 = 64
    public const double FailClosedConfidenceThreshold = 0.85;

    public static readonly string[] CanonicalLabels = [
        "O",
        "B-CONDITION", "I-CONDITION",
        "B-MEDICATION", "I-MEDICATION",
        "B-DOSAGE", "I-DOSAGE",
        "B-ANATOMICAL_SITE", "I-ANATOMICAL_SITE"
    ];

    private static readonly FrozenSet<string> SpecialTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "[CLS]", "[SEP]", "[PAD]", "[MASK]"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> FalsePositiveStoplist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Mandatory stopword & non-drug blocklist
        "of", "for", "to", "in", "on", "at", "by", "with", "from", "as",
        "is", "was", "were", "are", "be", "been", "and", "or", "but", "not", "no",
        "the", "a", "an", "this", "that", "it", "he", "she", "they", "we", "you",
        "mg", "ml", "mcg", "tab", "cap", "inj", "syp", "po", "iv", "im",
        "od", "bd", "bid", "tid", "qid", "qd", "hs", "prn", "stat",
        "x", "rx", "dx", "hx",
        // Extended clinical & posology stopwords
        "these", "those", "its", "his", "her", "their", "per", "into", "onto", "via",
        "about", "after", "before", "during", "under", "over", "between", "through",
        "out", "up", "down", "off", "then", "than", "more", "today", "tomorrow", "yesterday",
        "tabs", "tablet", "tablets", "caps", "capsule", "capsules", "syrup", "injection",
        "µg", "ug", "g", "gm", "gms", "mEq", "mmol", "units", "unit", "iu", "puffs", "drops", "dose", "dosage",
        "sc", "sq", "sl", "oral", "orally", "topical", "topically", "tds", "qds", "qhs", "sos",
        "daily", "once", "twice", "thrice", "every", "day", "days", "week", "weeks", "month", "months", "year", "years", "nightly",
        "patient", "pt", "history", "pmh", "fhx", "hpi", "status", "post", "s/p",
        "denies", "denied", "denying", "without", "negative", "negative for", "none",
        "rule", "rule out", "ruled", "ruled out", "absence", "absence of", "free of", "refuses",
        "stopped", "discontinued", "discontinue", "held", "hold", "start", "add", "continue", "continues",
        "increase", "decrease", "order", "ordered", "prescribe", "prescribed", "advised",
        "father", "mother", "brother", "sister", "admitted", "discharged", "admission", "discharge",
        "follow", "follow-up", "review", "evaluation", "clinic", "hospital", "doctor", "dr", "dr.",
        "consolidation", "meals", "meal", "bedtime", "morning", "evening", "night", "routine",
        // Investigation acronyms that must never be classified as medications
        "cue", "rft", "kft", "u&e", "cbc", "lft", "crp", "esr", "tsh", "hba1c", "ecg", "ekg", "cxr", "usg", "ct", "mri", "echo"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsValidMedicationSpan(string? spanText)
    {
        if (string.IsNullOrWhiteSpace(spanText))
            return false;

        string trimmed = spanText.Trim();
        int alphaCount = 0;
        foreach (char c in trimmed)
        {
            if (char.IsLetter(c))
                alphaCount++;
        }

        if (alphaCount < 2)
            return false;

        if (FalsePositiveStoplist.Contains(trimmed))
            return false;

        if (Regex.IsMatch(trimmed, @"^[\d\s.,;:\-_/\\#*•()]+$"))
            return false;

        return true;
    }

    public SlidingWindowOnnxEngine(string? modelPath = null, WordPieceTokenizer? tokenizer = null, double temperature = 0.5540, bool allowDeterministicTestScoring = false)
    {
        _tokenizer = tokenizer;
        _temperature = temperature > 0 ? temperature : 0.5540;

        if (!allowDeterministicTestScoring || modelPath != null)
            OnnxArtifactIntegrity.Verify(modelPath, tokenizer);
        if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
        {
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Environment.ProcessorCount
            };
            byte[] modelBytes = File.ReadAllBytes(modelPath);
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(modelBytes)) != OnnxArtifactIntegrity.ModelSha256)
                throw new OnnxIntegrityException("ONNX_MODEL_HASH_MISMATCH", "Local model integrity changed while loading.");
            _session = new InferenceSession(modelBytes, options);
            if (_session.InputMetadata.Count != 2 ||
                !_session.InputMetadata.TryGetValue("input_ids", out var ids) || ids.ElementType != typeof(long) || ids.Dimensions.Length != 2 ||
                !_session.InputMetadata.TryGetValue("attention_mask", out var mask) || mask.ElementType != typeof(long) || mask.Dimensions.Length != 2 ||
                _session.OutputMetadata.Count != 1 || _session.OutputMetadata.First().Value.ElementType != typeof(float) ||
                _session.OutputMetadata.First().Value.Dimensions.Length != 3 || _session.OutputMetadata.First().Value.Dimensions[2] != CanonicalLabels.Length)
            {
                _session.Dispose();
                throw new OnnxIntegrityException("ONNX_GRAPH_INCOMPATIBLE", "Local model graph does not implement the expected token-label contract.");
            }
            _hasModel = true;
        }
        else
        {
            _session = null;
            _hasModel = false;
        }
    }

    public bool HasOnnxModel => _hasModel;

    public List<RawSpan> InferSpans(string text, OnnxExecutionReceipt? receipt = null)
    {
        var tokenized = _tokenizer != null
            ? _tokenizer.Tokenize(text)
            : new TokenizedOutput([], [], [], []);
        return Predict(text, tokenized, receipt);
    }

    public List<RawSpan> Predict(string text, TokenizedOutput tokenized, OnnxExecutionReceipt? receipt = null)
    {
        if (receipt != null) receipt.ExecutionMode = _hasModel ? "NEURAL_ONNX" : "DETERMINISTIC_TEST_SCORING";
        if (tokenized.InputIds.Count != tokenized.AttentionMask.Count || tokenized.InputIds.Count != tokenized.Offsets.Count ||
            tokenized.InputIds.Count != tokenized.Tokens.Count || tokenized.InputIds.Any(id => id < 0 || id >= (_tokenizer?.VocabSize ?? 0)))
            throw new OnnxIntegrityException("ONNX_INPUT_INCOMPATIBLE", "Tokenizer output does not match the model input contract.");
        int numTokens = tokenized.InputIds.Count;
        if (numTokens <= 2)
        {
            return [];
        }

        float[][] aggregatedLogits;

        if (_hasModel && _session != null)
        {
            aggregatedLogits = PredictWithOnnxSlidingWindow(tokenized, receipt);
        }
        else
        {
            // Explicit test-only scoring mode; unavailable to the production registration.
            aggregatedLogits = DeterministicFallbackScoring(text, tokenized);
        }

        // Force special tokens ([CLS], [SEP], [PAD]) to O
        for (int j = 0; j < numTokens && j < aggregatedLogits.Length; j++)
        {
            bool isSpec = j < tokenized.Tokens.Count && SpecialTokens.Contains(tokenized.Tokens[j]);
            if (!isSpec && j < tokenized.Offsets.Count && tokenized.Offsets[j].Start == 0 && tokenized.Offsets[j].End == 0)
            {
                isSpec = true;
            }
            if (isSpec)
            {
                aggregatedLogits[j][0] = 100.0f;
                for (int l = 1; l < aggregatedLogits[j].Length; l++)
                {
                    aggregatedLogits[j][l] = -10000.0f;
                }
            }
        }

        // Apply temperature scaling: logits / T, then softmax
        float[][] probabilities = ApplySoftmaxWithTemperature(aggregatedLogits, _temperature);

        // Decode BIO sequence into exact word-consistent entity spans
        return DecodeSpans(text, tokenized, probabilities);
    }

    private float[][] PredictWithOnnxSlidingWindow(TokenizedOutput tokenized, OnnxExecutionReceipt? receipt)
    {
        int totalTokens = tokenized.InputIds.Count;
        int numLabels = CanonicalLabels.Length;

        var blendedLogits = new float[totalTokens][];
        var weights = new float[totalTokens];

        for (int i = 0; i < totalTokens; i++)
        {
            blendedLogits[i] = new float[numLabels];
        }

        int windowStart = 0;
        while (windowStart < totalTokens)
        {
            int windowEnd = Math.Min(windowStart + MaxWindowLength, totalTokens);
            int windowLen = windowEnd - windowStart;

            var windowIds = tokenized.InputIds.GetRange(windowStart, windowLen).ToArray();
            var windowMask = tokenized.AttentionMask.GetRange(windowStart, windowLen).ToArray();

            var inputIdsTensor = new DenseTensor<long>(windowIds, [1, windowLen]);
            var maskTensor = new DenseTensor<long>(windowMask, [1, windowLen]);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
                NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
            };

            long inferenceStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using var results = _session!.Run(inputs);
            if (receipt != null)
            {
                receipt.WindowsExecuted++;
                receipt.InferenceDurationMs += System.Diagnostics.Stopwatch.GetElapsedTime(inferenceStart).TotalMilliseconds;
            }
            var logitsTensor = results.First().AsTensor<float>();
            if (logitsTensor.Dimensions.Length != 3 || logitsTensor.Dimensions[0] != 1 || logitsTensor.Dimensions[1] != windowLen || logitsTensor.Dimensions[2] != numLabels)
                throw new OnnxIntegrityException("ONNX_OUTPUT_INCOMPATIBLE", "Neural inference returned an incompatible token-label tensor.");

            // Linear boundary weighting for partition of unity
            for (int j = 0; j < windowLen; j++)
            {
                int globalIdx = windowStart + j;
                float w = 1.0f;

                // Taper edges within overlap region
                if (windowStart > 0 && j < Overlap)
                {
                    w = (float)j / Overlap;
                }
                else if (windowEnd < totalTokens && j >= windowLen - Overlap)
                {
                    w = (float)(windowLen - 1 - j) / Overlap;
                }

                weights[globalIdx] += w;
                for (int l = 0; l < numLabels; l++)
                {
                    blendedLogits[globalIdx][l] += logitsTensor[0, j, l] * w;
                }
            }

            if (windowEnd == totalTokens)
            {
                break;
            }
            windowStart += WindowStride;
        }

        // Normalize by accumulated weights
        for (int i = 0; i < totalTokens; i++)
        {
            float norm = weights[i] > 0 ? weights[i] : 1.0f;
            for (int l = 0; l < numLabels; l++)
            {
                blendedLogits[i][l] /= norm;
            }
        }

        return blendedLogits;
    }

    private static float[][] DeterministicFallbackScoring(string text, TokenizedOutput tokenized)
    {
        int totalTokens = tokenized.InputIds.Count;
        int numLabels = CanonicalLabels.Length;
        var logits = new float[totalTokens][];

        for (int i = 0; i < totalTokens; i++)
        {
            logits[i] = new float[numLabels];
            logits[i][0] = 5.0f; // Default high O logit
        }

        string[] conditionKeywords = ["diabetes", "hypertension", "pneumonia", "cancer", "stemi", "infarction", "dyspnea", "pain", "uti", "infection", "headache", "fever", "gout", "hyperkalemia", "dyslipidemia", "bronchospasm"];
        string[] medKeywords = ["atorva", "atorvastatin", "metformin", "amoxicillin", "clavulanate", "augmentin", "aspirin", "clopidogrel", "thyronorm", "levothyroxine", "nitrofurantoin", "niftas", "telma", "telmisartan", "paracetamol", "dolo", "crocin", "azithromycin", "amlodipine", "lisinopril", "warfarin", "allopurinol", "omeprazole", "pantoprazole", "ibuprofen", "ciprofloxacin", "losartan", "salbutamol", "prednisolone", "tamsulosin", "ondansetron", "cisplatin", "pemetrexed"];
        string[] doseKeywords = ["mg", "mcg", "g", "tab", "cap", "po", "bid", "od", "tid", "daily"];

        for (int i = 1; i < totalTokens - 1; i++)
        {
            string tok = tokenized.Tokens[i].TrimStart('#').ToLowerInvariant();

            if (tok.Length < 3 || FalsePositiveStoplist.Contains(tok))
            {
                continue;
            }

            if (conditionKeywords.Any(k => k.Equals(tok, StringComparison.OrdinalIgnoreCase) || (tok.Length >= 4 && k.StartsWith(tok, StringComparison.OrdinalIgnoreCase))))
            {
                logits[i][0] = -2.0f;
                logits[i][1] = 6.0f; // B-CONDITION
                logits[i][2] = 4.0f; // I-CONDITION
            }
            else if (medKeywords.Any(k => k.Equals(tok, StringComparison.OrdinalIgnoreCase) || (tok.Length >= 4 && k.StartsWith(tok, StringComparison.OrdinalIgnoreCase))))
            {
                logits[i][0] = -2.0f;
                logits[i][3] = 6.0f; // B-MEDICATION
                logits[i][4] = 4.0f; // I-MEDICATION
            }
            else if (doseKeywords.Any(k => k.Equals(tok, StringComparison.OrdinalIgnoreCase)))
            {
                logits[i][0] = -2.0f;
                logits[i][5] = 6.0f; // B-DOSAGE
                logits[i][6] = 4.0f; // I-DOSAGE
            }
        }

        return logits;
    }

    private static float[][] ApplySoftmaxWithTemperature(float[][] logits, double temperature)
    {
        int n = logits.Length;
        var probs = new float[n][];

        for (int i = 0; i < n; i++)
        {
            float[] row = logits[i];
            int m = row.Length;
            probs[i] = new float[m];

            float maxLogit = float.MinValue;
            for (int j = 0; j < m; j++)
            {
                float scaled = (float)(row[j] / temperature);
                if (scaled > maxLogit) maxLogit = scaled;
            }

            float sumExp = 0.0f;
            for (int j = 0; j < m; j++)
            {
                float exp = MathF.Exp((float)(row[j] / temperature) - maxLogit);
                probs[i][j] = exp;
                sumExp += exp;
            }

            for (int j = 0; j < m; j++)
            {
                probs[i][j] /= (sumExp > 0 ? sumExp : 1.0f);
            }
        }

        return probs;
    }

    private static bool IsSpecialToken(string token, (int Start, int End) offset)
    {
        return SpecialTokens.Contains(token) || offset.Start == offset.End;
    }

    private static List<List<int>> GroupWords(string text, IReadOnlyList<string> tokens, IReadOnlyList<(int Start, int End)> offsets)
    {
        var words = new List<List<int>>();
        int count = Math.Min(tokens.Count, offsets.Count);

        for (int j = 0; j < count; j++)
        {
            string tok = tokens[j];
            var off = offsets[j];
            if (IsSpecialToken(tok, off))
                continue;

            bool joined = false;
            if (words.Count > 0 && words[^1][^1] == j - 1)
            {
                var (ps, pe) = offsets[j - 1];
                joined = tok.StartsWith("##", StringComparison.Ordinal) ||
                         (pe == off.Start && off.Start > 0 && off.Start < text.Length &&
                          char.IsLetterOrDigit(text[off.Start - 1]) && char.IsLetterOrDigit(text[off.Start]));
            }

            if (joined)
            {
                words[^1].Add(j);
            }
            else
            {
                words.Add([j]);
            }
        }

        return words;
    }

    private static string? ExtractCategory(string tag)
    {
        return (tag.StartsWith("B-", StringComparison.Ordinal) || tag.StartsWith("I-", StringComparison.Ordinal))
            ? tag[2..]
            : null;
    }

    private static string[] RepairBio(
        IReadOnlyList<string> rawTags,
        IReadOnlyList<string> tokens,
        IReadOnlyList<(int Start, int End)> offsets,
        string text)
    {
        var repaired = new string[rawTags.Count];
        Array.Fill(repaired, "O");

        string? prevCat = null;
        int prevLast = -2;

        foreach (var word in GroupWords(text, tokens, offsets))
        {
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (int j in word)
            {
                string? cat = ExtractCategory(rawTags[j]);
                if (cat != null)
                {
                    votes[cat] = votes.GetValueOrDefault(cat, 0) + 1;
                }
            }

            if (votes.Count == 0)
            {
                prevCat = null;
                continue;
            }

            int best = votes.Values.Max();
            string? headCat = ExtractCategory(rawTags[word[0]]);
            string wordCat = (headCat != null && votes.TryGetValue(headCat, out int hc) && hc == best)
                ? headCat
                : votes.First(kv => kv.Value == best).Key;

            bool adjacent = word[0] == prevLast + 1;
            bool startsNew = !(adjacent && prevCat == wordCat) ||
                             (rawTags[word[0]].StartsWith("B-", StringComparison.Ordinal) && headCat == wordCat);

            repaired[word[0]] = $"{(startsNew ? "B" : "I")}-{wordCat}";
            for (int k = 1; k < word.Count; k++)
            {
                repaired[word[k]] = $"I-{wordCat}";
            }

            prevCat = wordCat;
            prevLast = word[^1];
        }

        return repaired;
    }

    private static List<RawSpan> DecodeSpans(string text, TokenizedOutput tokenized, float[][] probabilities)
    {
        int totalTokens = Math.Min(tokenized.Tokens.Count, Math.Min(tokenized.Offsets.Count, probabilities.Length));
        var rawTags = new string[totalTokens];

        for (int i = 0; i < totalTokens; i++)
        {
            int bestLabel = 0;
            float maxP = probabilities[i][0];
            for (int l = 1; l < CanonicalLabels.Length; l++)
            {
                if (probabilities[i][l] > maxP)
                {
                    maxP = probabilities[i][l];
                    bestLabel = l;
                }
            }
            rawTags[i] = CanonicalLabels[bestLabel];
        }

        string[] fixedTags = RepairBio(rawTags, tokenized.Tokens, tokenized.Offsets, text);

        var labelIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int l = 0; l < CanonicalLabels.Length; l++)
        {
            labelIndex[CanonicalLabels[l]] = l;
        }

        double ComputeTokenConfidence(int j)
        {
            string tag = fixedTags[j];
            if (tag == rawTags[j])
            {
                return labelIndex.TryGetValue(tag, out int idx) ? probabilities[j][idx] : 0.0;
            }

            string cat = tag[2..];
            double sum = 0.0;
            if (labelIndex.TryGetValue($"B-{cat}", out int bIdx)) sum += probabilities[j][bIdx];
            if (labelIndex.TryGetValue($"I-{cat}", out int iIdx)) sum += probabilities[j][iIdx];
            return sum;
        }

        var spans = new List<RawSpan>();
        var current = new List<int>();

        void Flush()
        {
            if (current.Count == 0)
                return;

            int lo = 0;
            int hi = current.Count;

            while (lo < hi)
            {
                var (s, e) = tokenized.Offsets[current[lo]];
                if (s >= 0 && e <= text.Length && e > s && text[s..e].Any(char.IsLetterOrDigit))
                    break;
                lo++;
            }

            while (hi > lo)
            {
                var (s, e) = tokenized.Offsets[current[hi - 1]];
                if (s >= 0 && e <= text.Length && e > s && text[s..e].Any(char.IsLetterOrDigit))
                    break;
                hi--;
            }

            if (lo < hi)
            {
                var kept = current.GetRange(lo, hi - lo);
                int startChar = tokenized.Offsets[kept[0]].Start;
                int endChar = tokenized.Offsets[kept[^1]].End;

                if (startChar >= 0 && endChar <= text.Length && endChar > startChar)
                {
                    string category = fixedTags[kept[0]][2..];
                    string rawText = text[startChar..endChar].Trim();
                    double conf = kept.Average(ComputeTokenConfidence);

                    bool valid = category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase)
                        ? IsValidMedicationSpan(rawText)
                        : (rawText.Length > 1 && !FalsePositiveStoplist.Contains(rawText) && !Regex.IsMatch(rawText, @"^[\d\s.,;:\-_/\\#*•()]+$"));

                    if (valid)
                    {
                        spans.Add(new RawSpan(category, startChar, endChar, rawText, Math.Round(Math.Clamp(conf, 0.0, 1.0), 4)));
                    }
                }
            }

            current.Clear();
        }

        for (int j = 0; j < fixedTags.Length; j++)
        {
            string tag = fixedTags[j];
            if (tag == "O")
            {
                Flush();
            }
            else if (tag.StartsWith("B-", StringComparison.Ordinal))
            {
                Flush();
                current.Add(j);
            }
            else
            {
                current.Add(j);
            }
        }

        Flush();

        // Merge hyphenated medication spans (e.g. "amoxicillin" + "-" + "clavulanate")
        for (int i = spans.Count - 2; i >= 0; i--)
        {
            var first = spans[i];
            var second = spans[i + 1];
            if (first.Category == second.Category &&
                first.Category.Equals("MEDICATION", StringComparison.OrdinalIgnoreCase) &&
                second.StartChar - first.EndChar == 1 &&
                text[first.EndChar] == '-')
            {
                int mergedStart = first.StartChar;
                int mergedEnd = second.EndChar;
                double mergedConf = Math.Round((first.Confidence + second.Confidence) / 2.0, 4);
                spans[i] = new RawSpan(first.Category, mergedStart, mergedEnd, text[mergedStart..mergedEnd], mergedConf);
                spans.RemoveAt(i + 1);
            }
        }

        return spans;
    }

    public void Dispose()
    {
        _session?.Dispose();
        GC.SuppressFinalize(this);
    }
}
