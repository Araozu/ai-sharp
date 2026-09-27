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
    StoredMessage AppendMessage(string sessionId, string role, string text, string? providerResponseId = null, ProviderUsage? usage = null);
    StoredRun CreateRun(string sessionId, string prompt);
    void FinishRun(string sessionId, string runId, string status, string? responseText, ProviderUsage? usage, string? errorCode, string? errorMessage, string? providerResponseId);
    /// <summary>Mark runs left non-terminal by a crash as failed. Returns the count reconciled.</summary>
    int ReconcileInterruptedRuns();
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
        var s = new SessionRecord(Ids.NewSessionId(), now, now, title, [], []);
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

    public StoredMessage AppendMessage(string sessionId, string role, string text, string? providerResponseId = null, ProviderUsage? usage = null)
    {
        lock (_gate)
        {
            var s = Get_NoLock(sessionId);
            var m = new StoredMessage(Ids.NewMessageId(), role, text, DateTimeOffset.UtcNow, providerResponseId, usage);
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
            var run = new StoredRun(Ids.NewRunId(), sessionId, RunStatuses.Running, prompt, null, null, null, null, null, now, null);
            s.Runs.Add(run);
            WriteFile(PathFor(s.Id), s with { UpdatedAt = now });
            return run;
        }
    }

    public void FinishRun(string sessionId, string runId, string status, string? responseText, ProviderUsage? usage, string? errorCode, string? errorMessage, string? providerResponseId)
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
        var rec = JsonSerializer.Deserialize<SessionRecord>(json, JsonOpts);
        return rec ?? throw new InvalidDataException($"Empty session file: {path}");
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

    public int ReconcileInterruptedRuns()
    {
        lock (_gate)
        {
            var count = 0;
            foreach (var file in Directory.GetFiles(_sessionsDir, "*.json"))
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
            }
            return count;
        }
    }
}
