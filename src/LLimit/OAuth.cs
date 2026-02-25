using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLimit;

public static class OAuthRoutes
{
    public static void MapOAuth(this WebApplication app)
    {
        // Auth routes (public)
        app.MapGet("/auth/login", Login);
        app.MapGet("/auth/callback", Callback);
        app.MapGet("/auth/logout", Logout);

        // Portal routes
        var portal = app.MapGroup("/portal");
        portal.MapGet("/login", PortalLoginPage);

        var authed = portal.AddEndpointFilter(UserAuthFilter);
        authed.MapGet("/", PortalHome);
        authed.MapGet("/projects/{id}", PortalProjectDetail);
        authed.MapPost("/projects/{id}/keys", CreateUserKey);
        authed.MapPost("/projects/{id}/keys/revoke", RevokeUserKey);
    }

    // ── Auth Filter ──

    private static async ValueTask<object?> UserAuthFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<Store>();
        var cookie = ctx.HttpContext.Request.Cookies["llimit_user_session"];
        if (string.IsNullOrEmpty(cookie))
        {
            ctx.HttpContext.Response.Redirect("/portal/login");
            return Results.Empty;
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(cookie))).ToLowerInvariant();
        var session = store.GetUserSession(hash);
        if (session is null || DateTime.Parse(session.ExpiresAt) < DateTime.UtcNow)
        {
            if (session is not null) store.DeleteUserSession(hash);
            ctx.HttpContext.Response.Redirect("/portal/login");
            return Results.Empty;
        }

        ctx.HttpContext.Items["UserId"] = session.UserId;
        var user = store.GetUser(session.UserId);
        if (user is not null)
            ctx.HttpContext.Items["UserDisplayName"] = user.DisplayName;

        return await next(ctx);
    }

    // ── OAuth Login ──

    private static IResult Login(HttpContext ctx, IConfiguration cfg)
    {
        var clientId = cfg["AZURE_AD_CLIENT_ID"];
        var tenantId = cfg["AZURE_AD_TENANT_ID"];
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId))
            return Results.Content(
                "OAuth not configured. Set AZURE_AD_CLIENT_ID, AZURE_AD_CLIENT_SECRET, and AZURE_AD_TENANT_ID.",
                "text/plain", statusCode: 500);

        var state = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        ctx.Response.Cookies.Append("llimit_oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/callback";
        var authUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString("openid email profile")}" +
            $"&state={state}" +
            "&response_mode=query";

        return Results.Redirect(authUrl);
    }

    // ── OAuth Callback ──

    private static async Task<IResult> Callback(
        HttpContext ctx, IConfiguration cfg,
        Store store, IHttpClientFactory factory, ILogger<Program> logger)
    {
        var error = ctx.Request.Query["error"].FirstOrDefault();
        if (error is not null)
        {
            var desc = ctx.Request.Query["error_description"].FirstOrDefault() ?? error;
            logger.LogWarning("OAuth error from Azure AD: {Error} — {Desc}", error, desc);
            return Results.Redirect($"/portal/login?error={Uri.EscapeDataString(desc)}");
        }

        var state = ctx.Request.Query["state"].FirstOrDefault();
        var expectedState = ctx.Request.Cookies["llimit_oauth_state"];
        if (string.IsNullOrEmpty(state) || state != expectedState)
            return Results.Redirect("/portal/login?error=Invalid+state+parameter");
        ctx.Response.Cookies.Delete("llimit_oauth_state");

        var code = ctx.Request.Query["code"].FirstOrDefault();
        if (string.IsNullOrEmpty(code))
            return Results.Redirect("/portal/login?error=No+authorization+code");

        var clientId = cfg["AZURE_AD_CLIENT_ID"]!;
        var clientSecret = cfg["AZURE_AD_CLIENT_SECRET"]!;
        var tenantId = cfg["AZURE_AD_TENANT_ID"]!;
        var corporateDomain = cfg["AZURE_AD_CORPORATE_DOMAIN"];
        var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/auth/callback";

        // Exchange code for tokens
        using var http = factory.CreateClient();
        var tokenUrl =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";
        var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["scope"] = "openid email profile"
        });

        HttpResponseMessage tokenResp;
        try
        {
            tokenResp = await http.PostAsync(tokenUrl, tokenReq);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach Azure AD token endpoint");
            return Results.Redirect("/portal/login?error=Token+exchange+failed");
        }

        if (!tokenResp.IsSuccessStatusCode)
        {
            var body = await tokenResp.Content.ReadAsStringAsync();
            logger.LogWarning("Token exchange failed: {Status} {Body}",
                tokenResp.StatusCode, body);
            return Results.Redirect("/portal/login?error=Token+exchange+failed");
        }

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);

        if (!tokenDoc.RootElement.TryGetProperty("id_token", out var idTokenProp))
            return Results.Redirect("/portal/login?error=No+ID+token+in+response");

        var idToken = idTokenProp.GetString();
        if (string.IsNullOrEmpty(idToken))
            return Results.Redirect("/portal/login?error=Empty+ID+token");

        // Parse JWT payload (trusted — received from Azure AD over TLS)
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            return Results.Redirect("/portal/login?error=Invalid+token+format");

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        JsonDocument claimsDoc;
        try
        {
            claimsDoc = JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse JWT payload");
            return Results.Redirect("/portal/login?error=Invalid+token+payload");
        }

        using (claimsDoc)
        {
            var claims = claimsDoc.RootElement;

            var email =
                claims.TryGetProperty("email", out var ep) ? ep.GetString() :
                claims.TryGetProperty("preferred_username", out var up) ? up.GetString() :
                null;

            if (string.IsNullOrEmpty(email))
                return Results.Redirect("/portal/login?error=No+email+in+token");

            var emailParts = email.Split('@');
            if (emailParts.Length != 2)
                return Results.Redirect("/portal/login?error=Invalid+email+format");

            if (!string.IsNullOrEmpty(corporateDomain) &&
                !emailParts[1].Equals(corporateDomain, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Login rejected: {Email} not in domain {Domain}",
                    email, corporateDomain);
                return Results.Redirect("/portal/login?error=Email+domain+not+allowed");
            }

            var userId = emailParts[0].ToLowerInvariant();
            var displayName = claims.TryGetProperty("name", out var np)
                ? np.GetString() ?? userId : userId;

            store.UpsertUser(userId, email.ToLowerInvariant(), displayName);

            var sessionToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var sessionHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken))).ToLowerInvariant();
            store.CreateUserSession(sessionHash, userId, TimeSpan.FromDays(7));

            ctx.Response.Cookies.Append("llimit_user_session", sessionToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = TimeSpan.FromDays(7)
            });

            return Results.Redirect("/portal/");
        }
    }

    // ── Logout ──

    private static IResult Logout(HttpContext ctx, Store store)
    {
        var cookie = ctx.Request.Cookies["llimit_user_session"];
        if (!string.IsNullOrEmpty(cookie))
        {
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(cookie))).ToLowerInvariant();
            store.DeleteUserSession(hash);
        }
        ctx.Response.Cookies.Delete("llimit_user_session");
        return Results.Redirect("/portal/login");
    }

    // ── Portal Login Page ──

    private static IResult PortalLoginPage(HttpContext ctx, IConfiguration cfg)
    {
        var errorMsg = ctx.Request.Query["error"].FirstOrDefault();
        var errorHtml = errorMsg is not null
            ? $"<p style=\"color:var(--pico-color-red-500);\">{Enc(errorMsg)}</p>" : "";

        var oauthOk = !string.IsNullOrEmpty(cfg["AZURE_AD_CLIENT_ID"]);

        var html = PortalLayout("Login", $@"
<main class=""container"">
  <article style=""max-width:400px;margin:60px auto;"">
    <header><h2>LLimit Portal</h2></header>
    {errorHtml}
    <p>Sign in with your corporate account to manage API keys and track usage.</p>
    {(oauthOk
        ? @"<a href=""/auth/login"" role=""button"" style=""width:100%;"">Sign in with Microsoft</a>"
        : @"<p style=""color:var(--pico-color-red-500);"">OAuth not configured. Contact your administrator.</p>")}
  </article>
</main>", includeNav: false);

        return Results.Content(html, "text/html");
    }

    // ── Portal Home ──

    private static IResult PortalHome(HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["UserId"]!;
        var displayName = ctx.Items["UserDisplayName"] as string ?? userId;

        var projects = store.GetProjectsAllowingUserKeys();
        var userKeys = store.GetUserApiKeys(userId);
        var keyLookup = userKeys.ToDictionary(k => k.ProjectId);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var rows = "";
        foreach (var p in projects)
        {
            var hasKey = keyLookup.ContainsKey(p.Id);
            var keyBadge = hasKey
                ? "<span style=\"color:green;\">Active</span>"
                : "<span style=\"color:gray;\">Not created</span>";
            var cost = hasKey ? store.GetUserCostForDate(p.Id, userId, today) : 0;
            var bar = BuildBudgetBar(cost, p.DefaultUserBudgetDaily);

            rows += $@"
<tr>
  <td><a href=""/portal/projects/{Enc(p.Id)}"">{Enc(p.Name)}</a></td>
  <td>{keyBadge}</td>
  <td>${cost:F4}</td>
  <td>{Fmt(p.DefaultUserBudgetDaily)}</td>
  <td style=""min-width:120px;"">{bar}</td>
</tr>";
        }

        var html = PortalLayout($"Welcome, {Enc(displayName)}", $@"
<main class=""container"">
  <h2>Your Projects</h2>
  <p>Select a project to manage your API key and view usage.</p>
  <figure>
  <table role=""grid"">
    <thead>
      <tr><th>Project</th><th>Key</th><th>Today</th><th>Daily Limit</th><th>Usage</th></tr>
    </thead>
    <tbody>
      {(rows.Length > 0 ? rows : "<tr><td colspan=\"5\">No projects available for self-service keys.</td></tr>")}
    </tbody>
  </table>
  </figure>
</main>", userId: userId);

        return Results.Content(html, "text/html");
    }

    // ── Portal Project Detail ──

    private static IResult PortalProjectDetail(string id, HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["UserId"]!;
        var project = store.GetProject(id);
        if (project is null || !project.AllowUserKeys || !project.IsActive)
            return Results.Content(PortalLayout("Not Found",
                @"<main class=""container""><h2>Project not available</h2>
                  <p><a href=""/portal/"">Back to portal</a></p></main>",
                userId: userId), "text/html");

        var userKey = store.GetActiveUserApiKey(userId, id);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekAgo = today.AddDays(-7);
        var todayCost = store.GetUserCostForDate(id, userId, today);

        // Flash cookie for just-created key
        string? justCreated = null;
        if (ctx.Request.Query.ContainsKey("created"))
        {
            justCreated = ctx.Request.Cookies["llimit_flash_key"];
            if (justCreated is not null)
                ctx.Response.Cookies.Delete("llimit_flash_key",
                    new CookieOptions { Path = $"/portal/projects/{id}" });
        }

        string keySection;
        if (justCreated is not null)
        {
            keySection = $@"
<article style=""border-left:4px solid green;padding:1rem;"">
  <header><h4>Your API Key (shown once!)</h4></header>
  <p>Copy this key now. It will <strong>not</strong> be shown again.</p>
  <pre style=""word-break:break-all;white-space:pre-wrap;""><code>{Enc(justCreated)}</code></pre>
  <p>Use this in the <code>api-key</code> header when calling the proxy.</p>
</article>";
        }
        else if (userKey is not null)
        {
            keySection = $@"
<article>
  <header><h4>API Key</h4></header>
  <p>Active key created on {Enc(userKey.CreatedAt[..10])}.</p>
  <form method=""post"" action=""/portal/projects/{Enc(id)}/keys/revoke""
        onsubmit=""return confirm('Revoke this key? You will need to create a new one.')"">
    <button type=""submit"" class=""secondary outline"">Revoke Key</button>
  </form>
</article>";
        }
        else
        {
            keySection = $@"
<article>
  <header><h4>API Key</h4></header>
  <p>You don't have a key for this project yet.</p>
  <form method=""post"" action=""/portal/projects/{Enc(id)}/keys"">
    <button type=""submit"">Create API Key</button>
  </form>
</article>";
        }

        var recentUsage = store.GetUsage(id, weekAgo, today)
            .Where(u => u.UserId == userId)
            .OrderByDescending(u => u.Date)
            .ToList();

        var usageRows = "";
        foreach (var u in recentUsage)
        {
            usageRows += $@"
<tr>
  <td>{Enc(u.Date)}</td>
  <td>${u.TotalCost:F4}</td>
  <td>{u.RequestCount}</td>
  <td>{u.PromptTokens:N0}</td>
  <td>{u.CompletionTokens:N0}</td>
</tr>";
        }

        var html = PortalLayout($"Project: {Enc(project.Name)}", $@"
<main class=""container"">
  <nav aria-label=""breadcrumb""><ul>
    <li><a href=""/portal/"">Portal</a></li>
    <li>{Enc(project.Name)}</li>
  </ul></nav>

  <h2>{Enc(project.Name)}</h2>
  <p>Today: <strong>${todayCost:F4}</strong> | Daily limit: {Fmt(project.DefaultUserBudgetDaily)}</p>
  {BuildBudgetBar(todayCost, project.DefaultUserBudgetDaily)}

  <section style=""margin-top:2rem;"">
    <h3>Your API Key</h3>
    {keySection}
  </section>

  <section>
    <h3>Your Usage (last 7 days)</h3>
    <figure>
    <table role=""grid"">
      <thead><tr><th>Date</th><th>Cost</th><th>Requests</th><th>Prompt</th><th>Completion</th></tr></thead>
      <tbody>
        {(usageRows.Length > 0 ? usageRows : "<tr><td colspan=\"5\">No usage data</td></tr>")}
      </tbody>
    </table>
    </figure>
  </section>
</main>", userId: userId);

        return Results.Content(html, "text/html");
    }

    // ── Create / Revoke Keys ──

    private static IResult CreateUserKey(string id, HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["UserId"]!;
        var project = store.GetProject(id);
        if (project is null || !project.AllowUserKeys || !project.IsActive)
            return Results.Redirect("/portal/");

        if (store.GetActiveUserApiKey(userId, id) is not null)
            return Results.Redirect($"/portal/projects/{WebUtility.UrlEncode(id)}");

        var plainKey = store.CreateUserApiKey(userId, id);

        ctx.Response.Cookies.Append("llimit_flash_key", plainKey, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = $"/portal/projects/{id}",
            MaxAge = TimeSpan.FromMinutes(1)
        });

        return Results.Redirect($"/portal/projects/{WebUtility.UrlEncode(id)}?created=1");
    }

    private static IResult RevokeUserKey(string id, HttpContext ctx, Store store)
    {
        var userId = (string)ctx.Items["UserId"]!;
        store.RevokeUserApiKey(userId, id);
        return Results.Redirect($"/portal/projects/{WebUtility.UrlEncode(id)}");
    }

    // ── Helpers ──

    private static string PortalLayout(string title, string body,
        bool includeNav = true, string? userId = null)
    {
        var nav = includeNav && userId is not null ? $@"
    <nav class=""container"">
      <ul><li><strong><a href=""/portal/"">LLimit Portal</a></strong></li></ul>
      <ul>
        <li><a href=""/portal/"">My Projects</a></li>
        <li><span>{Enc(userId)}</span></li>
        <li><a href=""/auth/logout"">Logout</a></li>
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
    <small>LLimit User Portal</small>
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

    private static string Fmt(double? v) =>
        v.HasValue ? $"${v.Value:F2}" : "<span style=\"color:gray;\">None</span>";
}
