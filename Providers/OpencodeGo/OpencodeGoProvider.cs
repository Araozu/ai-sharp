using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiSharp.Core;

namespace AiSharp.Providers.OpencodeGo;

/// <summary>
/// Direct provider adapter for OpenCode Go via the OpenAI Responses API.
/// See https://opencode.ai/docs/go/ (endpoints) and https://opencode.ai/docs/zen/ (model table).
///
/// Go requirements honored:
/// - Custom User-Agent (e.g. ai-sharp/0.1), not a generic SDK name.
/// - Stable x-opencode-session header per conversation (AiSharp session id).
/// - Stateless: full transcript sent each turn with store:false; AI# owns history.
/// </summary>
public sealed class OpencodeGoProvider : IChatProvider
{
    private readonly HttpClient _http;
    private readonly OpencodeGoOptions _options;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string ProviderId => OpencodeGoOptions.ProviderId;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true,
        SupportsTools: false, // Phase 2 will add function tools
        SupportsVision: false, // text-only in Phase 1
        MaxOutputChars: 64_000);

    public OpencodeGoProvider(HttpClient http, OpencodeGoOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException("OpenCode Go API key is missing.", nameof(options));
    }

    public static OpencodeGoProvider Create(OpencodeGoOptions options, TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        return new OpencodeGoProvider(http, options);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _options.ModelsEndpoint);
        ApplyHeaders(req, "ai-sharp-models");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw ToProviderException(res.StatusCode, body);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var list = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id)) list.Add(id.GetString() ?? "");
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            throw new ProviderException(ProviderErrorCodes.Unknown, "Failed to parse models response.", false, (int)res.StatusCode, ex);
        }
    }

    public async Task<ProviderResult> CompleteAsync(ProviderRequest request)
    {
        var sb = new StringBuilder();
        ProviderUsage usage = new(0, 0, 0);
        string? responseId = null;
        await foreach (var ev in StreamAsync(request).ConfigureAwait(false))
        {
            switch (ev)
            {
                case TextDeltaEvent d: sb.Append(d.Delta); break;
                case CompletedEvent c:
                    // Prefer server-assembled full text if our delta stream was empty.
                    if (sb.Length == 0) sb.Append(c.FullText);
                    usage = c.Usage;
                    responseId = c.ProviderResponseId;
                    break;
            }
        }
        var text = sb.ToString();
        if (text.Length > Capabilities.MaxOutputChars)
            text = text[..Capabilities.MaxOutputChars];
        return new ProviderResult(text, usage, responseId);
    }

    public async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request)
    {
        // Use the request's CancellationToken (transport-neutral contract).
        var ct = request.CancellationToken;
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;

        var payload = new ResponsesRequest(
            Model: model,
            Input: BuildInput(request.Messages),
            Stream: true,
            Store: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, _options.ResponsesEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };
        ApplyHeaders(req, request.SessionId);

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorCodes.Cancelled, "Request was cancelled.", false);
        }
        catch (Exception ex)
        {
            throw new ProviderException(ProviderErrorCodes.Server, $"Provider transport error: {ex.Message}", true, null, ex);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                var errBody = await SafeReadBodyAsync(res, ct).ConfigureAwait(false);
                throw ToProviderException(res.StatusCode, errBody);
            }

            var fullText = new StringBuilder();
            ProviderUsage? finalUsage = null;
            string? responseId = null;
            string? completedText = null;

            await foreach (var (eventName, data) in ReadSseAsync(res, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(data)) continue;

                // Key streaming events from OpenAI Responses API:
                // - response.output_text.delta {delta}
                // - response.completed {response:{...}}
                // - response.failed / response.error
                // NOTE: yield must stay outside try/catch (C# CS1626), so parse first, yield after.
                string? pendingDelta = null;
                string? pendingError = null;
                bool malformed = false;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : eventName;

                    switch (type)
                    {
                        case "response.output_text.delta":
                        {
                            var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                            if (delta.Length > 0)
                            {
                                fullText.Append(delta);
                                if (fullText.Length <= Capabilities.MaxOutputChars + 4096)
                                    pendingDelta = delta;
                            }
                            break;
                        }
                        case "response.completed":
                        {
                            if (root.TryGetProperty("response", out var r))
                            {
                                responseId = r.TryGetProperty("id", out var id) ? id.GetString() : responseId;
                                finalUsage = ParseUsage(r);
                                completedText = ResponsesTextExtractor.ExtractText(new ResponsesResponse
                                {
                                    Output = r.TryGetProperty("output", out var o)
                                        ? o.EnumerateArray().ToList() : [],
                                    Usage = null,
                                });
                            }
                            break;
                        }
                        case "response.failed":
                        case "response.error":
                        case "error":
                        {
                            pendingError = ExtractSseError(root) ?? "Provider reported failure.";
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is not ProviderException)
                {
                    // Ignore malformed SSE data frames; keep streaming.
                    malformed = true;
                }

                if (pendingError is not null)
                    throw ClassifyErrorMessage(pendingError, null);
                if (malformed)
                    continue;
                if (pendingDelta is not null)
                    yield return new TextDeltaEvent(pendingDelta);
            }

            var text = fullText.Length > 0 ? fullText.ToString() : (completedText ?? "");
            if (text.Length > Capabilities.MaxOutputChars)
                text = text[..Capabilities.MaxOutputChars];
            yield return new CompletedEvent(text, finalUsage ?? new ProviderUsage(0, 0, 0), responseId);
        }
    }

    // ---- helpers ----

    private void ApplyHeaders(HttpRequestMessage req, string? sessionId)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd(_options.UserAgent);
        // Stable per-conversation id required by OpenCode Go for routing/cache.
        var sid = string.IsNullOrWhiteSpace(sessionId) ? "ai-sharp-" + Guid.NewGuid().ToString("N")[..8] : sessionId!;
        req.Headers.Remove("x-opencode-session");
        req.Headers.Add("x-opencode-session", sid);
    }

    private static List<object> BuildInput(IReadOnlyList<ChatMessage> messages)
    {
        var input = new List<object>(messages.Count);
        foreach (var m in messages)
        {
            var role = (m.Role ?? "user").ToLowerInvariant() switch
            {
                "system" => "system",
                "developer" => "developer",
                "assistant" => "assistant",
                _ => "user",
            };
            // Assistant history uses output_text parts (verified against live API);
            // user/system use input_text. Store:false keeps AI# as source of truth.
            var partType = role == "assistant" ? "output_text" : "input_text";
            input.Add(new Dictionary<string, object>
            {
                ["role"] = role,
                ["content"] = new List<Dictionary<string, string>>
                {
                    new() { ["type"] = partType, ["text"] = m.Content ?? "" }
                }
            });
        }
        return input;
    }

    private static async IAsyncEnumerable<(string? Event, string Data)> ReadSseAsync(
        HttpResponseMessage res,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? currentEvent = null;
        var dataBuf = new StringBuilder();
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (dataBuf.Length > 0)
                {
                    yield return (currentEvent, dataBuf.ToString());
                    dataBuf.Clear();
                    currentEvent = null;
                }
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
                currentEvent = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var chunk = line["data:".Length..].TrimStart();
                if (chunk == "[DONE]") yield break;
                if (dataBuf.Length > 0) dataBuf.Append('\n');
                dataBuf.Append(chunk);
            }
            // ignore comments (: ...) and other fields
        }
        if (dataBuf.Length > 0) yield return (currentEvent, dataBuf.ToString());
    }

    private static ProviderUsage ParseUsage(JsonElement responseObj)
    {
        try
        {
            if (!responseObj.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return new ProviderUsage(0, 0, 0);
            int In(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            int? cached = null, reasoning = null;
            if (u.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object
                && itd.TryGetProperty("cached_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
                cached = c.GetInt32();
            if (u.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object
                && otd.TryGetProperty("reasoning_tokens", out var r) && r.ValueKind == JsonValueKind.Number)
                reasoning = r.GetInt32();
            return new ProviderUsage(In(u, "input_tokens"), In(u, "output_tokens"), In(u, "total_tokens"), cached, reasoning);
        }
        catch
        {
            return new ProviderUsage(0, 0, 0);
        }
    }

    private static string? ExtractSseError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var e))
        {
            if (e.ValueKind == JsonValueKind.String) return e.GetString();
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString();
            return e.GetRawText();
        }
        if (root.TryGetProperty("message", out var msg)) return msg.GetString();
        return null;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return ""; }
    }

    internal static ProviderException ToProviderException(System.Net.HttpStatusCode status, string body)
    {
        var code = (int)status;
        var msg = TryExtractApiError(body) ?? $"Provider HTTP {(int)status}.";
        if (code == 401 || code == 403)
            return new ProviderException(ProviderErrorCodes.Auth, $"Authentication failed ({code}). Check your OpenCode Go API key. {msg}", false, code);
        if (code == 429)
            return new ProviderException(ProviderErrorCodes.RateLimited, $"Rate limited ({code}). {msg}", true, code);
        if (code == 400 || code == 404)
        {
            if (msg.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || msg.Contains("model", StringComparison.OrdinalIgnoreCase))
                return new ProviderException(ProviderErrorCodes.ModelUnavailable, msg, false, code);
            return new ProviderException(ProviderErrorCodes.BadRequest, msg, false, code);
        }
        if (code >= 500)
            return new ProviderException(ProviderErrorCodes.Server, msg, true, code);
        return ClassifyErrorMessage(msg, code);
    }

    private static string? TryExtractApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("message", out var m)) return m.GetString();
                    return e.GetRawText();
                }
            }
            if (root.TryGetProperty("type", out var t) && t.GetString() == "error"
                && root.TryGetProperty("error", out var e2))
                return e2.GetRawText();
            return body.Length <= 500 ? body : body[..500];
        }
        catch
        {
            return body.Length <= 500 ? body : body[..500];
        }
    }

    private static ProviderException ClassifyErrorMessage(string msg, int? code)
    {
        if (msg.Contains("API key", StringComparison.OrdinalIgnoreCase) || msg.Contains("AuthError", StringComparison.OrdinalIgnoreCase))
            return new ProviderException(ProviderErrorCodes.Auth, msg, false, code);
        if (msg.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return new ProviderException(ProviderErrorCodes.ModelUnavailable, msg, false, code);
        return new ProviderException(ProviderErrorCodes.Unknown, msg, code is >= 500 or 429, code);
    }
}
