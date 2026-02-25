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
// Named "Azure" client for Azure OpenAI with a 5-minute timeout for long-running completions.
// See: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines
builder.Services.AddHttpClient("Azure", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});
// Named "Foundry" client for Azure AI Foundry (Claude and other models).
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

// Validate backends — each must be fully configured (both endpoint and key) when either is set,
// and at least one backend must be configured.
var azureEndpoint = builder.Configuration["AZURE_OPENAI_ENDPOINT"];
var azureApiKey   = builder.Configuration["AZURE_OPENAI_API_KEY"];
var foundryEndpoint = builder.Configuration["AZURE_FOUNDRY_ENDPOINT"];
var foundryApiKey   = builder.Configuration["AZURE_FOUNDRY_API_KEY"];

if ((azureEndpoint is null) != (azureApiKey is null))
    throw new InvalidOperationException(
        "AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY must both be set or both be absent");
if ((foundryEndpoint is null) != (foundryApiKey is null))
    throw new InvalidOperationException(
        "AZURE_FOUNDRY_ENDPOINT and AZURE_FOUNDRY_API_KEY must both be set or both be absent");

var azureConfigured   = azureEndpoint   is not null;
var foundryConfigured = foundryEndpoint is not null;

if (!azureConfigured && !foundryConfigured)
    throw new InvalidOperationException(
        "At least one backend must be configured: " +
        "set AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY for Azure OpenAI, " +
        "or AZURE_FOUNDRY_ENDPOINT + AZURE_FOUNDRY_API_KEY for Azure AI Foundry, or both");

if (azureConfigured)
    logger.LogInformation("Azure OpenAI proxy enabled: {Endpoint}", azureEndpoint);
if (foundryConfigured)
    logger.LogInformation("Azure AI Foundry proxy enabled: {Endpoint}", foundryEndpoint);

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
if (azureConfigured)   app.MapAzureProxy();
if (foundryConfigured) app.MapFoundryProxy();
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
