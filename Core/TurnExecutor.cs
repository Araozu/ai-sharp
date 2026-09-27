using System.Collections.Concurrent;
using System.Text.Json;
using AiSharp.Providers.OpencodeGo;

namespace AiSharp.Core;

/// <summary>
/// Turn executor with the owned tool loop (Phase 2):
/// user prompt → provider stream (text + tool requests) → authorize/validate/execute
/// tools locally → feed results back → repeat (bounded) → final assistant message.
/// No provider-hosted state: history is rebuilt from durable records every turn.
/// </summary>
public sealed class TurnExecutor
{
    private readonly ISessionStore _store;
    private readonly IChatProvider _provider;
    private readonly string _defaultModel;
    private readonly ToolRegistry _tools;
    private readonly TimeSpan _runTimeout;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound on model → tool → model rounds per run.</summary>
    public const int MaxToolRounds = 8;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public TurnExecutor(
        ISessionStore store,
        IChatProvider provider,
        string defaultModel,
        ToolRegistry? tools = null,
        TimeSpan? runTimeout = null)
    {
        _store = store;
        _provider = provider;
        _defaultModel = defaultModel;
        _tools = tools ?? ToolRegistry.WithBuiltins();
        _runTimeout = runTimeout
            ?? (int.TryParse(Environment.GetEnvironmentVariable("AI_SHARP_RUN_TIMEOUT_SEC"), out var s) && s > 0
                ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(10));
    }

    public sealed record TurnResult(
        StoredRun Run,
        string Text,
        ProviderUsage? Usage,
        string? ProviderResponseId,
        IReadOnlyList<StoredToolCall> ToolCalls);

