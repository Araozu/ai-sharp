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
                "models" => await ModelsAsync(),
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
              ai-sharp models                                List provider models (GET /models)
              ai-sharp key save <API_KEY> | status           Store/inspect credential location (never prints key)

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
        return (cfg, new FileSessionStore(dataDir));
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

    private static async Task<int> ChatAsync(string[] args)
    {
        string? message = null, sessionId = null, model = null;
        var noStream = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-m":
                case "--message": message = args[++i]; break;
                case "--session": sessionId = args[++i]; break;
                case "--model": model = args[++i]; break;
                case "--no-stream": noStream = true; break;
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
        if (noStream)
        {
            var result = await executor.ExecuteAsync(sessionId, message, model);
            Console.WriteLine(result.Text);
            PrintUsage(result.Usage);
        }
        else
        {
            var result = await executor.ExecuteAsync(sessionId, message, model, delta =>
            {
                Console.Write(delta);
                return Task.CompletedTask;
            });
            Console.WriteLine();
            PrintUsage(result.Usage);
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
                    if (args[i] == "--title" && i + 1 < args.Length) title = args[++i];
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

    private static async Task<int> ModelsAsync()
    {
        var (cfg, _) = BootConfig();
        var provider = BootProvider(cfg);
        var models = await provider.ListModelsAsync();
        foreach (var m in models.OrderBy(x => x))
            Console.WriteLine((m == cfg.Model ? "* " : "  ") + m);
        return 0;
    }

    private static int KeyCmd(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: ai-sharp key save <KEY> | status"); return 1; }
        if (args[0] == "status")
        {
            Console.WriteLine($"configDir: {CredentialStore.ConfigDir}");
            Console.WriteLine($"rawKeyFile: {CredentialStore.RawKeyPath} exists={File.Exists(CredentialStore.RawKeyPath)}");
            Console.WriteLine($"env {CredentialStore.EnvPrimary}: {(string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CredentialStore.EnvPrimary)) ? "missing" : "set (redacted)")}");
            return 0;
        }
        if (args[0] == "save" && args.Length >= 2)
        {
            CredentialStore.SaveRawKey(args[1]);
            Console.WriteLine($"saved to {CredentialStore.RawKeyPath} (chmod 600)");
            return 0;
        }
        Console.Error.WriteLine("Usage: ai-sharp key save <KEY> | status");
        return 1;
    }

    private static void PrintUsage(ProviderUsage? u)
    {
        if (u is null) return;
        Console.WriteLine($"[usage in={u.InputTokens} out={u.OutputTokens} total={u.TotalTokens} cached={u.CachedInputTokens?.ToString() ?? "-"} reasoning={u.ReasoningOutputTokens?.ToString() ?? "-"}]");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
