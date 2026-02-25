using System.Net;

namespace LLimit;

/// <summary>
/// Self-service portal for OAuth-authenticated users.
/// Routes:
///   GET  /portal/login             – Sign-in page (redirects to Azure AD)
///   GET  /portal                   – Dashboard: keys + usage (requires user session)
///   POST /portal/keys/{projectId}  – Create (or rotate) personal API key for a project
///   POST /portal/keys/{projectId}/revoke – Revoke personal API key
/// </summary>
public static class UserPortalRoutes
{
    public static void MapUserPortal(this WebApplication app)
    {
        var portal = app.MapGroup("/portal");

        // Public
        portal.MapGet("/login", LoginPage);

        // Requires user session
        var authed = portal.AddEndpointFilter(UserAuthFilter);
        authed.MapGet("/", PortalHome);
        authed.MapPost("/keys/{projectId}", CreateKey);
        authed.MapPost("/keys/{projectId}/revoke", RevokeKey);
    }

    // ── Auth filter ──

    private static async ValueTask<object?> UserAuthFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var adminToken = cfg["LLIMIT_ADMIN_TOKEN"] ?? "";

        if (!OAuthHandler.TryGetOAuthConfig(cfg, out _))
        {
            ctx.HttpContext.Response.StatusCode = 503;
            return Results.Content(
                Layout("Unavailable", "<main class=\"container\"><p>OAuth login is not configured on this server.</p></main>"),
                "text/html");
        }

        var cookie = ctx.HttpContext.Request.Cookies["llimit_user_session"];
        var userId = OAuthHandler.ValidateUserSession(cookie, adminToken);
        if (userId is null)
        {
            ctx.HttpContext.Response.Redirect("/portal/login");
            return Results.Empty;
        }

