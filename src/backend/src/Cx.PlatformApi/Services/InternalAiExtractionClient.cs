using System.Net;
using System.Text;
using System.Text.Json;
using Cx.PlatformApi.Models;

namespace Cx.PlatformApi.Services;

public interface IInternalAiExtractionClient
{
    Task<ClinicalExtractionResponse> ExtractAsync(string noteText, string correlationId, CancellationToken cancellationToken = default);
}

public class InternalAiServiceException : Exception
{
    public InternalAiServiceException(string message) : base(message) { }
    public InternalAiServiceException(string message, Exception inner) : base(message, inner) { }
}

public class InternalAiTimeoutException : InternalAiServiceException
{
    public InternalAiTimeoutException(string message) : base(message) { }
}

public class InternalAiMalformedOutputException : InternalAiServiceException
{
    public InternalAiMalformedOutputException(string message) : base(message) { }
}

public class InternalAiServiceUnavailableException : InternalAiServiceException
{
    public InternalAiServiceUnavailableException(string message, Exception inner) : base(message, inner) { }
}

public class InternalAiExtractionClient : IInternalAiExtractionClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InternalAiExtractionClient> _logger;

    public InternalAiExtractionClient(HttpClient httpClient, ILogger<InternalAiExtractionClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ClinicalExtractionResponse> ExtractAsync(string noteText, string correlationId, CancellationToken cancellationToken = default)
    {
        var internalPayload = new
        {
            note_text = noteText,
            correlation_id = correlationId,
            model_deployment = "auto"
        };

        var requestJson = JsonSerializer.Serialize(internalPayload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/extract")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Correlation-ID", correlationId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Internal Python AI service for correlation {CorrelationId}", correlationId);
            throw new InternalAiServiceUnavailableException("The internal AI extraction service is unreachable or offline.", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("Internal Python AI service timed out on correlation {CorrelationId}", correlationId);
            throw new InternalAiTimeoutException("The internal AI extraction service timed out before responding.");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Internal AI service returned status {StatusCode} for correlation {CorrelationId}: {Body}",
                response.StatusCode, correlationId, responseBody);

            if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                throw new InternalAiTimeoutException("The AI inference engine timed out during extraction.");
            }
            if (response.StatusCode == HttpStatusCode.BadGateway)
            {
                throw new InternalAiMalformedOutputException("The AI model returned malformed output that could not be validated.");
            }
            if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == (HttpStatusCode)422)
            {
                throw new ArgumentException($"Invalid extraction request: {responseBody}");
            }

            throw new InternalAiServiceException($"Internal AI service error ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // Parse model metadata
        var adapterType = "unknown";
        var modelDeployment = "unknown";
        var promptVersion = "v1.0.0";
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;
        double? estimatedCostUsd = null;

        if (root.TryGetProperty("model_metadata", out var metaEl))
        {
            adapterType = metaEl.GetProperty("adapter_type").GetString() ?? adapterType;
            modelDeployment = metaEl.GetProperty("model_deployment").GetString() ?? modelDeployment;
            promptVersion = metaEl.GetProperty("prompt_version").GetString() ?? promptVersion;

            if (metaEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                promptTokens = pt.GetInt32();
            if (metaEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                completionTokens = ct.GetInt32();
            if (metaEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                totalTokens = tt.GetInt32();
            if (metaEl.TryGetProperty("estimated_cost_usd", out var ec) && ec.ValueKind == JsonValueKind.Number)
                estimatedCostUsd = ec.GetDouble();
        }

        // Parse preprocessed
        var preprocessedEl = root.GetProperty("preprocessed");
        var charLength = preprocessedEl.GetProperty("char_length").GetInt32();
        var sections = new List<SectionSpanDto>();
        if (preprocessedEl.TryGetProperty("sections", out var sectionsEl))
        {
            foreach (var s in sectionsEl.EnumerateArray())
            {
                sections.Add(new SectionSpanDto(
                    s.GetProperty("code").GetString() ?? "",
                    s.GetProperty("heading").GetString() ?? "",
                    s.GetProperty("start").GetInt32(),
                    s.GetProperty("end").GetInt32()
                ));
            }
        }

        // Parse medications
        var medications = new List<ExtractedMedicationDto>();
        if (root.TryGetProperty("medications", out var medsEl))
        {
            foreach (var m in medsEl.EnumerateArray())
            {
                var ev = m.GetProperty("evidence");
                var evidenceDto = new EvidenceDto(
                    ev.GetProperty("quote").GetString() ?? "",
                    ev.GetProperty("verified").GetBoolean(),
                    ev.GetProperty("start").ValueKind == JsonValueKind.Null ? null : ev.GetProperty("start").GetInt32(),
                    ev.GetProperty("end").ValueKind == JsonValueKind.Null ? null : ev.GetProperty("end").GetInt32()
                );

                var qualityFlags = new List<string>();
                if (m.TryGetProperty("quality_flags", out var qf))
                {
                    foreach (var f in qf.EnumerateArray()) qualityFlags.Add(f.GetString() ?? "");
                }

                var findings = new List<FindingDto>();
                if (m.TryGetProperty("findings", out var fd))
                {
                    foreach (var f in fd.EnumerateArray())
                    {
                        findings.Add(new FindingDto(
                            f.GetProperty("code").GetString() ?? "",
                            f.GetProperty("severity").GetString() ?? "",
                            f.GetProperty("field").ValueKind == JsonValueKind.Null ? null : f.GetProperty("field").GetString(),
                            f.GetProperty("message").GetString() ?? ""
                        ));
                    }
                }

                var normEl = m.GetProperty("normalization");
                var normalization = new NormalizationDto(
                    normEl.GetProperty("status").GetString() ?? "unresolved",
                    normEl.GetProperty("matched_code").ValueKind == JsonValueKind.Null ? null : normEl.GetProperty("matched_code").GetString(),
                    normEl.GetProperty("display_name").ValueKind == JsonValueKind.Null ? null : normEl.GetProperty("display_name").GetString(),
                    normEl.GetProperty("confidence").GetDouble()
                );

                medications.Add(new ExtractedMedicationDto(
                    m.GetProperty("name").GetString() ?? "",
                    m.GetProperty("strength").ValueKind == JsonValueKind.Null ? null : m.GetProperty("strength").GetString(),
                    m.GetProperty("dose").ValueKind == JsonValueKind.Null ? null : m.GetProperty("dose").GetString(),
                    m.GetProperty("route").ValueKind == JsonValueKind.Null ? null : m.GetProperty("route").GetString(),
                    m.GetProperty("frequency").ValueKind == JsonValueKind.Null ? null : m.GetProperty("frequency").GetString(),
                    m.GetProperty("duration").ValueKind == JsonValueKind.Null ? null : m.GetProperty("duration").GetString(),
                    m.GetProperty("formulation").ValueKind == JsonValueKind.Null ? null : m.GetProperty("formulation").GetString(),
                    m.GetProperty("instructions").ValueKind == JsonValueKind.Null ? null : m.GetProperty("instructions").GetString(),
                    m.GetProperty("assertion_status").GetString() ?? "uncertain",
                    evidenceDto,
                    qualityFlags,
                    findings,
                    normalization
                ));
            }
        }

        // Parse investigations
        var investigations = new List<ExtractedInvestigationDto>();
        if (root.TryGetProperty("investigations", out var invsEl))
        {
            foreach (var i in invsEl.EnumerateArray())
            {
                var ev = i.GetProperty("evidence");
                var evidenceDto = new EvidenceDto(
                    ev.GetProperty("quote").GetString() ?? "",
                    ev.GetProperty("verified").GetBoolean(),
                    ev.GetProperty("start").ValueKind == JsonValueKind.Null ? null : ev.GetProperty("start").GetInt32(),
                    ev.GetProperty("end").ValueKind == JsonValueKind.Null ? null : ev.GetProperty("end").GetInt32()
                );

                var qualityFlags = new List<string>();
                if (i.TryGetProperty("quality_flags", out var qf))
                {
                    foreach (var f in qf.EnumerateArray()) qualityFlags.Add(f.GetString() ?? "");
                }

                var findings = new List<FindingDto>();
                if (i.TryGetProperty("findings", out var fd))
                {
                    foreach (var f in fd.EnumerateArray())
                    {
                        findings.Add(new FindingDto(
                            f.GetProperty("code").GetString() ?? "",
                            f.GetProperty("severity").GetString() ?? "",
                            f.GetProperty("field").ValueKind == JsonValueKind.Null ? null : f.GetProperty("field").GetString(),
                            f.GetProperty("message").GetString() ?? ""
                        ));
                    }
                }

                var normEl = i.GetProperty("normalization");
                var normalization = new NormalizationDto(
                    normEl.GetProperty("status").GetString() ?? "unresolved",
                    normEl.GetProperty("matched_code").ValueKind == JsonValueKind.Null ? null : normEl.GetProperty("matched_code").GetString(),
                    normEl.GetProperty("display_name").ValueKind == JsonValueKind.Null ? null : normEl.GetProperty("display_name").GetString(),
                    normEl.GetProperty("confidence").GetDouble()
                );

                investigations.Add(new ExtractedInvestigationDto(
                    i.GetProperty("name").GetString() ?? "",
                    i.GetProperty("timing").ValueKind == JsonValueKind.Null ? null : i.GetProperty("timing").GetString(),
                    i.GetProperty("specimen").ValueKind == JsonValueKind.Null ? null : i.GetProperty("specimen").GetString(),
                    i.GetProperty("assertion_status").GetString() ?? "uncertain",
                    evidenceDto,
                    qualityFlags,
                    findings,
                    normalization
                ));
            }
        }

        var valEl = root.GetProperty("validation_summary");
        var valSummary = new ValidationSummaryDto(
            valEl.GetProperty("has_errors").GetBoolean(),
            valEl.GetProperty("error_count").GetInt32(),
            valEl.GetProperty("warning_count").GetInt32()
        );

        var durationMs = root.GetProperty("execution_duration_ms").GetDouble();

        return new ClinicalExtractionResponse(
            ExtractionId: Guid.NewGuid().ToString("D"),
            CorrelationId: correlationId,
            ExtractedAt: DateTime.UtcNow,
            ExecutionDurationMs: durationMs,
            ModelMetadata: new ModelMetadataDto(adapterType, modelDeployment, promptVersion, promptTokens, completionTokens, totalTokens, estimatedCostUsd),
            Preprocessed: new PreprocessedDto(charLength, sections),
            Medications: medications,
            Investigations: investigations,
            ValidationSummary: valSummary
        );
    }
}
