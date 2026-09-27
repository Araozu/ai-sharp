using System.Text.Json;

namespace AiSharp.Core;

/// <summary>
/// Phase 2 owned tool loop: registry, input validation, policy gate, timeouts, output limits.
/// Providers request tools by sending ToolDefinitions; the engine authorizes and executes.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJson);

public sealed record ToolCallRequest(string ItemId, string CallId, string Name, string ArgumentsJson);

public sealed record ToolExecResult(string Status, string? Output, string? Error);

public static class ToolStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Cancelled = "cancelled";
}

public sealed record StoredToolCall(
    string Id,
    string SessionId,
    string RunId,
    string Name,
    string ArgumentsJson,
    string Status,
    string? ResultText,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ProviderCallId = null,
    string? ProviderItemId = null);

public static class ToolIds
{
    public static string New() => "tlc_" + Guid.NewGuid().ToString("N")[..12];
}

/// <summary>
/// A locally-executed tool. Implementations must be deterministic, side-effect
/// constrained, and honor cancellation. Phase 2 ships safe built-ins only.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON Schema (subset: object/properties/required/additionalProperties) for the arguments object.</summary>
    string ParametersSchemaJson { get; }
    /// <summary>Approval: "auto" runs without asking; anything else is denied in Phase 2.</summary>
    string Approval { get; }
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    public int MaxOutputChars { get; }
    public TimeSpan DefaultTimeout { get; }

    public ToolRegistry(int maxOutputChars = 4_000, TimeSpan? defaultTimeout = null)
    {
        MaxOutputChars = maxOutputChars;
        DefaultTimeout = defaultTimeout
            ?? (int.TryParse(Environment.GetEnvironmentVariable("AI_SHARP_TOOL_TIMEOUT_SEC"), out var s) && s > 0
                ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(30));
    }

    public void Register(ITool tool) => _tools[tool.Name] = tool;
    public ITool? Find(string name) => _tools.TryGetValue(name, out var t) ? t : null;
    public IReadOnlyList<ToolDefinition> Definitions() =>
        _tools.Values.Select(t => new ToolDefinition(t.Name, t.Description, t.ParametersSchemaJson)).ToList();

    /// <summary>Policy gate: registered + auto approval runs; everything else is denied with a reason.</summary>
    public (bool Allowed, string? Reason) CheckPolicy(string name)
    {
        var tool = Find(name);
        if (tool is null) return (false, $"Tool '{name}' is not registered. Only registered local tools may run.");
        if (!string.Equals(tool.Approval, "auto", StringComparison.OrdinalIgnoreCase))
            return (false, $"Tool '{name}' requires approval '{tool.Approval}'; Phase 2 only auto-runs safe built-ins.");
        return (true, null);
    }

    /// <summary>Validate args against the tool's schema subset. Returns null when valid.</summary>
    public static string? ValidateArguments(string schemaJson, JsonElement args)
    {
        try
        {
            using var schema = JsonDocument.Parse(schemaJson);
            return ValidateAgainst(schema.RootElement, args, "$");
        }
        catch (JsonException ex)
        {
            return $"Tool schema is not valid JSON: {ex.Message}";
        }
    }

    private static string? ValidateAgainst(JsonElement schema, JsonElement value, string path)
    {
        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not null && !CheckType(type, value))
            return $"{path}: expected {type}, got {Describe(value)}.";
        if (type == "object" || (type is null && schema.TryGetProperty("properties", out _)))
        {
            if (value.ValueKind != JsonValueKind.Object) return $"{path}: expected object.";
            if (schema.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                {
                    var name = r.GetString() ?? "";
                    if (!value.TryGetProperty(name, out _)) return $"{path}: missing required property '{name}'.";
                }
            var allowExtra = true;
            if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False)
                allowExtra = false;
            var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;
            foreach (var prop in value.EnumerateObject())
            {
                if (props.HasValue && props.Value.TryGetProperty(prop.Name, out var sub))
                {
                    var err = ValidateAgainst(sub, prop.Value, path + "." + prop.Name);
                    if (err is not null) return err;
                }
                else if (!allowExtra)
                {
                    return $"{path}: unexpected property '{prop.Name}'.";
                }
            }
        }
        if (type == "array" && schema.TryGetProperty("items", out var items) && value.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var el in value.EnumerateArray())
            {
                var err = ValidateAgainst(items, el, $"{path}[{i}]");
                if (err is not null) return err;
                i++;
            }
        }
        return null;
    }

    private static bool CheckType(string type, JsonElement v) => type switch
    {
        "object" => v.ValueKind == JsonValueKind.Object,
        "array" => v.ValueKind == JsonValueKind.Array,
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => v.ValueKind == JsonValueKind.Null,
        _ => true, // unknown type keywords are ignored (forward compatible)
    };

    private static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out _) ? "integer" : "number",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => v.ValueKind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Execute with policy already checked by the caller: hard timeout + output cap.
    /// The timeout is enforced by WaitAsync, so even a tool that ignores cancellation
    /// cannot hold the turn past the deadline (its orphaned task is observed, never awaited).
    /// </summary>
    public async Task<ToolExecResult> ExecuteAsync(ITool tool, string argumentsJson, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ToolExecResult(ToolStatuses.Failed, null, $"Arguments are not valid JSON: {ex.Message}");
        }
        var schemaError = ValidateArguments(tool.ParametersSchemaJson, args);
        if (schemaError is not null)
            return new ToolExecResult(ToolStatuses.Failed, null, $"Argument validation failed: {schemaError}");
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            // Note: the call itself is inside try because sync tools throw synchronously.
            var task = tool.ExecuteAsync(args, linked.Token);
            // Observe orphaned faults; the turn never awaits past the deadline.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var output = await task.WaitAsync(DefaultTimeout, ct).ConfigureAwait(false) ?? "";
            if (output.Length > MaxOutputChars) output = output[..MaxOutputChars] + $"…[truncated to {MaxOutputChars} chars]";
            return new ToolExecResult(ToolStatuses.Completed, output, null);
        }
        catch (TimeoutException)
        {
            return new ToolExecResult(ToolStatuses.Failed, null, $"Tool timed out after {DefaultTimeout.TotalSeconds:n0}s.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cooperative tool observed the deadline token in the WaitAsync race.
            return new ToolExecResult(ToolStatuses.Failed, null, $"Tool timed out after {DefaultTimeout.TotalSeconds:n0}s.");
        }
        catch (OperationCanceledException)
        {
            return new ToolExecResult(ToolStatuses.Cancelled, null, "Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            return new ToolExecResult(ToolStatuses.Failed, null, $"Tool raised {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static ToolRegistry WithBuiltins() => new ToolRegistry().Also(r =>
    {
        r.Register(new CalcTool());
        r.Register(new TimeTool());
    });
}

internal static class RegistryFluent
{
    public static ToolRegistry Also(this ToolRegistry r, Action<ToolRegistry> f) { f(r); return r; }
}
