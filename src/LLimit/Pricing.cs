using System.Collections.Concurrent;
using System.Text.Json;

namespace LLimit;

public record ModelPrice(double InputPerToken, double OutputPerToken);

public class PricingTable
{
    private volatile Dictionary<string, ModelPrice> _prices = new();
    private readonly ILogger<PricingTable> _logger;
    private DateTime _lastRefreshed;

    public DateTime LastRefreshed => _lastRefreshed;

    public PricingTable(ILogger<PricingTable> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the cost for the given model and token counts, or null if the model is unknown.
    /// </summary>
    public double? Calculate(string model, int promptTokens, int completionTokens)
    {
        var prices = _prices;
        if (prices.TryGetValue(model, out var price))
            return promptTokens * price.InputPerToken + completionTokens * price.OutputPerToken;

        Interlocked.Increment(ref Diagnostics.UnknownModelHits);
        Diagnostics.UnknownModels.AddOrUpdate(model, 1, (_, v) => v + 1);
        _logger.LogWarning("Unknown model {Model} — no pricing found", model);
        return null;
    }

    /// <summary>
    /// Returns true if the given model has pricing data.
    /// </summary>
    public bool HasPricing(string model) => _prices.ContainsKey(model);

    /// <summary>
    /// Fetches pricing from LiteLLM, applies admin overrides, and saves to DB for fallback.
    /// </summary>
    public async Task LoadFromLiteLlmAsync(HttpClient http, Store store)
    {
        // Fetch pricing from LiteLLM's public model pricing JSON.
        // See: https://github.com/BerriAI/litellm
        var json = await http.GetStringAsync(
            "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");

        var litellmPrices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("litellm_provider", out var provider)) continue;
            if (provider.GetString() != "azure") continue;

            if (!prop.Value.TryGetProperty("input_cost_per_token", out var inputCost)) continue;
            if (!prop.Value.TryGetProperty("output_cost_per_token", out var outputCost)) continue;

            // Strip "azure/" prefix to match the model names Azure returns in responses
            var modelName = prop.Name;
            if (modelName.StartsWith("azure/", StringComparison.OrdinalIgnoreCase))
                modelName = modelName[6..];

            litellmPrices[modelName] = new ModelPrice(inputCost.GetDouble(), outputCost.GetDouble());
        }

        _logger.LogInformation("Loaded {Count} Azure model prices from LiteLLM", litellmPrices.Count);

        // Save to DB so we have a fallback if LiteLLM is unreachable next time
        store.SaveLiteLlmPrices(litellmPrices);

        // Admin overrides win
        ApplyOverridesAndSwap(litellmPrices, store);
    }

    /// <summary>
    /// Loads cached LiteLLM prices from DB (fallback when online fetch fails).
    /// </summary>
    public void LoadFromDbFallback(Store store)
    {
        var cached = store.LoadLiteLlmPrices();
        if (cached.Count == 0)
            throw new InvalidOperationException("No cached LiteLLM prices in DB — cannot start without pricing data");

        _logger.LogWarning("Using {Count} cached LiteLLM prices from DB (online fetch failed)", cached.Count);
        ApplyOverridesAndSwap(cached, store);
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

    private void ApplyOverridesAndSwap(Dictionary<string, ModelPrice> basePrices, Store store)
    {
        var overrides = store.GetAllPricing();
        foreach (var o in overrides)
        {
            basePrices[o.ModelPattern] = new ModelPrice(o.InputPerMillion / 1_000_000, o.OutputPerMillion / 1_000_000);
        }

        if (overrides.Count > 0)
            _logger.LogInformation("Applied {Count} admin pricing overrides", overrides.Count);

        _prices = basePrices;
        _lastRefreshed = DateTime.UtcNow;
    }
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
