using System.Net;

namespace LLimit;

public static class DashboardRoutes
{
    public static void MapDashboard(this WebApplication app)
    {
        var dash = app.MapGroup("/dashboard");

        // Public routes (no auth)
        dash.MapGet("/login", LoginPage);
        dash.MapPost("/login", HandleLogin);

        // Protected routes
        var authed = dash.AddEndpointFilter(DashboardAuthFilter);
        authed.MapGet("/", Overview);
        authed.MapGet("/projects/{id}", ProjectDetail);
        authed.MapGet("/projects/{id}/logs", LogViewer);
        authed.MapGet("/projects/{id}/settings", ProjectSettings);
        authed.MapPost("/projects/{id}/settings", SaveProjectSettings);
        authed.MapGet("/pricing", PricingPage);
    }

    // ── Auth Filter ──

    private static async ValueTask<object?> DashboardAuthFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var adminToken = cfg["LLIMIT_ADMIN_TOKEN"];
        if (string.IsNullOrEmpty(adminToken))
        {
            ctx.HttpContext.Response.StatusCode = 500;
            return Results.Content("<h1>Admin token not configured</h1>", "text/html");
        }

        var cookie = ctx.HttpContext.Request.Cookies["llimit_session"];
        if (cookie != adminToken)
        {
            ctx.HttpContext.Response.Redirect("/dashboard/login");
            return Results.Empty;
        }

