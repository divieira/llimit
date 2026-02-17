using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace LLimit;

public record Project(
    string Id, string Name, string ApiKeyHash,
    double? BudgetDaily, double? BudgetWeekly, double? BudgetMonthly,
    double? DefaultUserBudgetDaily, double? DefaultUserBudgetWeekly, double? DefaultUserBudgetMonthly,
    bool IsActive, string CreatedAt, string UpdatedAt);

public record ModelPricing(string ModelPattern, double InputPerMillion, double OutputPerMillion, string UpdatedAt);

public record RequestLogEntry(
    long Id, string ProjectId, string UserId, string Timestamp,
    string Model, string Deployment, string Endpoint,
    int PromptTokens, int CompletionTokens, int TotalTokens,
    double CostUsd, int StatusCode,
    int OverheadMs, int UpstreamMs, int TransferMs, int TotalMs,
    bool IsStream, bool UsedFallbackPricing);

public record UsageDaily(string ProjectId, string UserId, string Date,
    double TotalCost, int PromptTokens, int CompletionTokens, int RequestCount);

public class Store : IDisposable
{
    private readonly string _connStr;

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
        conn.Execute("PRAGMA journal_mode=WAL");
        conn.Execute("PRAGMA synchronous=NORMAL");
        conn.Execute("PRAGMA busy_timeout=5000");

