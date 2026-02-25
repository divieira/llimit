using System.Diagnostics;
using System.Text.Json;

namespace LLimit;

public record BudgetDeny(string Scope, double Budget, double Used)
{
    public string Error { get; } = "budget_exceeded";
}

public static class ProxyHandler
{
    public static void MapProxy(this WebApplication app)
    {
        app.MapPost("/openai/deployments/{deployment}/{**rest}", HandleProxy);
    }

    private static async Task HandleProxy(HttpContext ctx, string deployment, string rest,
        Store store, PricingTable pricing,
        IHttpClientFactory factory, IConfiguration cfg, ILogger<Program> logger)
    {
        var sw = Stopwatch.StartNew();

        // ── Auth: try project key first, then user key ──
        var apiKey = ctx.Request.Headers["api-key"].FirstOrDefault() ?? "";

        Project? proj;
        string? uid;

        var projectByKey = store.ResolveApiKey(apiKey);
        if (projectByKey is not null)
        {
            proj = projectByKey;
            // Project-key callers identify themselves via the X-LLimit-User header (optional)
            uid = ctx.Request.Headers["X-LLimit-User"].FirstOrDefault();
        }
        else
        {
            // Try resolving as a personal user key
            var userKey = store.ResolveUserApiKey(apiKey);
            if (userKey is not null)
            {
                (proj, uid) = userKey.Value;
            }
            else
            {
                // Give a more specific error for deactivated-project keys
                var inactiveProject = store.ResolveApiKeyIncludingInactive(apiKey);
                if (inactiveProject is not null)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "project_deactivated" });
                    return;
                }

