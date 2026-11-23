using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Apollo.Backend.Models;
using Apollo.Backend.Storage;
using Apollo.Backend.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Apollo.Backend.Tests;

// Cryptographically signed synthetic identities exist only inside this isolated test host.
public sealed class SecureApiFactory : WebApplicationFactory<Program>
{
    internal static readonly SymmetricSecurityKey Key = new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("Stage6 isolated synthetic JWT fixture signing material")));
    public bool Demo { get; init; }
    public bool DenyDataAccess { get; init; }
    public HashSet<string>? AllowedRecordIds { get; init; }
    public int Limit { get; init; } = 240;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Security:LocalDemoMode", Demo.ToString());
        builder.UseSetting("Security:Authority", "https://synthetic.invalid");
        builder.UseSetting("Security:Audience", "apollo-test");
        builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:LocalDemoMode"] = Demo.ToString(), ["Security:Authority"] = "https://synthetic.invalid",
            ["Security:Audience"] = "apollo-test", ["Security:RequestsPerMinute"] = Limit.ToString()
        }));
        builder.ConfigureServices(services =>
        {
            if (!DenyDataAccess)
            {
                services.RemoveAll<IClinicalDataAccess>();
                services.AddSingleton<IClinicalDataAccess>(new SyntheticTestRecordAccess(AllowedRecordIds));
            }
            services.RemoveAll<IExtractionRepository>(); services.AddSingleton<IExtractionRepository, InMemoryExtractionRepository>();
            services.PostConfigure<Apollo.Backend.Clinical.ExtractionOptions>(o => { o.AllowDirectLocal = true; o.DirectLocalReason = "isolated_test"; o.Provider = "LocalOnnx"; });
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = "https://synthetic.invalid" };
                options.Configuration.SigningKeys.Add(Key);
                options.ConfigurationManager = new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                options.TokenValidationParameters.ValidIssuer = "https://synthetic.invalid";
                options.TokenValidationParameters.ValidAudience = "apollo-test";
                options.TokenValidationParameters.IssuerSigningKey = Key;
            });
        });
    }
    public static string Token(string role, string audience = "apollo-test", bool expired = false, bool wrongKey = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "https://synthetic.invalid", Audience = audience,
        Subject = new ClaimsIdentity([new Claim("sub", "synthetic-test-subject"), new Claim("roles", role)]),
        NotBefore = DateTime.UtcNow.AddHours(-2), Expires = expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(wrongKey ? new SymmetricSecurityKey(new byte[32]) : Key, SecurityAlgorithms.HmacSha256)
    });
}

public class Stage6SecurityTests
{
    [Fact]
    public async Task SecureApi_AnonymousAndForgedBearerCannotReadClinicalData()
    {
        using var factory = new SecureApiFactory(); using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/extractions")).StatusCode);
        foreach (string token in new[] { "forged", SecureApiFactory.Token("ClinicalReader", "wrong"), SecureApiFactory.Token("ClinicalReader", expired: true), SecureApiFactory.Token("ClinicalReader", wrongKey: true) })
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/extractions")).StatusCode);
        }
    }

    [Fact]
    public async Task SecureApi_ReaderCannotDecide_ReviewerIdentityComesFromVerifiedSubject()
    {
        using var factory = new SecureApiFactory(); using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new("Bearer", SecureApiFactory.Token("ClinicalReader"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/extractions")).StatusCode);
        var review = new EntityReviewSubmissionRequest("MEDICATION", 0, "ACCEPTED", ExpectedVersion: 0);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/extractions/missing/reviews", review)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", SecureApiFactory.Token("ClinicalReviewer"));
        var extracted = await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest("Plan: Start aspirin 81 mg 1 tablet PO daily."));
        extracted.EnsureSuccessStatusCode(); var result = (await extracted.Content.ReadFromJsonAsync<ClinicalExtractionResponse>())!;
        string path = $"/api/v1/extractions/{result.ExtractionId}/reviews";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, review with { ExpectedVersion = null })).StatusCode);
        var saved = await client.PostAsJsonAsync(path, review); saved.EnsureSuccessStatusCode();
        var record = (await saved.Content.ReadFromJsonAsync<EntityReviewRecordDto>())!;
        Assert.Equal("https://synthetic.invalid|synthetic-test-subject", record.ReviewerIdentity);
        Assert.Equal("AUTHENTICATED_CLINICIAN", record.AuthMode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, review)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<EntityReviewRecordDto>>(path))!);
        Assert.Equal("no-store", saved.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/quarantine/test/resolve", new { reviewStatus = "approved" })).StatusCode);
    }

    [Fact]
    public async Task Demo_RateLimitAndCrossOriginWritesAreEnforced()
    {
        using var factory = new SecureApiFactory { Demo = true, Limit = 2 }; using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/extractions", new ClinicalExtractionRequest("test"))).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/health")).StatusCode);
        var limited = await client.GetAsync("/v1/health"); Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }
}

// Test-only entitlement adapter; never registered by the application.
public sealed class SyntheticTestRecordAccess(HashSet<string>? allowed) : IClinicalDataAccess
{
    public Task<bool> AuthorizeAsync(ClaimsPrincipal user, ClinicalDataAccessRequest request, CancellationToken ct) =>
        Task.FromResult(request.ExtractionId == null || allowed == null || allowed.Contains(request.ExtractionId));
}
