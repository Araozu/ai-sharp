using System.Text.Json;
using AiSharp.Core;

namespace AiSharp.Core;

/// <summary>
/// Durable local store: one JSON file per session under {DataDir}/sessions/{id}.json.
/// Atomic writes (temp + rename) so restart still sees a complete transcript.
/// </summary>
public interface ISessionStore
{
    SessionRecord Create(string? title = null);
    IReadOnlyList<SessionRecord> List();
    SessionRecord Get(string sessionId);
    void Save(SessionRecord session);
    StoredMessage AppendMessage(string sessionId, string role, string text, string? providerResponseId = null, ProviderUsage? usage = null);
    StoredRun CreateRun(string sessionId, string prompt);
    void FinishRun(string sessionId, string runId, string status, string? responseText, ProviderUsage? usage, string? errorCode, string? errorMessage, string? providerResponseId);
}

public sealed class FileSessionStore : ISessionStore
{
    private readonly string _sessionsDir;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public FileSessionStore(string dataDir)
    {
        _sessionsDir = Path.Combine(dataDir, "sessions");
        Directory.CreateDirectory(_sessionsDir);
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
        // Guard against path traversal.
        foreach (var c in Path.GetInvalidFileNameChars())
            sessionId = sessionId.Replace(c, '_');
        sessionId = sessionId.Replace("/", "_").Replace("\\", "_").Replace("..", "_");
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
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