        return await next(ctx);
    }

    // ── Login ──

    private static IResult LoginPage(HttpContext ctx)
    {
        var error = ctx.Request.Query.ContainsKey("error") ? "<p style=\"color:var(--pico-color-red-500);\">Invalid token. Please try again.</p>" : "";
        var html = Layout("Login", $@"
<main class=""container"">
  <article style=""max-width:400px;margin:60px auto;"">
    <header><h2>LLimit Dashboard</h2></header>
    {error}
    <form method=""post"" action=""/dashboard/login"">
      <label for=""token"">Admin Token</label>
      <input type=""password"" id=""token"" name=""token"" placeholder=""Enter admin token"" required>
      <button type=""submit"">Login</button>
    </form>
  </article>
</main>", includeNav: false);
        return Results.Content(html, "text/html");
    }

    private static IResult HandleLogin(HttpContext ctx, IConfiguration cfg)
    {
        var adminToken = cfg["LLIMIT_ADMIN_TOKEN"];
        var token = ctx.Request.Form["token"].FirstOrDefault();

        if (string.IsNullOrEmpty(adminToken) || token != adminToken)
            return Results.Redirect("/dashboard/login?error=1");

        ctx.Response.Cookies.Append("llimit_session", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });
        return Results.Redirect("/dashboard/");
    }

    // ── Overview ──

    private static IResult Overview(Store store, PricingCache pricingCache)
    {
        var projects = store.GetAllProjects();
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        // Projects table rows
        var projectRows = "";
        foreach (var p in projects)
        {
            var todayCost = store.GetProjectCostForPeriod(p.Id, today, today);
            var budgetBar = BuildBudgetBar(todayCost, p.BudgetDaily);
            var statusBadge = p.IsActive
                ? "<span style=\"color:green;\">Active</span>"
                : "<span style=\"color:red;\">Inactive</span>";

            projectRows += $@"
<tr>
  <td><a href=""/dashboard/projects/{Enc(p.Id)}"">{Enc(p.Name)}</a></td>
  <td><code>{Enc(p.Id)}</code></td>
  <td>{statusBadge}</td>
  <td>${todayCost:F4}</td>
  <td>{Fmt(p.BudgetDaily)}</td>
  <td>{Fmt(p.BudgetWeekly)}</td>
  <td>{Fmt(p.BudgetMonthly)}</td>
  <td style=""min-width:120px;"">{budgetBar}</td>
</tr>";
        }

        // Pricing health
        var prices = pricingCache.GetAllPrices();
        var unknownCount = Diagnostics.UnknownModels.Count;
        var unknownHits = Diagnostics.UnknownModelHits;
        var pricingRefreshFailures = Diagnostics.PricingRefreshFailures;
        var lastRefreshed = pricingCache.LastRefreshed;

        var pricingHealthColor = unknownCount == 0 ? "green" : "orange";
        var pricingHealthText = unknownCount == 0 ? "Healthy" : $"{unknownCount} unknown model(s)";

        // Latency summary from recent logs
        var latencySummary = "";
        foreach (var p in projects.Take(5))
        {
            var logs = store.GetLogs(p.Id, page: 1, perPage: 100);
            if (logs.Count == 0) continue;
            var avgTotal = logs.Average(l => l.TotalMs);
            var avgOverhead = logs.Average(l => l.OverheadMs);
            var avgUpstream = logs.Average(l => l.UpstreamMs);
            latencySummary += $@"
<tr>
  <td>{Enc(p.Name)}</td>
  <td>{avgOverhead:F0}ms</td>
  <td>{avgUpstream:F0}ms</td>
  <td>{avgTotal:F0}ms</td>
  <td>{logs.Count}</td>
</tr>";
        }

        var uptime = DateTime.UtcNow - Diagnostics.StartedAt;
        var asyncFails = Diagnostics.AsyncFailures;

        var html = Layout("Overview", $@"
<main class=""container"">
  <h2>Dashboard Overview</h2>

  <div hx-get=""/dashboard/"" hx-trigger=""every 30s"" hx-select=""main"" hx-target=""main"" hx-swap=""outerHTML""></div>

  <section>
    <h3>Projects</h3>
    <figure>
    <table role=""grid"">
      <thead>
        <tr>
          <th>Name</th><th>ID</th><th>Status</th><th>Today</th>
          <th>Daily Limit</th><th>Weekly Limit</th><th>Monthly Limit</th><th>Daily Usage</th>
        </tr>
      </thead>
      <tbody>
        {(projectRows.Length > 0 ? projectRows : "<tr><td colspan=\"8\">No projects yet</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>

  <div class=""grid"">
    <section>
      <article>
        <header><h4>Pricing Health</h4></header>
        <p>Status: <strong style=""color:{pricingHealthColor};"">{pricingHealthText}</strong></p>
        <p>Known models: <strong>{prices.Count}</strong></p>
        <p>Unknown model hits: <strong>{unknownHits}</strong></p>
        <p>Pricing refresh failures: <strong>{pricingRefreshFailures}</strong></p>
        <p>Last refreshed: <strong>{lastRefreshed:yyyy-MM-dd HH:mm:ss} UTC</strong></p>
        <footer><a href=""/dashboard/pricing"">View Pricing Table</a></footer>
      </article>
    </section>

    <section>
      <article>
        <header><h4>System</h4></header>
        <p>Uptime: <strong>{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m</strong></p>
        <p>Async failures: <strong>{asyncFails}</strong></p>
        <p>Projects: <strong>{projects.Count}</strong></p>
      </article>
    </section>
  </div>

  <section>
    <h3>Latency Summary (recent 100 requests)</h3>
    <figure>
    <table role=""grid"">
      <thead>
        <tr><th>Project</th><th>Avg Overhead</th><th>Avg Upstream</th><th>Avg Total</th><th>Sample Size</th></tr>
      </thead>
      <tbody>
        {(latencySummary.Length > 0 ? latencySummary : "<tr><td colspan=\"5\">No request data yet</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>
</main>");

        return Results.Content(html, "text/html");
    }

    // ── Project Detail ──

    private static IResult ProjectDetail(string id, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.Content(Layout("Not Found", "<main class=\"container\"><h2>Project not found</h2></main>"), "text/html");

        var users = store.GetProjectUsers(id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekAgo = today.AddDays(-7);
        var usage = store.GetUsage(id, weekAgo.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
        var todayCost = store.GetProjectCostForPeriod(id, today.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));

        // Users table
        var userRows = "";
        foreach (var u in users)
        {
            userRows += $@"
<tr>
  <td><code>{Enc(u.UserId)}</code></td>
  <td>${u.TodayCost:F4}</td>
</tr>";
        }

        // Daily usage for chart (aggregate by date)
        var dailyData = usage
            .GroupBy(u => u.Date)
            .OrderBy(g => g.Key)
            .Select(g => new { Date = g.Key, Cost = g.Sum(x => x.TotalCost), Requests = g.Sum(x => x.RequestCount) })
            .ToList();

        var chartRows = "";
        foreach (var d in dailyData)
        {
            chartRows += $@"
<tr>
  <td>{Enc(d.Date)}</td>
  <td>${d.Cost:F4}</td>
  <td>{d.Requests}</td>
</tr>";
        }

        // Latency breakdown from recent logs
        var logs = store.GetLogs(id, page: 1, perPage: 200);
        var latencyBreakdown = "";
        if (logs.Count > 0)
        {
            var avgOverhead = logs.Average(l => l.OverheadMs);
            var avgUpstream = logs.Average(l => l.UpstreamMs);
            var avgTransfer = logs.Average(l => l.TransferMs);
            var avgTotal = logs.Average(l => l.TotalMs);
            var p95Total = logs.OrderBy(l => l.TotalMs).ElementAt((int)(logs.Count * 0.95));
            var maxTotal = logs.Max(l => l.TotalMs);

            latencyBreakdown = $@"
<div class=""grid"">
  <article>
    <header><h4>Average Latency</h4></header>
    <p>Overhead: <strong>{avgOverhead:F0}ms</strong></p>
    <p>Upstream: <strong>{avgUpstream:F0}ms</strong></p>
    <p>Transfer: <strong>{avgTransfer:F0}ms</strong></p>
    <p>Total: <strong>{avgTotal:F0}ms</strong></p>
  </article>
  <article>
    <header><h4>Percentiles</h4></header>
    <p>p95 Total: <strong>{p95Total.TotalMs}ms</strong></p>
    <p>Max Total: <strong>{maxTotal}ms</strong></p>
    <p>Sample size: <strong>{logs.Count}</strong></p>
  </article>
</div>";
        }

        var statusBadge = project.IsActive
            ? "<span style=\"color:green;\">Active</span>"
            : "<span style=\"color:red;\">Inactive</span>";

        var html = Layout($"Project: {Enc(project.Name)}", $@"
<main class=""container"">
  <nav aria-label=""breadcrumb""><ul>
    <li><a href=""/dashboard/"">Dashboard</a></li>
    <li>{Enc(project.Name)}</li>
  </ul></nav>

  <hgroup>
    <h2>{Enc(project.Name)}</h2>
    <p>ID: <code>{Enc(project.Id)}</code> | Status: {statusBadge} | Today: <strong>${todayCost:F4}</strong></p>
  </hgroup>

  <div hx-get=""/dashboard/projects/{Enc(id)}"" hx-trigger=""every 30s"" hx-select=""main"" hx-target=""main"" hx-swap=""outerHTML""></div>

  <nav>
    <ul>
      <li><a href=""/dashboard/projects/{Enc(id)}/logs"">View Logs</a></li>
      <li><a href=""/dashboard/projects/{Enc(id)}/settings"">Settings</a></li>
    </ul>
  </nav>

  <section>
    <h3>Budget</h3>
    <div class=""grid"">
      <article>
        <header>Daily</header>
        <p>Limit: {Fmt(project.BudgetDaily)}</p>
        <p>Used: ${todayCost:F4}</p>
        {BuildBudgetBar(todayCost, project.BudgetDaily)}
      </article>
      <article>
        <header>Weekly</header>
        <p>Limit: {Fmt(project.BudgetWeekly)}</p>
      </article>
      <article>
        <header>Monthly</header>
        <p>Limit: {Fmt(project.BudgetMonthly)}</p>
      </article>
    </div>
  </section>

  <section>
    <h3>Users ({users.Count})</h3>
    <figure>
    <table role=""grid"">
      <thead><tr><th>User ID</th><th>Today Cost</th></tr></thead>
      <tbody>
        {(userRows.Length > 0 ? userRows : "<tr><td colspan=\"2\">No users yet</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>

  <section>
    <h3>Daily Usage (last 7 days)</h3>
    <figure>
    <table role=""grid"">
      <thead><tr><th>Date</th><th>Cost</th><th>Requests</th></tr></thead>
      <tbody>
        {(chartRows.Length > 0 ? chartRows : "<tr><td colspan=\"3\">No usage data</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>

  <section>
    <h3>Latency Breakdown (last {logs.Count} requests)</h3>
    {(latencyBreakdown.Length > 0 ? latencyBreakdown : "<p>No request data yet</p>")}
  </section>
</main>");

        return Results.Content(html, "text/html");
    }

    // ── Log Viewer ──

    private static IResult LogViewer(string id, HttpContext ctx, Store store)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.Content(Layout("Not Found", "<main class=\"container\"><h2>Project not found</h2></main>"), "text/html");

        var userId = ctx.Request.Query["user"].FirstOrDefault();
        var model = ctx.Request.Query["model"].FirstOrDefault();
        var pageStr = ctx.Request.Query["page"].FirstOrDefault();
        var page = int.TryParse(pageStr, out var pg) ? pg : 1;
        var perPage = 50;

        var logs = store.GetLogs(id, userId, model, page, perPage);

        var logRows = "";
        foreach (var log in logs)
        {
            var fallbackBadge = log.UsedFallbackPricing ? "<span title=\"Fallback pricing used\" style=\"color:orange;\">~</span>" : "";
            var streamBadge = log.IsStream ? "SSE" : "Buf";
            logRows += $@"
<tr>
  <td>{Enc(log.Timestamp[..19])}</td>
  <td><code>{Enc(log.UserId)}</code></td>
  <td>{Enc(log.Model)}</td>
  <td>{Enc(log.Deployment)}</td>
  <td>{log.PromptTokens}</td>
  <td>{log.CompletionTokens}</td>
  <td>${log.CostUsd:F6}{fallbackBadge}</td>
  <td>{log.StatusCode}</td>
  <td>{log.OverheadMs}</td>
  <td>{log.UpstreamMs}</td>
  <td>{log.TransferMs}</td>
  <td>{log.TotalMs}</td>
  <td>{streamBadge}</td>
</tr>";
        }

        var prevDisabled = page <= 1 ? "aria-disabled=\"true\"" : "";
        var nextDisabled = logs.Count < perPage ? "aria-disabled=\"true\"" : "";
        var filterQs = (userId != null ? $"&user={Enc(userId)}" : "") + (model != null ? $"&model={Enc(model)}" : "");

        var html = Layout($"Logs: {Enc(project.Name)}", $@"
<main class=""container"">
  <nav aria-label=""breadcrumb""><ul>
    <li><a href=""/dashboard/"">Dashboard</a></li>
    <li><a href=""/dashboard/projects/{Enc(id)}"">{Enc(project.Name)}</a></li>
    <li>Logs</li>
  </ul></nav>

  <h2>Request Logs</h2>

  <form method=""get"" action=""/dashboard/projects/{Enc(id)}/logs"" class=""grid"">
    <label>User <input type=""text"" name=""user"" value=""{Enc(userId ?? "")}"" placeholder=""Filter by user""></label>
    <label>Model <input type=""text"" name=""model"" value=""{Enc(model ?? "")}"" placeholder=""Filter by model""></label>
    <button type=""submit"">Filter</button>
  </form>

  <figure>
  <table role=""grid"">
    <thead>
      <tr>
        <th>Time</th><th>User</th><th>Model</th><th>Deployment</th>
        <th>Prompt</th><th>Completion</th><th>Cost</th><th>Status</th>
        <th>Overhead</th><th>Upstream</th><th>Transfer</th><th>Total</th><th>Type</th>
      </tr>
    </thead>
    <tbody>
      {(logRows.Length > 0 ? logRows : "<tr><td colspan=\"13\">No logs found</td></tr>")}
    </tbody>
  </table>
  </figure>

  <nav>
    <ul>
      <li><a href=""/dashboard/projects/{Enc(id)}/logs?page={page - 1}{filterQs}"" role=""button"" {prevDisabled}>Previous</a></li>
      <li>Page {page}</li>
      <li><a href=""/dashboard/projects/{Enc(id)}/logs?page={page + 1}{filterQs}"" role=""button"" {nextDisabled}>Next</a></li>
    </ul>
  </nav>
</main>");

        return Results.Content(html, "text/html");
    }

    // ── Project Settings ──

    private static IResult ProjectSettings(string id, Store store, HttpContext ctx)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.Content(Layout("Not Found", "<main class=\"container\"><h2>Project not found</h2></main>"), "text/html");

        var saved = ctx.Request.Query.ContainsKey("saved") ? "<p style=\"color:green;\">Settings saved successfully.</p>" : "";

        var html = Layout($"Settings: {Enc(project.Name)}", $@"
<main class=""container"">
  <nav aria-label=""breadcrumb""><ul>
    <li><a href=""/dashboard/"">Dashboard</a></li>
    <li><a href=""/dashboard/projects/{Enc(id)}"">{Enc(project.Name)}</a></li>
    <li>Settings</li>
  </ul></nav>

  <h2>Project Settings: {Enc(project.Name)}</h2>

  {saved}

  <form method=""post"" action=""/dashboard/projects/{Enc(id)}/settings"">
    <fieldset>
      <legend>General</legend>
      <label for=""name"">Name</label>
      <input type=""text"" id=""name"" name=""name"" value=""{Enc(project.Name)}"" required>

      <label>
        <input type=""checkbox"" name=""isActive"" value=""true"" {(project.IsActive ? "checked" : "")}>
        Active
      </label>
    </fieldset>

    <fieldset>
      <legend>Project Budgets</legend>
      <div class=""grid"">
        <label>Daily ($)
          <input type=""number"" name=""budgetDaily"" step=""0.01"" value=""{Val(project.BudgetDaily)}"" placeholder=""No limit"">
        </label>
        <label>Weekly ($)
          <input type=""number"" name=""budgetWeekly"" step=""0.01"" value=""{Val(project.BudgetWeekly)}"" placeholder=""No limit"">
        </label>
        <label>Monthly ($)
          <input type=""number"" name=""budgetMonthly"" step=""0.01"" value=""{Val(project.BudgetMonthly)}"" placeholder=""No limit"">
        </label>
      </div>
    </fieldset>

    <fieldset>
      <legend>Default User Budgets</legend>
      <div class=""grid"">
        <label>Daily ($)
          <input type=""number"" name=""defaultUserBudgetDaily"" step=""0.01"" value=""{Val(project.DefaultUserBudgetDaily)}"" placeholder=""No limit"">
        </label>
        <label>Weekly ($)
          <input type=""number"" name=""defaultUserBudgetWeekly"" step=""0.01"" value=""{Val(project.DefaultUserBudgetWeekly)}"" placeholder=""No limit"">
        </label>
        <label>Monthly ($)
          <input type=""number"" name=""defaultUserBudgetMonthly"" step=""0.01"" value=""{Val(project.DefaultUserBudgetMonthly)}"" placeholder=""No limit"">
        </label>
      </div>
    </fieldset>

    <button type=""submit"">Save Settings</button>
  </form>
</main>");

        return Results.Content(html, "text/html");
    }

    private static IResult SaveProjectSettings(string id, HttpContext ctx, Store store, AuthCache authCache)
    {
        var project = store.GetProject(id);
        if (project is null) return Results.Content(Layout("Not Found", "<main class=\"container\"><h2>Project not found</h2></main>"), "text/html");

        var form = ctx.Request.Form;
        var name = form["name"].FirstOrDefault();
        var isActive = form["isActive"].FirstOrDefault() == "true";
        var budgetDaily = ParseDouble(form["budgetDaily"].FirstOrDefault());
        var budgetWeekly = ParseDouble(form["budgetWeekly"].FirstOrDefault());
        var budgetMonthly = ParseDouble(form["budgetMonthly"].FirstOrDefault());
        var defaultUserBudgetDaily = ParseDouble(form["defaultUserBudgetDaily"].FirstOrDefault());
        var defaultUserBudgetWeekly = ParseDouble(form["defaultUserBudgetWeekly"].FirstOrDefault());
        var defaultUserBudgetMonthly = ParseDouble(form["defaultUserBudgetMonthly"].FirstOrDefault());

        store.UpdateProject(id, name, budgetDaily, budgetWeekly, budgetMonthly,
            defaultUserBudgetDaily, defaultUserBudgetWeekly, defaultUserBudgetMonthly, isActive,
            clearBudgets: true);

        authCache.Reload(store.GetAllProjects());

        return Results.Redirect($"/dashboard/projects/{id}/settings?saved=1");
    }

    // ── Pricing Page ──

    private static IResult PricingPage(Store store, PricingCache pricingCache)
    {
        var allPrices = pricingCache.GetAllPrices();
        var adminOverrides = store.GetAllPricing();
        var adminSet = new HashSet<string>(adminOverrides.Select(o => o.ModelPattern), StringComparer.OrdinalIgnoreCase);

        var rows = "";
        foreach (var kv in allPrices.OrderBy(k => k.Key))
        {
            var source = adminSet.Contains(kv.Key)
                ? "<span style=\"color:orange;\">Admin</span>"
                : "<span style=\"color:gray;\">LiteLLM</span>";

            rows += $@"
<tr>
  <td><code>{Enc(kv.Key)}</code></td>
  <td>${kv.Value.InputPerToken * 1_000_000:F4}</td>
  <td>${kv.Value.OutputPerToken * 1_000_000:F4}</td>
  <td>{source}</td>
</tr>";
        }

        // Unknown models
        var unknownRows = "";
        foreach (var kv in Diagnostics.UnknownModels.OrderByDescending(k => k.Value))
        {
            unknownRows += $"<tr><td><code>{Enc(kv.Key)}</code></td><td>{kv.Value}</td></tr>";
        }

        var html = Layout("Pricing", $@"
<main class=""container"">
  <nav aria-label=""breadcrumb""><ul>
    <li><a href=""/dashboard/"">Dashboard</a></li>
    <li>Pricing</li>
  </ul></nav>

  <h2>Model Pricing</h2>
  <p>Last refreshed: <strong>{pricingCache.LastRefreshed:yyyy-MM-dd HH:mm:ss} UTC</strong> | Total models: <strong>{allPrices.Count}</strong></p>

  <div hx-get=""/dashboard/pricing"" hx-trigger=""every 60s"" hx-select=""main"" hx-target=""main"" hx-swap=""outerHTML""></div>

  <figure>
  <table role=""grid"">
    <thead>
      <tr><th>Model</th><th>Input $/1M tokens</th><th>Output $/1M tokens</th><th>Source</th></tr>
    </thead>
    <tbody>
      {(rows.Length > 0 ? rows : "<tr><td colspan=\"4\">No pricing data</td></tr>")}
    </tbody>
  </table>
  </figure>

  {(Diagnostics.UnknownModels.Count > 0 ? $@"
  <section>
    <h3>Unknown Models ({Diagnostics.UnknownModels.Count})</h3>
    <p>These models were seen in requests but have no pricing data. Cost is recorded as $0.</p>
    <figure>
    <table role=""grid"">
      <thead><tr><th>Model</th><th>Hit Count</th></tr></thead>
      <tbody>{unknownRows}</tbody>
    </table>
    </figure>
  </section>" : "")}
</main>");

        return Results.Content(html, "text/html");
    }

    // ── Helpers ──

    private static string Layout(string title, string body, bool includeNav = true)
    {
        var nav = includeNav ? @"
    <nav class=""container"">
      <ul><li><strong><a href=""/dashboard/"">LLimit</a></strong></li></ul>
      <ul>
        <li><a href=""/dashboard/"">Overview</a></li>
        <li><a href=""/dashboard/pricing"">Pricing</a></li>
      </ul>
    </nav>" : "";

        return $@"<!DOCTYPE html>
<html lang=""en"" data-theme=""light"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>{title} - LLimit</title>
  <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/@picocss/pico@2/css/pico.min.css"">
  <script src=""https://unpkg.com/htmx.org@2.0.4""></script>
  <style>
    .budget-bar {{ background: #e9ecef; border-radius: 4px; height: 20px; overflow: hidden; }}
    .budget-bar-fill {{ height: 100%; border-radius: 4px; transition: width 0.3s; }}
    .budget-bar-fill.green {{ background: #2ecc71; }}
    .budget-bar-fill.yellow {{ background: #f39c12; }}
    .budget-bar-fill.red {{ background: #e74c3c; }}
    table {{ font-size: 0.875rem; }}
    code {{ font-size: 0.8rem; }}
  </style>
</head>
<body>
  {nav}
  {body}
  <footer class=""container"">
    <small>LLimit Dashboard | <a href=""/health"">Health</a></small>
  </footer>
</body>
</html>";
    }

    private static string BuildBudgetBar(double used, double? limit)
    {
        if (limit is null or 0) return "<span style=\"color:gray;\">No limit</span>";
        var pct = Math.Min(used / limit.Value * 100, 100);
        var color = pct < 70 ? "green" : pct < 90 ? "yellow" : "red";
        return $@"<div class=""budget-bar""><div class=""budget-bar-fill {color}"" style=""width:{pct:F1}%""></div></div><small>{pct:F1}%</small>";
    }

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Fmt(double? v) => v.HasValue ? $"${v.Value:F2}" : "<span style=\"color:gray;\">None</span>";

    private static string Val(double? v) => v.HasValue ? $"{v.Value}" : "";

    private static double? ParseDouble(string? s)
        => !string.IsNullOrWhiteSpace(s) && double.TryParse(s, out var v) ? v : null;
}
