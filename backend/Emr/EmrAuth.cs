using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Apollo.Backend.Emr;

public static class EmrAuth
{
    public const string Scheme = "ApolloDoctor";
    public static Guid Doctor(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);
    public static bool IsDoctorSession(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true && user.FindFirstValue("iss") == "apollo-local-emr";
    public static void AddDoctorEmr(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<EmrStore>();
        builder.Services.AddSingleton<DoctorSessions>();
        builder.Services.AddAntiforgery(o => { o.HeaderName = "X-XSRF-TOKEN"; o.Cookie.Name = "Apollo.Antiforgery"; o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
        builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = "ApolloSessionOrBearer"; o.DefaultChallengeScheme = "ApolloSessionOrBearer";
        }).AddPolicyScheme("ApolloSessionOrBearer", null, o => o.ForwardDefaultSelector = http =>
            http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : http.Request.Cookies.ContainsKey("Apollo.Doctor") || http.Request.Path.StartsWithSegments("/api/v1/emr") ? Scheme : JwtBearerDefaults.AuthenticationScheme)
        .AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "Apollo.Doctor"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async ctx =>
            {
                if (!Guid.TryParse(ctx.Principal?.FindFirstValue("sid"), out var sid) ||
                    !await ctx.HttpContext.RequestServices.GetRequiredService<DoctorSessions>().Valid(sid, ctx.HttpContext.RequestAborted))
                { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(Scheme); }
            };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("DoctorEmr", p => p.AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser().RequireClaim("iss", "apollo-local-emr").RequireRole("Doctor", "ClinicalReviewer", "Administrator")));
        builder.Services.AddRateLimiter(o => o.AddPolicy("doctor-login", http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions
            { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));
        builder.Services.AddSingleton<SimulatedNoteBroker>();
        builder.Services.AddSingleton<INoteBroker>(sp => builder.Configuration["Emr:Messaging:Mode"] == "AzureServiceBus"
            ? new AzureNoteBroker(builder.Configuration, sp.GetRequiredService<Azure.Core.TokenCredential>())
            : builder.Configuration["Emr:Messaging:Mode"] == "Simulated" && builder.Environment.IsDevelopment()
                ? sp.GetRequiredService<SimulatedNoteBroker>() : new DisabledNoteBroker());
        builder.Services.AddTransient<EmrConsumer>();
        builder.Services.AddHostedService<EmrMessagingService>();
    }
    public static async Task<bool> CheckCsrf(HttpContext http)
    {
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); return true; }
        catch(AntiforgeryValidationException) { return false; }
    }
    // Applied to the existing extraction API even in Development demo mode.
    public static async Task<bool> CheckLinkedExtraction(HttpContext http)
    {
        if (!IsDoctorSession(http.User) || http.Request.Path.StartsWithSegments("/api/v1/emr")) return true;
        if (!(http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/predict") || http.Request.Path.StartsWithSegments("/v1/ner"))) return true;
        if (http.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !await CheckCsrf(http))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "CSRF_INVALID",
                title = "Security Token Expired",
                detail = "Your verification token has expired or is invalid. Please reload the page."
            });
            return false;
        }
        if (http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null) return true;
        string? raw = http.Request.RouteValues["id"]?.ToString();
        bool allowed = http.Request.Path.StartsWithSegments("/api/v1/extractions") && Guid.TryParse(raw, out var id)
            && await http.RequestServices.GetRequiredService<EmrStore>().HasExtraction(Doctor(http.User), id, http.RequestAborted);
        // No unscoped history/search, quarantine or arbitrary synchronous inference for a doctor session.
        if (!allowed)
        {
            http.Response.StatusCode = raw == null ? 403 : 404;
            await http.Response.WriteAsJsonAsync(new
            {
                code = "CLINICAL_ACCESS_DENIED",
                title = "Direct Extraction Restricted",
                detail = raw == null
                    ? "Direct extraction is reserved for clinical review staff. Outpatient clinical notes must be documented and signed within assigned encounters in OP EMR."
                    : "The requested extraction record does not exist or is not associated with your assigned encounters."
            });
        }
        return allowed;
    }
}

public sealed class DoctorSessions(EmrStore store)
{
    private readonly PasswordHasher<string> hasher = new();
    private readonly string dummy = new PasswordHasher<string>().HashPassword("", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    public async Task<(DoctorProfile Profile, Guid Session)?> Login(string login, string password, CancellationToken ct)
    {
        await using var c = await store.Open(ct); await using var tx = await c.BeginTransactionAsync(ct);
        Guid? id = null; string hash = dummy; string name="", role=""; int failures=0; bool enabled=false, locked=false;
        await using(var command = EmrStore.Command(c, "SELECT id,password_hash,display_name,role,failed_attempts,enabled,locked_until>now() FROM emr_doctors WHERE login=@login FOR UPDATE",tx,("login",login)))
        await using(var r = await command.ExecuteReaderAsync(ct))
        {
            if(await r.ReadAsync(ct)) { id=r.GetGuid(0); hash=r.GetString(1); name=r.GetString(2); role=r.GetString(3); failures=r.GetInt32(4); enabled=r.GetBoolean(5); locked=!r.IsDBNull(6)&&r.GetBoolean(6); }
        }
        bool correct = hasher.VerifyHashedPassword(login,hash,password) != PasswordVerificationResult.Failed;
        if(id is null || !correct || !enabled || locked)
        {
            if(id is not null && !locked) await EmrStore.Execute(c,"UPDATE emr_doctors SET failed_attempts=CASE WHEN @n>=5 THEN 0 ELSE @n END,locked_until=CASE WHEN @n>=5 THEN now()+interval '15 minutes' ELSE NULL END WHERE id=@id",ct,tx,("id",id),("n",failures+1));
            await EmrStore.Execute(c,"INSERT INTO emr_security_events(id,doctor_id,event_type) VALUES(@event,@doctor,'LoginRejected')",ct,tx,("event",Guid.NewGuid()),("doctor",id));
            await tx.CommitAsync(ct); return null;
        }
        Guid session = Guid.NewGuid();
        await EmrStore.Execute(c,"""
            UPDATE emr_doctors SET failed_attempts=0,locked_until=NULL WHERE id=@id;
            INSERT INTO emr_sessions(id,doctor_id,expires_at) VALUES(@session,@id,now()+interval '30 minutes');
            INSERT INTO emr_security_events(id,doctor_id,event_type) VALUES(@event,@id,'LoginSucceeded');
            """,ct,tx,("id",id),("session",session),("event",Guid.NewGuid()));
        await tx.CommitAsync(ct); return (new(id.Value,login,name,role),session);
    }
    public async Task<bool> Valid(Guid session, CancellationToken ct)
    {
        await using var c = await store.Open(ct); await using var cmd = EmrStore.Command(c,"SELECT EXISTS(SELECT 1 FROM emr_sessions s JOIN emr_doctors d ON d.id=s.doctor_id WHERE s.id=@id AND s.revoked_at IS NULL AND s.expires_at>now() AND d.enabled)",null,("id",session));
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    public async Task Logout(Guid doctor, Guid session, CancellationToken ct)
    {
        await using var c=await store.Open(ct); await using var tx=await c.BeginTransactionAsync(ct);
        await EmrStore.Execute(c,"UPDATE emr_sessions SET revoked_at=now() WHERE id=@id AND doctor_id=@doctor; INSERT INTO emr_security_events(id,doctor_id,event_type) VALUES(@event,@doctor,'Logout');",ct,tx,("id",session),("doctor",doctor),("event",Guid.NewGuid()));
        await tx.CommitAsync(ct);
    }
}
