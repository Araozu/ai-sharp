using AiSharp.Core;

namespace AiSharp.Core;

/// <summary>
/// Normalized, provider-agnostic streaming events. Phase 1 is text-only;
/// tool-call events are reserved for Phase 2.
/// </summary>
public abstract record ProviderStreamEvent;

public sealed record TextDeltaEvent(string Delta) : ProviderStreamEvent;

/// <summary>A completed model request for a local tool call. The engine authorizes,
/// executes, and feeds the result back; provider IDs are preserved for the echo.</summary>
public sealed record ToolCallEvent(ToolCallRequest Call) : ProviderStreamEvent;

public sealed record CompletedEvent(
    string FullText,
    ProviderUsage Usage,
    string? ProviderResponseId) : ProviderStreamEvent;

/// <summary>
/// Request passed to a provider adapter. SessionId is used for gateways
/// that need a stable conversation id (OpenCode Go: x-opencode-session).
/// Tools, when non-empty, are offered as function tools with tool_choice=auto.
/// </summary>
public sealed record ProviderRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    string? SessionId,
    CancellationToken CancellationToken,
    IReadOnlyList<ToolDefinition>? Tools = null,
    IReadOnlyList<object>? ExtraInput = null,
    IReadOnlyList<object>? FullInput = null);

public sealed record ProviderResult(
    string Text,
    ProviderUsage Usage,
    string? ProviderResponseId);

public sealed record ProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsVision,
    int MaxOutputChars);

/// <summary>
/// Transport-neutral provider contract for Phase 1.
/// Phase 2 will extend this with tool-call data without breaking the shape.
/// </summary>
public interface IChatProvider
{
    string ProviderId { get; }
    ProviderCapabilities Capabilities { get; }

    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request);

    Task<ProviderResult> CompleteAsync(ProviderRequest request);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}

/// <summary>
/// Normalized provider failure. Never includes the API key.
/// </summary>
public sealed class ProviderException : Exception
{
    public string Code { get; }
    public bool Retryable { get; }
    public int? StatusCode { get; }

    public ProviderException(string code, string message, bool retryable = false, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
        StatusCode = statusCode;
    }
}

public static class ProviderErrorCodes
{
    public const string Auth = "provider_auth";
    public const string ModelUnavailable = "model_unavailable";
    public const string RateLimited = "rate_limited";
    public const string Timeout = "provider_timeout";
    public const string Server = "provider_server";
    public const string BadRequest = "provider_bad_request";
    public const string Cancelled = "cancelled";
    public const string Unknown = "provider_unknown";
}
