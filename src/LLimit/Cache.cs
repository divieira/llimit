using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLimit;

// ── Auth Cache ──

public class AuthCache
{
    private volatile Dictionary<string, Project> _byHash = new();

    public Project? Resolve(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        return _byHash.TryGetValue(hash, out var p) && p.IsActive ? p : null;
    }

    public Project? ResolveIncludingInactive(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        return _byHash.GetValueOrDefault(hash);
    }

    public void Reload(IEnumerable<Project> all)
        => _byHash = all.ToDictionary(p => p.ApiKeyHash);
}

// ── Budget check result ──

public record BudgetDeny(string Scope, double Budget, double Used)
{
    public string Error { get; } = "budget_exceeded";
}

// ── Pricing Cache (LiteLLM + admin overrides) ──

public record ModelPrice(double InputPerToken, double OutputPerToken);

public class PricingCache
{
    private volatile Dictionary<string, ModelPrice> _prices = new();
    private readonly ILogger<PricingCache> _logger;
    private DateTime _lastRefreshed;

    public DateTime LastRefreshed => _lastRefreshed;

    public PricingCache(ILogger<PricingCache> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns (cost, found: true) if the model has pricing, or (0, found: false) if unknown.
    /// </summary>
    public (double cost, bool found) Calculate(string model, int promptTokens, int completionTokens)
    {
        var prices = _prices;
        if (prices.TryGetValue(model, out var price))
            return (promptTokens * price.InputPerToken + completionTokens * price.OutputPerToken, true);

        Interlocked.Increment(ref Diagnostics.UnknownModelHits);
        Diagnostics.UnknownModels.AddOrUpdate(model, 1, (_, v) => v + 1);
        _logger.LogWarning("Unknown model {Model} — no pricing found", model);
        return (0, false);
    }

    public async Task LoadFromLiteLlmAsync(HttpClient http, Store store)
    {
        var dict = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);

        // Fetch LiteLLM pricing
        var json = await http.GetStringAsync(
            "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");

        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("litellm_provider", out var provider)) continue;
            if (provider.GetString() != "azure") continue;

            if (!prop.Value.TryGetProperty("input_cost_per_token", out var inputCost)) continue;
            if (!prop.Value.TryGetProperty("output_cost_per_token", out var outputCost)) continue;

            // Strip "azure/" prefix
            var modelName = prop.Name;
            if (modelName.StartsWith("azure/", StringComparison.OrdinalIgnoreCase))
                modelName = modelName[6..];

            dict[modelName] = new ModelPrice(inputCost.GetDouble(), outputCost.GetDouble());
        }

        _logger.LogInformation("Loaded {Count} Azure model prices from LiteLLM", dict.Count);

        // Admin overrides win
        var overrides = store.GetAllPricing();
        foreach (var o in overrides)
        {
            dict[o.ModelPattern] = new ModelPrice(o.InputPerMillion / 1_000_000, o.OutputPerMillion / 1_000_000);
        }

        if (overrides.Count > 0)
            _logger.LogInformation("Applied {Count} admin pricing overrides", overrides.Count);

        _prices = dict;
        _lastRefreshed = DateTime.UtcNow;
    }

    public void ApplyAdminOverrides(Store store)
    {
        var dict = new Dictionary<string, ModelPrice>(_prices, StringComparer.OrdinalIgnoreCase);
        var overrides = store.GetAllPricing();
        foreach (var o in overrides)
        {
            dict[o.ModelPattern] = new ModelPrice(o.InputPerMillion / 1_000_000, o.OutputPerMillion / 1_000_000);
        }
        _prices = dict;
    }

    public Dictionary<string, ModelPrice> GetAllPrices() => new(_prices);
}

// ── Diagnostics ──

public static class Diagnostics
{
    public static readonly ConcurrentDictionary<string, int> UnknownModels = new();
    public static long UnknownModelHits;
    public static long AsyncFailures;
    public static long PricingRefreshFailures;
    public static readonly DateTime StartedAt = DateTime.UtcNow;
}
