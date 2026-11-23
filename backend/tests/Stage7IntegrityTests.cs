using Apollo.Backend.Clinical;
using Apollo.Backend.Nlp;
using Apollo.Backend.Tests.Evaluation;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Apollo.Backend.Models;
using Microsoft.AspNetCore.Hosting;

namespace Apollo.Backend.Tests;

public class Stage7IntegrityTests
{
    private static string Artifacts => Path.Combine(HeldOutBenchmarkRunner.Backend, "artifacts", "bc5cdr_latest");
    private static string Model => Path.Combine(Artifacts, "onnx", "model_int8.onnx");
    private static WordPieceTokenizer Tokenizer() => new(Path.Combine(Artifacts, "clinical_tokenizer.json"));

    [Fact]
    public void MissingArtifacts_CannotBecomeDeterministicInference()
    {
        Assert.Throws<OnnxIntegrityException>(() => new SlidingWindowOnnxEngine());
        Assert.Throws<OnnxIntegrityException>(() => new SlidingWindowOnnxEngine("missing-model.onnx", Tokenizer()));
        Assert.Throws<OnnxIntegrityException>(() => new WordPieceTokenizer("missing-tokenizer.json"));
        Assert.Throws<OnnxIntegrityException>(() => new SlidingWindowOnnxEngine(Model, new WordPieceTokenizer()));
    }

    [Fact]
    public void CorruptAndIncompatibleArtifacts_AreRejectedBeforeAcceptingLocalMode()
    {
        string directory = Path.Combine(Path.GetTempPath(), "apollo-stage7-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string corrupted = Path.Combine(directory, "corrupt.onnx"); File.WriteAllText(corrupted, "not an ONNX graph");
            Assert.Equal("ONNX_MODEL_HASH_MISMATCH", Assert.Throws<OnnxIntegrityException>(() => new SlidingWindowOnnxEngine(corrupted, Tokenizer())).Code);
            string tokenFile = Path.Combine(directory, "tokenizer.json");
            File.WriteAllText(tokenFile, "{bad-json");
            Assert.Throws<OnnxIntegrityException>(() => new WordPieceTokenizer(tokenFile));
            File.WriteAllText(tokenFile, File.ReadAllText(Path.Combine(Artifacts, "clinical_tokenizer.json")) + " ");
            Assert.Equal("ONNX_TOKENIZER_INCOMPATIBLE", Assert.Throws<OnnxIntegrityException>(() => new SlidingWindowOnnxEngine(Model, new WordPieceTokenizer(tokenFile))).Code);
        }
        finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }

    [Fact]
    public void RealInference_ValidatesInputAndExecutesMultipleWindows()
    {
        var tokenizer = Tokenizer(); using var engine = new SlidingWindowOnnxEngine(Model, tokenizer);
        string source = string.Join(" ", Enumerable.Repeat("Continue aspirin 81 mg orally daily.", 180));
        var receipt = new OnnxExecutionReceipt(); var spans = engine.InferSpans(source, receipt);
        Assert.True(engine.HasOnnxModel); Assert.Equal("NEURAL_ONNX", receipt.ExecutionMode);
        Assert.True(receipt.WindowsExecuted > 1); Assert.True(receipt.InferenceDurationMs > 0);
        Assert.All(spans, span => Assert.Equal(span.Text, source[span.StartChar..span.EndChar]));
        var bad = tokenizer.Tokenize("aspirin"); bad.InputIds[1] = 20000;
        Assert.Throws<OnnxIntegrityException>(() => engine.Predict("aspirin", bad));
    }

    [Fact]
    public async Task SecureRecordAccess_DeniesOtherRecordsAndUnconfiguredConnectors()
    {
        var allowed = new HashSet<string>();
        using var factory = new SecureApiFactory { AllowedRecordIds = allowed };
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SecureApiFactory.Token("ClinicalReviewer"));
        using var first = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest("Plan: Start aspirin 81 mg PO daily.")); first.EnsureSuccessStatusCode();
        string ownId = (await first.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!.ExtractionId; allowed.Add(ownId);
        using var second = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest("Plan: Start metformin 500 mg PO daily.")); second.EnsureSuccessStatusCode();
        string otherId = (await second.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!.ExtractionId;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/extractions/" + ownId)).StatusCode);
        foreach (string suffix in new[] { "", "/reviews", "/fhir" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/extractions/" + otherId + suffix)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/extractions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/extractions/search?q=aspirin")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Patient-ID", "allowed-record");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/extractions/other-record")).StatusCode);
        using var denied = new SecureApiFactory { DenyDataAccess = true };
        using var deniedClient = denied.CreateClient(new() { BaseAddress = new("https://localhost") });
        deniedClient.DefaultRequestHeaders.Authorization = new("Bearer", SecureApiFactory.Token("ClinicalAdministrator"));
        Assert.Equal(HttpStatusCode.NotFound, (await deniedClient.GetAsync("/api/v1/extractions/allowed-record")).StatusCode);
        using var httpClient = denied.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await httpClient.GetAsync("/api/v1/extractions/allowed-record")).StatusCode);
    }
    [Fact]
    public async Task WrongConfiguredModelPath_FailsReadinessAndApiWithoutLeakingPath()
    {
        using var factory = new TestWebApplicationFactory().WithWebHostBuilder(builder => builder.UseSetting("LocalOnnx:ModelPath", "incorrect-stage7-path.onnx"));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        using var response = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest("Plan: Start aspirin 81 mg PO daily."));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(); Assert.Contains("ONNX_ARTIFACT_UNAVAILABLE", body);
        Assert.DoesNotContain("incorrect-stage7-path", body); Assert.DoesNotContain("System.", body);
    }

}
