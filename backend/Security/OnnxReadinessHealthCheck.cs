using Apollo.Backend.Nlp;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Apollo.Backend.Security;

public sealed class OnnxReadinessHealthCheck(IServiceProvider services, IConfiguration config) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool needed = config["Extraction:Provider"] == "LocalOnnx" || config["Extraction:FallbackProvider"] == "LocalOnnx";
        if (!needed) return Task.FromResult(HealthCheckResult.Healthy("Local provider not configured"));
        try
        {
            var engine = services.GetRequiredService<SlidingWindowOnnxEngine>();
            var receipt = new OnnxExecutionReceipt();
            engine.InferSpans("aspirin", receipt);
            return Task.FromResult(engine.HasOnnxModel && receipt.WindowsExecuted > 0 && receipt.ExecutionMode == "NEURAL_ONNX"
                ? HealthCheckResult.Healthy("Pinned local artifacts verified; neural inference executed")
                : HealthCheckResult.Unhealthy("Local neural inference unavailable"));
        }
        catch (Exception) { return Task.FromResult(HealthCheckResult.Unhealthy("Local artifact or inference verification failed")); }
    }
}
