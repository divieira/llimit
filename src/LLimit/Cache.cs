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

// ── Budget Cache (separate dicts, tuple keys) ──

public record BudgetDeny(string Limit, string Scope, double Budget, double Used)
{
    public string Error { get; } = "budget_exceeded";
}

public class BudgetCache
{
    // Live counters — incremented on every request, atomically swapped on reconciliation
    private volatile ConcurrentDictionary<(string Pid, DateOnly Date), double> _projectCost = new();
    private volatile ConcurrentDictionary<(string Pid, string Uid, DateOnly Date), double> _userCost = new();

    public void Add(string pid, string? uid, double cost)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow);
        _projectCost.AddOrUpdate((pid, d), cost, (_, v) => v + cost);
        if (uid is not null)
            _userCost.AddOrUpdate((pid, uid, d), cost, (_, v) => v + cost);
    }

    public BudgetDeny? CheckAll(Project proj, string? uid)
    {
        return CheckPeriod("daily", proj.Id, uid, proj.BudgetDaily, proj.DefaultUserBudgetDaily, GetDayRange)
            ?? CheckPeriod("weekly", proj.Id, uid, proj.BudgetWeekly, proj.DefaultUserBudgetWeekly, GetWeekRange)
            ?? CheckPeriod("monthly", proj.Id, uid, proj.BudgetMonthly, proj.DefaultUserBudgetMonthly, GetMonthRange);
    }

    private BudgetDeny? CheckPeriod(string period, string pid, string? uid,
        double? projectLimit, double? userLimit, Func<(DateOnly from, DateOnly to)> rangeFunc)
    {
        if (projectLimit is { } pl)
        {
            var used = SumProjectCost(pid, rangeFunc());
            if (used >= pl) return new BudgetDeny(period, "project", pl, used);
        }
        if (userLimit is { } ul && uid is not null)
        {
            var used = SumUserCost(pid, uid, rangeFunc());
            if (used >= ul) return new BudgetDeny(period, "user", ul, used);
        }
        return null;
    }

    private double SumProjectCost(string pid, (DateOnly from, DateOnly to) range)
    {
        var costs = _projectCost;
        double total = 0;
        for (var d = range.from; d <= range.to; d = d.AddDays(1))
            total += costs.GetValueOrDefault((pid, d));
        return total;
    }

    private double SumUserCost(string pid, string uid, (DateOnly from, DateOnly to) range)
    {
        var costs = _userCost;
        double total = 0;
        for (var d = range.from; d <= range.to; d = d.AddDays(1))
            total += costs.GetValueOrDefault((pid, uid, d));
        return total;
    }

    public void LoadFromStore(Store store)
    {
        // Build new dictionaries, then swap atomically — no race with concurrent Add() calls
        var newProjectCost = new ConcurrentDictionary<(string Pid, DateOnly Date), double>();
        var newUserCost = new ConcurrentDictionary<(string Pid, string Uid, DateOnly Date), double>();

        // Load data from the earliest possible budget window start (month start or week start,
        // whichever is earlier) to cover daily, weekly, and monthly checks
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var startDate = weekStart < monthStart ? weekStart : monthStart;

        var projects = store.GetAllProjects();
        foreach (var proj in projects)
        {
            var usage = store.GetUsage(proj.Id, startDate.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
            foreach (var u in usage)
            {
                var d = DateOnly.ParseExact(u.Date, "yyyy-MM-dd");
                newProjectCost.AddOrUpdate((proj.Id, d), u.TotalCost, (_, v) => v + u.TotalCost);
                newUserCost.AddOrUpdate((proj.Id, u.UserId, d), u.TotalCost, (_, v) => v + u.TotalCost);
            }
        }

        _projectCost = newProjectCost;
        _userCost = newUserCost;
    }

    private static (DateOnly from, DateOnly to) GetDayRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (today, today);
    }

    private static (DateOnly from, DateOnly to) GetWeekRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-(int)today.DayOfWeek);
        return (from, today);
    }

    private static (DateOnly from, DateOnly to) GetMonthRange()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = new DateOnly(today.Year, today.Month, 1);
        return (from, today);
    }
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
    /// Callers should decide how to handle unknown models (e.g. reject the request).
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
