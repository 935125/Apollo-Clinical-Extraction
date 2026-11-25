using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Apollo.Backend.Configuration;

/// <summary>
/// Lightweight PostgreSQL connectivity health check for /health/ready and /health/db.
/// Never modifies data or schema.
/// </summary>
public sealed class PostgresReadinessHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        string? connStr = configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings:Postgres"]
            ?? configuration.GetConnectionString("Emr");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            return HealthCheckResult.Unhealthy("ConnectionStrings:Postgres is not configured.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(timeoutCts.Token);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            var scalar = await cmd.ExecuteScalarAsync(timeoutCts.Token);
            return Convert.ToInt32(scalar) == 1
                ? HealthCheckResult.Healthy("PostgreSQL cx_platform reachable")
                : HealthCheckResult.Unhealthy("PostgreSQL readiness probe returned unexpected result.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy($"PostgreSQL unreachable ({ex.GetType().Name}).");
        }
    }
}

/// <summary>
/// Development-only OpenAPI 3.0.3 specification and interactive Swagger UI.
/// Never exposed in non-Development environments; does not relax any API authorization policy.
/// </summary>
public static class DevelopmentOpenApiEndpoints
{
    public static void MapDevelopmentOpenApi(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;

        app.MapGet("/openapi/v1.json", () => Results.Ok(BuildSpec())).AllowAnonymous();
        app.MapGet("/swagger", () => Results.Content(SwaggerHtml, "text/html; charset=utf-8")).AllowAnonymous();
        app.MapGet("/swagger/index.html", () => Results.Redirect("/swagger")).AllowAnonymous();
    }

