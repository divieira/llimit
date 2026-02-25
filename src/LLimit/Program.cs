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
// Named "Foundry" client with a 5-minute timeout for long-running LLM completions.
// See: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
builder.Services.AddHttpClient("Foundry", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient(); // default client for LiteLLM fetch

var app = builder.Build();

// ── Startup: validate config ──
var store = app.Services.GetRequiredService<Store>();
var pricingTable = app.Services.GetRequiredService<PricingTable>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Validate required Foundry configuration — fail fast on startup
var foundryEndpoint = builder.Configuration["AZURE_FOUNDRY_ENDPOINT"]
    ?? throw new InvalidOperationException("AZURE_FOUNDRY_ENDPOINT environment variable is required");
var foundryApiKey = builder.Configuration["AZURE_FOUNDRY_API_KEY"]
    ?? throw new InvalidOperationException("AZURE_FOUNDRY_API_KEY environment variable is required");
logger.LogInformation("Azure AI Foundry endpoint configured: {Endpoint}", foundryEndpoint);

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
