using AiSharp.Core;

namespace AiSharp.Core;

/// <summary>
/// Smallest turn executor for Phase 1: user prompt -> provider stream -> persisted assistant message.
/// No tools yet (Phase 2). Bounds output size; cancellation yields a cancelled run.
/// </summary>
public sealed class TurnExecutor
{
    private readonly ISessionStore _store;
    private readonly IChatProvider _provider;
    private readonly string _defaultModel;

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
    /// </summary>
    public async Task<TurnResult> ExecuteAsync(
        string sessionId,
        string prompt,
        string? modelOverride = null,
        Func<string, Task>? onDelta = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is empty.", nameof(prompt));
        var session = _store.Get(sessionId);
        var model = string.IsNullOrWhiteSpace(modelOverride) ? _defaultModel : modelOverride!;

        // Persist user message first so restart never loses the prompt.
        _store.AppendMessage(sessionId, Roles.User, prompt);

        // Build provider history from durable transcript (now includes the new user msg).
        session = _store.Get(sessionId);
        var history = session.Messages.Select(m => new ChatMessage(m.Role, m.Text)).ToList();

        var run = _store.CreateRun(sessionId, prompt);
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
