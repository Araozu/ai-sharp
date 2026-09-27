namespace AiSharp.Core;

/// <summary>
/// Canonical domain types for Phase 0/1. Transport-neutral; no HTTP or provider wire types here.
/// </summary>
public static class Roles
{
    public const string System = "system";
    public const string Developer = "developer";
    public const string User = "user";
    public const string Assistant = "assistant";
}

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage UserMsg(string text) => new(Roles.User, text);
    public static ChatMessage AssistantMsg(string text) => new(Roles.Assistant, text);
    public static ChatMessage SystemMsg(string text) => new(Roles.System, text);
}

public sealed record ProviderUsage(
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int? CachedInputTokens = null,
    int? ReasoningOutputTokens = null);

public sealed record StoredMessage(
    string Id,
    string Role,
    string Text,
    DateTimeOffset CreatedAt,
    string? ProviderResponseId = null,
    ProviderUsage? Usage = null);

public sealed record StoredRun(
    string Id,
    string SessionId,
    string Status, // queued | running | completed | failed | cancelled
    string Prompt,
    string? ResponseText,
    ProviderUsage? Usage,
    string? ErrorCode,
    string? ErrorMessage,
    string? ProviderResponseId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record SessionRecord(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Title,
    List<StoredMessage> Messages,
    List<StoredRun> Runs);

public static class Ids
{
    public static string NewSessionId() => "sess_" + Guid.NewGuid().ToString("N")[..12];
    public static string NewRunId() => "run_" + Guid.NewGuid().ToString("N")[..12];
    public static string NewMessageId() => "msg_" + Guid.NewGuid().ToString("N")[..12];
}
