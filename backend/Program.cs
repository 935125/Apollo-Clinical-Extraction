using Apollo.Backend.Configuration;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using Apollo.Backend.Clinical;
using Apollo.Backend.Fhir;
using Apollo.Backend.Models;
using Apollo.Backend.Nlp;
using Apollo.Backend.Security;
using Apollo.Backend.Storage;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UglyToad.PdfPig;

bool diagnosticsOnly = args.Contains("--routing-diagnostics", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--routing-diagnostics").ToArray());
builder.AddLocalDevelopmentDefaults();
if (diagnosticsOnly)
{
    Console.WriteLine(JsonSerializer.Serialize(ProviderDiagnostics.Snapshot(builder.Configuration, builder.Environment), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return;
}
// Container/service accounts must not require Windows Event Log write privileges.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Configure Kestrel limits
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 25 * 1024 * 1024; // 25 MiB ceiling
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200", "http://127.0.0.1:4200"])
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("X-Correlation-ID");
    });
});

// Health Checks
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("Backend alive"), tags: ["live"])
    .AddCheck<OnnxReadinessHealthCheck>("pipeline", tags: ["ready"])
    .AddCheck<ProviderConfigurationHealthCheck>("provider_configuration", tags: ["ready"]);

// NLP & Pipeline Singletons
builder.Services.AddSingleton<WordPieceTokenizer>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    string directory = config["LocalOnnx:ArtifactDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "artifacts", "bc5cdr_latest");
    return new WordPieceTokenizer(config["LocalOnnx:TokenizerPath"] ?? Path.Combine(directory, "clinical_tokenizer.json"));
});
builder.Services.AddSingleton<SlidingWindowOnnxEngine>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    string directory = config["LocalOnnx:ArtifactDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "artifacts", "bc5cdr_latest");
    return new SlidingWindowOnnxEngine(config["LocalOnnx:ModelPath"] ?? Path.Combine(directory, "onnx", "model_int8.onnx"), sp.GetRequiredService<WordPieceTokenizer>());
});

builder.AddClinicalApiSecurity();

builder.Services.AddSingleton<ClinicalPipelineService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FoundryCircuitBreaker>();
builder.Services.AddHostedService<ProviderStartupDiagnostics>();
builder.Services.Configure<ExtractionOptions>(builder.Configuration.GetSection(ExtractionOptions.SectionName));
builder.Services.Configure<FoundryOptions>(builder.Configuration.GetSection(FoundryOptions.SectionName));
builder.Services.AddSingleton<TokenCredential>(sp => builder.Environment.IsDevelopment()
    ? new AzureCliCredential()
    : new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = true,
        ManagedIdentityClientId = sp.GetRequiredService<IOptions<FoundryOptions>>().Value.ManagedIdentityClientId
    }));
builder.Services.AddHttpClient<IFoundryClinicalExtractor, FoundryClinicalExtractor>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ILocalClinicalExtractor, OnnxClinicalExtractor>();
builder.Services.AddTransient<MicrosoftFoundryExtractionAdapter>();
builder.Services.AddSingleton<ClinicalResultValidator>();
builder.Services.AddTransient<ExtractionWorkflow>();
builder.Services.AddSingleton<ClinicalSafetyRuleEngine>();
builder.Services.AddSingleton<ActiveLearningFeedbackService>();
builder.Services.AddSingleton<IExtractionRepository>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<SqlExtractionRepository>>();
    string connStr = config.GetConnectionString("Postgres") ?? config["ConnectionStrings:Postgres"]
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres must be configured. Extraction records cannot be saved without PostgreSQL.");
    return new SqlExtractionRepository(connStr, logger, config.GetValue("Persistence:AutoMigrate", false));
});

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async http =>
{
    var error = http.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError("API operation failed; error type {ErrorType}", error?.GetType().Name);
    http.Response.StatusCode = error is ClinicalExtractionException clinical ? clinical.Status : 503;
    await http.Response.WriteAsJsonAsync(new { code = error is ClinicalExtractionException known ? known.Code : "SERVICE_UNAVAILABLE",
        detail = error is ClinicalExtractionException safe ? safe.Message : "The operation could not complete. Retry after reloading the latest state.",
        routing = (error as ClinicalExtractionException)?.Routing });
}));
app.UseRouting();
app.UseCors("AllowAll");
app.UseClinicalApiSecurity();

