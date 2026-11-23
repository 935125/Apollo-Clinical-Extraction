using System.Net;
using System.Text;
using System.Text.Json;

namespace Apollo.Backend.Tests.Evaluation;

// This ledger is only for the explicitly authorized synthetic development assessment.
// CreateNew prevents repeat runs from silently resetting the user's one-run budget.
public sealed class FoundryEvaluationBudget : IDisposable
{
    public const int MaxCalls = 20, MaxOutputTokens = 2000;
    public const decimal MaxUsd = 5, InputUsdPerMillion = 6, OutputUsdPerMillion = 18;
    public const string PricingSource = "https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/introducing-gpt-6-1-sol-in-microsoft-foundry-advanced-intelligence-optimized-for/4560811";
    private readonly StreamWriter ledger;
    public int Calls { get; private set; }
    public decimal ChargedOrReservedUsd { get; private set; }
    public bool Stopped { get; private set; }
    public string? LastRawResponse { get; private set; }
    public List<object> Receipts { get; } = [];
    public FoundryEvaluationBudget(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ledger = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Write(new { type = "authorization", maxCalls = MaxCalls, maxCompletionTokensIncludingReasoning = MaxOutputTokens, maxUsd = MaxUsd,
            inputUsdPerMillion = InputUsdPerMillion, outputUsdPerMillion = OutputUsdPerMillion, source = PricingSource,
            pricingBasis = "Conservative highest published Standard long-context rates including cache-write input; no cache discounts. Deployment billing tier is not verified. Estimates are not invoices.",
            inputReservation = "UTF-8 bytes of entire serialized request plus 1024 tokens; every byte charged as a token, including schema and framing margin." });
    }
    private void Write(object item) => ledger.WriteLine(JsonSerializer.Serialize(item));
    public decimal Reserve(string body, string source)
    {
        LastRawResponse = null;
        using var request = JsonDocument.Parse(body);
        if (request.RootElement.GetProperty("max_completion_tokens").GetInt32() > MaxOutputTokens)
            throw new InvalidOperationException("Evaluation output-token cap exceeded");
        decimal reserved = ((Encoding.UTF8.GetByteCount(body) + 1024) * InputUsdPerMillion + MaxOutputTokens * OutputUsdPerMillion) / 1_000_000m;
        if (Calls >= MaxCalls || ChargedOrReservedUsd + reserved > MaxUsd)
        { Stopped = true; throw new InvalidOperationException("Evaluation budget exhausted before dispatch"); }
        Calls++; ChargedOrReservedUsd += reserved;
        Write(new { type = "dispatch", index = Calls, reservedUsd = reserved, remainingUsd = MaxUsd - ChargedOrReservedUsd,
            syntheticSourceSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(source))), at = DateTimeOffset.UtcNow });
        return reserved;
    }
    public void Complete(decimal reservation, int? status, string? responseBody, double latencyMs, string? transportError)
    {
        LastRawResponse = responseBody;
        int? input = null, output = null, reasoning = null; string? model = null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody ?? "{}"); var root = doc.RootElement;
            if (root.TryGetProperty("model", out var m)) model = m.GetString();
            if (root.TryGetProperty("usage", out var u))
            {
                if (u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out int n)) input = n;
                if (u.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out int k)) output = k;
                if (u.TryGetProperty("completion_tokens_details", out var details) && details.TryGetProperty("reasoning_tokens", out var r) && r.TryGetInt32(out int j)) reasoning = j;
            }
        }
        catch (JsonException) { }
        decimal? estimate = input >= 0 && output >= 0 ? (input.Value * InputUsdPerMillion + output.Value * OutputUsdPerMillion) / 1_000_000m : null;
        // Unknown usage retains its full reservation. An anomalous usage over cap stops further requests.
        ChargedOrReservedUsd += (estimate ?? reservation) - reservation;
        if (output > MaxOutputTokens || ChargedOrReservedUsd >= MaxUsd) Stopped = true;
        var receipt = new { index = Calls, status, model, inputTokens = input, outputTokensIncludingReasoning = output, reasoningTokens = reasoning,
            latencyMs, estimatedCostUsd = estimate, budgetChargedUsd = estimate ?? reservation, remainingUsd = MaxUsd - ChargedOrReservedUsd,
            transportError, syntheticRawResponse = responseBody };
        Receipts.Add(receipt); Write(receipt);
    }
    public void Dispose() => ledger.Dispose();
}

public sealed class BudgetedFoundryHandler(FoundryEvaluationBudget budget) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = await request.Content!.ReadAsStringAsync(ct);
        using var parsed = JsonDocument.Parse(body); string source = parsed.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        decimal reservation = budget.Reserve(body, source); var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, ct);
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, ct);
            string result = await response.Content.ReadAsStringAsync(ct);
            budget.Complete(reservation, (int)response.StatusCode, result, watch.Elapsed.TotalMilliseconds, null);
            return response;
        }
        catch (Exception ex) { budget.Complete(reservation, null, null, watch.Elapsed.TotalMilliseconds, ex.GetType().Name); throw; }
    }
}
