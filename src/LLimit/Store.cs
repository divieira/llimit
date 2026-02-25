using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace LLimit;

public record Project(
    string Id, string Name, string ApiKeyHash,
    string EndpointUrl, string EndpointKey,
    double? BudgetDaily, double? DefaultUserBudgetDaily,
    bool AllowUserKeys, bool IsActive,
    string CreatedAt, string UpdatedAt);

public record User(string Id, string Email, string DisplayName, string CreatedAt, string LastLogin);

public record UserApiKey(int Id, string UserId, string ProjectId, string ApiKeyHash, string CreatedAt, string? RevokedAt);

public record UserSession(string TokenHash, string UserId, string CreatedAt, string ExpiresAt);

public record ModelPricing(string ModelPattern, double InputPerMillion, double OutputPerMillion, string UpdatedAt);

public record RequestLogEntry(
    long Id, string ProjectId, string? UserId, string Timestamp,
    string Model, string Deployment, string Endpoint,
    int PromptTokens, int CompletionTokens, int TotalTokens,
    double CostUsd, int StatusCode,
    int OverheadMs, int UpstreamMs, int TransferMs, int TotalMs,
    bool IsStream);

public record UsageDaily(string ProjectId, string? UserId, string Date,
    double TotalCost, int PromptTokens, int CompletionTokens, int RequestCount);

public class Store : IDisposable
{
    private readonly string _connStr;

    // DRY: shared column list for all Project SELECT queries
    private const string ProjectCols =
        "id, name, api_key_hash AS ApiKeyHash, " +
        "endpoint_url AS EndpointUrl, endpoint_key AS EndpointKey, " +
        "budget_daily AS BudgetDaily, default_user_budget_daily AS DefaultUserBudgetDaily, " +
        "allow_user_keys AS AllowUserKeys, is_active AS IsActive, " +
        "created_at AS CreatedAt, updated_at AS UpdatedAt";

    public Store(string dbPath)
    {
        _connStr = $"Data Source={dbPath}";
        Init();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        // WAL mode allows concurrent reads during writes.
        // See: https://www.sqlite.org/wal.html
        conn.Execute("PRAGMA journal_mode=WAL");
        // NORMAL sync is safe with WAL — only loses data on OS crash, not app crash.
        // See: https://www.sqlite.org/pragma.html#pragma_synchronous
        conn.Execute("PRAGMA synchronous=NORMAL");
        // Wait up to 5s for locks instead of failing immediately.
        // See: https://www.sqlite.org/pragma.html#pragma_busy_timeout
        conn.Execute("PRAGMA busy_timeout=5000");

        var sql = GetMigrationSql();
        conn.Execute(sql);
    }

    private static string GetMigrationSql()
    {
        // SQL migrations are embedded in the assembly to ship as a single binary.
        // See: https://learn.microsoft.com/en-us/dotnet/core/extensions/resources
        var asm = typeof(Store).Assembly;
        using var stream = asm.GetManifestResourceStream("LLimit.Migrations.001_initial.sql")
            ?? throw new InvalidOperationException("Migration SQL not found as embedded resource");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Projects ──

    public List<Project> GetAllProjects()
    {
        using var conn = Open();
        return conn.Query<Project>($"SELECT {ProjectCols} FROM projects").AsList();
    }

    public Project? GetProject(string id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<Project>(
            $"SELECT {ProjectCols} FROM projects WHERE id = @id", new { id });
    }

    public (Project project, string plainKey) CreateProject(string id, string name,
        string endpointUrl, string endpointKey,
        double? budgetDaily = null, double? defaultUserBudgetDaily = null,
        bool allowUserKeys = false)
    {
        var plainKey = $"llimit-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey))).ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = Open();
        conn.Execute(
            "INSERT INTO projects (id, name, api_key_hash, endpoint_url, endpoint_key, " +
            "budget_daily, default_user_budget_daily, allow_user_keys, is_active, created_at, updated_at) " +
            "VALUES (@id, @name, @hash, @endpointUrl, @endpointKey, " +
            "@budgetDaily, @defaultUserBudgetDaily, @allowUserKeys, 1, @now, @now)",
            new { id, name, hash, endpointUrl, endpointKey, budgetDaily, defaultUserBudgetDaily, allowUserKeys, now });

        var project = GetProject(id)!;
        return (project, plainKey);
    }

    public void UpdateProject(string id, string? name = null,
        double? budgetDaily = null, double? defaultUserBudgetDaily = null,
        bool? isActive = null, bool clearBudgets = false,
        bool? allowUserKeys = null,
        string? endpointUrl = null, string? endpointKey = null,
        bool clearEndpoint = false)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();

        var sets = new List<string> { "updated_at = @now" };
        if (name != null) sets.Add("name = @name");
        if (clearBudgets || budgetDaily != null) sets.Add("budget_daily = @budgetDaily");
        if (clearBudgets || defaultUserBudgetDaily != null) sets.Add("default_user_budget_daily = @defaultUserBudgetDaily");
        if (isActive != null) sets.Add("is_active = @isActive");
        if (allowUserKeys != null) sets.Add("allow_user_keys = @allowUserKeys");
        if (clearEndpoint || endpointUrl != null) sets.Add("endpoint_url = @endpointUrl");
        if (clearEndpoint || endpointKey != null) sets.Add("endpoint_key = @endpointKey");

        conn.Execute($"UPDATE projects SET {string.Join(", ", sets)} WHERE id = @id",
            new { id, name, budgetDaily, defaultUserBudgetDaily, isActive, allowUserKeys, endpointUrl, endpointKey, now });
    }