    /// <summary>
    /// Execute one turn. onDelta receives live text deltas; onEvent receives every
    /// persisted run event as (name, run-scoped seq, JSON payload) — the same seq ids
    /// the replay endpoint serves, so a reconnecting client can resume with ?after=N.
    /// onRunStarted fires right after the run row is created.
    /// </summary>
    public async Task<TurnResult> ExecuteAsync(
        string sessionId,
        string prompt,
        string? modelOverride = null,
        Func<string, Task>? onDelta = null,
        CancellationToken ct = default,
        Func<StoredRun, Task>? onRunStarted = null,
        Func<string, int, string, Task>? onEvent = null,
        string? clientRequestId = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is empty.", nameof(prompt));
        FileSessionStore.ValidateSessionId(sessionId);
        var gate = Gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteLockedAsync(sessionId, prompt, modelOverride, onDelta, ct, onRunStarted, onEvent, clientRequestId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TurnResult> ExecuteLockedAsync(
        string sessionId,
        string prompt,
        string? modelOverride,
        Func<string, Task>? onDelta,
        CancellationToken ct,
        Func<StoredRun, Task>? onRunStarted,
        Func<string, int, string, Task>? onEvent,
        string? clientRequestId)
    {
        // Whole-run timeout + cross-process mutual exclusion for the turn.
        using var timeout = new CancellationTokenSource(_runTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var rct = linked.Token;

        var sessionsDir = _store is FileSessionStore fs ? fs.SessionsDir : Path.Combine(_store.DataDir, "sessions");
        using var _ = SessionLockFile.Acquire(sessionsDir, sessionId, LockTimeout, rct);
        var model = string.IsNullOrWhiteSpace(modelOverride) ? _defaultModel : modelOverride!;

        // Idempotency claim happens under the turn lock: a concurrent duplicate
        // either waits here and then sees the key, or registered its own first.
        if (!string.IsNullOrWhiteSpace(clientRequestId)
            && _store.TryGetRequestKey(sessionId, clientRequestId, out var priorRunId))
            throw new DuplicateRunException(priorRunId);

        StoredRun? run = null;
        var historyBase = new List<object>();
        var loopTail = new List<object>();
        var fullText = new System.Text.StringBuilder();
        var totalIn = 0; var totalOut = 0; var cached = 0; var reasoning = 0;
        ProviderUsage? lastUsage = null;
        string? providerResponseId = null;
        var toolCalls = new List<StoredToolCall>();
        var toolRounds = 0;
        var terminalRecorded = false;
        ProviderUsage TotalUsage() => new(totalIn, totalOut, totalIn + totalOut,
            cached == 0 ? null : cached, reasoning == 0 ? null : reasoning);

        // Every terminal path persists the run status BEFORE notifying, so a failed
        // SSE write can never leave a durable Running row behind.
        async Task EmitAsync(string name, object payload)
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var seq = _store.AppendRunEvent(sessionId, run!.Id, name, json);
            if (onEvent is not null) await onEvent(name, seq, json).ConfigureAwait(false);
        }

        try
        {
            // Persist user message first so restart never loses the prompt.
            _store.AppendMessage(sessionId, Roles.User, prompt);

            run = _store.CreateRun(sessionId, prompt);
            if (!string.IsNullOrWhiteSpace(clientRequestId))
                _store.SetRequestKey(sessionId, clientRequestId, run.Id);
            if (onRunStarted is not null) await onRunStarted(run).ConfigureAwait(false);
            await EmitAsync("run.start", new { sessionId, runId = run.Id, model }).ConfigureAwait(false);

            historyBase = BuildFullInput(sessionId);

            for (var round = 1; round <= MaxToolRounds; round++)
            {
                rct.ThrowIfCancellationRequested();
                var roundInput = new List<object>(historyBase.Count + loopTail.Count);
                roundInput.AddRange(historyBase);
                roundInput.AddRange(loopTail);
                var req = new ProviderRequest(model, [], sessionId, rct, _tools.Definitions(), null, roundInput);
                var roundCalls = new List<ToolCallRequest>();
                var roundText = new System.Text.StringBuilder();

                await foreach (var ev in _provider.StreamAsync(req).ConfigureAwait(false))
                {
                    rct.ThrowIfCancellationRequested();
                    switch (ev)
                    {
                        case TextDeltaEvent d:
                            if (fullText.Length < _provider.Capabilities.MaxOutputChars)
                            {
                                var room = _provider.Capabilities.MaxOutputChars - fullText.Length;
                                var chunk = d.Delta.Length <= room ? d.Delta : d.Delta[..room];
                                fullText.Append(chunk);
                                roundText.Append(chunk);
                                await EmitAsync("text.delta", new { sessionId, runId = run.Id, round, delta = chunk }).ConfigureAwait(false);
                                if (onDelta is not null) await onDelta(chunk).ConfigureAwait(false);
                            }
                            break;
                        case ToolCallEvent t:
                            roundCalls.Add(t.Call);
                            break;
                        case CompletedEvent c:
                            lastUsage = c.Usage;
                            providerResponseId = c.ProviderResponseId ?? providerResponseId;
                            totalIn += c.Usage.InputTokens; totalOut += c.Usage.OutputTokens;
                            cached += c.Usage.CachedInputTokens ?? 0; reasoning += c.Usage.ReasoningOutputTokens ?? 0;
                            if (roundText.Length == 0 && !string.IsNullOrEmpty(c.FullText))
                            {
                                // Non-streamed fallback travels the normal delta path (capped + emitted).
                                var room = _provider.Capabilities.MaxOutputChars - fullText.Length;
                                if (room > 0)
                                {
                                    var t = c.FullText.Length <= room ? c.FullText : c.FullText[..room];
                                    fullText.Append(t);
                                    roundText.Append(t);
                                    await EmitAsync("text.delta", new { sessionId, runId = run.Id, round, delta = t }).ConfigureAwait(false);
                                    if (onDelta is not null) await onDelta(t).ConfigureAwait(false);
                                }
                            }
                            break;
                    }
                }

                // Each round's assistant text is its own durable message at its position;
                // the final round's text is the run's answer.
                var roundAnswer = roundText.ToString();
                if (roundAnswer.Length > 0)
                    _store.AppendMessage(sessionId, Roles.Assistant, roundAnswer, providerResponseId, lastUsage, run.Id);

                if (roundCalls.Count == 0)
                {
                    var usage = TotalUsage();
                    _store.FinishRun(sessionId, run.Id, RunStatuses.Completed, roundAnswer, usage, null, null, providerResponseId, toolRounds);
                    terminalRecorded = true;
                    await EmitAsync("run.completed", new { sessionId, runId = run.Id, status = RunStatuses.Completed, rounds = round }).ConfigureAwait(false);
                    var finished = _store.Get(sessionId).Runs.First(r => r.Id == run.Id);
                    return new TurnResult(finished, roundAnswer, usage, providerResponseId, toolCalls);
                }

                // Execute this round's tool requests before the next model round.
                foreach (var call in roundCalls)
                {
                    rct.ThrowIfCancellationRequested();
                    var stored = _store.CreateToolCall(sessionId, run.Id, call.Name, call.ArgumentsJson, call.CallId, call.ItemId);
                    toolCalls.Add(stored);
                    await EmitAsync("tool.call.request", new { sessionId, runId = run.Id, toolCallId = stored.Id, name = call.Name, arguments = call.ArgumentsJson }).ConfigureAwait(false);

                    var (_, output) = await RunOneToolAsync(sessionId, stored, call, rct).ConfigureAwait(false);
                    var final = _store.Get(sessionId).ToolCalls.FirstOrDefault(t => t.Id == stored.Id);
                    if (final is not null)
                    {
                        toolCalls[toolCalls.FindIndex(t => t.Id == stored.Id)] = final;
                        await EmitAsync("tool.result", new { sessionId, runId = run.Id, toolCallId = final.Id, name = final.Name, status = final.Status, output = final.ResultText, error = final.Error }).ConfigureAwait(false);
                    }

                    // Echo intent + result so the next round is stateless.
                    loopTail.Add(OpencodeGoProvider.FunctionCallItem(call.ItemId, call.CallId, call.Name, call.ArgumentsJson));
                    loopTail.Add(OpencodeGoProvider.FunctionCallOutputItem(call.CallId, output));
                }
                toolRounds++;

                if (round == MaxToolRounds)
                {
                    var msg = $"Tool loop bound reached ({MaxToolRounds} rounds); stopping without a final answer.";
                    _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, fullText.ToString(), TotalUsage(), "tool_loop_bound", msg, providerResponseId, toolRounds);
                    terminalRecorded = true;
                    await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = "tool_loop_bound", message = msg }).ConfigureAwait(false);
                    throw new ProviderException("tool_loop_bound", msg, false);
                }
            }

            throw new InvalidOperationException("Unreachable: tool loop always returns or throws.");
        }
        catch (OperationCanceledException) when (run is not null && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            const string msg = "Run exceeded its time budget.";
            _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, fullText.ToString(), TotalUsage(), ProviderErrorCodes.Timeout, msg, providerResponseId, toolRounds);
            terminalRecorded = true;
            await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = "provider_timeout", message = msg }).ConfigureAwait(false);
            throw new ProviderException(ProviderErrorCodes.Timeout, msg, true);
        }
        catch (OperationCanceledException) when (run is not null)
        {
            _store.FinishRun(sessionId, run.Id, RunStatuses.Cancelled, fullText.ToString(), TotalUsage(), ProviderErrorCodes.Cancelled, "Run was cancelled.", providerResponseId, toolRounds);
            terminalRecorded = true;
            await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = ProviderErrorCodes.Cancelled, message = "Run was cancelled." }).ConfigureAwait(false);
            throw new ProviderException(ProviderErrorCodes.Cancelled, "Run was cancelled.", false);
        }
        catch (ProviderException pex) when (run is not null && pex.Code == ProviderErrorCodes.Cancelled)
        {
            // The provider reports cancellation (e.g. aborted SendAsync) as a typed error.
            // Attribute it the same way: run timeout vs caller cancellation.
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                const string msg = "Run exceeded its time budget.";
                _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, fullText.ToString(), TotalUsage(), ProviderErrorCodes.Timeout, msg, providerResponseId, toolRounds);
                terminalRecorded = true;
                await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = "provider_timeout", message = msg }).ConfigureAwait(false);
                throw new ProviderException(ProviderErrorCodes.Timeout, msg, true);
            }
            _store.FinishRun(sessionId, run.Id, RunStatuses.Cancelled, fullText.ToString(), TotalUsage(), ProviderErrorCodes.Cancelled, "Run was cancelled.", providerResponseId, toolRounds);
            terminalRecorded = true;
            await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = ProviderErrorCodes.Cancelled, message = "Run was cancelled." }).ConfigureAwait(false);
            throw new ProviderException(ProviderErrorCodes.Cancelled, "Run was cancelled.", false);
        }
        catch (ProviderException pex) when (run is not null)
        {
            if (terminalRecorded) throw; // already persisted + emitted (e.g. loop bound)
            _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, fullText.ToString(), TotalUsage(), pex.Code, pex.Message, providerResponseId, toolRounds);
            terminalRecorded = true;
            await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = pex.Code, message = pex.Message }).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (run is not null)
        {
            _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, fullText.ToString(), TotalUsage(), ProviderErrorCodes.Unknown, ex.Message, providerResponseId, toolRounds);
            terminalRecorded = true;
            await EmitAsync("run.failed", new { sessionId, runId = run.Id, error = ProviderErrorCodes.Unknown, message = ex.Message }).ConfigureAwait(false);
            throw new ProviderException(ProviderErrorCodes.Unknown, ex.Message, false, null, ex);
        }
    }

    /// <summary>
    /// Rebuild the full stateless input from durable records: messages and tool
    /// intent/result pairs merged chronologically, so no provider-side state is needed.
    /// </summary>
    private List<object> BuildFullInput(string sessionId)
    {
        var session = _store.Get(sessionId);
        var items = new List<(DateTimeOffset At, bool MsgFirst, object Item)>();
        foreach (var m in session.Messages)
            items.Add((m.CreatedAt, true, OpencodeGoProvider.MessageItem(m.Role, m.Text)));
        foreach (var t in session.ToolCalls)
        {
            if (t.Status is ToolStatuses.Running or ToolStatuses.Pending)
                continue; // crash leftovers are reconciled to failed; never send an open call
            var itemId = t.ProviderItemId ?? t.Id;
            var callId = t.ProviderCallId ?? t.Id;
            items.Add((t.CreatedAt, false, OpencodeGoProvider.FunctionCallItem(itemId, callId, t.Name, t.ArgumentsJson)));
            var output = t.Status == ToolStatuses.Completed
                ? (t.ResultText ?? "")
                : $"Tool '{t.Name}' {t.Status}: {t.Error ?? t.ResultText ?? ""}";
            items.Add((t.CompletedAt ?? t.CreatedAt, false, OpencodeGoProvider.FunctionCallOutputItem(callId, output)));
        }
        return items.OrderBy(x => x.At).ThenBy(x => x.MsgFirst ? 0 : 1)
            .Select(x => x.Item).ToList();
    }

    /// <summary>Authorize, validate, execute one tool call; persists result. Returns (policyAllowed, outputForModel).</summary>
    private async Task<(bool, string)> RunOneToolAsync(string sessionId, StoredToolCall stored, ToolCallRequest call, CancellationToken ct)
    {
        var (allowed, reason) = _tools.CheckPolicy(call.Name);
        if (!allowed)
        {
            _store.FinishToolCall(sessionId, stored.Id, ToolStatuses.Denied, null, reason);
            return (false, $"Tool call denied: {reason}");
        }
        var tool = _tools.Find(call.Name)!;
        var result = await _tools.ExecuteAsync(tool, call.ArgumentsJson, ct).ConfigureAwait(false);
        if (result.Status == ToolStatuses.Completed)
        {
            _store.FinishToolCall(sessionId, stored.Id, ToolStatuses.Completed, result.Output, null);
            return (true, result.Output ?? "");
        }
        _store.FinishToolCall(sessionId, stored.Id, result.Status, result.Output, result.Error);
        return (true, $"Tool '{call.Name}' {result.Status}: {result.Error ?? result.Output ?? ""}");
    }
}
