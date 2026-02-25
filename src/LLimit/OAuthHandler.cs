using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LLimit;

/// <summary>
/// Handles Azure AD (Entra) OAuth 2.0 authorization-code flow for user sign-in.
/// Requires the following environment variables to be set:
///   AZURE_AD_TENANT_ID      – Azure AD tenant ID
///   AZURE_AD_CLIENT_ID      – OAuth application (client) ID
///   AZURE_AD_CLIENT_SECRET  – OAuth client secret
///   LLIMIT_BASE_URL         – Public base URL of this service (e.g. https://llimit.example.com)
///   AZURE_AD_DOMAIN         – (optional) Corporate email domain; users from this domain get a
///                             short username (the part before @), others use their full email.
/// </summary>
public static class OAuthHandler
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/auth/login", HandleLogin);
        app.MapGet("/auth/callback", HandleCallback);
        app.MapGet("/auth/logout", HandleLogout);
    }

    // ── Routes ──

    private static IResult HandleLogin(HttpContext ctx, IConfiguration cfg)
    {
        if (!TryGetOAuthConfig(cfg, out var config))
        {
            ctx.Response.StatusCode = 503;
            return Results.Content("<h1>OAuth login is not configured on this server.</h1>", "text/html");
        }

        // Store a random state value in a short-lived cookie to prevent CSRF.
        // See: https://datatracker.ietf.org/doc/html/rfc6749#section-10.12
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        ctx.Response.Cookies.Append("llimit_oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var redirectUri = $"{config.BaseUrl}/auth/callback";
        var authUrl = "https://login.microsoftonline.com/" + Uri.EscapeDataString(config.TenantId) +
            "/oauth2/v2.0/authorize" +
            "?client_id=" + Uri.EscapeDataString(config.ClientId) +
            "&response_type=code" +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&scope=" + Uri.EscapeDataString("openid email profile") +
            "&state=" + state +
            "&response_mode=query";

        return Results.Redirect(authUrl);
    }

    private static async Task<IResult> HandleCallback(HttpContext ctx, IConfiguration cfg,
        Store store, IHttpClientFactory factory)
    {
        if (!TryGetOAuthConfig(cfg, out var config))
        {
            ctx.Response.StatusCode = 503;
            return Results.Content("<h1>OAuth login is not configured on this server.</h1>", "text/html");
        }

        // Validate state to prevent CSRF
        var state = ctx.Request.Query["state"].FirstOrDefault();
        var cookieState = ctx.Request.Cookies["llimit_oauth_state"];
        ctx.Response.Cookies.Delete("llimit_oauth_state");

        if (string.IsNullOrEmpty(state) || state != cookieState)
            return Results.Redirect("/portal/login?error=state_mismatch");

        var code = ctx.Request.Query["code"].FirstOrDefault();
        if (string.IsNullOrEmpty(code))
        {
            var oauthError = ctx.Request.Query["error"].FirstOrDefault() ?? "no_code";
            return Results.Redirect($"/portal/login?error={Uri.EscapeDataString(oauthError)}");
        }

        string email, displayName;
        try
        {
            var redirectUri = $"{config.BaseUrl}/auth/callback";
            using var http = factory.CreateClient();
            (email, displayName) = await ExchangeCodeForUser(http, config, redirectUri, code);
        }
        catch (Exception)
        {
            return Results.Redirect("/portal/login?error=token_exchange_failed");
        }

        var userId = ExtractUsername(email, config.CorporateDomain);

        store.GetOrCreateUser(userId, email, displayName);

        // Sign the user ID with the admin token so we can verify sessions without a DB lookup.
        var adminToken = cfg["LLIMIT_ADMIN_TOKEN"] ?? "";
        var sessionValue = CreateUserSession(userId, adminToken);
        ctx.Response.Cookies.Append("llimit_user_session", sessionValue, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromDays(7)
        });

        return Results.Redirect("/portal");
    }

    private static IResult HandleLogout(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete("llimit_user_session");
        return Results.Redirect("/portal/login");
    }

    // ── Session helpers (used by UserPortal for auth filter) ──

    /// <summary>
    /// Creates a signed session cookie value: base64(userId)|HMAC-SHA256(userId, adminToken).
    /// </summary>
    public static string CreateUserSession(string userId, string adminToken)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(userId));
        var hmac = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(adminToken),
            Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();
        return $"{encoded}|{hmac}";
    }

    /// <summary>
    /// Validates the session cookie and returns the userId, or null if invalid.
    /// Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    public static string? ValidateUserSession(string? cookieValue, string adminToken)
    {
        if (string.IsNullOrEmpty(cookieValue)) return null;
        var pipeIdx = cookieValue.LastIndexOf('|');
        if (pipeIdx < 0) return null;

        var encoded = cookieValue[..pipeIdx];
        var providedHmac = cookieValue[(pipeIdx + 1)..];

        string userId;
        try { userId = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch { return null; }

        var expectedHmac = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(adminToken),
            Encoding.UTF8.GetBytes(userId))).ToLowerInvariant();

        // Both strings must be the same length for FixedTimeEquals — pad if needed to avoid
        // short-circuit on length mismatch, which would leak timing information.
        var providedBytes = Encoding.UTF8.GetBytes(providedHmac.PadRight(expectedHmac.Length));
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHmac);

        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            return null;

        return userId;
    }

    // ── OAuth config ──

    public record OAuthConfig(
        string TenantId, string ClientId, string ClientSecret,
        string BaseUrl, string? CorporateDomain);

    public static bool TryGetOAuthConfig(IConfiguration cfg, out OAuthConfig config)
    {
        config = default!;
        var tenantId = cfg["AZURE_AD_TENANT_ID"];
        var clientId = cfg["AZURE_AD_CLIENT_ID"];
        var clientSecret = cfg["AZURE_AD_CLIENT_SECRET"];
        var baseUrl = cfg["LLIMIT_BASE_URL"];

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) ||
            string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(baseUrl))
            return false;

        config = new OAuthConfig(tenantId, clientId, clientSecret,
            baseUrl.TrimEnd('/'), cfg["AZURE_AD_DOMAIN"]);
        return true;
    }

    // ── Token exchange ──

    private static async Task<(string email, string displayName)> ExchangeCodeForUser(
        HttpClient http, OAuthConfig config, string redirectUri, string code)
    {
        var tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(config.TenantId)}/oauth2/v2.0/token";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid email profile"
        });

        var resp = await http.PostAsync(tokenUrl, content);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("id_token", out var idTokenEl))
            throw new InvalidOperationException($"id_token missing from token response: {json}");

        var idToken = idTokenEl.GetString()
            ?? throw new InvalidOperationException("id_token is null");

        return ParseIdToken(idToken);
    }

    // Decodes a JWT payload without signature verification.
    // This is safe because the token is obtained directly from Azure's token endpoint
    // over HTTPS — we trust it implicitly without re-verifying the RS256 signature.
    private static (string email, string displayName) ParseIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) throw new InvalidOperationException("Invalid JWT format");

        // base64url → standard base64, then pad
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        var bytes = Convert.FromBase64String(payload);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        // Azure may use "email", "preferred_username", or "upn" for the user's address
        var email = (root.TryGetProperty("email", out var e) ? e.GetString() : null)
            ?? (root.TryGetProperty("preferred_username", out var pu) ? pu.GetString() : null)
            ?? (root.TryGetProperty("upn", out var upn) ? upn.GetString() : null);

        if (string.IsNullOrEmpty(email))
            throw new InvalidOperationException("Email claim missing from ID token");

        var displayName = root.TryGetProperty("name", out var n) ? n.GetString() ?? email : email;
        return (email, displayName);
    }

    // ── Username derivation ──

    /// <summary>
    /// Derives the user ID from an email address.
    /// For corporate-domain users the ID is the local part (before @);
    /// for all others the full email is used.
    /// </summary>
    public static string ExtractUsername(string email, string? corporateDomain)
    {
        if (!string.IsNullOrEmpty(corporateDomain)
            && email.EndsWith("@" + corporateDomain, StringComparison.OrdinalIgnoreCase))
        {
            var atIdx = email.IndexOf('@');
            if (atIdx > 0) return email[..atIdx];
        }
        return email;
    }
}
