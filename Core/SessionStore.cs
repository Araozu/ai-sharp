using System.Text.Json;
using AiSharp.Core;

namespace AiSharp.Core;

/// <summary>
/// Durable local store: one JSON file per session under {DataDir}/sessions/{id}.json.
/// Atomic writes (temp + rename) so restart still sees a complete transcript.
/// </summary>
public interface ISessionStore
{
    string DataDir { get; }
    SessionRecord Create(string? title = null);
    IReadOnlyList<SessionRecord> List();
    SessionRecord Get(string sessionId);
    void Save(SessionRecord session);
    StoredMessage AppendMessage(string sessionId, string role, string text, string? providerResponseId = null, ProviderUsage? usage = null, string? runId = null);
    StoredRun CreateRun(string sessionId, string prompt);
    void FinishRun(string sessionId, string runId, string status, string? responseText, ProviderUsage? usage, string? errorCode, string? errorMessage, string? providerResponseId, int toolRounds = 0);
    /// <summary>Mark runs left non-terminal by a crash as failed. Returns the count reconciled.</summary>
    int ReconcileInterruptedRuns();
    StoredToolCall CreateToolCall(string sessionId, string runId, string name, string argumentsJson, string? providerCallId = null, string? providerItemId = null);    void FinishToolCall(string sessionId, string toolCallId, string status, string? resultText, string? error);
    /// <summary>Append a run event; returns its run-scoped sequence number. Oldest text deltas stop persisting past the cap.</summary>
    int AppendRunEvent(string sessionId, string runId, string name, string dataJson);
    IReadOnlyList<StoredRunEvent> GetRunEvents(string sessionId, string runId, int afterSeq = -1);
    /// <summary>Idempotency index: client request key -&gt; run id, scoped to a session.</summary>
    bool TryGetRequestKey(string sessionId, string clientRequestId, out string runId);
    void SetRequestKey(string sessionId, string clientRequestId, string runId);
}

/// <summary>
/// Cross-process per-session lock file. Held by TurnExecutor for the whole turn so a
/// CLI process and the HTTP host cannot interleave writes to one session. Short store
/// ops rely on this plus the in-process gate; direct concurrent Save callers are out of scope.
/// </summary>
public static class SessionLockFile
{
    public static FileStream Acquire(string sessionsDir, string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var path = Path.Combine(sessionsDir, sessionId + ".lock");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}

public sealed class FileSessionStore : ISessionStore
{
    private readonly string _sessionsDir;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly System.Text.RegularExpressions.Regex IdPattern = new("^[A-Za-z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly object _gate = new();

    public string DataDir { get; }

    public FileSessionStore(string dataDir)
    {
        DataDir = dataDir;
        _sessionsDir = Path.Combine(dataDir, "sessions");
        Directory.CreateDirectory(_sessionsDir);
    }

    internal string SessionsDir => _sessionsDir;

    public static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !IdPattern.IsMatch(sessionId))
            throw new ArgumentException($"Invalid session id '{sessionId}'. Expected 1-64 chars of [A-Za-z0-9_-].", nameof(sessionId));
    }

    public SessionRecord Create(string? title = null)
    {
        var now = DateTimeOffset.UtcNow;
        var s = new SessionRecord(Ids.NewSessionId(), now, now, title, [], [], [], new());
        Save(s);
        return s;
    }

    public IReadOnlyList<SessionRecord> List()
    {
        lock (_gate)
        {
            var files = Directory.GetFiles(_sessionsDir, "*.json");
            var list = new List<SessionRecord>();
            foreach (var f in files.OrderBy(x => x))
            {
                try { list.Add(ReadFile(f)); }
                catch { /* skip corrupt files; diagnostics surface elsewhere */ }
            }
            return list.OrderBy(s => s.CreatedAt).ToList();
        }
    }

    public SessionRecord Get(string sessionId)
    {
        lock (_gate)
        {
            var path = PathFor(sessionId);
            if (!File.Exists(path)) throw new KeyNotFoundException($"Session not found: {sessionId}");
            return ReadFile(path);
        }
    }

    public void Save(SessionRecord session)
    {
        lock (_gate)
        {
            var updated = session with { UpdatedAt = DateTimeOffset.UtcNow };
            WriteFile(PathFor(updated.Id), updated);
        }
    }