// Serve static frontend assets if directory exists
string staticDir = Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist", "cx-review-ui", "browser");
if (!Directory.Exists(staticDir))
    staticDir = Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist");
if (Directory.Exists(staticDir))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticDir)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticDir)
    });
}

// ----------------------------------------------------------------------------
// Health Endpoints
// ----------------------------------------------------------------------------
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}).AllowAnonymous();

app.MapGet("/v1/routing", (IConfiguration config, IHostEnvironment environment, IOptions<ExtractionOptions> extraction, IOptions<FoundryOptions> foundry) =>
    Results.Ok(ProviderDiagnostics.Snapshot(config, environment, extraction.Value, foundry.Value))).AllowAnonymous();
app.MapGet("/v1/health", () => Results.Ok(new { status = "healthy", version = "2.1.0-dotnet" })).AllowAnonymous();
app.MapGet("/api/v1/ping", () => Results.Ok(new { status = "active", timestamp = DateTime.UtcNow })).AllowAnonymous();

// ----------------------------------------------------------------------------
// cx-review-ui Endpoints: /api/v1/extractions (POST, GET list, GET by ID)
// ----------------------------------------------------------------------------
app.MapPost("/api/v1/extractions", async (
    [FromBody] ClinicalExtractionRequest? request,
    HttpContext httpContext,
    ExtractionWorkflow workflow) =>
{
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("D");
    }
    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

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

    try
    {
        var result = await workflow.ExtractAndSaveAsync(
            request.NoteText, correlationId, "text", null, httpContext.RequestAborted);
        return Results.Ok(result);
    }
    catch (PIILeakageException piiEx)
    {
        return Results.Json(new
        {
            error = "PII_LEAKAGE_DETECTED",
            message = "Clinical note contains prohibited protected health information (PHI) or personal identifiers.",
            flaggedCount = piiEx.FlaggedIdentifiers.Count,
            flaggedTypes = piiEx.FlaggedIdentifiers.Select(f => f.Type).Distinct().ToList()
        }, statusCode: (int)HttpStatusCode.UnprocessableEntity);
    }
    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(statusCode: 499, title: "Extraction cancelled", extensions: new Dictionary<string, object?> { ["code"] = "REQUEST_CANCELLED" });
    }
    catch (ClinicalExtractionException ex)
    {
        return Results.Problem(statusCode: ex.Status, title: ex.Code == "PERSISTENCE_FAILED" ? "Extraction could not be saved" : "Extraction unavailable",
            detail: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["correlationId"] = correlationId, ["routing"] = ex.Routing });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Extraction failed; correlation {CorrelationId}, error type {ErrorType}", correlationId, ex.GetType().Name);
        return Results.Problem(statusCode: 503, title: "Extraction unavailable", detail: "The clinical extraction could not complete.",
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_UNAVAILABLE", ["correlationId"] = correlationId });
    }
});

app.MapGet("/api/v1/extractions", async (
    [FromQuery] int? limit,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var list = await repo.ListExtractionsAsync(limit ?? 50, ct);
    return Results.Ok(list);
});

app.MapGet("/api/v1/extractions/search", async (
    [FromQuery] string? q,
    [FromQuery] int? limit,
    [FromQuery] double? minSimilarity,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest(new { error = "Search query parameter 'q' is required." });
    }

    var results = await repo.SearchBySemanticSimilarityAsync(q, limit ?? 10, minSimilarity ?? 0.05, ct);
    return Results.Ok(results);
});