                var inactiveUserKey = store.ResolveUserApiKeyIncludingInactive(apiKey);
                if (inactiveUserKey is not null)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsJsonAsync(new { error = "project_deactivated" });
                    return;
                }

                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "invalid_api_key" });
                return;
            }
        }

        // ── Budget check (daily, queries DB directly) ──
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (proj.BudgetDaily is { } projectLimit)
        {
            var used = store.GetProjectCostForDate(proj.Id, today);
            if (used >= projectLimit)
            {
                ctx.Response.StatusCode = 429;
                await ctx.Response.WriteAsJsonAsync(new BudgetDeny("project", projectLimit, used));
                return;
            }
        }
        if (proj.DefaultUserBudgetDaily is { } userLimit && uid is not null)
        {
            var used = store.GetUserCostForDate(proj.Id, uid, today);
            if (used >= userLimit)
            {
                ctx.Response.StatusCode = 429;
                await ctx.Response.WriteAsJsonAsync(new BudgetDeny("user", userLimit, used));
                return;
            }
        }

        // ── Parse request body ──
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var bodyBytes = ms.ToArray();
        var isStream = false;
        var requestModel = (string?)null;

        try
        {
            using var doc = JsonDocument.Parse(bodyBytes);
            isStream = doc.RootElement.TryGetProperty("stream", out var sv) && sv.GetBoolean();

            // Extract model from request body if present (OpenAI SDK clients include it).
            // Azure OpenAI ignores this field (deployment determines the model), but we use
            // it for pre-validation when available.
            if (doc.RootElement.TryGetProperty("model", out var mv))
                requestModel = mv.GetString();

            // Ensure stream_options.include_usage=true for streaming requests.
            // Azure OpenAI only includes token usage in the final SSE chunk when this is set.
            // For non-streaming requests, usage is always included in the response body.
            // See: https://learn.microsoft.com/en-us/azure/ai-services/openai/reference#chat-completions
            if (isStream)
                bodyBytes = EnsureStreamIncludeUsage(doc, bodyBytes);
        }
        catch (JsonException)
        {
            // Not JSON or malformed — forward as-is
        }

        // ── Pre-validate model pricing (if model known from request) ──
        if (requestModel is not null && !pricing.HasPricing(requestModel))
        {
            ctx.Response.StatusCode = 422;
            await ctx.Response.WriteAsJsonAsync(new { error = "unknown_model", model = requestModel });
            return;
        }

        // ── Determine Azure endpoint and API key ──
        // Per-project endpoint overrides the global configuration.  This lets different
        // projects route to different Azure OpenAI resources or regions.
        var azureEndpoint = proj.EndpointUrl ?? cfg["AZURE_OPENAI_ENDPOINT"]!;
        var azureKey = proj.EndpointKey ?? cfg["AZURE_OPENAI_API_KEY"]!;
        var url = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{deployment}/{rest}{ctx.Request.QueryString}";

        var client = factory.CreateClient("Azure");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("api-key", azureKey);

        var overheadMs = (int)sw.ElapsedMilliseconds;

        // For streaming responses, start processing as soon as headers arrive rather than
        // buffering the entire body. This enables SSE pass-through with minimal latency.
        // See: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption
        var completionOption = isStream
            ? HttpCompletionOption.ResponseHeadersRead
            : HttpCompletionOption.ResponseContentRead;

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, completionOption);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reach Azure for {Deployment}/{Endpoint}", deployment, rest);
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "upstream_error" });
            return;
        }

        var upstreamMs = (int)sw.ElapsedMilliseconds - overheadMs;

        // ── Copy response headers ──
        // Skip transfer-encoding: Kestrel manages its own chunked encoding when writing
        // to the response body. Forwarding the upstream's "chunked" header causes a
        // double-encoding conflict.
        // See: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel
        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (h.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }

        // ── Forward response body and extract usage ──
        // Both streaming and buffered paths forward the response to the client first,
        // then extract usage. For streaming (SSE), data is forwarded line-by-line as it
        // arrives; the final chunk with empty choices[] contains the usage summary.
        // For buffered responses, the full body is sent then parsed.
        // See: https://learn.microsoft.com/en-us/azure/ai-services/openai/reference#chat-completions
        string model;
        int promptTokens, completionTokens;

        using (resp)
        {
            if (isStream)
                (model, promptTokens, completionTokens) = await ForwardStream(ctx, resp, logger);
            else
                (model, promptTokens, completionTokens) = await ForwardBuffered(ctx, resp, logger);
        }

        var totalMs = (int)sw.ElapsedMilliseconds;
        var transferMs = totalMs - overheadMs - upstreamMs;

        // ── Calculate cost (synchronous — only DB write is async) ──
        var cost = pricing.Calculate(model, promptTokens, completionTokens) ?? 0;

        // Fire-and-forget: persist to DB only
        _ = Task.Run(() =>
        {
            try
            {
                var now = DateTime.UtcNow;
                store.LogRequest(proj.Id, uid, now.ToString("o"), model, deployment, rest,
                    promptTokens, completionTokens, cost, (int)resp.StatusCode,
                    overheadMs, upstreamMs, transferMs, totalMs, isStream);
                store.UpsertUsageDaily(proj.Id, uid, DateOnly.FromDateTime(now), cost, promptTokens, completionTokens);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Diagnostics.AsyncFailures);
                logger.LogError(ex, "Async DB write failed for {Project}/{Deployment}", proj.Id, deployment);
            }
        });
    }

    /// <summary>
    /// Forwards a buffered (non-streaming) response to the client and extracts usage.
    /// </summary>
    private static async Task<(string model, int promptTokens, int completionTokens)> ForwardBuffered(
        HttpContext ctx, HttpResponseMessage resp, ILogger logger)
    {
        var body = await resp.Content.ReadAsByteArrayAsync();
        await ctx.Response.Body.WriteAsync(body);

        // For non-streaming responses, Azure always includes usage in the response body.
        // See: https://learn.microsoft.com/en-us/azure/ai-services/openai/reference#chat-completions
        return ExtractUsage(body, logger);
    }

    /// <summary>
    /// Forwards an SSE streaming response line-by-line and extracts usage from the final chunk.
    /// Azure OpenAI streams as Server-Sent Events: each event is "data: {json}\n\n",
    /// the final usage chunk has choices=[] with a usage object, and the stream ends with
    /// "data: [DONE]".
    /// See: https://learn.microsoft.com/en-us/azure/ai-services/openai/reference#chat-completions
    /// </summary>
    private static async Task<(string model, int promptTokens, int completionTokens)> ForwardStream(
        HttpContext ctx, HttpResponseMessage resp, ILogger logger)
    {
        var model = "";
        int pt = 0, ct = 0;
        byte[]? lastUsageChunk = null;

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            await ctx.Response.WriteAsync(line + "\n");
            await ctx.Response.Body.FlushAsync();

            if (!line.StartsWith("data: ") || line == "data: [DONE]")
                continue;

            try
            {
                using var chunk = JsonDocument.Parse(line.AsMemory(6));

                if (chunk.RootElement.TryGetProperty("model", out var mv))
                    model = mv.GetString() ?? model;

                // The final chunk has choices=[] and contains the usage summary
                if (chunk.RootElement.TryGetProperty("usage", out _)
                    && chunk.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() == 0)
                {
                    lastUsageChunk = System.Text.Encoding.UTF8.GetBytes(line[6..]);
                }
            }
            catch (JsonException) { }
        }

        // Extract usage from the final chunk using the shared extraction method
        if (lastUsageChunk is not null)
        {
            (model, pt, ct) = ExtractUsage(lastUsageChunk, logger);
        }
        else
        {
            logger.LogWarning("No usage chunk found in SSE stream — model={Model}", model);
        }

        return (model, pt, ct);
    }

    /// <summary>
    /// Parses a JSON response body to extract model name and token usage.
    /// Throws if the usage property is missing (Azure always includes it when
    /// stream_options.include_usage=true or for non-streaming responses).
    /// </summary>
    private static (string model, int promptTokens, int completionTokens) ExtractUsage(
        byte[] jsonBody, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var model = doc.RootElement.TryGetProperty("model", out var mv)
                ? mv.GetString() ?? ""
                : "";

            // Don't TryGet — usage must be present. Azure includes it in all non-streaming
            // responses and in the final streaming chunk when include_usage=true.
            var usage = doc.RootElement.GetProperty("usage");
            var pt = usage.GetProperty("prompt_tokens").GetInt32();
            var ct = usage.GetProperty("completion_tokens").GetInt32();
            return (model, pt, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract usage from response body");
            return ("", 0, 0);
        }
    }

    /// <summary>
    /// Ensures stream_options.include_usage=true is set in the request JSON.
    /// If stream_options is absent, adds it. If present without include_usage, adds the property.
    /// </summary>
    private static byte[] EnsureStreamIncludeUsage(JsonDocument doc, byte[] original)
    {
        // Check if include_usage is already set
        if (doc.RootElement.TryGetProperty("stream_options", out var so)
            && so.TryGetProperty("include_usage", out var iu)
            && iu.GetBoolean())
        {
            return original; // Already configured correctly
        }

        // Rewrite the JSON with stream_options.include_usage=true
        using var buf = new MemoryStream();
        using var writer = new Utf8JsonWriter(buf);
        writer.WriteStartObject();

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name == "stream_options")
            {
                // Rewrite stream_options with include_usage=true added/overridden
                writer.WritePropertyName("stream_options");
                writer.WriteStartObject();
                foreach (var sp in p.Value.EnumerateObject())
                {
                    if (sp.Name != "include_usage")
                        sp.WriteTo(writer);
                }
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }
            else
            {
                p.WriteTo(writer);
            }
        }

        // If stream_options wasn't present at all, add it
        if (!doc.RootElement.TryGetProperty("stream_options", out _))
        {
            writer.WritePropertyName("stream_options");
            writer.WriteStartObject();
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();
        return buf.ToArray();
    }
}
