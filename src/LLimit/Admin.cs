using System.Security.Cryptography;
using System.Text;

namespace LLimit;

public static class AdminRoutes
{
    public static void MapAdmin(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1").AddEndpointFilter(AdminAuthFilter);

        // Projects
        admin.MapGet("/projects", ListProjects);
        admin.MapPost("/projects", CreateProject);
        admin.MapGet("/projects/{id}", GetProject);
        admin.MapPut("/projects/{id}", UpdateProject);
        admin.MapDelete("/projects/{id}", DeleteProject);

        // Users
        admin.MapGet("/projects/{id}/users", GetProjectUsers);

        // Usage & Logs
        admin.MapGet("/projects/{id}/usage", GetUsage);
        admin.MapGet("/projects/{id}/logs", GetLogs);

        // Pricing
        admin.MapGet("/pricing", GetPricing);
        admin.MapPut("/pricing/{modelPattern}", UpsertPricing);
        admin.MapDelete("/pricing/{modelPattern}", DeletePricing);

        // Diagnostics
        admin.MapGet("/diagnostics", GetDiagnostics);
    }

    private static async ValueTask<object?> AdminAuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var adminToken = cfg["LLIMIT_ADMIN_TOKEN"];
        if (string.IsNullOrEmpty(adminToken))
        {
            ctx.HttpContext.Response.StatusCode = 500;
            return new { error = "admin token not configured" };
        }

