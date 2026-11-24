using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace Apollo.Backend.Security;

public static class ClinicalApiSecurity
{
    public static bool IsDemo(IConfiguration config, IHostEnvironment environment) =>
        environment.IsDevelopment() && config.GetValue("Security:LocalDemoMode", true);

    public static void AddClinicalApiSecurity(this WebApplicationBuilder builder)
    {
        bool demo = IsDemo(builder.Configuration, builder.Environment);
        string? authority = builder.Configuration["Security:Authority"];
        string? audience = builder.Configuration["Security:Audience"];
        if (!demo && (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrWhiteSpace(audience)))
            throw new InvalidOperationException("Secure mode requires an HTTPS Security:Authority and Security:Audience. Local demo is Development-only.");

        builder.Services.AddSingleton<IClinicalDataAccess, DenyUnconfiguredClinicalAccess>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.IncludeErrorDetails = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true,
                NameClaimType = "sub", RoleClaimType = "roles", ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAssertion(context =>
            {
                if (Apollo.Backend.Emr.EmrAuth.IsDoctorSession(context.User))
                    return context.User.IsInRole("Doctor") || context.User.IsInRole("ClinicalReviewer") || context.User.IsInRole("Administrator");
                if (demo) return true;
                if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(context.User.FindFirstValue("sub"))) return false;
                var http = context.Resource as HttpContext;
                if (http?.Request.Method is "GET" or "HEAD")
                    return context.User.IsInRole("ClinicalReader") || context.User.IsInRole("ClinicalReviewer") || context.User.IsInRole("ClinicalAdministrator");
                if (http?.Request.Path.StartsWithSegments("/api/v1/quarantine") == true)
                    return context.User.IsInRole("ClinicalAdministrator");
                return context.User.IsInRole("ClinicalReviewer") || context.User.IsInRole("ClinicalAdministrator");
            }).Build());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.User.Identity?.IsAuthenticated == true ? $"{http.User.FindFirstValue("iss")}|{http.User.FindFirstValue("sub")}" : http.Connection.RemoteIpAddress?.ToString() ?? "local-test",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = builder.Configuration.GetValue("Security:RequestsPerMinute", 240),
                        Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                    }));
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new { code = "RATE_LIMITED", detail = "Request limit reached. Retry later." }, ct);
            };
        });
    }

    public static void UseClinicalApiSecurity(this WebApplication app)
    {
        bool demo = IsDemo(app.Configuration, app.Environment);
        if (!demo) { app.UseHsts(); app.UseHttpsRedirection(); }
        app.Use(async (http, next) =>
        {
            if (!demo && !http.Request.IsHttps)
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { code = "HTTPS_REQUIRED" });
                return;
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (demo && http.Connection.RemoteIpAddress is { } address && !System.Net.IPAddress.IsLoopback(address))
            {
                http.Response.StatusCode = 403;
                await http.Response.WriteAsJsonAsync(new { code = "LOCAL_DEMO_LOOPBACK_ONLY" });
                return;
            }
            if (http.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && http.Request.Headers.Origin.FirstOrDefault() is { } origin &&
                !(app.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200", "http://127.0.0.1:4200", "http://127.0.0.1:5000"]).Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                http.Response.StatusCode = 403;
                await http.Response.WriteAsJsonAsync(new { code = "ORIGIN_NOT_ALLOWED" });
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseClinicalDataAccessBoundary();
    }

    public static string Reviewer(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.FindFirstValue("sub") is { Length: > 0 } sub
        ? $"{user.FindFirstValue("iss")}|{sub}"
        : "Unauthenticated Session (Local Demonstration Mode)";
}
