using System.Diagnostics;
using System.Text.Json;

namespace LLimit;

public static class ProxyHandler
{
    public static void MapProxy(this WebApplication app)
    {
        app.MapPost("/openai/deployments/{deployment}/{**rest}", HandleProxy);
    }

    private static async Task HandleProxy(HttpContext ctx, string deployment, string rest,
        AuthCache auth, PricingCache pricing, Store store,
        IHttpClientFactory factory, IConfiguration cfg, ILogger<Program> logger)
    {
        var sw = Stopwatch.StartNew();

        // ── Auth ──
        var apiKey = ctx.Request.Headers["api-key"].FirstOrDefault() ?? "";
        var proj = auth.Resolve(apiKey);
        if (proj is null)
        {
            // Check if key exists but project is inactive
            var inactive = auth.ResolveIncludingInactive(apiKey);
            if (inactive is not null)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "project_deactivated" });
                return;
            }
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_api_key" });
            return;
        }

        var uid = ctx.Request.Headers["X-LLimit-User"].FirstOrDefault();

        // ── Budget check (daily only, queries DB) ──
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
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

        // ── Read body, inject stream_options ──
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var bodyBytes = ms.ToArray();
        var isStream = false;
        var endpoint = rest;

        try
        {
            using var doc = JsonDocument.Parse(bodyBytes);
            isStream = doc.RootElement.TryGetProperty("stream", out var sv) && sv.GetBoolean();

            if (isStream && !doc.RootElement.TryGetProperty("stream_options", out _))
            {
                using var buf = new MemoryStream();
                using var writer = new Utf8JsonWriter(buf);
                writer.WriteStartObject();
                foreach (var p in doc.RootElement.EnumerateObject())
                    p.WriteTo(writer);
                writer.WritePropertyName("stream_options");
                writer.WriteStartObject();
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.Flush();
                bodyBytes = buf.ToArray();
            }
        }
        catch (JsonException)
        {
            // Not JSON or malformed — forward as-is
        }

        // ── Forward to Azure (validated at startup, guaranteed non-null) ──
        var azureEndpoint = cfg["AZURE_OPENAI_ENDPOINT"]!;
        var azureKey = cfg["AZURE_OPENAI_API_KEY"]!;
        var url = $"{azureEndpoint.TrimEnd('/')}/openai/deployments/{deployment}/{rest}{ctx.Request.QueryString}";

        var client = factory.CreateClient("Azure");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        req.Headers.Add("api-key", azureKey);

        var overheadMs = (int)sw.ElapsedMilliseconds; // t1 - t0

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
            logger.LogError(ex, "Failed to reach Azure for {Deployment}/{Endpoint}", deployment, endpoint);
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsJsonAsync(new { error = "upstream_error" });
            return;
        }

        var upstreamMs = (int)sw.ElapsedMilliseconds - overheadMs; // t2 - t1

        // ── Copy response headers ──
        // Skip transfer-encoding: Kestrel manages its own chunked encoding when writing
        // to the response body. Forwarding the upstream's "chunked" header causes a
        // double-encoding conflict. See: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/request-draining
        ctx.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers.Concat(resp.Content.Headers))
        {
            if (h.Key.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) continue;
            ctx.Response.Headers[h.Key] = h.Value.ToArray();
        }

        // ── Stream or buffer response ──
        using (resp)
        {
            if (isStream)
                await HandleStream(ctx, resp, proj, uid, deployment, endpoint, pricing, store, logger, sw, overheadMs, upstreamMs);
            else
                await HandleBuffer(ctx, resp, proj, uid, deployment, endpoint, pricing, store, logger, sw, overheadMs, upstreamMs);
        }
    }

    private static async Task HandleBuffer(HttpContext ctx, HttpResponseMessage resp,
        Project proj, string? uid, string deployment, string endpoint,
        PricingCache pricing, Store store, ILogger logger,
        Stopwatch sw, int overheadMs, int upstreamMs)
    {
        var body = await resp.Content.ReadAsByteArrayAsync();
        await ctx.Response.Body.WriteAsync(body);

        var totalMs = (int)sw.ElapsedMilliseconds;
        var transferMs = totalMs - overheadMs - upstreamMs;

        // Fire-and-forget: extract usage, calculate cost, log
        _ = Task.Run(() =>
        {
            try
            {
                var model = "";
                int pt = 0, ct = 0;

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("model", out var mv))
                        model = mv.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("usage", out var usage))
                    {
                        pt = usage.GetProperty("prompt_tokens").GetInt32();
                        ct = usage.GetProperty("completion_tokens").GetInt32();
                    }
                }
                catch { }

                var (cost, found) = pricing.Calculate(model, pt, ct);
                var now = DateTime.UtcNow;
                var dbUid = uid ?? "_anonymous";
                store.LogRequest(proj.Id, dbUid, now.ToString("o"), model, deployment, endpoint,
                    pt, ct, cost, (int)resp.StatusCode, overheadMs, upstreamMs, transferMs, totalMs, false, !found);
                store.UpsertUsageDaily(proj.Id, dbUid, DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"), cost, pt, ct);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Diagnostics.AsyncFailures);
                logger.LogError(ex, "Async logging failed for {Project}/{Deployment}", proj.Id, deployment);
            }
        });
    }

    private static async Task HandleStream(HttpContext ctx, HttpResponseMessage resp,
        Project proj, string? uid, string deployment, string endpoint,
        PricingCache pricing, Store store, ILogger logger,
        Stopwatch sw, int overheadMs, int upstreamMs)
    {
        var model = "";
        int pt = 0, ct = 0;

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            await ctx.Response.WriteAsync(line + "\n");
            await ctx.Response.Body.FlushAsync();

            if (line.StartsWith("data: ") && line != "data: [DONE]")
            {
                try
                {
                    using var chunk = JsonDocument.Parse(line.AsMemory(6));
                    if (chunk.RootElement.TryGetProperty("model", out var mv))
                        model = mv.GetString() ?? model;
                    if (chunk.RootElement.TryGetProperty("usage", out var usage)
                        && chunk.RootElement.TryGetProperty("choices", out var choices)
                        && choices.GetArrayLength() == 0)
                    {
                        pt = usage.GetProperty("prompt_tokens").GetInt32();
                        ct = usage.GetProperty("completion_tokens").GetInt32();
                    }
                }
                catch (JsonException) { }
            }
        }

        var totalMs = (int)sw.ElapsedMilliseconds;
        var transferMs = totalMs - overheadMs - upstreamMs;

        // Fire-and-forget: cost + log
        _ = Task.Run(() =>
        {
            try
            {
                var (cost, found) = pricing.Calculate(model, pt, ct);
                var now = DateTime.UtcNow;
                var dbUid = uid ?? "_anonymous";
                store.LogRequest(proj.Id, dbUid, now.ToString("o"), model, deployment, endpoint,
                    pt, ct, cost, (int)resp.StatusCode, overheadMs, upstreamMs, transferMs, totalMs, true, !found);
                store.UpsertUsageDaily(proj.Id, dbUid, DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"), cost, pt, ct);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Diagnostics.AsyncFailures);
                logger.LogError(ex, "Async logging failed for {Project}/{Deployment}", proj.Id, deployment);
            }
        });
    }
}