        var sql = GetMigrationSql();
        conn.Execute(sql);
    }

    private static string GetMigrationSql()
    {
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
        return conn.Query<Project>(
            "SELECT id, name, api_key_hash AS ApiKeyHash, budget_daily AS BudgetDaily, budget_weekly AS BudgetWeekly, budget_monthly AS BudgetMonthly, " +
            "default_user_budget_daily AS DefaultUserBudgetDaily, default_user_budget_weekly AS DefaultUserBudgetWeekly, default_user_budget_monthly AS DefaultUserBudgetMonthly, " +
            "is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt FROM projects").AsList();
    }

    public Project? GetProject(string id)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<Project>(
            "SELECT id, name, api_key_hash AS ApiKeyHash, budget_daily AS BudgetDaily, budget_weekly AS BudgetWeekly, budget_monthly AS BudgetMonthly, " +
            "default_user_budget_daily AS DefaultUserBudgetDaily, default_user_budget_weekly AS DefaultUserBudgetWeekly, default_user_budget_monthly AS DefaultUserBudgetMonthly, " +
            "is_active AS IsActive, created_at AS CreatedAt, updated_at AS UpdatedAt FROM projects WHERE id = @id", new { id });
    }

    public (Project project, string plainKey) CreateProject(string id, string name,
        double? budgetDaily = null, double? budgetWeekly = null, double? budgetMonthly = null,
        double? defaultUserBudgetDaily = null, double? defaultUserBudgetWeekly = null, double? defaultUserBudgetMonthly = null)
    {
        var plainKey = $"llimit-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey))).ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = Open();
        conn.Execute(
            "INSERT INTO projects (id, name, api_key_hash, budget_daily, budget_weekly, budget_monthly, " +
            "default_user_budget_daily, default_user_budget_weekly, default_user_budget_monthly, is_active, created_at, updated_at) " +
            "VALUES (@id, @name, @hash, @budgetDaily, @budgetWeekly, @budgetMonthly, @defaultUserBudgetDaily, @defaultUserBudgetWeekly, @defaultUserBudgetMonthly, 1, @now, @now)",
            new { id, name, hash, budgetDaily, budgetWeekly, budgetMonthly, defaultUserBudgetDaily, defaultUserBudgetWeekly, defaultUserBudgetMonthly, now });

        var project = GetProject(id)!;
        return (project, plainKey);
    }

    public void UpdateProject(string id, string? name = null,
        double? budgetDaily = null, double? budgetWeekly = null, double? budgetMonthly = null,
        double? defaultUserBudgetDaily = null, double? defaultUserBudgetWeekly = null, double? defaultUserBudgetMonthly = null,
        bool? isActive = null, bool clearBudgets = false)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();

        var sets = new List<string> { "updated_at = @now" };
        if (name != null) sets.Add("name = @name");
        if (clearBudgets || budgetDaily != null) sets.Add("budget_daily = @budgetDaily");
        if (clearBudgets || budgetWeekly != null) sets.Add("budget_weekly = @budgetWeekly");
        if (clearBudgets || budgetMonthly != null) sets.Add("budget_monthly = @budgetMonthly");
        if (clearBudgets || defaultUserBudgetDaily != null) sets.Add("default_user_budget_daily = @defaultUserBudgetDaily");
        if (clearBudgets || defaultUserBudgetWeekly != null) sets.Add("default_user_budget_weekly = @defaultUserBudgetWeekly");
        if (clearBudgets || defaultUserBudgetMonthly != null) sets.Add("default_user_budget_monthly = @defaultUserBudgetMonthly");
        if (isActive != null) sets.Add("is_active = @isActive");

        conn.Execute($"UPDATE projects SET {string.Join(", ", sets)} WHERE id = @id",
            new { id, name, budgetDaily, budgetWeekly, budgetMonthly, defaultUserBudgetDaily, defaultUserBudgetWeekly, defaultUserBudgetMonthly, isActive, now });
    }

    public void DeactivateProject(string id)
    {
        using var conn = Open();
        conn.Execute("UPDATE projects SET is_active = 0, updated_at = @now WHERE id = @id",
            new { id, now = DateTime.UtcNow.ToString("o") });
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

    // ── Request Log ──

    public void LogRequest(string projectId, string userId, string timestamp,
        string model, string deployment, string endpoint,
        int promptTokens, int completionTokens, double costUsd, int statusCode,
        int overheadMs, int upstreamMs, int transferMs, int totalMs,
        bool isStream, bool usedFallbackPricing)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO request_log (project_id, user_id, timestamp, model, deployment, endpoint, " +
            "prompt_tokens, completion_tokens, total_tokens, cost_usd, status_code, " +
            "overhead_ms, upstream_ms, transfer_ms, total_ms, is_stream, used_fallback_pricing) " +
            "VALUES (@projectId, @userId, @timestamp, @model, @deployment, @endpoint, " +
            "@promptTokens, @completionTokens, @totalTokens, @costUsd, @statusCode, " +
            "@overheadMs, @upstreamMs, @transferMs, @totalMs, @isStream, @usedFallbackPricing)",
            new
            {
                projectId, userId, timestamp, model, deployment, endpoint,
                promptTokens, completionTokens, totalTokens = promptTokens + completionTokens,
                costUsd, statusCode, overheadMs, upstreamMs, transferMs, totalMs,
                isStream, usedFallbackPricing
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
            "is_stream AS IsStream, used_fallback_pricing AS UsedFallbackPricing " +
            $"FROM request_log {where} ORDER BY id DESC LIMIT @perPage OFFSET @offset",
            new { projectId, userId, model, perPage, offset = (page - 1) * perPage }).AsList();
    }

    // ── Usage Daily ──

    public void UpsertUsageDaily(string projectId, string userId, string date,
        double cost, int promptTokens, int completionTokens)
    {
        using var conn = Open();
        conn.Execute(
            "INSERT INTO usage_daily (project_id, user_id, date, total_cost, prompt_tokens, completion_tokens, request_count) " +
            "VALUES (@projectId, @userId, @date, @cost, @promptTokens, @completionTokens, 1) " +
            "ON CONFLICT(project_id, user_id, date) DO UPDATE SET " +
            "total_cost = total_cost + @cost, prompt_tokens = prompt_tokens + @promptTokens, " +
            "completion_tokens = completion_tokens + @completionTokens, request_count = request_count + 1",
            new { projectId, userId, date, cost, promptTokens, completionTokens });
    }

    public List<UsageDaily> GetUsage(string projectId, string? from = null, string? to = null)
    {
        using var conn = Open();
        var where = "WHERE project_id = @projectId";
        if (from != null) where += " AND date >= @from";
        if (to != null) where += " AND date <= @to";

        return conn.Query<UsageDaily>(
            "SELECT project_id AS ProjectId, user_id AS UserId, date, total_cost AS TotalCost, " +
            "prompt_tokens AS PromptTokens, completion_tokens AS CompletionTokens, request_count AS RequestCount " +
            $"FROM usage_daily {where} ORDER BY date DESC",
            new { projectId, from, to }).AsList();
    }

    public Dictionary<string, double> GetAllProjectCostsForDate(string date)
    {
        using var conn = Open();
        return conn.Query<(string ProjectId, double TotalCost)>(
            "SELECT project_id AS ProjectId, COALESCE(SUM(total_cost), 0) AS TotalCost FROM usage_daily WHERE date = @date GROUP BY project_id",
            new { date }).ToDictionary(x => x.ProjectId, x => x.TotalCost);
    }

    public double GetProjectCostForPeriod(string projectId, string fromDate, string toDate)
    {
        using var conn = Open();
        return conn.ExecuteScalar<double>(
            "SELECT COALESCE(SUM(total_cost), 0) FROM usage_daily WHERE project_id = @projectId AND date >= @fromDate AND date <= @toDate",
            new { projectId, fromDate, toDate });
    }

    public double GetUserCostForPeriod(string projectId, string userId, string fromDate, string toDate)
    {
        using var conn = Open();
        return conn.ExecuteScalar<double>(
            "SELECT COALESCE(SUM(total_cost), 0) FROM usage_daily WHERE project_id = @projectId AND user_id = @userId AND date >= @fromDate AND date <= @toDate",
            new { projectId, userId, fromDate, toDate });
    }

    public List<(string UserId, double TodayCost)> GetProjectUsers(string projectId)
    {
        using var conn = Open();
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        return conn.Query<(string UserId, double TodayCost)>(
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
