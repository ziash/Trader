namespace Trader.Core.Config;

/// <summary>Raised when the secrets file is missing, malformed or unsafe. Messages never contain secret values.</summary>
public sealed class SecretsException(string message) : Exception(message);

/// <summary>Reads the KEY=VALUE secrets file kept outside the repo (CLAUDE.md "Secrets").</summary>
public static class SecretsFile
{
    public const string ClientIdKey = "CTRADER_CLIENT_ID";
    public const string ClientSecretKey = "CTRADER_CLIENT_SECRET";
    public const string AccessTokenKey = "CTRADER_ACCESS_TOKEN";
    public const string RefreshTokenKey = "CTRADER_REFRESH_TOKEN";
    public const string EnvKey = "CTRADER_ENV";

    /// <summary>~/.config/trader/secrets.env, unless TRADER_SECRETS_FILE points elsewhere.</summary>
    public static string DefaultPath =>
        System.Environment.GetEnvironmentVariable("TRADER_SECRETS_FILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".config", "trader", "secrets.env");

    public static TraderSecrets Load(string path)
    {
        if (!File.Exists(path))
            throw new SecretsException($"Secrets file not found: {path}");

        CheckPermissions(path);
        return Parse(File.ReadAllLines(path));
    }

    internal static TraderSecrets Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new SecretsException($"Secrets file line {lineNo}: expected KEY=VALUE");

            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal))
                key = key["export ".Length..].Trim();
            values[key] = Unquote(line[(eq + 1)..].Trim());
        }

        string Required(string key) =>
            values.TryGetValue(key, out var v) && v.Length > 0
                ? v
                : throw new SecretsException($"{key} is not set in the secrets file");

        return new TraderSecrets(
            Required(ClientIdKey),
            Required(ClientSecretKey),
            Required(AccessTokenKey),
            Required(RefreshTokenKey),
            ParseEnvironment(values.GetValueOrDefault(EnvKey)));
    }

    /// <summary>Demo is the default. Live is refused until Phase 5 (CLAUDE.md "Demo only").</summary>
    internal static ApiEnvironment ParseEnvironment(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "demo" => ApiEnvironment.Demo,
            "live" => throw new SecretsException(
                $"{EnvKey}=live is refused: live trading is not enabled before Phase 5. Use {EnvKey}=demo."),
            _ => throw new SecretsException($"{EnvKey} must be 'demo'"),
        };

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'') ? v[1..^1] : v;

    private static void CheckPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        const UnixFileMode groupOrOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        if ((File.GetUnixFileMode(path) & groupOrOther) != 0)
            throw new SecretsException($"Secrets file {path} is readable by other users. Fix with: chmod 600 {path}");
    }
}
