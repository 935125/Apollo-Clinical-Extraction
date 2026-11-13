using System.Net;
using System.Net.Http.Json;
using Cx.PlatformApi.Models;
using Cx.PlatformApi.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Cx.PlatformApi.Tests;

public class ExtractionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ExtractionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Extract_EmptyNoteText_Returns400BadRequest()
    {
        var client = _factory.CreateClient();
        var request = new ClinicalExtractionRequest(NoteText: "");

        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("EMPTY_NOTE_TEXT");
    }

    [Fact]
    public async Task Extract_NoteExceeds64KiB_Returns400BadRequest()
    {
        var client = _factory.CreateClient();
        var hugeText = new string('A', 65537);
        var request = new ClinicalExtractionRequest(NoteText: hugeText);

        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("NOTE_EXCEEDS_64KIB_LIMIT");
    }

    [Fact]
    public async Task Extract_PropagatesCorrelationIdHeader()
    {
        var correlationId = "corr-" + Guid.NewGuid().ToString("N");
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IInternalAiExtractionClient>(_ => new FakeAiClient(
                    new ClinicalExtractionResponse(
                        ExtractionId: Guid.NewGuid().ToString(),
                        CorrelationId: correlationId,
                        ExtractedAt: DateTime.UtcNow,
                        ExecutionDurationMs: 45.2,
                        ModelMetadata: new ModelMetadataDto("synthetic_mock", "synthetic-engine", "v1.0"),
                        Preprocessed: new PreprocessedDto(50, new List<SectionSpanDto>()),
                        Medications: new List<ExtractedMedicationDto>(),
                        Investigations: new List<ExtractedInvestigationDto>(),
                        ValidationSummary: new ValidationSummaryDto(false, 0, 0)
                    )
                ));
            });
        }).CreateClient();

        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);
        var request = new ClinicalExtractionRequest(NoteText: "Patient presents with mild cough.");

        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("X-Correlation-ID").ShouldBeTrue();
        response.Headers.GetValues("X-Correlation-ID").First().ShouldBe(correlationId);
    }

    [Fact]
    public async Task Extract_SuccessfulExtraction_ReturnsStructuredResults()
    {
        var testMed = new ExtractedMedicationDto(
            Name: "Metformin",
            Strength: "500mg",
            Dose: "1 tab",
            Route: "PO",
            Frequency: "BD",
            Duration: "30 days",
            Formulation: "tablet",
            Instructions: "after meals",
            AssertionStatus: "new_order",
            Evidence: new EvidenceDto("Metformin 500mg PO BD", true, 10, 31),
            QualityFlags: new List<string>(),
            Findings: new List<FindingDto>(),
            Normalization: new NormalizationDto("exact_match", "RX-MET-500", "Metformin 500mg", 1.0)
        );

        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IInternalAiExtractionClient>(_ => new FakeAiClient(
                    new ClinicalExtractionResponse(
                        ExtractionId: Guid.NewGuid().ToString(),
                        CorrelationId: "test-corr",
                        ExtractedAt: DateTime.UtcNow,
                        ExecutionDurationMs: 82.5,
                        ModelMetadata: new ModelMetadataDto("synthetic_mock", "synthetic-engine", "v1.0"),
                        Preprocessed: new PreprocessedDto(100, new List<SectionSpanDto>()),
                        Medications: new List<ExtractedMedicationDto> { testMed },
                        Investigations: new List<ExtractedInvestigationDto>(),
                        ValidationSummary: new ValidationSummaryDto(false, 0, 0)
                    )
                ));
            });
        }).CreateClient();

        var request = new ClinicalExtractionRequest(NoteText: "Plan: Metformin 500mg PO BD after meals.");
        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ClinicalExtractionResponse>();
        result.ShouldNotBeNull();
        result.Medications.Count.ShouldBe(1);
        result.Medications[0].Name.ShouldBe("Metformin");
        result.Medications[0].Strength.ShouldBe("500mg");
        result.Medications[0].Normalization.Status.ShouldBe("exact_match");
        result.Medications[0].Normalization.MatchedCode.ShouldBe("RX-MET-500");
    }

    [Fact]
    public async Task Extract_InternalAiTimeout_Returns504GatewayTimeout()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IInternalAiExtractionClient>(_ => new FaultyAiClient(
                    new InternalAiTimeoutException("Model inference timed out after 30s")
                ));
            });
        }).CreateClient();

        var request = new ClinicalExtractionRequest(NoteText: "Plan: Aspirin 100mg OD.");
        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("AI_MODEL_TIMEOUT");
    }

    [Fact]
    public async Task Extract_InternalAiServiceUnavailable_Returns503()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IInternalAiExtractionClient>(_ => new FaultyAiClient(
                    new InternalAiServiceUnavailableException("Connection refused on 127.0.0.1:5005", new Exception("Offline"))
                ));
            });
        }).CreateClient();

        var request = new ClinicalExtractionRequest(NoteText: "Plan: Aspirin 100mg OD.");
        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("AI_SERVICE_UNAVAILABLE");
    }

    [Fact]
    public async Task Extract_InternalAiMalformedOutput_Returns502BadGateway()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IInternalAiExtractionClient>(_ => new FaultyAiClient(
                    new InternalAiMalformedOutputException("Invalid JSON from LLM")
                ));
            });
        }).CreateClient();

        var request = new ClinicalExtractionRequest(NoteText: "Plan: Aspirin 100mg OD.");
        var response = await client.PostAsJsonAsync("/api/v1/extractions", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("MALFORMED_MODEL_OUTPUT");
    }

    private class FakeAiClient : IInternalAiExtractionClient
    {
        private readonly ClinicalExtractionResponse _response;

        public FakeAiClient(ClinicalExtractionResponse response)
        {
            _response = response;
        }

        public Task<ClinicalExtractionResponse> ExtractAsync(string noteText, string correlationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_response);
        }
    }

    private class FaultyAiClient : IInternalAiExtractionClient
    {
        private readonly Exception _exception;

        public FaultyAiClient(Exception exception)
        {
            _exception = exception;
        }

        public Task<ClinicalExtractionResponse> ExtractAsync(string noteText, string correlationId, CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }
}

