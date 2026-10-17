using System.Globalization;
using System.Text;

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
    /// <summary>Written by the bot after each token refresh (UTC, ISO 8601). Absent until the first refresh.</summary>
    public const string ExpiresKey = "CTRADER_ACCESS_TOKEN_EXPIRES_UTC";

    private const string ExpiresFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

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

            values[KeyOf(line[..eq])] = Unquote(line[(eq + 1)..].Trim());
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
            ParseEnvironment(values.GetValueOrDefault(EnvKey)),
            ParseExpiry(values.GetValueOrDefault(ExpiresKey)));
    }

    internal static DateTime? ParseExpiry(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTime.TryParseExact(value.Trim(), ExpiresFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t
                : throw new SecretsException($"{ExpiresKey} must look like 2026-11-15T08:00:00Z");

    /// <summary>
    /// Replaces the token lines (access, refresh, expiry) and keeps every other line as it is.
    /// Atomic: writes a temp file (mode 600) in the same folder, flushes it to disk, then renames it
    /// over the original, so a crash leaves either the old file or the new one, never half of each.
    /// </summary>
    public static void WriteTokens(string path, TraderSecrets secrets)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccessTokenKey] = secrets.AccessToken,
            [RefreshTokenKey] = secrets.RefreshToken,
        };
        if (secrets.AccessTokenExpiresUtc is { } expires)
            updates[ExpiresKey] = expires.ToUniversalTime().ToString(ExpiresFormat, CultureInfo.InvariantCulture);

        var lines = File.ReadAllLines(path).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            var eq = line.IndexOf('=');
            if (line.StartsWith('#') || eq <= 0)
                continue;

            var key = KeyOf(line[..eq]);
            if (updates.Remove(key, out var value))
                lines[i] = (line.StartsWith("export ", StringComparison.Ordinal) ? "export " : "") + $"{key}={value}";
        }
        lines.AddRange(updates.Select(u => $"{u.Key}={u.Value}"));

        var tmp = $"{path}.tmp-{System.Environment.ProcessId}";
        File.Delete(tmp); // stale leftover from a crash, if any
        try
        {
            using (var fs = new FileStream(tmp, PrivateFileOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                foreach (var l in lines)
                    w.Write(l + "\n");
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>
    /// Exclusive lock (secrets.env.lock) so that only one bot instance refreshes the tokens at a time.
    /// Waits up to <paramref name="timeout"/>, then throws <see cref="TimeoutException"/>.
    /// </summary>
    public static async Task<IDisposable> LockAsync(string path, TimeSpan timeout, CancellationToken ct = default)
    {
        var lockPath = path + ".lock";
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, PrivateFileOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, ct);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Could not lock {lockPath} within {timeout.TotalSeconds:0} s", ex);
            }
        }
    }

    private static FileStreamOptions PrivateFileOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var o = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows())
            o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return o;
    }

    private static string KeyOf(string left)
    {
        var key = left.Trim();
        return key.StartsWith("export ", StringComparison.Ordinal) ? key["export ".Length..].Trim() : key;
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