app.MapPost("/api/v1/extractions/search", async (
    [FromBody] SemanticSearchRequest? req,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Query))
    {
        return Results.BadRequest(new { error = "Search query is required." });
    }

    var results = await repo.SearchBySemanticSimilarityAsync(req.Query, req.Limit, req.MinSimilarity, ct);
    return Results.Ok(results);
});

app.MapGet("/api/v1/extractions/{id}", async (
    string id,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var extraction = await repo.GetExtractionAsync(id, ct);
    if (extraction == null)
    {
        return Results.Problem(
            title: "Extraction run not found",
            detail: $"No extraction run exists with ID '{id}'.",
            statusCode: (int)HttpStatusCode.NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_NOT_FOUND", ["extractionId"] = id }
        );
    }

    return Results.Ok(extraction);
});

// ----------------------------------------------------------------------------
// Stage 5: Entity-Level Clinical Review & Audit History Endpoints
// ----------------------------------------------------------------------------
app.MapGet("/api/v1/extractions/{id}/reviews", async (
    string id,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var extraction = await repo.GetExtractionAsync(id, ct);
    if (extraction == null)
    {
        return Results.Problem(
            title: "Extraction run not found",
            detail: $"No extraction run exists with ID '{id}'.",
            statusCode: (int)HttpStatusCode.NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_NOT_FOUND", ["extractionId"] = id }
        );
    }

    var reviews = await repo.ListEntityReviewsAsync(id, ct);
    return Results.Ok(reviews);
});

