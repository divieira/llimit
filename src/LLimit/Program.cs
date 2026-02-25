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
builder.Services.AddSingleton<PricingTable>();
// Named "Azure" client with a 5-minute timeout for long-running LLM completions.
// See: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
builder.Services.AddHttpClient("Azure", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient(); // default client for LiteLLM fetch + OAuth

var app = builder.Build();

// ── Startup: validate config ──
var store = app.Services.GetRequiredService<Store>();
var pricingTable = app.Services.GetRequiredService<PricingTable>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Admin token is required for the admin API and dashboard
var adminToken = builder.Configuration["LLIMIT_ADMIN_TOKEN"]
    ?? throw new InvalidOperationException("LLIMIT_ADMIN_TOKEN environment variable is required");
logger.LogInformation("Admin token configured");

// OAuth config is optional — portal features require it
var oauthConfigured = !string.IsNullOrEmpty(builder.Configuration["AZURE_AD_CLIENT_ID"])
    && !string.IsNullOrEmpty(builder.Configuration["AZURE_AD_TENANT_ID"])
    && !string.IsNullOrEmpty(builder.Configuration["AZURE_AD_CLIENT_SECRET"]);
if (oauthConfigured)
    logger.LogInformation("Azure AD OAuth configured (tenant: {Tenant})", builder.Configuration["AZURE_AD_TENANT_ID"]);
else
    logger.LogWarning("Azure AD OAuth not configured — user portal will be unavailable");

// Load pricing: try LiteLLM online, fall back to DB-cached prices.
// On success, prices are saved to DB for next startup's fallback.
var httpFactory = app.Services.GetRequiredService<IHttpClientFactory>();
try
{
    using var httpClient = httpFactory.CreateClient();
    pricingTable.LoadFromLiteLlmAsync(httpClient, store).GetAwaiter().GetResult();
    logger.LogInformation("Pricing loaded from LiteLLM");
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to load pricing from LiteLLM — falling back to DB cache");
    // Falls back to DB-cached prices; throws if no cache exists (first run with no network)
    pricingTable.LoadFromDbFallback(store);
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
            await pricingTable.LoadFromLiteLlmAsync(http, store);
            logger.LogInformation("Pricing refreshed from LiteLLM");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref Diagnostics.PricingRefreshFailures);
            logger.LogError(ex, "Failed to refresh LiteLLM pricing");
        }
    }
}, shutdownToken);

// Periodic expired session cleanup (every 1 hour)
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
    while (await timer.WaitForNextTickAsync(shutdownToken))
    {
        try
        {
            store.CleanExpiredSessions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to clean expired sessions");
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
app.MapOAuth();

// ── Graceful shutdown ──
// The cancellation token stops background timers, and Kestrel's shutdown timeout
// (default 30s, configurable via ASPNETCORE_SHUTDOWNTIMEOUT) drains active connections.
// See: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/generic-host#shutdowntimeout
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Shutting down — waiting for in-flight requests to drain");
});

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
