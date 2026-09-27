using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiSharp.Providers.OpencodeGo;

/// <summary>
/// Options for the OpenCode Go provider (OpenAI Responses API).
/// Docs: https://opencode.ai/docs/go/ — model muse-spark-1.3-contributor,
/// endpoint https://opencode.ai/zen/go/v1/responses, package @ai-sdk/openai.
/// </summary>
public sealed record OpencodeGoOptions(
    string BaseUrl,
    string Model,
    string ApiKey,
    string UserAgent)
{
    public const string ProviderId = "opencode-go";
    public const string DefaultModel = "muse-spark-1.3-contributor";

    public string ResponsesEndpoint => BaseUrl.TrimEnd('/') + "/responses";
    public string ModelsEndpoint => BaseUrl.TrimEnd('/') + "/models";
}

// ---- Responses API DTOs (minimal subset we need) ----

internal sealed record ResponsesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] List<object> Input,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("store")] bool Store);

internal sealed class ResponsesResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("output")] public List<JsonElement> Output { get; set; } = [];
    [JsonPropertyName("usage")] public ResponsesUsage? Usage { get; set; }
    [JsonPropertyName("error")] public JsonElement Error { get; set; }
}

internal sealed class ResponsesUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
    [JsonPropertyName("input_tokens_details")] public TokenDetails? InputTokensDetails { get; set; }
    [JsonPropertyName("output_tokens_details")] public TokenDetails? OutputTokensDetails { get; set; }
}

internal sealed class TokenDetails
{
    [JsonPropertyName("cached_tokens")] public int? CachedTokens { get; set; }
    [JsonPropertyName("reasoning_tokens")] public int? ReasoningTokens { get; set; }
}

internal static class ResponsesTextExtractor
{
    /// <summary>Extract concatenated assistant text from a non-stream Responses object.</summary>
    public static string ExtractText(ResponsesResponse resp)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in resp.Output)
        {
            if (!item.TryGetProperty("type", out var t)) continue;
            if (t.GetString() != "message") continue;
            if (!item.TryGetProperty("content", out var content)) continue;
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "output_text"
                    && part.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
        }
        return sb.ToString();
    }

    public static string? ExtractErrorMessage(JsonElement errorElement)
    {
        if (errorElement.ValueKind == JsonValueKind.Undefined || errorElement.ValueKind == JsonValueKind.Null)
            return null;
        if (errorElement.ValueKind == JsonValueKind.String) return errorElement.GetString();
        if (errorElement.TryGetProperty("message", out var m)) return m.GetString();
        return errorElement.GetRawText();
    }
}