app.MapPost("/api/v1/extractions/{id}/reviews", async (
    string id,
    [FromBody] EntityReviewSubmissionRequest? req,
    HttpContext httpContext,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? Guid.NewGuid().ToString("D");
    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

    if (req == null || string.IsNullOrWhiteSpace(req.EntityType) || string.IsNullOrWhiteSpace(req.ReviewStatus))
    {
        return Results.Problem(
            title: "Invalid review submission",
            detail: "EntityType ('MEDICATION' or 'INVESTIGATION') and ReviewStatus ('PENDING_REVIEW', 'ACCEPTED', 'CORRECTED', 'REJECTED', or 'NEEDS_CLARIFICATION') are required.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_REVIEW_REQUEST", ["correlationId"] = correlationId }
        );
    }

    if (req.ExpectedVersion < 0 || (!ClinicalApiSecurity.IsDemo(app.Configuration, app.Environment) && req.ExpectedVersion == null))
        return Results.Problem(statusCode: 400, title: "Review version required", detail: "Supply the last observed expectedVersion (zero for the first review).",
            extensions: new Dictionary<string, object?> { ["code"] = "REVIEW_VERSION_REQUIRED" });
    if (req.ReviewComment?.Length > 4000 || req.CorrectedEntity?.Count > 20 || req.CorrectedEntity?.Any(p => p.Value?.ToString()?.Length > 4000) == true)
        return Results.Problem(statusCode: 400, title: "Review payload too large");
    if (!req.ReviewStatus.Trim().Equals("CORRECTED", StringComparison.OrdinalIgnoreCase) && req.CorrectedEntity != null)
        return Results.Problem(statusCode: 400, title: "Correction requires CORRECTED status");

    string entityType = req.EntityType.Trim().ToUpperInvariant();
    if (entityType is not ("MEDICATION" or "INVESTIGATION"))
    {
        return Results.Problem(
            title: "Unsupported entity type",
            detail: $"EntityType '{req.EntityType}' is not supported. Use 'MEDICATION' or 'INVESTIGATION'.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_ENTITY_TYPE", ["correlationId"] = correlationId }
        );
    }

    string reviewStatus = req.ReviewStatus.Trim().ToUpperInvariant();
    string[] allowedStatuses = ["PENDING_REVIEW", "ACCEPTED", "CORRECTED", "REJECTED", "NEEDS_CLARIFICATION"];
    if (!allowedStatuses.Contains(reviewStatus, StringComparer.Ordinal))
    {
        return Results.Problem(
            title: "Invalid review status",
            detail: $"ReviewStatus '{req.ReviewStatus}' is invalid. Allowed values: {string.Join(", ", allowedStatuses)}.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_REVIEW_STATUS", ["correlationId"] = correlationId }
        );
    }

    if (reviewStatus is "REJECTED" or "NEEDS_CLARIFICATION" && string.IsNullOrWhiteSpace(req.ReviewComment))
    {
        return Results.Problem(
            title: "Review comment required",
            detail: $"A clinical rationale or comment is required when marking an entity as {reviewStatus}.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "REVIEW_COMMENT_REQUIRED", ["correlationId"] = correlationId }
        );
    }

    if (reviewStatus == "CORRECTED")
    {
        if (req.CorrectedEntity == null || req.CorrectedEntity.Count == 0)
        {
            return Results.Problem(
                title: "Corrected fields required",
                detail: "CorrectedEntity payload is required when ReviewStatus is 'CORRECTED'.",
                statusCode: (int)HttpStatusCode.BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "CORRECTED_ENTITY_REQUIRED", ["correlationId"] = correlationId }
            );
        }

        string[] allowed = entityType == "MEDICATION"
            ? ["name", "strength", "dose", "formulation", "route", "frequency", "duration", "instructions", "action", "assertionStatus", "rxNormCode"]
            : ["name", "timing", "specimen", "method", "assertionStatus", "loincCode", "clinicalCategory"];
        if (req.CorrectedEntity.Any(p => !allowed.Contains(p.Key, StringComparer.Ordinal) || p.Value is JsonElement element && element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            return Results.Problem(statusCode: 400, title: "Invalid correction fields", detail: "Corrections accept only the documented clinical fields as strings or null.");

        string? correctedName = req.CorrectedEntity.TryGetValue("name", out var nVal) ? nVal?.ToString() : null;
        if (string.IsNullOrWhiteSpace(correctedName))
        {
            return Results.Problem(
                title: "Entity name required",
                detail: "The corrected entity 'name' field cannot be empty.",
                statusCode: (int)HttpStatusCode.BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "ENTITY_NAME_REQUIRED", ["correlationId"] = correlationId }
            );
        }

        // Validate RxNorm or LOINC code format if supplied
        if (entityType == "MEDICATION" && req.CorrectedEntity.TryGetValue("rxNormCode", out var rxVal) && rxVal != null)
        {
            string rxCode = rxVal.ToString()!.Trim();
            if (rxCode.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(rxCode, @"^\d{2,10}$"))
            {
                return Results.Problem(
                    title: "Invalid RxNorm concept code",
                    detail: $"RxNorm concept code '{rxCode}' must be a valid numeric RxCUI (2-10 digits).",
                    statusCode: (int)HttpStatusCode.BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "INVALID_RXNORM_CODE", ["correlationId"] = correlationId }
                );
            }
        }

        if (entityType == "INVESTIGATION" && req.CorrectedEntity.TryGetValue("loincCode", out var loincVal) && loincVal != null)
        {
            string loincCode = loincVal.ToString()!.Trim();
            if (loincCode.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(loincCode, @"^\d{1,7}-\d$"))
            {
                return Results.Problem(
                    title: "Invalid LOINC concept code",
                    detail: $"LOINC concept code '{loincCode}' must follow standard LOINC format (e.g. '2823-3').",
                    statusCode: (int)HttpStatusCode.BadRequest,
                    extensions: new Dictionary<string, object?> { ["code"] = "INVALID_LOINC_CODE", ["correlationId"] = correlationId }
                );
            }
        }
    }

    bool isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(httpContext.User.FindFirst("sub")?.Value);
    string reviewerIdentity = ClinicalApiSecurity.Reviewer(httpContext.User);
    string authMode = isAuthenticated ? "AUTHENTICATED_CLINICIAN" : "LOCAL_DEMO_MODE";

    try
    {
        var savedReview = await repo.AddEntityReviewAsync(
            id,
            req with { EntityType = entityType, ReviewStatus = reviewStatus },
            reviewerIdentity,
            authMode,
            ct);
        return Results.Ok(savedReview);
    }
    catch (KeyNotFoundException)
    {
        return Results.Problem(
            title: "Extraction run not found",
            detail: $"No extraction run exists with ID '{id}'.",
            statusCode: (int)HttpStatusCode.NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_NOT_FOUND", ["extractionId"] = id }
        );
    }
    catch (ReviewConflictException)
    {
        return Results.Problem(statusCode: 409, title: "Review has changed", detail: "Reload the extraction and review the latest decision before resubmitting.",
            extensions: new Dictionary<string, object?> { ["code"] = "REVIEW_VERSION_CONFLICT" });
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState is "40001" or "23505")
    {
        return Results.Problem(statusCode: 409, title: "Concurrent review conflict", detail: "Reload the latest review and retry.",
            extensions: new Dictionary<string, object?> { ["code"] = "REVIEW_VERSION_CONFLICT" });
    }
    catch (ArgumentOutOfRangeException ex)
    {
        return Results.Problem(
            title: "Entity index out of range",
            detail: ex.Message,
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "ENTITY_INDEX_OUT_OF_RANGE", ["correlationId"] = correlationId }
        );
    }
});

