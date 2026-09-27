using AiSharp.Config;
using AiSharp.Core;
using AiSharp.Hosting;
using AiSharp.Providers.OpencodeGo;

namespace AiSharp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            return PrintHelp();

        var cmd = args[0].ToLowerInvariant();
        try
        {
            return cmd switch
            {
                "serve" => await ServeAsync(args[1..]),
                "chat" => await ChatAsync(args[1..]),
                "sessions" => Sessions(args[1..]),
                "runs" => Runs(args[1..]),
                "tools" => Tools(args[1..]),
                "models" => await ModelsAsync(),
                "doctor" => await DoctorAsync(),
                "selftest" => await SelfTest.RunAsync(),
                "key" => KeyCmd(args[1..]),
                _ => PrintHelp(),
            };
        }
        catch (ProviderException pex)
        {
            Console.Error.WriteLine($"provider error [{pex.Code}]: {pex.Message} (retryable={pex.Retryable})");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            ai-sharp — Phase 1: OpenCode Go provider (muse-spark-1.3-contributor)
              Docs: https://opencode.ai/docs/go/ (endpoint) + https://opencode.ai/docs/zen/

            Usage:
              ai-sharp serve [--port 5111]                 Start HTTP+SSE host (loopback only by default)
              ai-sharp chat -m "prompt" [--session ID] [--model ID] [--no-stream]
              ai-sharp sessions create [--title T] | list | show ID
              ai-sharp runs events SESSION RUN [--after N]  Replay persisted run events
              ai-sharp tools list                          List registered local tools
              ai-sharp models                                List provider models (GET /models; protocol annotated)
              ai-sharp doctor                                Validate config, credential, store, provider reachability
              ai-sharp selftest                              Offline engine tests (tools, cancel, concurrency, recovery)
              ai-sharp key save [-]                          Save key from hidden prompt, stdin (-) or KEY arg (arg stays in shell history)
              ai-sharp key status                            Show credential location (never prints key)

            Config (non-secret): OPENCODE_GO_BASE_URL, OPENCODE_GO_MODEL, AI_SHARP_DATA_DIR, AI_SHARP_PORT
            Secret: OPENCODE_GO_API_KEY (env) or ~/.config/ai-sharp/opencode-go.key (chmod 600)
            Data: ./data/sessions/*.json (or $AI_SHARP_DATA_DIR) — survives restart
            """);
        return 0;
    }

    private static (AiSharpConfig Config, FileSessionStore Store) BootConfig()
    {
        var dataDir = Environment.GetEnvironmentVariable("AI_SHARP_DATA_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        // Fall back to user-local dir when repo dir is not writable.
        try { Directory.CreateDirectory(Path.Combine(dataDir, "sessions")); }
        catch
        {
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "ai-sharp");
            Directory.CreateDirectory(Path.Combine(dataDir, "sessions"));
        }
        var cfg = AiSharpConfig.Default(dataDir);
        var problems = cfg.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException("Invalid configuration:\n- " + string.Join("\n- ", problems));
        var store = new FileSessionStore(dataDir);
        var reconciled = store.ReconcileInterruptedRuns();
        if (reconciled > 0)
            Console.Error.WriteLine($"reconciled {reconciled} interrupted run(s) to failed (previous process ended mid-run).");
        return (cfg, store);
    }

    private static OpencodeGoProvider BootProvider(AiSharpConfig cfg)
    {
        var apiKey = CredentialStore.ResolveApiKey();
        var opts = new OpencodeGoOptions(cfg.BaseUrl, cfg.Model, apiKey, cfg.UserAgent);
        return OpencodeGoProvider.Create(opts);
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        var (cfg, store) = BootConfig();
        var port = cfg.DefaultPort;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) port = p;
        var provider = BootProvider(cfg);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await HttpHost.RunAsync(store, provider, cfg, port, cts.Token);
        return 0;
    }

    private static string? TakeValue(string[] args, ref int i, string option)
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"Missing value for {option}.");
            return null;
        }
        i++;
        return args[i];
    }

    private static async Task<int> ChatAsync(string[] args)
    {
        string? message = null, sessionId = null, model = null;
        var noStream = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-m":
                case "--message": message = TakeValue(args, ref i, args[i]); if (message is null) return 1; break;
                case "--session": sessionId = TakeValue(args, ref i, args[i]); if (sessionId is null) return 1; break;
                case "--model": model = TakeValue(args, ref i, args[i]); if (model is null) return 1; break;
                case "--no-stream": noStream = true; break;
                default: Console.Error.WriteLine($"Unknown option: {args[i]}"); return 1;
            }
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            Console.Error.WriteLine("Missing prompt. Use: ai-sharp chat -m \"hello\" [--session ID]");
            return 1;
        }
        var (cfg, store) = BootConfig();
        var provider = BootProvider(cfg);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var s = store.Create("cli chat");
            sessionId = s.Id;
            Console.WriteLine($"session: {sessionId}");
        }
        else
        {
            // Validate early for a clear error.
            store.Get(sessionId);
        }

        var executor = new TurnExecutor(store, provider, model ?? cfg.Model);
        Console.WriteLine($"model: {model ?? cfg.Model}  provider: opencode-go");
        Task OnEvent(string name, int seq, string dataJson)
        {
            if (name is "tool.call.request" or "tool.result")
                Console.WriteLine($"\n[{name}] {Truncate(dataJson, 220)}");
            return Task.CompletedTask;
        }
        if (noStream)
        {
            var result = await executor.ExecuteAsync(sessionId, message, model, null, CancellationToken.None, null, OnEvent);
            Console.WriteLine(result.Text);
            PrintUsage(result.Usage);
            PrintTools(result.ToolCalls);
        }
        else
        {
            var result = await executor.ExecuteAsync(sessionId, message, model, delta =>
            {
                Console.Write(delta);
                return Task.CompletedTask;
            }, CancellationToken.None, null, OnEvent);
            Console.WriteLine();
            PrintUsage(result.Usage);
            PrintTools(result.ToolCalls);
        }
        Console.WriteLine($"session: {sessionId} (transcript persisted under {cfg.DataDir})");
        return 0;
    }

    private static int Sessions(string[] args)
    {
        var (_, store) = BootConfig();
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "create":
            {
                string? title = null;
                for (var i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--title")
                    {
                        title = TakeValue(args, ref i, "--title");
                        if (title is null) return 1;
                    }
                    else { Console.Error.WriteLine($"Unknown option: {args[i]}"); return 1; }
                }
                var s = store.Create(title);
                Console.WriteLine(s.Id);
                return 0;
            }
            case "list":
            {
                foreach (var s in store.List())
                    Console.WriteLine($"{s.Id}\t{s.CreatedAt:u}\t{s.Title ?? ""}\tmsgs={s.Messages.Count}\truns={s.Runs.Count}");
                return 0;
            }
            case "show":
            {
                if (args.Length < 2) { Console.Error.WriteLine("Usage: ai-sharp sessions show ID"); return 1; }
                var s = store.Get(args[1]);
                Console.WriteLine($"session {s.Id} created {s.CreatedAt:u} title={s.Title ?? "-"}");
                foreach (var m in s.Messages)
                    Console.WriteLine($"[{m.CreatedAt:HH:mm:ss} {m.Role}] {Truncate(m.Text, 500)}");
                foreach (var r in s.Runs)
                    Console.WriteLine($"run {r.Id} {r.Status} usage={r.Usage?.TotalTokens ?? 0}");
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: ai-sharp sessions create|list|show ID");
                return 1;
        }
    }

    private static int Tools(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (sub != "list") { Console.Error.WriteLine("Usage: ai-sharp tools list"); return 1; }
        var reg = ToolRegistry.WithBuiltins();
        foreach (var d in reg.Definitions())
            Console.WriteLine($"{d.Name}\t{d.Description}\n  schema: {d.ParametersJson}");
        Console.WriteLine($"policy: registered safe tools run automatically; unknown tools are denied.");
        return 0;
    }

    private static int Runs(string[] args)
    {
        if (args.Length >= 1 && args[0].ToLowerInvariant() == "events")
        {
            if (args.Length < 3) { Console.Error.WriteLine("Usage: ai-sharp runs events SESSION RUN [--after N]"); return 1; }
            var (_, store) = BootConfig();
            var after = -1;
            for (var i = 3; i < args.Length; i++)
                if (args[i] == "--after")
                {
                    var v = TakeValue(args, ref i, "--after");
                    if (v is null || !int.TryParse(v, out after)) { Console.Error.WriteLine("Invalid --after value."); return 1; }
                }
            foreach (var e in store.GetRunEvents(args[1], args[2], after))
                Console.WriteLine($"[{e.Seq} {e.Name}] {Truncate(e.DataJson, 400)}");
            return 0;
        }
        Console.Error.WriteLine("Usage: ai-sharp runs events SESSION RUN [--after N]");
        return 1;
    }

    private static async Task<int> DoctorAsync()
    {
        var failures = 0;
        void Line(bool ok, string msg)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {msg}");
            if (!ok) failures++;
        }
        var dataDir = Environment.GetEnvironmentVariable("AI_SHARP_DATA_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
        var cfg = AiSharpConfig.Default(dataDir);
        var problems = cfg.Validate();
        Line(problems.Count == 0, problems.Count == 0
            ? $"config valid (model={cfg.Model} base={cfg.BaseUrl})"
            : "config: " + string.Join("; ", problems));
        var proto = OpencodeGoProvider.ProtocolFor(cfg.Model);
        Line(proto == "responses", proto == "responses"
            ? $"model protocol: responses"
            : $"model '{cfg.Model}' uses {proto}; Phase 1 adapter speaks responses only");
        try { CredentialStore.ResolveApiKey(); Line(true, "credential present (never printed)"); }
        catch (Exception ex) { Line(false, "credential: " + ex.Message); }
        try
        {
            var probe = new FileSessionStore(dataDir);
            var n = probe.ReconcileInterruptedRuns();
            Line(true, $"store writable at {dataDir} (reconciled {n} interrupted)");
        }
        catch (Exception ex) { Line(false, "store: " + ex.Message); }
        var reg = ToolRegistry.WithBuiltins();
        Line(true, $"tools: {string.Join(", ", reg.Definitions().Select(d => d.Name))} (auto-approved safe built-ins)");
        if (failures == 0)
        {
            try
            {
                var provider = BootProvider(cfg);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var models = await provider.ListModelsAsync(cts.Token);
                Line(models.Contains(cfg.Model), $"provider reachable: {models.Count} models; default {(models.Contains(cfg.Model) ? "listed" : "NOT listed")}");
            }
            catch (Exception ex) { Line(false, "provider: " + ex.Message); }
        }
        else
        {
            Console.WriteLine("skip  provider reachability (fix failures above first)");
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> ModelsAsync()
    {
        var (cfg, _) = BootConfig();
        var provider = BootProvider(cfg);
        var models = await provider.ListModelsAsync();
        foreach (var m in models.OrderBy(x => x))
        {
            var proto = OpencodeGoProvider.ProtocolFor(m);
            var tag = proto == "responses" ? "responses" : $"{proto} (needs Phase 4 adapter)";
            Console.WriteLine($"{(m == cfg.Model ? "* " : "  ")}{m}  [{tag}]");
        }
        return 0;
    }

    private static int KeyCmd(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: ai-sharp key save [-] | status"); return 1; }
        if (args[0] == "status")
        {
            Console.WriteLine($"configDir: {CredentialStore.ConfigDir}");
            Console.WriteLine($"rawKeyFile: {CredentialStore.RawKeyPath} exists={File.Exists(CredentialStore.RawKeyPath)}");
            Console.WriteLine($"env {CredentialStore.EnvPrimary}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CredentialStore.EnvPrimary)) ? "missing" : "set (redacted)")}");
            return 0;
        }
        if (args[0] == "save")
        {
            string? key = args.Length >= 2 ? args[1] : null;
            if (key == "-") key = Console.In.ReadToEnd().Trim();
            else if (key is null) key = ReadHidden("Enter OpenCode Go API key: ");
            else Console.Error.WriteLine("warning: passing the key as an argument stores it in shell history; prefer `ai-sharp key save` (prompt) or `-` (stdin).");
            if (string.IsNullOrWhiteSpace(key)) { Console.Error.WriteLine("Empty key; not saved."); return 1; }
            CredentialStore.SaveRawKey(key);
            Console.WriteLine($"saved to {CredentialStore.RawKeyPath} (chmod 600)");
            return 0;
        }
        Console.Error.WriteLine("Usage: ai-sharp key save [-] | status");
        return 1;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) return Console.In.ReadLine()?.Trim() ?? "";
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString().Trim();
    }

    private static void PrintUsage(ProviderUsage? u)
    {
        if (u is null) return;
        Console.WriteLine($"[usage in={u.InputTokens} out={u.OutputTokens} total={u.TotalTokens} cached={u.CachedInputTokens?.ToString() ?? "-"} reasoning={u.ReasoningOutputTokens?.ToString() ?? "-"}]");
    }

    private static void PrintTools(IReadOnlyList<StoredToolCall> tools)
    {
        foreach (var t in tools)
            Console.WriteLine($"[tool {t.Name} {t.Status}] {Truncate(t.ResultText ?? t.Error ?? "", 200)}");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