    public void DeactivateProject(string id)
    {
        using var conn = Open();
        conn.Execute("UPDATE projects SET is_active = 0, updated_at = @now WHERE id = @id",
            new { id, now = DateTime.UtcNow.ToString("o") });
    }

    // ── API Key Resolution ──

    public Project? ResolveApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        using var conn = Open();
        return conn.QueryFirstOrDefault<Project>(
            $"SELECT {ProjectCols} FROM projects WHERE api_key_hash = @hash AND is_active = 1", new { hash });
    }

    public Project? ResolveApiKeyIncludingInactive(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        using var conn = Open();
        return conn.QueryFirstOrDefault<Project>(
            $"SELECT {ProjectCols} FROM projects WHERE api_key_hash = @hash", new { hash });
    }

    /// <summary>
    /// Resolves a user API key to (Project, UserId). Returns null if not found or revoked.
    /// </summary>
    public (Project Project, string UserId)? ResolveUserApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        using var conn = Open();

        var row = conn.QueryFirstOrDefault<(string UserId, string ProjectId)>(
            "SELECT user_id AS UserId, project_id AS ProjectId FROM user_api_keys " +
            "WHERE api_key_hash = @hash AND revoked_at IS NULL", new { hash });

        if (row.ProjectId is null) return null;

        var proj = conn.QueryFirstOrDefault<Project>(
            $"SELECT {ProjectCols} FROM projects WHERE id = @id AND is_active = 1",
            new { id = row.ProjectId });

        return proj is null ? null : (proj, row.UserId);
    }

    // ── Users ──

    public void UpsertUser(string id, string email, string displayName)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        conn.Execute(
            "INSERT INTO users (id, email, display_name, created_at, last_login) " +
            "VALUES (@id, @email, @displayName, @now, @now) " +
            "ON CONFLICT(id) DO UPDATE SET display_name = @displayName, last_login = @now",
            new { id, email, displayName, now });
    }

    public User? GetUser(string id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<User>(
            "SELECT id, email, display_name AS DisplayName, created_at AS CreatedAt, last_login AS LastLogin " +
            "FROM users WHERE id = @id", new { id });
    }

    // ── User API Keys ──

    public string CreateUserApiKey(string userId, string projectId)
    {
        var plainKey = $"llimit-u-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey))).ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = Open();
        conn.Execute(
            "INSERT INTO user_api_keys (user_id, project_id, api_key_hash, created_at) " +
            "VALUES (@userId, @projectId, @hash, @now)",
            new { userId, projectId, hash, now });

        return plainKey;
    }

    public UserApiKey? GetActiveUserApiKey(string userId, string projectId)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<UserApiKey>(
            "SELECT id, user_id AS UserId, project_id AS ProjectId, api_key_hash AS ApiKeyHash, " +
            "created_at AS CreatedAt, revoked_at AS RevokedAt " +
            "FROM user_api_keys WHERE user_id = @userId AND project_id = @projectId AND revoked_at IS NULL",
            new { userId, projectId });
    }

    public void RevokeUserApiKey(string userId, string projectId)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        conn.Execute(
            "UPDATE user_api_keys SET revoked_at = @now WHERE user_id = @userId AND project_id = @projectId AND revoked_at IS NULL",
            new { userId, projectId, now });
    }

    public List<UserApiKey> GetUserApiKeys(string userId)
    {
        using var conn = Open();
        return conn.Query<UserApiKey>(
            "SELECT id, user_id AS UserId, project_id AS ProjectId, api_key_hash AS ApiKeyHash, " +
            "created_at AS CreatedAt, revoked_at AS RevokedAt " +
            "FROM user_api_keys WHERE user_id = @userId AND revoked_at IS NULL",
            new { userId }).AsList();
    }

    public List<Project> GetProjectsAllowingUserKeys()
    {
        using var conn = Open();
        return conn.Query<Project>(
            $"SELECT {ProjectCols} FROM projects WHERE allow_user_keys = 1 AND is_active = 1").AsList();
    }

    // ── User Sessions ──

    public void CreateUserSession(string tokenHash, string userId, TimeSpan duration)
    {
        var now = DateTime.UtcNow;
        using var conn = Open();
        conn.Execute(
            "INSERT INTO user_sessions (token_hash, user_id, created_at, expires_at) " +
            "VALUES (@tokenHash, @userId, @createdAt, @expiresAt)",
            new { tokenHash, userId, createdAt = now.ToString("o"), expiresAt = now.Add(duration).ToString("o") });
    }

    public UserSession? GetUserSession(string tokenHash)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<UserSession>(
            "SELECT token_hash AS TokenHash, user_id AS UserId, created_at AS CreatedAt, expires_at AS ExpiresAt " +
            "FROM user_sessions WHERE token_hash = @tokenHash", new { tokenHash });
    }

    public void DeleteUserSession(string tokenHash)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM user_sessions WHERE token_hash = @tokenHash", new { tokenHash });
    }

    public void CleanExpiredSessions()
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        conn.Execute("DELETE FROM user_sessions WHERE expires_at < @now", new { now });
    }

    // ── Pricing ──

    public List<ModelPricing> GetAllPricing()
    {
        using var conn = Open();
        return conn.Query<ModelPricing>(
            "SELECT model_pattern AS ModelPattern, input_per_million AS InputPerMillion, output_per_million AS OutputPerMillion, updated_at AS UpdatedAt FROM model_pricing").AsList();
    }

    public void UpsertPricing(string modelPattern, double inputPerMillion, double outputPerMillion)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO model_pricing (model_pattern, input_per_million, output_per_million, updated_at) " +
            "VALUES (@modelPattern, @inputPerMillion, @outputPerMillion, @now) " +
            "ON CONFLICT(model_pattern) DO UPDATE SET input_per_million = @inputPerMillion, output_per_million = @outputPerMillion, updated_at = @now",
            new { modelPattern, inputPerMillion, outputPerMillion, now = DateTime.UtcNow.ToString("o") });
    }

    public void DeletePricing(string modelPattern)
    {
        using var conn = Open();
        conn.Execute("DELETE FROM model_pricing WHERE model_pattern = @modelPattern", new { modelPattern });
    }

    // ── LiteLLM Price Cache (DB fallback) ──

    public void SaveLiteLlmPrices(Dictionary<string, ModelPrice> prices)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM litellm_prices", transaction: tx);
        var now = DateTime.UtcNow.ToString("o");
        foreach (var kv in prices)
        {
            conn.Execute(
                "INSERT INTO litellm_prices (model, input_per_token, output_per_token, fetched_at) " +
                "VALUES (@model, @input, @output, @now)",
                new { model = kv.Key, input = kv.Value.InputPerToken, output = kv.Value.OutputPerToken, now },
                transaction: tx);
        }
        tx.Commit();
    }

    public Dictionary<string, ModelPrice> LoadLiteLlmPrices()
    {
        using var conn = Open();
        return conn.Query<(string Model, double InputPerToken, double OutputPerToken)>(
            "SELECT model, input_per_token AS InputPerToken, output_per_token AS OutputPerToken FROM litellm_prices")
            .ToDictionary(r => r.Model, r => new ModelPrice(r.InputPerToken, r.OutputPerToken), StringComparer.OrdinalIgnoreCase);
    }

    // ── Request Log ──

    public void LogRequest(string projectId, string? userId, string timestamp,
        string model, string deployment, string endpoint,
        int promptTokens, int completionTokens, double costUsd, int statusCode,
        int overheadMs, int upstreamMs, int transferMs, int totalMs,
        bool isStream)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO request_log (project_id, user_id, timestamp, model, deployment, endpoint, " +
            "prompt_tokens, completion_tokens, total_tokens, cost_usd, status_code, " +
            "overhead_ms, upstream_ms, transfer_ms, total_ms, is_stream) " +
            "VALUES (@projectId, @userId, @timestamp, @model, @deployment, @endpoint, " +
            "@promptTokens, @completionTokens, @totalTokens, @costUsd, @statusCode, " +
            "@overheadMs, @upstreamMs, @transferMs, @totalMs, @isStream)",
            new
            {
                projectId, userId, timestamp, model, deployment, endpoint,
                promptTokens, completionTokens, totalTokens = promptTokens + completionTokens,
                costUsd, statusCode, overheadMs, upstreamMs, transferMs, totalMs,
                isStream
            });
    }

    public List<RequestLogEntry> GetLogs(string projectId, string? userId = null, string? model = null, int page = 1, int perPage = 50)
    {
        using var conn = Open();
        var where = "WHERE project_id = @projectId";
        if (userId != null) where += " AND user_id = @userId";
        if (model != null) where += " AND model = @model";

        return conn.Query<RequestLogEntry>(
            "SELECT id, project_id AS ProjectId, user_id AS UserId, timestamp, model, deployment, endpoint, " +
            "prompt_tokens AS PromptTokens, completion_tokens AS CompletionTokens, total_tokens AS TotalTokens, " +
            "cost_usd AS CostUsd, status_code AS StatusCode, " +
            "overhead_ms AS OverheadMs, upstream_ms AS UpstreamMs, transfer_ms AS TransferMs, total_ms AS TotalMs, " +
            "is_stream AS IsStream " +
            $"FROM request_log {where} ORDER BY id DESC LIMIT @perPage OFFSET @offset",
            new { projectId, userId, model, perPage, offset = (page - 1) * perPage }).AsList();
    }

    // ── Usage Daily ──

    public void UpsertUsageDaily(string projectId, string? userId, DateOnly date,
        double cost, int promptTokens, int completionTokens)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var conn = Open();
        conn.Execute(
            "INSERT INTO usage_daily (project_id, user_id, date, total_cost, prompt_tokens, completion_tokens, request_count) " +
            "VALUES (@projectId, @userId, @dateStr, @cost, @promptTokens, @completionTokens, 1) " +
            "ON CONFLICT(project_id, user_id, date) DO UPDATE SET " +
            "total_cost = total_cost + @cost, prompt_tokens = prompt_tokens + @promptTokens, " +
            "completion_tokens = completion_tokens + @completionTokens, request_count = request_count + 1",
            new { projectId, userId, dateStr, cost, promptTokens, completionTokens });
    }

    public List<UsageDaily> GetUsage(string projectId, DateOnly? from = null, DateOnly? to = null)
    {
        using var conn = Open();
        var where = "WHERE project_id = @projectId";
        var fromStr = from?.ToString("yyyy-MM-dd");
        var toStr = to?.ToString("yyyy-MM-dd");
        if (from != null) where += " AND date >= @fromStr";
        if (to != null) where += " AND date <= @toStr";

        return conn.Query<UsageDaily>(
            "SELECT project_id AS ProjectId, user_id AS UserId, date, total_cost AS TotalCost, " +
            "prompt_tokens AS PromptTokens, completion_tokens AS CompletionTokens, request_count AS RequestCount " +
            $"FROM usage_daily {where} ORDER BY date DESC",
            new { projectId, fromStr, toStr }).AsList();
    }

    public Dictionary<string, double> GetAllProjectCostsForDate(DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var conn = Open();
        return conn.Query<(string ProjectId, double TotalCost)>(
            "SELECT project_id AS ProjectId, COALESCE(SUM(total_cost), 0) AS TotalCost FROM usage_daily WHERE date = @dateStr GROUP BY project_id",
            new { dateStr }).ToDictionary(x => x.ProjectId, x => x.TotalCost);
    }

    public double GetProjectCostForDate(string projectId, DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var conn = Open();
        return conn.ExecuteScalar<double>(
            "SELECT COALESCE(SUM(total_cost), 0) FROM usage_daily WHERE project_id = @projectId AND date = @dateStr",
            new { projectId, dateStr });
    }

    public double GetUserCostForDate(string projectId, string userId, DateOnly date)
    {
        var dateStr = date.ToString("yyyy-MM-dd");
        using var conn = Open();
        return conn.ExecuteScalar<double>(
            "SELECT COALESCE(SUM(total_cost), 0) FROM usage_daily WHERE project_id = @projectId AND user_id = @userId AND date = @dateStr",
            new { projectId, userId, dateStr });
    }

    public List<(string? UserId, double TodayCost)> GetProjectUsers(string projectId)
    {
        using var conn = Open();
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        return conn.Query<(string? UserId, double TodayCost)>(
            "SELECT user_id AS UserId, COALESCE(SUM(CASE WHEN date = @today THEN total_cost ELSE 0 END), 0) AS TodayCost " +
            "FROM usage_daily WHERE project_id = @projectId GROUP BY user_id ORDER BY TodayCost DESC",
            new { projectId, today }).AsList();
    }

    // ── Health ──

    public bool CheckDb()
    {
        try
        {
            using var conn = Open();
            conn.ExecuteScalar<int>("SELECT 1");
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        // Connections are opened/closed per call — nothing to dispose
        GC.SuppressFinalize(this);
    }
}
