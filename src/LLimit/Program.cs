using LLimit;

var builder = WebApplication.CreateBuilder(args);

// Config from env vars
builder.Configuration.AddEnvironmentVariables();

// Services
builder.Services.AddSingleton(sp =>
{
    var dbPath = builder.Configuration["LLIMIT_DB_PATH"] ?? "llimit.db";
    return new Store(dbPath);
});
builder.Services.AddSingleton<AuthCache>();
builder.Services.AddSingleton<BudgetCache>();
builder.Services.AddSingleton<PricingCache>();
builder.Services.AddHttpClient("Azure", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient(); // default client for LiteLLM fetch

var app = builder.Build();

// ── Startup: load caches ──
var store = app.Services.GetRequiredService<Store>();
var authCache = app.Services.GetRequiredService<AuthCache>();
var budgetCache = app.Services.GetRequiredService<BudgetCache>();
var pricingCache = app.Services.GetRequiredService<PricingCache>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Load projects into auth cache
var allProjects = store.GetAllProjects();
authCache.Reload(allProjects);
logger.LogInformation("Loaded {Count} projects into auth cache", allProjects.Count);

// Load budget cache from DB
budgetCache.LoadFromStore(store);
logger.LogInformation("Budget cache loaded from DB");

// Load pricing from LiteLLM
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
try
{
    using var httpClient = httpFactory.CreateClient();
    pricingCache.LoadFromLiteLlmAsync(httpClient, store).GetAwaiter().GetResult();
    logger.LogInformation("Pricing loaded from LiteLLM");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Failed to load pricing from LiteLLM — startup aborted");
    throw;
}

// ── Background tasks ──
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var shutdownToken = lifetime.ApplicationStopping;

// Periodic LiteLLM pricing refresh (every 6 hours)
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
    while (await timer.WaitForNextTickAsync(shutdownToken))
    {
        try
        {
            using var http = httpFactory.CreateClient();
            await pricingCache.LoadFromLiteLlmAsync(http, store);
            logger.LogInformation("Pricing refreshed from LiteLLM");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref Diagnostics.PricingRefreshFailures);
            logger.LogError(ex, "Failed to refresh LiteLLM pricing");
        }
    }
}, shutdownToken);

// Periodic budget cache reconciliation (every 5 minutes)
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
    while (await timer.WaitForNextTickAsync(shutdownToken))
    {
        try
        {
            budgetCache.LoadFromStore(store);
            logger.LogDebug("Budget cache reconciled from DB");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to reconcile budget cache");
        }
    }
}, shutdownToken);

// ── Health ──
app.MapGet("/health", (Store s) =>
{
    var dbOk = s.CheckDb();
    var unknownCount = Diagnostics.UnknownModels.Count;
    var asyncFails = Diagnostics.AsyncFailures;
    var status = dbOk && unknownCount == 0 && asyncFails <= 10 ? "ok" : "degraded";

    return Results.Ok(new
    {
        status,
        db = dbOk ? "ok" : "error",
        unknown_models = unknownCount,
        async_failures = asyncFails
    });
});

// ── Routes ──
app.MapProxy();
app.MapAdmin();
app.MapDashboard();

// ── Graceful shutdown ──
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Shutting down — flushing pending operations");
    // Give in-flight Task.Run operations time to complete
    Thread.Sleep(2000);
});

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
