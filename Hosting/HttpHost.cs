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

            var executor = new TurnExecutor(store, provider, body.Model ?? config.Model);
            string? runId = null;

            if (!wantsSse)
            {
                // Blocking JSON mode.
                try
                {
                    var result = await executor.ExecuteAsync(id, body.Prompt, body.Model, null, ctx.RequestAborted,
                        onRunStarted: r => { runId = r.Id; return Task.CompletedTask; });
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        runId = result.Run.Id, sessionId = id, status = result.Run.Status,
                        text = result.Text, usage = result.Usage, providerResponseId = result.ProviderResponseId,
                    }, jsonOpts, ctx.RequestAborted);
                }
                catch (ProviderException pex)
                {
                    ctx.Response.StatusCode = MapProviderErrorToStatus(pex);
                    await ctx.Response.WriteAsJsonAsync(new { error = pex.Code, message = pex.Message, retryable = pex.Retryable, runId, sessionId = id });
                }
                return;
            }

            // SSE mode: our normalized events (run.start, text.delta, run.completed/failed).
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/event-stream";
            var seq = 0;
            async Task WriteEvent(string name, object data)
            {
                var line = $"id: {seq}\nevent: {name}\ndata: {JsonSerializer.Serialize(data, jsonOpts)}\n\n";
                await ctx.Response.WriteAsync(line, ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                seq++;
            }

            try
            {
                var result = await executor.ExecuteAsync(
                    id, body.Prompt, body.Model,
                    onDelta: async delta => await WriteEvent("text.delta", new { sessionId = id, runId, delta }),
                    ct: ctx.RequestAborted,
                    onRunStarted: async r => { runId = r.Id; await WriteEvent("run.start", new { sessionId = id, runId = r.Id, model = body.Model ?? config.Model }); });

                await WriteEvent("run.completed", new
                {
                    sessionId = id, runId = result.Run.Id, status = result.Run.Status,
                    text = result.Text, usage = result.Usage, providerResponseId = result.ProviderResponseId,
                });
            }
            catch (OperationCanceledException)
            {
                try { await WriteEvent("run.failed", new { sessionId = id, runId, error = "cancelled", message = "Client disconnected or cancelled." }); } catch { }
            }
            catch (ProviderException pex)
            {
                try { await WriteEvent("run.failed", new { sessionId = id, runId, error = pex.Code, message = pex.Message, retryable = pex.Retryable }); } catch { }
            }
            catch (Exception ex)
            {
                try { await WriteEvent("run.failed", new { sessionId = id, runId, error = "unknown", message = ex.Message }); } catch { }
            }
        });

        Console.WriteLine($"ai-sharp serving on http://{bindHost}:{port} (provider={provider.ProviderId} model={config.Model})");
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
        }),
    };

    public sealed record SessionCreateBody(string? Title);
    public sealed record RunCreateBody(string Prompt, string? Model);
}
