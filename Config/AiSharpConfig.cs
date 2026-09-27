namespace AiSharp.Config;

/// <summary>
/// Non-secret configuration. Secrets are never stored here.
/// </summary>
public sealed record AiSharpConfig(
    string BaseUrl,
    string Model,
    string UserAgent,
    string DataDir,
    int DefaultPort)
{
    public static AiSharpConfig Default(string dataDir) => new(
        BaseUrl: Environment.GetEnvironmentVariable("OPENCODE_GO_BASE_URL")
            ?? "https://opencode.ai/zen/go/v1",
        Model: Environment.GetEnvironmentVariable("OPENCODE_GO_MODEL")
            ?? "muse-spark-1.3-contributor",
        UserAgent: Environment.GetEnvironmentVariable("AI_SHARP_USER_AGENT")
            ?? "ai-sharp/0.1",
        DataDir: dataDir,
        DefaultPort: int.TryParse(Environment.GetEnvironmentVariable("AI_SHARP_PORT"), out var p) ? p : 5111);
}

/// <summary>
/// Credential resolution. Order:
/// 1. OPENCODE_GO_API_KEY env
/// 2. OPENCODE_API_KEY env (compat)
/// 3. $HOME/.config/ai-sharp/opencode-go.key (raw token, chmod 600)
/// 4. $HOME/.config/ai-sharp/credentials.json {"opencodeGoApiKey":"..."}
/// Never logs the key.
/// </summary>
public static class CredentialStore
{
    public const string EnvPrimary = "OPENCODE_GO_API_KEY";
    public const string EnvFallback = "OPENCODE_API_KEY";

    public static string ConfigDir =>
        Environment.GetEnvironmentVariable("AI_SHARP_CONFIG_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "ai-sharp");

    public static string RawKeyPath => Path.Combine(ConfigDir, "opencode-go.key");
    public static string JsonCredentialsPath => Path.Combine(ConfigDir, "credentials.json");

    public static string ResolveApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvPrimary);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();

        var fallback = Environment.GetEnvironmentVariable(EnvFallback);
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();

        if (File.Exists(RawKeyPath))
        {
            var raw = File.ReadAllText(RawKeyPath).Trim();
            if (!string.IsNullOrWhiteSpace(raw)) return raw;
        }

        if (File.Exists(JsonCredentialsPath))
        {
            try
            {
                var json = File.ReadAllText(JsonCredentialsPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("opencodeGoApiKey", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
                if (doc.RootElement.TryGetProperty("OPENCODE_GO_API_KEY", out var v2) && v2.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v2.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }
            }
            catch
            {
                // fall through to actionable error
            }
        }

        throw new InvalidOperationException(
            $"Missing OpenCode Go API key. Set {EnvPrimary} env var, or save the key to {RawKeyPath} (chmod 600). " +
            $"Get a key at https://opencode.ai/auth (Go subscription) and use `ai-sharp models` to verify.");
    }

    public static void SaveRawKey(string apiKey)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API key is empty.", nameof(apiKey));
        Directory.CreateDirectory(ConfigDir);
        // Refuse to follow/replace a symlink: an attacker-writable link could redirect the secret.
        if (new FileInfo(RawKeyPath).LinkTarget is not null)
            throw new InvalidOperationException($"Refusing to write key: {RawKeyPath} is a symlink.");
        if (!OperatingSystem.IsWindows())
        {
            // Create with restrictive mode from the outset (no umask race).
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var fs = new FileStream(RawKeyPath, options))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(key + "\n");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            var mode = File.GetUnixFileMode(RawKeyPath);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException($"Could not secure {RawKeyPath} (mode {mode}); refusing to leave a readable key file.");
        }
        else
        {
            // Windows: user profile dir is private by default; fail loudly if we cannot write.
            File.WriteAllText(RawKeyPath, key + "\n");
        }
    }

    public static bool HasStoredKey()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvPrimary))) return true;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvFallback))) return true;
        if (File.Exists(RawKeyPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(RawKeyPath))) return true;
        return false;
    }

    /// <summary>Redacted form for logs/diagnostics</summary>
    public static string Redact(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Length < 12) return "***";
        return apiKey[..6] + "***" + apiKey[^4..];
    }
}