// ----------------------------------------------------------------------------
// HL7 FHIR R4 Interoperability & Export Endpoints (MedicationRequest & DiagnosticReport)
// ----------------------------------------------------------------------------
app.MapGet("/api/v1/extractions/{id}/fhir", async (
    string id,
    [FromQuery] string? patientId,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var extraction = await repo.GetExtractionAsync(id, ct);
    if (extraction == null)
    {
        return Results.Problem(
            title: "Extraction run not found",
            detail: $"No extraction run exists with ID '{id}'.",
            statusCode: (int)HttpStatusCode.NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_NOT_FOUND", ["extractionId"] = id }
        );
    }

    var bundle = FhirR4Mapper.ToMedicationRequestAndDiagnosticReportBundle(extraction, patientId);
    return Results.Ok(bundle);
});

app.MapPost("/api/v1/fhir/export", (
    [FromBody] ClinicalNERResponse? nerResponse,
    [FromQuery] string? patientId) =>
{
    if (nerResponse == null)
    {
        return Results.BadRequest(new { error = "ClinicalNERResponse payload is required." });
    }

    var bundle = FhirR4Mapper.ToMedicationRequestAndDiagnosticReportBundle(nerResponse, patientId);
    return Results.Ok(bundle);
});

// ----------------------------------------------------------------------------
// Active Learning & OOV Quarantine Endpoints
// ----------------------------------------------------------------------------
app.MapGet("/api/v1/quarantine", async (
    [FromQuery] string? status,
    [FromQuery] int? limit,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    var items = await repo.ListQuarantineItemsAsync(status, limit ?? 100, ct);
    return Results.Ok(items);
});

app.MapPost("/api/v1/quarantine", async (
    [FromBody] CreateQuarantineItemRequest? req,
    IExtractionRepository repo,
    CancellationToken ct) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.Mention))
    {
        return Results.BadRequest(new { error = "Mention is required to create a quarantine item." });
    }

    string scrubbed = ZeroLeakageValidator.Scrub(string.IsNullOrWhiteSpace(req.Context) ? req.Mention : req.Context);
    var resolver = new IndianOntologyResolver();
    resolver.QuarantineOov(
        req.Category,
        req.Mention,
        req.StartChar ?? 0,
        req.EndChar ?? req.Mention.Length,
        scrubbed);

    var created = await repo.AddQuarantineItemAsync(new QuarantineItemDto(
        QuarantineId: Guid.NewGuid().ToString("D"),
        ExtractionId: req.ExtractionId,
        Category: req.Category.ToUpperInvariant(),
        Mention: req.Mention.Trim(),
        NormalizedMention: req.Mention.Trim().ToLowerInvariant(),
        Confidence: req.Confidence,
        Reason: req.Reason,
        StartChar: req.StartChar,
        EndChar: req.EndChar,
        ScrubbedContext: scrubbed,
        ReviewStatus: "pending"
    ), ct);

    return Results.Ok(created);
});

