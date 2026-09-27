using AiSharp.Core;

using System.Collections.Concurrent;

namespace AiSharp.Core;

/// <summary>
/// Smallest turn executor for Phase 1: user prompt -> provider stream -> persisted assistant message.
/// No tools yet (Phase 2). Bounds output size; cancellation yields a cancelled run.
/// One turn per session at a time: an in-process semaphore plus a cross-process lock file
/// serialize competing turns so concurrent runs cannot swap prompts/history.
/// </summary>
public sealed class TurnExecutor
{
    private readonly ISessionStore _store;
    private readonly IChatProvider _provider;
    private readonly string _defaultModel;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);

    public TurnExecutor(ISessionStore store, IChatProvider provider, string defaultModel)
    {
        _store = store;
        _provider = provider;
        _defaultModel = defaultModel;
    }

    public sealed record TurnResult(
        StoredRun Run,
        string Text,
        ProviderUsage? Usage,
        string? ProviderResponseId);

    /// <summary>
    /// Execute one turn. onDelta receives live text deltas (for SSE/CLI streaming).
    /// onRunStarted fires right after the run row is created, so transports can
    /// correlate start/delta/completion events with the durable run id.
    /// </summary>
    public async Task<TurnResult> ExecuteAsync(
        string sessionId,
        string prompt,
        string? modelOverride = null,
        Func<string, Task>? onDelta = null,
        CancellationToken ct = default,
        Func<StoredRun, Task>? onRunStarted = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is empty.", nameof(prompt));
        FileSessionStore.ValidateSessionId(sessionId);
        var gate = Gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExecuteLockedAsync(sessionId, prompt, modelOverride, onDelta, ct, onRunStarted).ConfigureAwait(false);
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
        Func<StoredRun, Task>? onRunStarted)
    {
        // Cross-process mutual exclusion for the whole turn (see SessionLockFile).
        var sessionsDir = _store is FileSessionStore fs ? fs.SessionsDir : Path.Combine(_store.DataDir, "sessions");
        using var _ = SessionLockFile.Acquire(sessionsDir, sessionId, LockTimeout, ct);
        var session = _store.Get(sessionId);
        var model = string.IsNullOrWhiteSpace(modelOverride) ? _defaultModel : modelOverride!;

        // Persist user message first so restart never loses the prompt.
        _store.AppendMessage(sessionId, Roles.User, prompt);

        // Build provider history from durable transcript (now includes the new user msg).
        session = _store.Get(sessionId);
        var history = session.Messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList();

        var run = _store.CreateRun(sessionId, prompt);
        if (onRunStarted is not null) await onRunStarted(run).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        ProviderUsage? usage = null;
        string? providerResponseId = null;

        try
        {
            var req = new ProviderRequest(model, history, sessionId, ct);
            await foreach (var ev in _provider.StreamAsync(req).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                switch (ev)
                {
                    case TextDeltaEvent d:
                        // Bound output size early.
                        if (sb.Length < _provider.Capabilities.MaxOutputChars)
                        {
                            var room = _provider.Capabilities.MaxOutputChars - sb.Length;
                            var chunk = d.Delta.Length <= room ? d.Delta : d.Delta[..room];
                            sb.Append(chunk);
                            if (onDelta is not null) await onDelta(chunk).ConfigureAwait(false);
                        }
                        break;
                    case CompletedEvent c:
                        usage = c.Usage;
                        providerResponseId = c.ProviderResponseId;
                        if (sb.Length == 0 && !string.IsNullOrEmpty(c.FullText))
                        {
                            var t = c.FullText.Length <= _provider.Capabilities.MaxOutputChars
                                ? c.FullText : c.FullText[.._provider.Capabilities.MaxOutputChars];
                            sb.Append(t);
                            if (onDelta is not null) await onDelta(t).ConfigureAwait(false);
                        }
                        break;
                }
            }

            var text = sb.ToString();
            _store.AppendMessage(sessionId, Roles.Assistant, text, providerResponseId, usage);
            _store.FinishRun(sessionId, run.Id, RunStatuses.Completed, text, usage, null, null, providerResponseId);
            var finished = _store.Get(sessionId).Runs.First(r => r.Id == run.Id);
            return new TurnResult(finished, text, usage, providerResponseId);
        }
        catch (OperationCanceledException)
        {
            var partial = sb.ToString();
            _store.FinishRun(sessionId, run.Id, RunStatuses.Cancelled, partial, usage, ProviderErrorCodes.Cancelled, "Run was cancelled.", providerResponseId);
            throw new ProviderException(ProviderErrorCodes.Cancelled, "Run was cancelled.", false);
        }
        catch (ProviderException pex)
        {
            var status = pex.Code == ProviderErrorCodes.Cancelled ? RunStatuses.Cancelled : RunStatuses.Failed;
            _store.FinishRun(sessionId, run.Id, status, sb.ToString(), usage, pex.Code, pex.Message, providerResponseId);
            throw;
        }
        catch (Exception ex)
        {
            _store.FinishRun(sessionId, run.Id, RunStatuses.Failed, sb.ToString(), usage, ProviderErrorCodes.Unknown, ex.Message, providerResponseId);
            throw new ProviderException(ProviderErrorCodes.Unknown, ex.Message, false, null, ex);
        }
    }
}
