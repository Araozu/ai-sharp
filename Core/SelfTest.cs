using AiSharp.Core;
using AiSharp.Providers.OpencodeGo;

namespace AiSharp.Core;

/// <summary>
/// Offline self-test (Phase 3): exercises the engine without a network provider —
/// tool validation/policy/timeout, cancellation, concurrency, recovery, idempotency keys,
/// event replay. Run with `ai-sharp selftest`. Prints PASS/FAIL per case.
/// </summary>
public static class SelfTest
{
    private sealed record Case(string Name, Func<Task> Run);
    private static int _passed;
    private static int _failed;

    public static async Task<int> RunAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-sharp-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        var cases = new List<Case>
        {
            new("calc-valid", () => CalcValid(dir)),
            new("calc-bad-expression", () => CalcBadExpr(dir)),
            new("unknown-tool-denied", () => UnknownDenied(dir)),
            new("malformed-args-fail", () => MalformedArgs(dir)),
            new("tool-timeout", () => ToolTimeout(dir)),
            new("tool-timeout-noncooperative", () => ToolTimeoutNonCooperative(dir)),
            new("sse-function-call-fixture", () => SseFunctionCallFixture()),
            new("cancel-mid-run", () => CancelMidRun(dir)),
            new("concurrent-turns-serialize", () => ConcurrentTurns(dir)),
            new("reconcile-interrupted", () => Reconcile(dir)),
            new("request-key-idempotency", () => RequestKeys(dir)),
            new("event-replay-order", () => EventReplay(dir)),
        };
        foreach (var c in cases)
        {
            try { await c.Run().ConfigureAwait(false); Pass(c.Name); }
            catch (Exception ex) { Fail(c.Name, ex); }
        }
        Console.WriteLine($"selftest: {_passed} passed, {_failed} failed");
        try { Directory.Delete(dir, recursive: true); } catch { }
        return _failed == 0 ? 0 : 1;
    }

    private static void Pass(string n) { _passed++; Console.WriteLine($"  PASS {n}"); }
    private static void Fail(string n, Exception ex)
    {
        _failed++;
        var msg = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
        Console.WriteLine($"  FAIL {n}: {ex.GetType().Name}: {msg}");
    }

    private static void Check(bool cond, string msg)
    {
        if (!cond) throw new InvalidOperationException(msg);
    }

    private static FileSessionStore Store(string dir)
    {
        var d = Path.Combine(dir, Guid.NewGuid().ToString("N")[..8]);
        return new FileSessionStore(d);
    }

    // ---- scripted fake provider: per-round script of events ----

    private sealed class Script : IChatProvider
    {
        public string ProviderId => "fake";
        public ProviderCapabilities Capabilities { get; } = new(true, true, false, 64_000);
        private readonly Queue<Func<List<ProviderStreamEvent>>> _rounds = new();
        public int Calls;
        public ProviderRequest? LastRequest;
        public void Enqueue(params ProviderStreamEvent[] evs) => _rounds.Enqueue(() => evs.ToList());
        public void EnqueueTool(string name, string args)
        {
            _rounds.Enqueue(() => [new ToolCallEvent(new ToolCallRequest("fc-1", "call-1", name, args))]);
        }
        public async IAsyncEnumerable<ProviderStreamEvent> StreamAsync(ProviderRequest request)
        {
            Calls++;
            LastRequest = request;
            await Task.Yield();
            List<ProviderStreamEvent> evs;
            if (_rounds.Count > 0) evs = _rounds.Dequeue()();
            else evs = [new CompletedEvent("done", new ProviderUsage(1, 1, 2), "resp-fake")];
            foreach (var e in evs)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                yield return e;
            }
            if (evs.All(e => e is not CompletedEvent))
                yield return new CompletedEvent("", new ProviderUsage(1, 1, 2), "resp-fake");
        }
        public async Task<ProviderResult> CompleteAsync(ProviderRequest request)
        {
            var text = "";
            await foreach (var e in StreamAsync(request))
                if (e is CompletedEvent c) text = c.FullText;
            return new ProviderResult(text, new ProviderUsage(1, 1, 2), "resp-fake");
        }
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }

    private sealed class SleepTool : ITool
    {
        public string Name => "sleepy";
        public string Description => "Sleeps; test-only.";
        public string Approval => "auto";
        public string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";
        public async Task<string> ExecuteAsync(System.Text.Json.JsonElement args, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "woke";
        }
    }

    private static bool HasEcho(IReadOnlyList<object>? input, string type, string match)
    {
        if (input is null) return false;
        foreach (var item in input)
        {
            if (item is not Dictionary<string, object> d) continue;
            if (!d.TryGetValue("type", out var t) || (t as string) != type) continue;
            var flat = string.Join(" ", d.Values.Select(v => v?.ToString()));
            if (flat.Contains(match, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static async Task CalcValid(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("calc", """{"expression":"12*13"}""");
        script.Enqueue(new TextDeltaEvent("156"), new CompletedEvent("156", new ProviderUsage(10, 5, 15), "r1"));
        var ex = new TurnExecutor(store, script, "fake-model");
        var res = await ex.ExecuteAsync(s.Id, "12*13?");
        Check(res.Run.Status == RunStatuses.Completed, $"status={res.Run.Status}");
        Check(res.Text.Contains("156"), $"text={res.Text}");
        Check(res.ToolCalls.Count == 1 && res.ToolCalls[0].Status == ToolStatuses.Completed, "tool row");
        Check(res.ToolCalls[0].ResultText == "156", $"result={res.ToolCalls[0].ResultText}");
        Check(script.Calls == 2, $"rounds={script.Calls}");
        // The second model round must carry the stateless echo: intent + result.
        Check(HasEcho(script.LastRequest?.FullInput, "function_call", "12*13"), "round 2 echoes function_call");
        Check(HasEcho(script.LastRequest?.FullInput, "function_call_output", "156"), "round 2 echoes output 156");
    }

    private static async Task CalcBadExpr(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("calc", """{"expression":"2++"}""");
        script.Enqueue(new CompletedEvent("bad", new ProviderUsage(1, 1, 2), "r1"));
        var ex = new TurnExecutor(store, script, "fake-model");
        var res = await ex.ExecuteAsync(s.Id, "go");
        Check(res.ToolCalls[0].Status == ToolStatuses.Failed, $"status={res.ToolCalls[0].Status}");
        Check(res.Run.Status == RunStatuses.Completed, "run continues to final answer after tool failure");
    }

    private static async Task UnknownDenied(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("rm_rf", """{}""");
        script.Enqueue(new CompletedEvent("refused", new ProviderUsage(1, 1, 2), "r1"));
        var ex = new TurnExecutor(store, script, "fake-model");
        var res = await ex.ExecuteAsync(s.Id, "go");
        Check(res.ToolCalls[0].Status == ToolStatuses.Denied, $"status={res.ToolCalls[0].Status}");
        // Denials are echoed too: the API never sees an unanswered call.
        Check(HasEcho(script.LastRequest?.FullInput, "function_call_output", "denied"), "denial echoed");
    }

    private static async Task MalformedArgs(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("calc", """{"wrong":"1"}""");
        script.Enqueue(new CompletedEvent("fixed", new ProviderUsage(1, 1, 2), "r1"));
        var ex = new TurnExecutor(store, script, "fake-model");
        var res = await ex.ExecuteAsync(s.Id, "go");
        Check(res.ToolCalls[0].Status == ToolStatuses.Failed, $"status={res.ToolCalls[0].Status}");
        Check(res.ToolCalls[0].Error?.Contains("required") == true, $"error={res.ToolCalls[0].Error}");
    }

    private sealed class NonCooperativeTool : ITool
    {
        public string Name => "stubborn";
        public string Description => "Ignores cancellation; test-only.";
        public string Approval => "auto";
        public string ParametersSchemaJson => """{"type":"object","properties":{},"additionalProperties":false}""";
        public Task<string> ExecuteAsync(System.Text.Json.JsonElement args, CancellationToken ct) =>
            // Deliberately ignores ct: the registry's hard bound must still win.
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => "late");
    }

    private static async Task ToolTimeoutNonCooperative(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("stubborn", """{}""");
        script.Enqueue(new CompletedEvent("late", new ProviderUsage(1, 1, 2), "r1"));
        var reg = new ToolRegistry(defaultTimeout: TimeSpan.FromMilliseconds(100));
        reg.Register(new NonCooperativeTool());
        var ex = new TurnExecutor(store, script, "fake-model", reg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await ex.ExecuteAsync(s.Id, "go");
        sw.Stop();
        Check(res.ToolCalls[0].Status == ToolStatuses.Failed, $"status={res.ToolCalls[0].Status}");
        Check(res.ToolCalls[0].Error?.Contains("timed out") == true, $"error={res.ToolCalls[0].Error}");
        Check(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed} (hard bound not enforced)");
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(res);
        }
    }

    /// <summary>Wire-level fixture: canned Responses SSE through the real provider adapter.</summary>
    private static async Task SseFunctionCallFixture()
    {
        var sse = """
            event: response.output_item.added
            data: {"type":"response.output_item.added","sequence_number":0,"output_index":0,"item":{"id":"fc_1","type":"function_call","status":"in_progress","name":"calc","call_id":"call_1","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","sequence_number":1,"output_index":0,"item_id":"fc_1","delta":"{\"expression\":"}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","sequence_number":2,"output_index":0,"item_id":"fc_1","delta":"\"12*13\"}"}

            event: response.output_item.done
            data: {"type":"response.output_item.done","sequence_number":3,"output_index":0,"item":{"id":"fc_1","type":"function_call","status":"completed","name":"calc","call_id":"call_1","arguments":"{\"expression\":\"12*13\"}"}}

            event: response.completed
            data: {"type":"response.completed","sequence_number":4,"response":{"id":"resp_1","status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":7,"total_tokens":12}}}

            """;
        var provider = new OpencodeGoProvider(
            new HttpClient(new CannedHandler(sse)),
            new OpencodeGoOptions("https://selftest.invalid/v1", "m", "dummy", "ai-sharp-selftest/0"));
        var tools = new List<ToolDefinition> { new("calc", "c", """{"type":"object"}""") };
        var req = new ProviderRequest("m", [new ChatMessage(Roles.User, "hi")], "sess-test", CancellationToken.None, tools);
        ToolCallRequest? call = null;
        ProviderUsage? usage = null;
        await foreach (var ev in provider.StreamAsync(req))
        {
            if (ev is ToolCallEvent t) call = t.Call;
            if (ev is CompletedEvent c) usage = c.Usage;
        }
        Check(call is not null, "no ToolCallEvent parsed");
        Check(call!.Name == "calc" && call.CallId == "call_1", $"call={call.Name}/{call.CallId}");
        Check(call.ArgumentsJson.Contains("12*13"), $"args={call.ArgumentsJson}");
        Check(usage?.InputTokens == 5 && usage?.OutputTokens == 7, $"usage={usage}");
    }

    private static async Task ToolTimeout(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("sleepy", """{}""");
        script.Enqueue(new CompletedEvent("late", new ProviderUsage(1, 1, 2), "r1"));
        var reg = new ToolRegistry(defaultTimeout: TimeSpan.FromMilliseconds(100));
        reg.Register(new CalcTool());
        reg.Register(new TimeTool());
        reg.Register(new SleepTool());
        var ex = new TurnExecutor(store, script, "fake-model", reg);
        var res = await ex.ExecuteAsync(s.Id, "go");
        Check(res.ToolCalls[0].Status == ToolStatuses.Failed, $"status={res.ToolCalls[0].Status}");
        Check(res.ToolCalls[0].Error?.Contains("timed out") == true, $"error={res.ToolCalls[0].Error}");
    }

    private static async Task CancelMidRun(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script();
        script.EnqueueTool("sleepy", """{}""");
        var reg = new ToolRegistry(defaultTimeout: TimeSpan.FromSeconds(30));
        reg.Register(new SleepTool());
        var ex = new TurnExecutor(store, script, "fake-model", reg);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(300);
        try
        {
            await ex.ExecuteAsync(s.Id, "go", ct: cts.Token);
            throw new InvalidOperationException("expected cancellation");
        }
        catch (ProviderException pex) when (pex.Code == ProviderErrorCodes.Cancelled)
        {
            var run = store.Get(s.Id).Runs.Last();
            Check(run.Status == RunStatuses.Cancelled, $"status={run.Status}");
        }
    }

    private static async Task ConcurrentTurns(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var script = new Script(); // default: immediate completion, thread-safe enough for the gate test
        var ex = new TurnExecutor(store, script, "fake-model");
        var t1 = ex.ExecuteAsync(s.Id, "first");
        var t2 = ex.ExecuteAsync(s.Id, "second");
        var results = await Task.WhenAll(t1, t2);
        Check(results.All(r => r.Run.Status == RunStatuses.Completed), "both completed");
        var got = store.Get(s.Id);
        Check(got.Messages.Count(m => m.Role == Roles.User) == 2, "2 user msgs");
        Check(got.Messages.Count(m => m.Role == Roles.Assistant) == 2, "2 assistant msgs");
    }

    private static Task Reconcile(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var run = store.CreateRun(s.Id, "stuck");
        store.CreateToolCall(s.Id, run.Id, "calc", """{"expression":"1+1"}""");
        var n = store.ReconcileInterruptedRuns();
        Check(n == 2, $"reconciled={n}");
        var got = store.Get(s.Id);
        Check(got.Runs[0].Status == RunStatuses.Failed, "run failed");
        Check(got.ToolCalls[0].Status == ToolStatuses.Failed, "tool failed");
        return Task.CompletedTask;
    }

    private static Task RequestKeys(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        Check(!store.TryGetRequestKey(s.Id, "k1", out _), "miss first");
        store.SetRequestKey(s.Id, "k1", "run_abc");
        Check(store.TryGetRequestKey(s.Id, "k1", out var r) && r == "run_abc", "hit");
        var again = store.Get(s.Id);
        Check(again.RequestKeys?.TryGetValue("k1", out var r2) == true && r2 == "run_abc", "persisted");
        return Task.CompletedTask;
    }

    private static Task EventReplay(string dir)
    {
        var store = Store(dir);
        var s = store.Create("t");
        var run = store.CreateRun(s.Id, "ev");
        var a = store.AppendRunEvent(s.Id, run.Id, "run.start", """{"a":1}""");
        var b = store.AppendRunEvent(s.Id, run.Id, "text.delta", """{"delta":"hi"}""");
        Check(a == 0 && b == 1, $"seqs={a},{b}");
        var all = store.GetRunEvents(s.Id, run.Id);
        Check(all.Count == 2 && all[0].Name == "run.start", "all");
        var tail = store.GetRunEvents(s.Id, run.Id, afterSeq: 0);
        Check(tail.Count == 1 && tail[0].Seq == 1, "after filter");
        return Task.CompletedTask;
    }
}
