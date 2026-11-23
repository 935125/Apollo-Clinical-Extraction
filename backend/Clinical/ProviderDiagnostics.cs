using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Apollo.Backend.Clinical;

public static class ProviderDiagnostics
{
    public static object Snapshot(IConfiguration config, IHostEnvironment environment, ExtractionOptions? extraction = null, FoundryOptions? foundry = null)
    {
        extraction ??= config.GetSection("Extraction").Get<ExtractionOptions>() ?? new();
        foundry ??= config.GetSection("Foundry").Get<FoundryOptions>() ?? new();
        string? error = Error(extraction, foundry);
        string? source = (config as IConfigurationRoot)?.Providers.Reverse().FirstOrDefault(p => p.TryGet("Extraction:Provider", out _))?.GetType().Name;
        return new { environment = environment.EnvironmentName, requestedPrimaryProvider = extraction.Provider,
            fallbackProvider = extraction.FallbackProvider, directLocalAuthorized = ProviderRoutingPolicy.DirectLocalFault(extraction) == null,
            directLocalReason = extraction.DirectLocalReason is "isolated_test" or "authorized_offline" ? extraction.DirectLocalReason : null,
            foundryDeployment = foundry.EffectiveDeployment, targetModel = foundry.TargetModel,
            authenticationMode = foundry.EffectiveAuthenticationMode, configurationError = error,
            configurationReady = error == null, providerConfigurationSource = source,
            readinessScope = "Configuration only; no provider request or token acquisition", extractionInferenceExecuted = false };
    }
    public static string? Error(ExtractionOptions extraction, FoundryOptions foundry) =>
        extraction.Provider.Equals("LocalOnnx", StringComparison.OrdinalIgnoreCase) ? ProviderRoutingPolicy.DirectLocalFault(extraction) :
        extraction.Provider.Equals("MicrosoftFoundry", StringComparison.OrdinalIgnoreCase) ? ProviderRoutingPolicy.ConfigurationFault(foundry) : "EXTRACTION_PROVIDER_INVALID";
}

public sealed class ProviderConfigurationHealthCheck(IOptions<ExtractionOptions> extraction, IOptions<FoundryOptions> foundry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string? error = ProviderDiagnostics.Error(extraction.Value, foundry.Value);
        return Task.FromResult(error == null ? HealthCheckResult.Healthy("Provider configuration valid; external authorization not probed")
            : HealthCheckResult.Unhealthy(error));
    }
}

public sealed class ProviderStartupDiagnostics(IHostEnvironment environment, IOptions<ExtractionOptions> extraction,
    IOptions<FoundryOptions> foundry, ILogger<ProviderStartupDiagnostics> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        string? error = ProviderDiagnostics.Error(extraction.Value, foundry.Value);
        if (error != null) logger.LogError("Provider routing configuration failed: {Code}; no silent provider substitution", error);
        logger.LogInformation("Provider routing: environment {Environment}; primary {Primary}; fallback {Fallback}; local authorization {LocalAllowed}; configuration {ConfigurationStatus}",
            environment.EnvironmentName, extraction.Value.Provider, extraction.Value.FallbackProvider,
            ProviderRoutingPolicy.DirectLocalFault(extraction.Value) == null, ProviderDiagnostics.Error(extraction.Value, foundry.Value) ?? "valid_not_live_verified");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
