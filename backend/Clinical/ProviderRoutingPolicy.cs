using Apollo.Backend.Models;

namespace Apollo.Backend.Clinical;

public static class ProviderRoutingPolicy
{
    public static bool IsTransient(string reason) => reason is "AZURE_TIMEOUT" or "AZURE_UNREACHABLE" or "AZURE_UNAVAILABLE" or "AZURE_RATE_LIMITED" or "AZURE_CIRCUIT_OPEN";
    public static bool AllowsFallback(FoundryAttemptException failure, FoundryOptions foundry, ExtractionOptions extraction) =>
        !failure.OperatorFault && foundry.AllowLocalFallback && string.Equals(extraction.FallbackProvider, "LocalOnnx", StringComparison.OrdinalIgnoreCase) &&
        (IsTransient(failure.Reason) || foundry.AllowParsingFallback && failure.Reason is "AZURE_UNUSABLE_JSON" or "AZURE_TRUNCATED");

    public static string? ConfigurationFault(FoundryOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !(uri.AbsolutePath.TrimEnd('/') is "" or "/openai/v1") || string.IsNullOrWhiteSpace(options.EffectiveDeployment) ||
            !string.Equals(options.TargetModel, "gpt-6.1-sol", StringComparison.OrdinalIgnoreCase) ||
            !double.IsFinite(options.TimeoutSeconds) || options.TimeoutSeconds <= 0 || options.TimeoutSeconds > 300 ||
            options.MaxCompletionTokens is < 256 or > 64000)
            return "AZURE_NOT_CONFIGURED";
        string mode = options.EffectiveAuthenticationMode;
        if (mode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)) return string.IsNullOrWhiteSpace(options.ApiKey) ? "AZURE_CREDENTIALS_MISSING" : null;
        return mode.Equals("Entra", StringComparison.OrdinalIgnoreCase) || mode.Equals("EntraId", StringComparison.OrdinalIgnoreCase)
            ? null : "AZURE_AUTHENTICATION_INVALID";
    }
    public static string? DirectLocalFault(ExtractionOptions options) =>
        options.AllowDirectLocal && options.DirectLocalReason is "isolated_test" or "authorized_offline" ? null : "DIRECT_LOCAL_NOT_AUTHORIZED";

    public static ClinicalExtractionException Reject(FoundryAttemptException failure, ClinicalValidationDiagnosticDto? diagnostic = null) => new(failure.Reason,
        failure.Reason == "AZURE_CREDENTIALS_INVALID" || failure.Reason == "AZURE_CREDENTIALS_MISSING" ? 401 :
        failure.Reason is "AZURE_INVALID_EVIDENCE" or "AZURE_UNUSABLE_JSON" or "AZURE_TRUNCATED" or "AZURE_CONTEXT_LIMIT" ? 422 : 503,
        failure.Reason == "AZURE_INVALID_EVIDENCE"
            ? "Primary extraction evidence was rejected. Clinical review is required; local fallback is prohibited for this failure."
            : $"Microsoft Foundry could not complete ({failure.Reason}). No extraction was accepted.")
        { Routing = new("MicrosoftFoundry", [failure.Attempt], false, failure.Reason, "failed_requires_review", diagnostic) };
}

public sealed class FoundryCircuitBreaker(TimeProvider clock)
{
    private readonly object gate = new();
    private int failures;
    private DateTimeOffset until;
    private bool probe;
    public bool Enter(FoundryOptions options)
    {
        if (options.CircuitFailureThreshold <= 0) return true;
        lock (gate)
        {
            if (until == default) return true;
            if (clock.GetUtcNow() < until || probe) return false;
            probe = true; return true;
        }
    }
    public void Success() { lock (gate) { failures = 0; until = default; probe = false; } }
    public void TransientFailure(FoundryOptions options)
    {
        lock (gate)
        {
            probe = false;
            if (options.CircuitFailureThreshold > 0 && ++failures >= options.CircuitFailureThreshold)
                until = clock.GetUtcNow().AddSeconds(Math.Clamp(options.CircuitBreakSeconds, 1, 300));
        }
    }
}

public static class FoundryInvocationContext
{
    private static readonly AsyncLocal<string?> Current = new();
    public static string? CorrelationId => Current.Value;
    public static IDisposable Begin(string correlationId)
    {
        string? prior = Current.Value; Current.Value = correlationId; return new Scope(prior);
    }
    private sealed class Scope(string? prior) : IDisposable { public void Dispose() => Current.Value = prior; }
}
