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
    private volatile ConcurrentDictionary<(string Pid, DateOnly Date), double> _project = new();
    private volatile ConcurrentDictionary<(string Pid, string Uid, DateOnly Date), double> _user = new();

    public void Add(string pid, string uid, double cost)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow);
        _project.AddOrUpdate((pid, d), cost, (_, v) => v + cost);
        _user.AddOrUpdate((pid, uid, d), cost, (_, v) => v + cost);
    }

    public BudgetDeny? CheckAll(Project proj, string uid)
    {
        return CheckPeriod("daily", proj.Id, uid, proj.BudgetDaily, proj.DefaultUserBudgetDaily, GetDayRange)
            ?? CheckPeriod("weekly", proj.Id, uid, proj.BudgetWeekly, proj.DefaultUserBudgetWeekly, GetWeekRange)
            ?? CheckPeriod("monthly", proj.Id, uid, proj.BudgetMonthly, proj.DefaultUserBudgetMonthly, GetMonthRange);
    }

    private BudgetDeny? CheckPeriod(string period, string pid, string uid,
        double? projectLimit, double? userLimit, Func<(DateOnly from, DateOnly to)> rangeFunc)
    {
        if (projectLimit is { } pl)
        {
            var used = SumProjectCost(pid, rangeFunc());
            if (used >= pl) return new BudgetDeny(period, "project", pl, used);
        }
        if (userLimit is { } ul && uid != "_anonymous")
        {
            var used = SumUserCost(pid, uid, rangeFunc());
            if (used >= ul) return new BudgetDeny(period, "user", ul, used);
        }
        return null;
    }

    private double SumProjectCost(string pid, (DateOnly from, DateOnly to) range)
    {
        var proj = _project;
        double total = 0;
        for (var d = range.from; d <= range.to; d = d.AddDays(1))
            total += proj.GetValueOrDefault((pid, d));
        return total;
    }

    private double SumUserCost(string pid, string uid, (DateOnly from, DateOnly to) range)
    {
        var user = _user;
        double total = 0;
        for (var d = range.from; d <= range.to; d = d.AddDays(1))
            total += user.GetValueOrDefault((pid, uid, d));
        return total;
    }

    public void LoadFromStore(Store store)
    {
        // Build new dictionaries, then swap atomically — no race with concurrent Add() calls
        var newProject = new ConcurrentDictionary<(string Pid, DateOnly Date), double>();
        var newUser = new ConcurrentDictionary<(string Pid, string Uid, DateOnly Date), double>();

        // Load current month's data (covers daily, weekly, and monthly checks)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var projects = store.GetAllProjects();
        foreach (var proj in projects)
        {
            var usage = store.GetUsage(proj.Id, monthStart.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
            foreach (var u in usage)
            {
                var d = DateOnly.ParseExact(u.Date, "yyyy-MM-dd");
                newProject.AddOrUpdate((proj.Id, d), u.TotalCost, (_, v) => v + u.TotalCost);
                newUser.AddOrUpdate((proj.Id, u.UserId, d), u.TotalCost, (_, v) => v + u.TotalCost);
            }
        }

        _project = newProject;
        _user = newUser;
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

    public (double cost, bool fallback) Calculate(string model, int promptTokens, int completionTokens)
    {
        if (TryResolve(model, out var price))
            return (promptTokens * price.InputPerToken + completionTokens * price.OutputPerToken, false);

        Interlocked.Increment(ref Diagnostics.UnknownModelHits);
        Diagnostics.UnknownModels.AddOrUpdate(model, 1, (_, v) => v + 1);
        _logger.LogWarning("Unknown model {Model} — cost will be $0, budget blind", model);
        return (0, true);
    }

    private bool TryResolve(string model, out ModelPrice price)
    {
        var prices = _prices;

        // 1. Exact match
        if (prices.TryGetValue(model, out price!)) return true;

        // 2. Azure naming quirk: gpt-35-turbo → gpt-3.5-turbo
        var normalized = model.Replace("gpt-35-turbo", "gpt-3.5-turbo");
        if (normalized != model && prices.TryGetValue(normalized, out price!)) return true;

        // 3. Prefix match: gpt-4o-2024-08-06 → gpt-4o
        // Try progressively shorter prefixes by removing trailing segments after last '-'
        var candidate = model;
        while (true)
        {
            var lastDash = candidate.LastIndexOf('-');
            if (lastDash <= 0) break;
            candidate = candidate[..lastDash];
            if (prices.TryGetValue(candidate, out price!)) return true;
        }

        // Also try normalized version prefix match
        candidate = normalized;
        while (true)
        {
            var lastDash = candidate.LastIndexOf('-');
            if (lastDash <= 0) break;
            candidate = candidate[..lastDash];
            if (prices.TryGetValue(candidate, out price!)) return true;
        }

        price = default!;
        return false;
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