    private const string SwaggerHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Apollo Clinical Extraction API — Swagger UI (Development)</title>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css" />
          <style>body{margin:0;background:#f7f5f0}.topbar{background:#064b41!important}</style>
        </head>
        <body>
          <div id="swagger-ui"></div>
          <script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
          <script>
            window.onload = () => {
              SwaggerUIBundle({
                url: '/openapi/v1.json',
                dom_id: '#swagger-ui',
                deepLinking: true,
                persistAuthorization: true
              });
            };
          </script>
        </body>
        </html>
        """;

    private static object BuildSpec() => new
    {
        openapi = "3.0.3",
        info = new
        {
            title = "Apollo Clinical Extraction Platform API",
            version = "2.1.0",
            description = "Headless ASP.NET Core .NET 10 clinical extraction API with PostgreSQL persistence, transactional outbox, Azure Service Bus asynchronous processing, hybrid Microsoft Foundry GPT / Local ONNX extraction, and UHID/patient-scoped isolation. Development-only specification."
        },
        servers = new[]
        {
            new { url = "http://127.0.0.1:5000", description = "Local Development Kestrel HTTP" }
        },
        components = new
        {
            securitySchemes = new
            {
                doctorCookie = new { type = "apiKey", @in = "cookie", name = "Apollo.Doctor", description = "Server-side Doctor session cookie issued by /api/v1/emr/auth/login or /api/v1/emr/auth/dev-session." },
                xsrfHeader = new { type = "apiKey", @in = "header", name = "X-XSRF-TOKEN", description = "Anti-forgery token returned by GET /api/v1/emr/auth/csrf." },
                bearerAuth = new { type = "http", scheme = "bearer", bearerFormat = "JWT", description = "Institutional JWT Bearer token for non-demo deployments." }
            }
        },
        paths = new Dictionary<string, object>
        {
            ["/health"] = new { get = Op("Infrastructure", "API liveness probe", "Returns JSON liveness status without hitting external cloud providers.", false) },
            ["/health/live"] = new { get = Op("Infrastructure", "Process liveness check", "ASP.NET Core liveness health check.", false) },
            ["/health/db"] = new { get = Op("Infrastructure", "PostgreSQL readiness check", "Verifies live PostgreSQL connectivity (SELECT 1).", false) },
            ["/health/ready"] = new { get = Op("Infrastructure", "Full readiness check", "Checks PostgreSQL, local ONNX pipeline, and provider configuration.", false) },
            ["/v1/routing"] = new { get = Op("Provider Routing", "Effective provider routing diagnostics", "Returns active primary/fallback provider configuration without executing inference.", false) },
            ["/api/v1/emr/auth/csrf"] = new { get = Op("Authentication", "Issue CSRF token", "Issues XSRF-TOKEN cookie and JSON request token for state-changing requests.", false) },
            ["/api/v1/emr/auth/dev-session"] = new { post = Op("Authentication", "Development loopback session", "Development + DemoMode + localhost loopback only. Returns 403 outside local dev.", false) },
            ["/api/v1/emr/auth/login"] = new { post = Op("Authentication", "Doctor sign-in", "Authenticates synthetic doctor credentials with CSRF validation.", false) },
            ["/api/v1/emr/auth/me"] = new { get = Op("Authentication", "Current authenticated clinician", "Returns active DoctorProfile; returns 401 when unauthenticated.", true) },
            ["/api/v1/emr/encounters"] = new { get = Op("Clinical Note Ingestion", "List assigned encounters", "Returns encounters scoped to the authenticated clinician via emr_encounter_assignments.", true) },
            ["/api/v1/emr/encounters/{id}/notes"] = new { post = Op("Clinical Note Ingestion", "Create draft clinical note", "Creates version 1 draft note for an assigned encounter (requires Doctor role + CSRF).", true) },
            ["/api/v1/emr/notes/{id}"] = new { get = Op("Clinical Note Ingestion", "Get clinical note state", "Returns current note text, version, status, extraction status, and linked extractionId.", true) },
            ["/api/v1/emr/notes/{id}/corrections"] = new { post = Op("Clinical Note Ingestion", "Create new draft version from signed note", "Creates next immutable draft version using optimistic concurrency expectedVersion.", true) },
            ["/api/v1/emr/notes/{id}/sign"] = new { post = Op("Clinical Note Ingestion", "Sign clinical note and enqueue outbox event", "Atomically commits immutable emr_note_versions snapshot + emr_outbox_events row. Returns 409 on duplicate version sign.", true) },
            ["/api/v1/queue"] = new { get = Op("Queue & Polling", "List latest extraction submission events (default limit=10)", "Read-only PostgreSQL projection returning the latest individual extraction submissions ordered by event time descending (default limit=10, max=100, offset pagination). Never collapses rows by patient and never consumes Azure Service Bus.", true) },
            ["/api/v1/queue/{noteId}"] = new { get = Op("Queue & Polling", "Get patient-scoped note & linked extraction", "Returns exact source note + linked extraction for (noteId, version) with optional ?version=&patient= enforcement and within-patient notes only.", true) },
            ["/api/v1/queue/patients/{patientReference}"] = new { get = Op("Patient Isolation", "List notes/versions for one patient", "Returns only notes/versions belonging to the specified patient reference assigned to the caller; 404 otherwise.", true) },
            ["/api/v1/extractions"] = new { post = Op("Direct Extraction", "Direct synchronous extraction (Reviewer/Admin)", "Restricted from Doctor role (403); available to authorized ClinicalReviewer/Admin sessions.", true) },
            ["/api/v1/extractions/{id}"] = new { get = Op("Extraction Results", "Retrieve persisted extraction run", "Returns full medications, investigations, validationSummary, findings, and provider modelMetadata.", true) },
            ["/api/v1/extractions/{id}/fhir"] = new { get = Op("Extraction Results", "Export FHIR R4 Bundle", "Generates FHIR R4 MedicationRequest / ServiceRequest bundle for a persisted extraction.", true) }
        }
    };

    private static object Op(string tag, string summary, string description, bool secured) => new
    {
        tags = new[] { tag },
        summary,
        description,
        security = secured
            ? new object[] { new { doctorCookie = Array.Empty<string>() }, new { bearerAuth = Array.Empty<string>() } }
            : Array.Empty<object>(),
        responses = new Dictionary<string, object>
        {
            ["200"] = new { description = "Success" },
            ["400"] = new { description = "Validation or CSRF error" },
            ["401"] = new { description = "Unauthenticated" },
            ["403"] = new { description = "Forbidden" },
            ["404"] = new { description = "Resource not found or not assigned to caller" },
            ["409"] = new { description = "Version conflict / duplicate sign rejected" }
        }
    };
}
