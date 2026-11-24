using System.Security.Claims;

namespace Apollo.Backend.Security;

public record ClinicalDataAccessRequest(string Operation, string? ExtractionId, string ResourceRoute);
public record InstitutionalClinicalBinding(string TenantId, string PatientId, string EncounterId, string ExtractionId);

/// <summary>
/// Institutional adapter must resolve immutable extraction -> tenant/patient/encounter
/// bindings and current subject entitlement from trusted storage. Client IDs/headers
/// and role membership alone are never evidence of record access.
/// </summary>
public interface IClinicalDataAccess
{
    Task<bool> AuthorizeAsync(ClaimsPrincipal user, ClinicalDataAccessRequest request, CancellationToken ct);
}

public sealed class DenyUnconfiguredClinicalAccess : IClinicalDataAccess
{
    public Task<bool> AuthorizeAsync(ClaimsPrincipal user, ClinicalDataAccessRequest request, CancellationToken ct) => Task.FromResult(false);
}

public static class ClinicalDataAccessBoundary
{
    public static void UseClinicalDataAccessBoundary(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            if (!await Apollo.Backend.Emr.EmrAuth.CheckLinkedExtraction(http)) return;
            if (http.Request.Path.StartsWithSegments("/api/v1/emr") || Apollo.Backend.Emr.EmrAuth.IsDoctorSession(http.User))
            { await next(); return; }
            if (ClinicalApiSecurity.IsDemo(app.Configuration, app.Environment) ||
                http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null ||
                !(http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/v1/ner") || http.Request.Path.StartsWithSegments("/predict")))
            { await next(); return; }

            string? id = http.Request.RouteValues["id"]?.ToString();
            // Current repository queries return unscoped collections. A boolean grant
            // cannot safely authorize those queries, including for administrators.
            bool collection = id == null && http.Request.Path.StartsWithSegments("/api/v1/extractions") &&
                (http.Request.Method == "GET" || http.Request.Path.Value?.EndsWith("/search", StringComparison.OrdinalIgnoreCase) == true);
            bool allowed = !collection && await http.RequestServices.GetRequiredService<IClinicalDataAccess>()
                .AuthorizeAsync(http.User, new(http.Request.Method, id, (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? ""), http.RequestAborted);
            if (!allowed)
            {
                http.Response.StatusCode = id == null ? 403 : 404;
                await http.Response.WriteAsJsonAsync(new { code = "CLINICAL_ACCESS_DENIED", detail = "Clinical data access is not authorized." });
                return;
            }
            await next();
        });
    }
}
