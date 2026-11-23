using System.Collections.Frozen;
using System.Text;
using System.Text.Json;

namespace Apollo.Backend.Nlp;

public record TokenizedOutput(
    List<string> Tokens,
    List<long> InputIds,
    List<long> AttentionMask,
    List<(int Start, int End)> Offsets
);

public class WordPieceTokenizer
{
    private readonly FrozenDictionary<string, int> _vocab;
    private readonly FrozenDictionary<int, string> _idToToken;
    private readonly string _unkToken = "[UNK]";
    private readonly int _unkId = 1;
    private readonly int _clsId = 2;
    private readonly int _sepId = 3;
    private readonly int _padId = 0;

    public WordPieceTokenizer(string? tokenizerJsonPath = null)
    {
        if (!string.IsNullOrWhiteSpace(tokenizerJsonPath) && !File.Exists(tokenizerJsonPath))
            throw new OnnxIntegrityException("ONNX_ARTIFACT_UNAVAILABLE", "Required tokenizer file is unavailable.");
        var vocabDict = new Dictionary<string, int>(StringComparer.Ordinal);
        var idDict = new Dictionary<int, string>();

        if (!string.IsNullOrWhiteSpace(tokenizerJsonPath) && File.Exists(tokenizerJsonPath))
        {
            byte[] bytes = File.ReadAllBytes(tokenizerJsonPath);
            SourceSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            using var doc = ParseArtifact(bytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var modelEl) && modelEl.TryGetProperty("vocab", out var vocabEl))
            {
                foreach (var prop in vocabEl.EnumerateObject())
                {
                    int id = prop.Value.GetInt32();
                    vocabDict[prop.Name] = id;
                    idDict[id] = prop.Name;
                }
            }
        }

        LoadedFromArtifact = vocabDict.Count > 5;
        if (vocabDict.Count == 0)
        {
            // Fallback vocabulary
            string[] baseTokens = ["[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]"];
            for (int i = 0; i < baseTokens.Length; i++)
            {
                vocabDict[baseTokens[i]] = i;
                idDict[i] = baseTokens[i];
            }
        }

        _vocab = vocabDict.ToFrozenDictionary(StringComparer.Ordinal);
        _idToToken = idDict.ToFrozenDictionary();

        if (_vocab.TryGetValue("[UNK]", out int uId)) _unkId = uId;
        if (_vocab.TryGetValue("[CLS]", out int cId)) _clsId = cId;
        if (_vocab.TryGetValue("[SEP]", out int sId)) _sepId = sId;
        if (_vocab.TryGetValue("[PAD]", out int pId)) _padId = pId;
    }

    private static JsonDocument ParseArtifact(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new OnnxIntegrityException("ONNX_TOKENIZER_INVALID", "Local tokenizer is not a valid artifact."); }
    }

    public WordPieceTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab.ToFrozenDictionary(StringComparer.Ordinal);
        _idToToken = vocab.ToDictionary(kv => kv.Value, kv => kv.Key).ToFrozenDictionary();
        if (_vocab.TryGetValue("[UNK]", out int uId)) _unkId = uId;
        if (_vocab.TryGetValue("[CLS]", out int cId)) _clsId = cId;
        if (_vocab.TryGetValue("[SEP]", out int sId)) _sepId = sId;
        if (_vocab.TryGetValue("[PAD]", out int pId)) _padId = pId;
    }

    public string? SourceSha256 { get; }
    public bool HasBaselineSpecialTokens => _padId == 0 && _unkId == 1 && _clsId == 2 && _sepId == 3 && _vocab.GetValueOrDefault("[MASK]", -1) == 4;
    public bool LoadedFromArtifact { get; private set; }
    public int VocabSize => _vocab.Count;

    public TokenizedOutput Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TokenizedOutput(
                Tokens: ["[CLS]", "[SEP]"],
                InputIds: [_clsId, _sepId],
                AttentionMask: [1, 1],
                Offsets: [(0, 0), (0, 0)]
            );
        }

        var tokens = new List<string> { "[CLS]" };
        var inputIds = new List<long> { _clsId };
        var offsets = new List<(int Start, int End)> { (0, 0) };

        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (char.IsPunctuation(text[i]) || char.IsSymbol(text[i]))
            {
                int start = i;
                i++;
                int end = i;
                string punct = text[start..end];
                string punctLower = punct.ToLowerInvariant();

                if (_vocab.TryGetValue(punctLower, out int pId))
                {
                    tokens.Add(punctLower);
                    inputIds.Add(pId);
                }
                else
                {
                    tokens.Add(_unkToken);
                    inputIds.Add(_unkId);
                }
                offsets.Add((start, end));
                continue;
            }

            // Word token
            int wordStart = i;
            while (i < len && !char.IsWhiteSpace(text[i]) && !char.IsPunctuation(text[i]) && !char.IsSymbol(text[i]))
            {
                i++;
            }
            int wordEnd = i;
            string word = text[wordStart..wordEnd].ToLowerInvariant();

            // WordPiece subword breakdown
            int subStart = 0;
            bool isBad = false;
            var subTokens = new List<string>();
            var subIds = new List<long>();
            var subOffsets = new List<(int, int)>();

            while (subStart < word.Length)
            {
                int subEnd = word.Length;
                string curSubStr = "";
                int matchedId = -1;

                while (subStart < subEnd)
                {
                    string substr = word[subStart..subEnd];
                    if (subStart > 0)
                    {
                        substr = "##" + substr;
                    }

                    if (_vocab.TryGetValue(substr, out int id))
                    {
                        curSubStr = substr;
                        matchedId = id;
                        break;
                    }

                    subEnd--;
                }

                if (matchedId == -1)
                {
                    isBad = true;
                    break;
                }

                subTokens.Add(curSubStr);
                subIds.Add(matchedId);
                subOffsets.Add((wordStart + subStart, wordStart + subEnd));
                subStart = subEnd;
            }

            if (isBad || subTokens.Count == 0)
            {
                tokens.Add(_vocab.Count <= 5 ? word : _unkToken);
                inputIds.Add(_unkId);
                offsets.Add((wordStart, wordEnd));
            }
            else
            {
                tokens.AddRange(subTokens);
                inputIds.AddRange(subIds);
                offsets.AddRange(subOffsets);
            }
        }

        tokens.Add("[SEP]");
        inputIds.Add(_sepId);
        offsets.Add((len, len));

        var attentionMask = Enumerable.Repeat(1L, inputIds.Count).ToList();

        return new TokenizedOutput(tokens, inputIds, attentionMask, offsets);
    }
}
