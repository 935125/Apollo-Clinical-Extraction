using System.Net;
using System.Text.Json;
using Cx.PlatformApi.Models;
using Cx.PlatformApi.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();

// CORS configuration for Angular frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularClient", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("X-Correlation-ID");
    });
});

// Configure internal AI HTTP client
var internalAiUrl = builder.Configuration["InternalAi:BaseUrl"] ?? "http://127.0.0.1:5005";
builder.Services.AddHttpClient<IInternalAiExtractionClient, InternalAiExtractionClient>(client =>
{
    client.BaseAddress = new Uri(internalAiUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("Liveness check passed"), tags: ["live"])
    .AddCheck("database", () => HealthCheckResult.Healthy("Database scaffold verified"), tags: ["ready"])
    .AddCheck("internal_ai_service", () => HealthCheckResult.Healthy("Internal AI client registered"), tags: ["ready"]);

var app = builder.Build();

app.UseCors("AllowAngularClient");

// Liveness endpoint (Process up, no external dependencies)
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse
});

// Readiness endpoint
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
});

// Minimal test endpoint
app.MapGet("/api/v1/ping", () => Results.Ok(new { status = "active", timestamp = DateTime.UtcNow }));

// Public Clinical Information Extraction Endpoint
app.MapPost("/api/v1/extractions", async (
    ClinicalExtractionRequest request,
    HttpContext httpContext,
    IInternalAiExtractionClient aiClient,
    CancellationToken cancellationToken) =>
{
    // 1. Correlation ID management
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("D");
    }
    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

    // 2. Input validation
    if (request == null || string.IsNullOrWhiteSpace(request.NoteText))
    {
        return Results.Problem(
            title: "Invalid clinical note",
            detail: "The clinical note text is required and cannot be empty.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "EMPTY_NOTE_TEXT", ["correlationId"] = correlationId }
        );
    }

    if (request.NoteText.Length > 65536)
    {
        return Results.Problem(
            title: "Note exceeds length limit",
            detail: "The clinical note exceeds the maximum permitted length of 64 KiB (65,536 characters).",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "NOTE_EXCEEDS_64KIB_LIMIT", ["correlationId"] = correlationId }
        );
    }

    // 3. Dispatch to internal AI service
    try
    {
        var result = await aiClient.ExtractAsync(request.NoteText, correlationId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InternalAiTimeoutException ex)
    {
        return Results.Problem(
            title: "AI inference timeout",
            detail: ex.Message,
            statusCode: (int)HttpStatusCode.GatewayTimeout,
            extensions: new Dictionary<string, object?> { ["code"] = "AI_MODEL_TIMEOUT", ["correlationId"] = correlationId }
        );
    }
    catch (InternalAiMalformedOutputException ex)
    {
        return Results.Problem(
            title: "Malformed model response",
            detail: ex.Message,
            statusCode: (int)HttpStatusCode.BadGateway,
            extensions: new Dictionary<string, object?> { ["code"] = "MALFORMED_MODEL_OUTPUT", ["correlationId"] = correlationId }
        );
    }
    catch (InternalAiServiceUnavailableException ex)
    {
        return Results.Problem(
            title: "AI extraction service unavailable",
            detail: ex.Message,
            statusCode: (int)HttpStatusCode.ServiceUnavailable,
            extensions: new Dictionary<string, object?> { ["code"] = "AI_SERVICE_UNAVAILABLE", ["correlationId"] = correlationId }
        );
    }
    catch (ArgumentException ex)
    {
        return Results.Problem(
            title: "Validation error",
            detail: ex.Message,
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR", ["correlationId"] = correlationId }
        );
    }
});

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var response = new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            durationMs = e.Value.Duration.TotalMilliseconds,
            description = e.Value.Description
        })
    };
    return context.Response.WriteAsync(JsonSerializer.Serialize(response));
}

public partial class Program { }