        // Make userId available to handlers via Items
        ctx.HttpContext.Items["userId"] = userId;
        return await next(ctx);
    }

    // ── Pages ──

    private static IResult LoginPage(HttpContext ctx, IConfiguration cfg)
    {
        if (!OAuthHandler.TryGetOAuthConfig(cfg, out _))
        {
            return Results.Content(
                Layout("Unavailable", "<main class=\"container\"><p>OAuth login is not configured on this server.</p></main>"),
                "text/html");
        }

        var errorParam = ctx.Request.Query["error"].FirstOrDefault();
        var errorHtml = errorParam switch
        {
            null or "" => "",
            "state_mismatch" => Alert("Login failed: security state mismatch. Please try again."),
            "no_code" or "access_denied" => Alert("Login was cancelled or denied."),
            "token_exchange_failed" => Alert("Failed to complete login. Please try again."),
            _ => Alert($"Login error: {Enc(errorParam)}")
        };

        var html = Layout("Sign In", $@"
<main class=""container"">
  <article style=""max-width:420px;margin:80px auto;"">
    <header><h2>LLimit User Portal</h2></header>
    {errorHtml}
    <p>Sign in with your Microsoft account to manage your personal API keys and view usage.</p>
    <a href=""/auth/login"" role=""button"" style=""display:block;text-align:center;"">
      Sign in with Microsoft
    </a>
  </article>
</main>", includeNav: false);

        return Results.Content(html, "text/html");
    }

    private static IResult PortalHome(HttpContext ctx, Store store, IConfiguration cfg)
    {
        var userId = (string)ctx.Items["userId"]!;
        var user = store.GetUser(userId);
        if (user is null)
        {
            ctx.Response.Cookies.Delete("llimit_user_session");
            ctx.Response.Redirect("/portal/login");
            return Results.Empty;
        }

        // Projects where the admin has enabled user key generation
        var allProjects = store.GetAllProjects()
            .Where(p => p.IsActive && p.AllowUserKeys)
            .ToList();

        // The user's existing keys
        var userKeys = store.GetUserKeys(userId)
            .ToDictionary(k => k.ProjectId);

        // Check if a key was just created (show it once)
        var newKey = ctx.Request.Query["newkey"].FirstOrDefault();
        var newKeyProject = ctx.Request.Query["project"].FirstOrDefault();
        var newKeyBanner = "";
        if (!string.IsNullOrEmpty(newKey))
        {
            newKeyBanner = $@"
<article style=""border:2px solid var(--pico-color-green-500);"">
  <header><strong>Your new API key for project <em>{Enc(newKeyProject)}</em></strong></header>
  <p>Copy this key now — it will <strong>not</strong> be shown again.</p>
  <pre style=""word-break:break-all;""><code>{Enc(newKey)}</code></pre>
  <p><small>Use this key as the <code>api-key</code> header when calling the proxy.</small></p>
</article>";
        }

        // Projects table
        var projectRows = "";
        foreach (var p in allProjects)
        {
            var hasKey = userKeys.TryGetValue(p.Id, out var uk);
            var keyInfo = hasKey
                ? $"<code>llimit-…{Enc(uk!.Id[..Math.Min(4, uk.Id.Length)])}</code> <small>(created {Enc(uk.CreatedAt[..10])})</small>"
                : "<span style=\"color:gray;\">None</span>";

            var actionBtn = hasKey
                ? $@"<form method=""post"" action=""/portal/keys/{Enc(p.Id)}/revoke"" style=""display:inline;"">
                       <button type=""submit"" class=""outline"" style=""font-size:0.8rem;padding:0.2rem 0.6rem;"">Revoke</button>
                     </form>
                     <form method=""post"" action=""/portal/keys/{Enc(p.Id)}"" style=""display:inline;"">
                       <button type=""submit"" class=""outline"" style=""font-size:0.8rem;padding:0.2rem 0.6rem;"">Rotate</button>
                     </form>"
                : $@"<form method=""post"" action=""/portal/keys/{Enc(p.Id)}"" style=""display:inline;"">
                       <button type=""submit"" style=""font-size:0.8rem;padding:0.2rem 0.6rem;"">Create key</button>
                     </form>";

            projectRows += $@"
<tr>
  <td>{Enc(p.Name)}</td>
  <td>{keyInfo}</td>
  <td>{actionBtn}</td>
</tr>";
        }

        // Usage last 14 days
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-13);
        var usage = store.GetUserUsage(userId, from);

        var usageRows = "";
        foreach (var u in usage.OrderByDescending(x => x.Date))
        {
            usageRows += $@"
<tr>
  <td>{Enc(u.Date)}</td>
  <td>{Enc(u.ProjectId)}</td>
  <td>{u.RequestCount}</td>
  <td>{u.PromptTokens + u.CompletionTokens}</td>
  <td>${u.TotalCost:F6}</td>
</tr>";
        }

        var html = Layout($"Portal – {Enc(userId)}", $@"
<main class=""container"">
  <hgroup>
    <h2>Welcome, {Enc(user.DisplayName)}</h2>
    <p>User ID: <code>{Enc(userId)}</code> · <a href=""/auth/logout"">Sign out</a></p>
  </hgroup>

  {newKeyBanner}

  <section>
    <h3>My API Keys</h3>
    {(allProjects.Count == 0
        ? "<p>No projects are currently enabled for personal key generation.</p>"
        : $@"<figure>
    <table role=""grid"">
      <thead><tr><th>Project</th><th>Key</th><th>Actions</th></tr></thead>
      <tbody>
        {(projectRows.Length > 0 ? projectRows : "<tr><td colspan=\"3\">No projects available.</td></tr>")}
      </tbody>
    </table>
    </figure>")}
  </section>

  <section>
    <h3>My Usage (last 14 days)</h3>
    <figure>
    <table role=""grid"">
      <thead><tr><th>Date</th><th>Project</th><th>Requests</th><th>Tokens</th><th>Cost</th></tr></thead>
      <tbody>
        {(usageRows.Length > 0 ? usageRows : "<tr><td colspan=\"5\">No usage yet.</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>
</main>");

        return Results.Content(html, "text/html");
    }

    private static IResult CreateKey(string projectId, HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["userId"]!;

        var project = store.GetProject(projectId);
        if (project is null || !project.IsActive || !project.AllowUserKeys)
            return Results.Redirect("/portal?error=project_not_found");

        var (_, plainKey) = store.CreateUserKey(userId, projectId);

        // Redirect with the key in the query string so it can be shown once.
        // We use a GET redirect to prevent re-submission on browser refresh.
        var qs = $"newkey={Uri.EscapeDataString(plainKey)}&project={Uri.EscapeDataString(project.Name)}";
        return Results.Redirect($"/portal?{qs}");
    }

    private static IResult RevokeKey(string projectId, HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["userId"]!;
        store.DeleteUserKey(userId, projectId);
        return Results.Redirect("/portal");
    }

    // ── HTML helpers ──

    private static string Layout(string title, string body, bool includeNav = true)
    {
        var nav = includeNav ? @"
    <nav class=""container"">
      <ul><li><strong><a href=""/portal"">LLimit Portal</a></strong></li></ul>
      <ul>
        <li><a href=""/portal"">My Keys</a></li>
        <li><a href=""/auth/logout"">Sign out</a></li>
      </ul>
    </nav>" : "";

        return $@"<!DOCTYPE html>
<html lang=""en"" data-theme=""light"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>{Enc(title)} - LLimit Portal</title>
  <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/@picocss/pico@2/css/pico.min.css"">
  <style>
    table {{ font-size: 0.875rem; }}
    code  {{ font-size: 0.8rem; }}
  </style>
</head>
<body>
  {nav}
  {body}
  <footer class=""container"">
    <small>LLimit User Portal</small>
  </footer>
</body>
</html>";
    }

    private static string Alert(string message) =>
        $"<p style=\"color:var(--pico-color-red-500);\">{message}</p>";

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