app.MapPost("/api/v1/quarantine/{id}/resolve", async (
    string id,
    [FromBody] QuarantineResolutionRequest? req,
    IExtractionRepository repo,
    ActiveLearningFeedbackService activeLearning,
    CancellationToken ct) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.ReviewStatus))
    {
        return Results.BadRequest(new { error = "ReviewStatus ('approved' or 'rejected') is required." });
    }

    string normalizedStatus = req.ReviewStatus.Trim().ToLowerInvariant();
    if (normalizedStatus != "approved" && normalizedStatus != "rejected")
    {
        return Results.BadRequest(new { error = "ReviewStatus must be 'approved' or 'rejected'." });
    }

    var resolved = await repo.ResolveQuarantineItemAsync(id, req with { ReviewStatus = normalizedStatus }, ct);
    if (resolved == null)
    {
        return Results.Problem(
            title: "Quarantine item not found",
            detail: $"No quarantine item exists with ID '{id}'.",
            statusCode: (int)HttpStatusCode.NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "QUARANTINE_NOT_FOUND", ["quarantineId"] = id }
        );
    }

    if (normalizedStatus == "approved")
    {
        activeLearning.ApplyResolution(resolved, req);
    }

    return Results.Ok(resolved);
});

app.MapGet("/api/v1/active-learning/state", (ActiveLearningFeedbackService activeLearning) =>
{
    return Results.Ok(activeLearning.GetState());
});

app.MapPost("/api/v1/safety/evaluate", (
    [FromBody] ClinicalExtractionRequest? req,
    ClinicalPipelineService pipeline) =>
{
    if (req == null || string.IsNullOrWhiteSpace(req.NoteText))
    {
        return Results.BadRequest(new { error = "NoteText is required." });
    }

    var extraction = pipeline.ProcessPlatformExtraction(req, Guid.NewGuid().ToString("D"));
    return Results.Ok(extraction.SafetyAlerts ?? []);
});

// ----------------------------------------------------------------------------
// Apollo Studio Endpoint: /v1/ner/predict
// ----------------------------------------------------------------------------
app.MapPost("/v1/ner/predict", (
    [FromBody] ClinicalNERRequest? request,
    ClinicalPipelineService pipeline) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "Request body or 'text' cannot be empty" });
    }

    try
    {
        var result = pipeline.ProcessApolloNer(request);
        return Results.Ok(result);
    }
    catch (PIILeakageException piiEx)
    {
        return Results.Json(new PiiErrorResponse(
            Message: "PII/PHI detected in text. Processing blocked to prevent data leakage.",
            Flagged: piiEx.FlaggedIdentifiers
        ), statusCode: (int)HttpStatusCode.UnprocessableEntity);
    }
});