    public StoredMessage AppendMessage(string sessionId, string role, string text, string? providerResponseId = null, ProviderUsage? usage = null, string? runId = null)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var m = new StoredMessage(Ids.NewMessageId(), role, text, DateTimeOffset.UtcNow, providerResponseId, usage, runId);
            s.Messages.Add(m);
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
            return m;
        }
    }

    public StoredRun CreateRun(string sessionId, string prompt)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var now = DateTimeOffset.UtcNow;
            var run = new StoredRun(Ids.NewRunId(), sessionId, RunStatuses.Running, prompt, null, null, null, null, null, now, null, []);
            s.Runs.Add(run);
            WriteFile(PathFor(s.Id), s with { UpdatedAt = now });
            return run;
        }
    }

    public void FinishRun(string sessionId, string runId, string status, string? responseText, ProviderUsage? usage, string? errorCode, string? errorMessage, string? providerResponseId, int toolRounds = 0)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var idx = s.Runs.FindIndex(r => r.Id == runId);
            if (idx < 0) throw new KeyNotFoundException($"Run not found: {runId}");
            var old = s.Runs[idx];
            s.Runs[idx] = old with
            {
                Status = status,
                ResponseText = responseText,
                Usage = usage,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ProviderResponseId = providerResponseId,
                ToolRounds = toolRounds,
                CompletedAt = DateTimeOffset.UtcNow
            };
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    private string PathFor(string sessionId)
    {
        // Reject rather than rewrite: rewriting aliases distinct ids (a/b vs a_b) to one file.
        ValidateSessionId(sessionId);
        return Path.Combine(_sessionsDir, sessionId + ".json");
    }

    private SessionRecord Get_NoLock(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) throw new KeyNotFoundException($"Session not found: {sessionId}");
        return ReadFile(path);
    }

    private static SessionRecord ReadFile(string path)
    {
        var json = File.ReadAllText(path);
        var rec = JsonSerializer.Deserialize<SessionRecord>(json, JsonOpts)
            ?? throw new InvalidDataException($"Empty session file: {path}");
        // Migrate older files (new nullable collections).
        return rec with
        {
            ToolCalls = rec.ToolCalls ?? [],
            RequestKeys = rec.RequestKeys ?? [],
            Runs = rec.Runs.Select(r => r with { Events = r.Events ?? [] }).ToList(),
        };
    }

    private static void WriteFile(string path, SessionRecord session)
    {
        // Unique temp name: a shared ".tmp" path lets two processes clobber each other.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public int AppendRunEvent(string sessionId, string runId, string name, string dataJson)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var idx = s.Runs.FindIndex(r => r.Id == runId);
            if (idx < 0) throw new KeyNotFoundException($"Run not found: {runId}");
            var run = s.Runs[idx];
            // Normalized non-null by Create/ReadFile; lists are mutated in place.
            // Cap: stop persisting (not streaming) text deltas past 5000 events.
            // Dropped deltas return -1 so transports never issue a replayable id for them.
            if (run.Events.Count >= MaxRunEvents && name == "text.delta")
                return -1;
            var seq = run.Events.Count;
            run.Events.Add(new StoredRunEvent(seq, name, dataJson, DateTimeOffset.UtcNow));
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
            return seq;
        }
    }

    public IReadOnlyList<StoredRunEvent> GetRunEvents(string sessionId, string runId, int afterSeq = -1)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var run = s.Runs.FirstOrDefault(r => r.Id == runId)
                ?? throw new KeyNotFoundException($"Run not found: {runId}");
            return run.Events.Where(e => e.Seq > afterSeq).ToList();
        }
    }

    public StoredToolCall CreateToolCall(string sessionId, string runId, string name, string argumentsJson, string? providerCallId = null, string? providerItemId = null)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var tc = new StoredToolCall(ToolIds.New(), sessionId, runId, name, argumentsJson,
                ToolStatuses.Running, null, null, DateTimeOffset.UtcNow, null, providerCallId, providerItemId);
            s.ToolCalls.Add(tc);
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
            return tc;
        }
    }

    public void FinishToolCall(string sessionId, string toolCallId, string status, string? resultText, string? error)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var idx = s.ToolCalls.FindIndex(t => t.Id == toolCallId);
            if (idx < 0) throw new KeyNotFoundException($"Tool call not found: {toolCallId}");
            var old = s.ToolCalls[idx];
            s.ToolCalls[idx] = old with
            {
                Status = status, ResultText = resultText, Error = error, CompletedAt = DateTimeOffset.UtcNow,
            };
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    public bool TryGetRequestKey(string sessionId, string clientRequestId, out string runId)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            return s.RequestKeys.TryGetValue(clientRequestId, out runId!);
        }
    }

    public void SetRequestKey(string sessionId, string clientRequestId, string runId)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            s.RequestKeys[clientRequestId] = runId;
            WriteFile(PathFor(s.Id), s with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }

    private const int MaxRunEvents = 5000;

    public int ReconcileInterruptedRuns()
    {
        var count = 0;
        foreach (var file in Directory.GetFiles(_sessionsDir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            // Only touch sessions whose owner is gone: a live turn holds this lock.
            // A CLI racing the server skips the session instead of killing its run.
            FileStream? sessionLock = null;
            try { sessionLock = SessionLockFile.Acquire(_sessionsDir, id, TimeSpan.Zero, CancellationToken.None); }
            catch (IOException) { continue; }
            using (sessionLock)
            lock (_gate)
            {
                SessionRecord s;
                try { s = ReadFile(file); }
                catch { continue; }
                var dirty = false;
                for (var i = 0; i < s.Runs.Count; i++)
                {
                    var r = s.Runs[i];
                    if (r.Status is RunStatuses.Running or RunStatuses.Queued)
                    {
                        s.Runs[i] = r with
                        {
                            Status = RunStatuses.Failed,
                            ErrorCode = "interrupted",
                            ErrorMessage = "Process ended before the run completed; last durable state preserved.",
                            CompletedAt = DateTimeOffset.UtcNow,
                        };
                        count++;
                        dirty = true;
                    }
                }
                if (dirty) WriteFile(file, s with { UpdatedAt = DateTimeOffset.UtcNow });
                var tdirty = false;
                for (var i = 0; i < s.ToolCalls.Count; i++)
                {
                    var t = s.ToolCalls[i];
                    if (t.Status is ToolStatuses.Running or ToolStatuses.Pending)
                    {
                        s.ToolCalls[i] = t with
                        {
                            Status = ToolStatuses.Failed,
                            Error = "Process ended before the tool finished; last durable state preserved.",
                            CompletedAt = DateTimeOffset.UtcNow,
                        };
                        count++;
                        tdirty = true;
                    }
                }
                if (tdirty) WriteFile(file, s with { UpdatedAt = DateTimeOffset.UtcNow });
            }
        }
        return count;
    }
}
