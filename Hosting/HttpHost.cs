using System.Text.Json;
using AiSharp.Config;
using AiSharp.Core;
using Microsoft.Extensions.Logging;

namespace AiSharp.Hosting;

/// <summary>
/// Local HTTP host: sessions/runs + SSE streaming. Binds loopback by default.
/// Transport adapter only — all behavior lives in TurnExecutor / SessionStore / IChatProvider.
/// </summary>
public static class HttpHost
{
    public static async Task RunAsync(
        ISessionStore store,
        IChatProvider provider,
        AiSharpConfig config,
        int port,
        CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = "ai-sharp",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; });
        // Security baseline (SCOPE §MVP.7): loopback only. Remote binding needs an
        // explicit auth design (Phase 3), so refuse non-loopback outright for now.
        var bindHost = Environment.GetEnvironmentVariable("AI_SHARP_BIND") ?? "127.0.0.1";
        if (bindHost is not ("127.0.0.1" or "::1" or "localhost"))
            throw new InvalidOperationException(
                $"Refusing to bind {bindHost}: ai-sharp has no authentication yet and serves session transcripts. " +
                "Bind loopback only (127.0.0.1/::1/localhost) until the Phase 3 auth policy lands.");
        builder.WebHost.UseUrls($"http://{bindHost}:{port}");

        var app = builder.Build();
        var logger = app.Logger;
        var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            provider = provider.ProviderId,
            model = config.Model,
            protocols = new[] { "responses" },
            streaming = provider.Capabilities.SupportsStreaming,
            tools = provider.Capabilities.SupportsTools,
        }));

        app.MapGet("/openapi.json", () => Results.Content(OpenApiDoc.Generate(), "application/json"));

        app.MapPost("/v1/sessions", (SessionCreateBody? body) =>
        {
            var s = store.Create(body?.Title);
            return Results.Json(new { id = s.Id, createdAt = s.CreatedAt, title = s.Title });
        });

        app.MapGet("/v1/sessions", () =>
        {
            var list = store.List().Select(s => new { id = s.Id, createdAt = s.CreatedAt, title = s.Title, messages = s.Messages.Count, runs = s.Runs.Count });
            return Results.Json(list);
        });

        app.MapGet("/v1/sessions/{id}", (string id) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_session_id", id }); }
            try { return Results.Json(SessionView(store.Get(id))); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "session_not_found", id }); }
        });

        app.MapGet("/v1/sessions/{id}/messages", (string id) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_session_id", id }); }
            try
            {
                var s = store.Get(id);
                return Results.Json(s.Messages.Select(m => new
                {
                    id = m.Id, role = m.Role, text = m.Text, createdAt = m.CreatedAt,
                    providerResponseId = m.ProviderResponseId, usage = m.Usage,
                }));
            }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "session_not_found", id }); }
        });

        app.MapGet("/v1/sessions/{id}/runs", (string id) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_session_id", id }); }
            try { return Results.Json(store.Get(id).Runs); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "session_not_found", id }); }
        });

        app.MapGet("/v1/sessions/{id}/tool-calls", (string id) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_session_id", id }); }
            try { return Results.Json(store.Get(id).ToolCalls); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "session_not_found", id }); }
        });

        // Reconnectable event replay: ordered, persisted, run-scoped seq ids.
        // JSON by default; SSE when Accept: text/event-stream (replay stored, then close).
        app.MapGet("/v1/sessions/{id}/runs/{runId}/events", async (HttpContext ctx, string id, string runId) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "invalid_session_id", id }); return; }
            SessionRecord session;
            try { session = store.Get(id); }
            catch (KeyNotFoundException) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "session_not_found", id }); return; }
            var run = session.Runs.FirstOrDefault(r => r.Id == runId);
            if (run is null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "run_not_found", runId }); return; }
            var afterSeq = -1;
            if (ctx.Request.Query.TryGetValue("after", out var aq)) int.TryParse(aq, out afterSeq);
            var events = store.GetRunEvents(id, runId, afterSeq);
            var wantsSse = ctx.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (!wantsSse)
            {
                await ctx.Response.WriteAsJsonAsync(new
                {
                    sessionId = id, runId, status = run.Status,
                    events = events.Select(e => new { seq = e.Seq, name = e.Name, data = JsonSerializer.Deserialize<JsonElement>(e.DataJson), createdAt = e.CreatedAt }),
                });
                return;
            }
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/event-stream";
            foreach (var e in events)
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(e.DataJson);
                await ctx.Response.WriteAsync($"id: {e.Seq}\nevent: {e.Name}\ndata: {JsonSerializer.Serialize(payload, jsonOpts)}\n\n", ctx.RequestAborted);
            }
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        });

        app.MapPost("/v1/sessions/{id}/runs", async (HttpContext ctx, string id, RunCreateBody body) =>
        {
            try { FileSessionStore.ValidateSessionId(id); }
            catch (ArgumentException) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "invalid_session_id", id }); return; }
            try { _ = store.Get(id); }
            catch (KeyNotFoundException) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "session_not_found", id }); return; }

            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "empty_prompt" });
                return;
            }

            var wantsSse = (ctx.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
                || string.Equals(ctx.Request.Query["stream"], "true", StringComparison.OrdinalIgnoreCase);
            var clientRequestId = !string.IsNullOrWhiteSpace(body.ClientRequestId) ? body.ClientRequestId!.Trim()
                : ctx.Request.Headers.TryGetValue("X-Request-Id", out var hv) ? hv.ToString().Trim() : null;
            if (clientRequestId is not null && clientRequestId.Length > 128) clientRequestId = clientRequestId[..128];

            // Duplicate submission: the key claim happens inside the executor under the
            // turn lock, so concurrent duplicates serialize and the loser serves the original.
            async Task ServeDuplicateAsync(string dupRunId)
            {
                var prior = store.Get(id).Runs.FirstOrDefault(r => r.Id == dupRunId);
                if (prior is null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "run_not_found", runId = dupRunId });
                    return;
                }
                logger.LogInformation("duplicate run key {KeyHash} session {Session} -> run {Run} ({Status})",
                    HashKey(clientRequestId), id, prior.Id, prior.Status);
                if (!wantsSse)
                {
                    if (prior.Status is RunStatuses.Running or RunStatuses.Queued)
                    {
                        ctx.Response.StatusCode = 409;
                        await ctx.Response.WriteAsJsonAsync(new { error = "run_in_progress", runId = prior.Id, sessionId = id, status = prior.Status }, jsonOpts, ctx.RequestAborted);
                        return;
                    }
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        duplicate = true, runId = prior.Id, sessionId = id, status = prior.Status,
                        text = prior.ResponseText, usage = prior.Usage, providerResponseId = prior.ProviderResponseId,
                    }, jsonOpts, ctx.RequestAborted);
                    return;
                }
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.ContentType = "text/event-stream";
                foreach (var e in store.GetRunEvents(id, prior.Id))
                {
                    var payload = JsonSerializer.Deserialize<JsonElement>(e.DataJson);
                    await ctx.Response.WriteAsync($"id: {e.Seq}\nevent: {e.Name}\ndata: {JsonSerializer.Serialize(payload, jsonOpts)}\n\n", ctx.RequestAborted);
                }
                await ctx.Response.WriteAsync($"event: replay.end\ndata: {JsonSerializer.Serialize(new { sessionId = id, runId = prior.Id, duplicate = true }, jsonOpts)}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            var executor = new TurnExecutor(store, provider, body.Model ?? config.Model);
            string? runId = null;
            async Task OnStarted(StoredRun r)
            {
                runId = r.Id;
                logger.LogInformation("run {Run} started session {Session} model {Model} key {KeyHash}",
                    r.Id, id, body.Model ?? config.Model, HashKey(clientRequestId));
                await Task.CompletedTask;
            }

            if (!wantsSse)
            {
                // Blocking JSON mode.
                try
                {
                    var result = await executor.ExecuteAsync(id, body.Prompt, body.Model, null, ctx.RequestAborted, OnStarted, null, clientRequestId);
                    logger.LogInformation("run {Run} {Status} session {Session} in={In} out={Out}",
                        result.Run.Id, result.Run.Status, id, result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        runId = result.Run.Id, sessionId = id, status = result.Run.Status,
                        text = result.Text, usage = result.Usage, providerResponseId = result.ProviderResponseId,
                        toolCalls = result.ToolCalls.Select(t => new { id = t.Id, name = t.Name, status = t.Status }),
                    }, jsonOpts, ctx.RequestAborted);
                }
                catch (DuplicateRunException dup)
                {
                    await ServeDuplicateAsync(dup.RunId);
                }
                catch (ProviderException pex)
                {
                    logger.LogWarning("run {Run} failed session {Session}: {Code} {Message}", runId ?? "-", id, pex.Code, RedactSecrets(pex.Message));
                    ctx.Response.StatusCode = MapProviderErrorToStatus(pex);
                    await ctx.Response.WriteAsJsonAsync(new { error = pex.Code, message = pex.Message, retryable = pex.Retryable, runId, sessionId = id });
                }
                return;
            }

            // SSE mode: stream persisted events live (run.start, text.delta, tool.call.request,
            // tool.result, run.completed/failed). SSE ids are the persisted run-scoped seqs,
            // so a reconnecting client resumes with GET .../events?after=N.
            // Live-only notices use no id so they can never collide with replay ids.
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/event-stream";
            async Task WriteLiveEvent(int seq, string name, JsonElement payload)
            {
                var idLine = seq >= 0 ? $"id: {seq}\n" : "";
                var line = $"{idLine}event: {name}\ndata: {JsonSerializer.Serialize(payload, jsonOpts)}\n\n";
                await ctx.Response.WriteAsync(line, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            try
            {
                var result = await executor.ExecuteAsync(
                    id, body.Prompt, body.Model, null, ctx.RequestAborted, OnStarted,
                    onEvent: async (name, seq, dataJson) =>
                    {
                        await WriteLiveEvent(seq, name, JsonSerializer.Deserialize<JsonElement>(dataJson));
                    },
                    clientRequestId: clientRequestId);
                logger.LogInformation("run {Run} {Status} session {Session} in={In} out={Out} tools={Tools}",
                    result.Run.Id, result.Run.Status, id, result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0, result.ToolCalls.Count);
            }
            // Note: the executor persists and streams run.failed itself via onEvent;
            // these catches only cover failures before a run row existed (runId null).
            catch (DuplicateRunException dup)
            {
                await ServeDuplicateAsync(dup.RunId);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("run {Run} cancelled session {Session}", runId ?? "-", id);
                if (runId is null) try { await WriteLiveEvent(-1, "run.failed", JsonSerializer.Deserialize<JsonElement>("{\"error\":\"cancelled\"}")); } catch { }
            }
            catch (ProviderException pex)
            {
                logger.LogWarning("run {Run} failed session {Session}: {Code}", runId ?? "-", id, pex.Code);
                if (runId is null) try { await WriteLiveEvent(-1, "run.failed", JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { sessionId = id, error = pex.Code, message = RedactSecrets(pex.Message) }, jsonOpts))); } catch { }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "run {Run} errored session {Session}", runId ?? "-", id);
                if (runId is null) try { await WriteLiveEvent(-1, "run.failed", JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { sessionId = id, error = "unknown", message = ex.Message }, jsonOpts))); } catch { }
            }
        });

        logger.LogInformation("ai-sharp serving on http://{Host}:{Port} (provider={Provider} model={Model})", bindHost, port, provider.ProviderId, config.Model);
        await app.RunAsync(ct);
    }

    private static int MapProviderErrorToStatus(ProviderException pex) => pex.Code switch
    {
        ProviderErrorCodes.RateLimited => 429,
        ProviderErrorCodes.BadRequest => 400,
        ProviderErrorCodes.ModelUnavailable => 400,
        ProviderErrorCodes.Cancelled => 408,
        _ => 502, // auth (server-side secret), upstream server, timeouts, unknown
    };

    /// <summary>Stable opaque handle for a client request key: safe to log, useless to steal.</summary>
    private static string HashKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "-";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..12];
    }

    /// <summary>Strip anything shaped like an API key from text about to be logged.</summary>
    private static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"(sk-|oc_sk_)[A-Za-z0-9_\-]+", "[redacted-key]");
    }

    private static object SessionView(SessionRecord s) => new
    {
        id = s.Id, createdAt = s.CreatedAt, updatedAt = s.UpdatedAt, title = s.Title,
        messages = s.Messages.Select(m => new
        {
            id = m.Id, role = m.Role, text = m.Text, createdAt = m.CreatedAt,
            providerResponseId = m.ProviderResponseId, usage = m.Usage,
        }),
        runs = s.Runs.Select(r => new
        {
            id = r.Id, status = r.Status, prompt = r.Prompt, responseText = r.ResponseText,
            usage = r.Usage, errorCode = r.ErrorCode, errorMessage = r.ErrorMessage,
            providerResponseId = r.ProviderResponseId, createdAt = r.CreatedAt, completedAt = r.CompletedAt,
            eventCount = r.Events?.Count ?? 0, toolRounds = r.ToolRounds,
        }),
        toolCalls = s.ToolCalls.Select(t => new
        {
            id = t.Id, runId = t.RunId, name = t.Name, arguments = t.ArgumentsJson,
            status = t.Status, result = t.ResultText, error = t.Error,
            createdAt = t.CreatedAt, completedAt = t.CompletedAt,
        }),
    };

    public sealed record SessionCreateBody(string? Title);
    public sealed record RunCreateBody(string Prompt, string? Model, string? ClientRequestId);
}