// ----------------------------------------------------------------------------
// PDF Extraction Endpoints: /v1/ner/predict/pdf and /predict/pdf
// ----------------------------------------------------------------------------
var pdfHandler = async (HttpContext httpContext, ExtractionWorkflow workflow) =>
{
    var httpRequest = httpContext.Request;
    string correlationId = httpRequest.Headers["X-Correlation-ID"].FirstOrDefault()
        ?? Guid.NewGuid().ToString("D");
    httpContext.Response.Headers["X-Correlation-ID"] = correlationId;

    if (!httpRequest.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Content-Type must be multipart/form-data" });
    }

    var form = await httpRequest.ReadFormAsync(httpContext.RequestAborted);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "No PDF file uploaded" });
    }

    // 20 MiB hard limit check -> 413 Payload Too Large
    const long maxPdfBytes = 20 * 1024 * 1024;
    if (file.Length > maxPdfBytes)
    {
        return Results.Problem(
            title: "PDF File Too Large",
            detail: $"The uploaded PDF size ({file.Length} bytes) exceeds the maximum allowed limit of 20 MiB ({maxPdfBytes} bytes).",
            statusCode: (int)HttpStatusCode.RequestEntityTooLarge
        );
    }

    string extractedText;
    try
    {
        using var stream = file.OpenReadStream();
        extractedText = PdfTextReconstructor.ExtractText(stream);
    }
    catch (Exception ex)
    {
        app.Logger.LogInformation("Rejected invalid PDF; error type {ErrorType}", ex.GetType().Name);
        return Results.Problem(
            title: "Invalid PDF",
            detail: "The uploaded file could not be read as a PDF.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_PDF", ["correlationId"] = correlationId });
    }

    if (string.IsNullOrWhiteSpace(extractedText))
    {
        return Results.Problem(
            title: "Scanned PDF requires OCR",
            detail: "No embedded text was found. OCR is not available; upload a text-based PDF or paste the note text.",
            statusCode: (int)HttpStatusCode.UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "OCR_REQUIRED", ["correlationId"] = correlationId });
    }

    if (extractedText.Length > 65536)
    {
        return Results.Problem(
            title: "PDF text exceeds length limit",
            detail: "The extracted PDF text exceeds 65,536 characters.",
            statusCode: (int)HttpStatusCode.BadRequest,
            extensions: new Dictionary<string, object?> { ["code"] = "NOTE_EXCEEDS_64KIB_LIMIT", ["correlationId"] = correlationId });
    }

    try
    {
        var result = await workflow.ExtractAndSaveAsync(
            extractedText, correlationId, "pdf", Path.GetFileName(file.FileName), httpContext.RequestAborted);
        return Results.Ok(result);
    }
    catch (PIILeakageException piiEx)
    {
        return Results.Json(new PiiErrorResponse(
            Message: "PII/PHI detected in PDF text. Processing blocked to prevent data leakage.",
            Flagged: piiEx.FlaggedIdentifiers
        ), statusCode: (int)HttpStatusCode.UnprocessableEntity);
    }
    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
    {
        return Results.Problem(statusCode: 499, title: "Extraction cancelled", extensions: new Dictionary<string, object?> { ["code"] = "REQUEST_CANCELLED" });
    }
    catch (ClinicalExtractionException ex)
    {
        return Results.Problem(statusCode: ex.Status, title: ex.Code == "PERSISTENCE_FAILED" ? "Extraction could not be saved" : "Extraction unavailable",
            detail: ex.Message, extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["correlationId"] = correlationId, ["routing"] = ex.Routing });
    }
    catch (Exception ex)
    {
        app.Logger.LogError("Extraction failed; correlation {CorrelationId}, error type {ErrorType}", correlationId, ex.GetType().Name);
        return Results.Problem(statusCode: 503, title: "Extraction unavailable", detail: "The clinical extraction could not complete.",
            extensions: new Dictionary<string, object?> { ["code"] = "EXTRACTION_UNAVAILABLE", ["correlationId"] = correlationId });
    }
};

app.MapPost("/api/v1/extractions/pdf", pdfHandler).DisableAntiforgery();
app.MapPost("/v1/ner/predict/pdf", pdfHandler).DisableAntiforgery();
app.MapPost("/predict/pdf", pdfHandler).DisableAntiforgery();

string frontendIndex = Path.Combine(staticDir, "index.html");
if (File.Exists(frontendIndex))
{
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(frontendIndex);
    }).AllowAnonymous();
}

app.Run();

// ----------------------------------------------------------------------------
// Helper to locate model artifacts across workspace directories
// ----------------------------------------------------------------------------
public partial class Program { }