        var auth = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (auth is null || !auth.StartsWith("Bearer ") ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(auth[7..]),
                Encoding.UTF8.GetBytes(adminToken)))
        {
            ctx.HttpContext.Response.StatusCode = 401;
            return new { error = "unauthorized" };
        }

        return await next(ctx);
    }

    // ── Projects ──

    private static IResult ListProjects(Store store)
    {
        var projects = store.GetAllProjects();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayCosts = store.GetAllProjectCostsForDate(today);
        var result = projects.Select(p => new
        {
            p.Id, p.Name, p.IsActive,
            p.EndpointUrl,
            EndpointKeyConfigured = !string.IsNullOrEmpty(p.EndpointKey),
            p.BudgetDaily, p.DefaultUserBudgetDaily,
            p.AllowUserKeys,
            UsageToday = todayCosts.GetValueOrDefault(p.Id)
        });
        return Results.Ok(result);
    }

    private static IResult CreateProject(CreateProjectRequest req, Store store)
    {
        if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name))
            return Results.BadRequest(new { error = "id and name are required" });
        if (string.IsNullOrWhiteSpace(req.EndpointUrl) || string.IsNullOrWhiteSpace(req.EndpointKey))
            return Results.BadRequest(new { error = "endpointUrl and endpointKey are required" });

        try
        {
            var (project, plainKey) = store.CreateProject(req.Id, req.Name,
                req.EndpointUrl, req.EndpointKey,
                req.BudgetDaily, req.DefaultUserBudgetDaily,
                req.AllowUserKeys ?? false);

            return Results.Created($"/api/v1/projects/{project.Id}", new
            {
                project.Id, project.Name, ApiKey = plainKey,
                project.EndpointUrl,
                EndpointKeyConfigured = true,
                project.BudgetDaily, project.DefaultUserBudgetDaily,
                project.AllowUserKeys
            });
        }
        catch (Exception ex) when (ex.Message.Contains("UNIQUE"))
        {
            return Results.Conflict(new { error = "project id or api key already exists" });
        }
    }

    private static IResult GetProject(string id, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todayCost = store.GetProjectCostForDate(id, today);

        return Results.Ok(new
        {
            project.Id, project.Name, project.IsActive,
            project.EndpointUrl,
            EndpointKeyConfigured = !string.IsNullOrEmpty(project.EndpointKey),
            project.BudgetDaily, project.DefaultUserBudgetDaily,
            project.AllowUserKeys,
            project.CreatedAt, project.UpdatedAt,
            UsageToday = todayCost
        });
    }

    private static IResult UpdateProject(string id, UpdateProjectRequest req, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        store.UpdateProject(id, req.Name, req.BudgetDaily, req.DefaultUserBudgetDaily, req.IsActive,
            allowUserKeys: req.AllowUserKeys,
            endpointUrl: req.EndpointUrl, endpointKey: req.EndpointKey);
        return Results.Ok(store.GetProject(id));
    }

    private static IResult DeleteProject(string id, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        store.DeactivateProject(id);
        return Results.Ok(new { message = "project deactivated" });
    }

    // ── Users ──

    private static IResult GetProjectUsers(string id, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        var users = store.GetProjectUsers(id);
        return Results.Ok(users.Select(u => new { u.UserId, u.TodayCost }));
    }

    // ── Usage & Logs ──

    private static IResult GetUsage(string id, string? period, string? from, string? to, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        var fromDate = from is not null && DateOnly.TryParse(from, out var fd) ? fd : (DateOnly?)null;
        var toDate = to is not null && DateOnly.TryParse(to, out var td) ? td : (DateOnly?)null;
        var usage = store.GetUsage(id, fromDate, toDate);
        return Results.Ok(usage);
    }

    private static IResult GetLogs(string id, string? user, string? model, int? page, int? per_page, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.NotFound(new { error = "project not found" });

        var logs = store.GetLogs(id, user, model, page ?? 1, per_page ?? 50);
        return Results.Ok(logs);
    }

    // ── Pricing ──

    private static IResult GetPricing(Store store, PricingTable pricing)
    {
        var adminOverrides = store.GetAllPricing();
        var adminSet = new HashSet<string>(adminOverrides.Select(o => o.ModelPattern), StringComparer.OrdinalIgnoreCase);
        var allPrices = pricing.GetAllPrices();

        var result = allPrices.Select(kv => new
        {
            Model = kv.Key,
            InputPerMillion = kv.Value.InputPerToken * 1_000_000,
            OutputPerMillion = kv.Value.OutputPerToken * 1_000_000,
            Source = adminSet.Contains(kv.Key) ? "admin" : "litellm"
        }).OrderBy(x => x.Model);

        return Results.Ok(result);
    }

    private static IResult UpsertPricing(string modelPattern, UpsertPricingRequest req, Store store, PricingTable pricing)
    {
        store.UpsertPricing(modelPattern, req.InputPerMillion, req.OutputPerMillion);
        pricing.ApplyAdminOverrides(store);
        return Results.Ok(new { message = "pricing updated", modelPattern });
    }

    private static async Task<IResult> DeletePricing(string modelPattern, Store store, PricingTable pricing, IHttpClientFactory factory)
    {
        store.DeletePricing(modelPattern);
        // Reload full pricing to remove the override and revert to LiteLLM
        try
        {
            using var http = factory.CreateClient();
            await pricing.LoadFromLiteLlmAsync(http, store);
        }
        catch (Exception)
        {
            // LiteLLM fetch failed — at least re-apply overrides from DB to remove the deleted one
            pricing.ApplyAdminOverrides(store);
        }
        return Results.Ok(new { message = "pricing override removed, reverting to LiteLLM", modelPattern });
    }

    // ── Diagnostics ──

    private static IResult GetDiagnostics(PricingTable pricing)
    {
        return Results.Ok(new
        {
            unknown_models = Diagnostics.UnknownModels.ToDictionary(kv => kv.Key, kv => kv.Value),
            unknown_model_hits = Diagnostics.UnknownModelHits,
            async_failures = Diagnostics.AsyncFailures,
            pricing_refresh_failures = Diagnostics.PricingRefreshFailures,
            pricing_last_refreshed = pricing.LastRefreshed.ToString("o"),
            pricing_source = "litellm",
            uptime_seconds = (long)(DateTime.UtcNow - Diagnostics.StartedAt).TotalSeconds
        });
    }
}

// ── Request DTOs ──

public record CreateProjectRequest(string Id, string Name,
    string EndpointUrl, string EndpointKey,
    double? BudgetDaily = null, double? DefaultUserBudgetDaily = null,
    bool? AllowUserKeys = null);

public record UpdateProjectRequest(string? Name = null,
    double? BudgetDaily = null, double? DefaultUserBudgetDaily = null,
    bool? IsActive = null, bool? AllowUserKeys = null,
    string? EndpointUrl = null, string? EndpointKey = null);

public record UpsertPricingRequest(double InputPerMillion, double OutputPerMillion);
