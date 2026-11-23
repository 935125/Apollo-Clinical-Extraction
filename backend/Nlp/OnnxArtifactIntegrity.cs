using Apollo.Backend.Clinical;
using Apollo.Backend.Security;

namespace Apollo.Backend.Nlp;

public sealed class OnnxIntegrityException(string code, string message) : ClinicalExtractionException(code, 503, message);

/// <summary>The existing Stage 6 baseline pair, pinned independently of writable manifests.</summary>
public static class OnnxArtifactIntegrity
{
    public const string ModelSha256 = "59830ca601c97246f068cc265c215d00c1024e2d3ae3549a5d053723f4a65cd0";
    public const string TokenizerSha256 = "00a99d2e62f065e43eb57d570e38ee7e814b3044f27e2da70755e9587ff33c81";

    public static void Verify(string? modelPath, WordPieceTokenizer? tokenizer)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath) || tokenizer?.LoadedFromArtifact != true)
            throw new OnnxIntegrityException("ONNX_ARTIFACT_UNAVAILABLE", "Required local model weights or tokenizer are unavailable.");
        if (ArtifactChecksumVerifier.ComputeSha256(modelPath) != ModelSha256)
            throw new OnnxIntegrityException("ONNX_MODEL_HASH_MISMATCH", "Local model integrity verification failed.");
        if (tokenizer.SourceSha256 != TokenizerSha256 || tokenizer.VocabSize != 20000 ||
            !tokenizer.HasBaselineSpecialTokens)
            throw new OnnxIntegrityException("ONNX_TOKENIZER_INCOMPATIBLE", "Local tokenizer does not match the pinned model pair.");
    }
}
