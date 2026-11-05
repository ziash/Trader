using System.Text.Json;
using Trader.Core.Config;

namespace Trader.DashboardExport;

/// <summary>The accounts the dashboard shows: those in scout.json and tm.json (dashboard.md §3.1).</summary>
public static class DashAccounts
{
    public static IReadOnlyList<DashAccount> Load(string configDir)
    {
        var scout = Read(Path.Combine(configDir, "scout.json"));
        var tm = Read(Path.Combine(configDir, "tm.json"));
        return scout.Union(tm)
            .Select(a => new DashAccount(a.Broker, a.AccountId, scout.Contains(a), tm.Contains(a)))
            .OrderBy(a => a.Broker, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<(string Broker, long AccountId)> Read(string file)
    {
        var found = new HashSet<(string, long)>();
        if (!File.Exists(file))
            return found;
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("accounts", out var list) || list.ValueKind != JsonValueKind.Array)
            return found;
        foreach (var a in list.EnumerateArray())
        {
            if (a.TryGetProperty("broker", out var b) && b.GetString() is { } broker && System.Text.RegularExpressions.Regex.IsMatch(broker, "^[A-Za-z0-9]{1,32}$")
                && a.TryGetProperty("accountId", out var id) && id.TryGetInt64(out var accountId) && accountId > 0)
                found.Add((broker, accountId));
        }
        return found;
    }
}

/// <summary>
/// The values no uploaded file may contain (dashboard.md §6.2): everything secret in secrets.env and the dashboard's
/// machine secret. Held in memory only; never logged or printed.
/// </summary>
public static class SecretValues
{
    public static string M2mFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "trader", "dashboard-m2m.env");

    /// <summary>Shorter values would match ordinary text; real tokens and secrets are far longer.</summary>
    public const int MinLength = 8;

    public static IReadOnlyList<string> Load(string secretsEnv, string m2mEnv)
    {
        var s = SecretsFile.Load(secretsEnv);
        var values = new List<string> { s.ClientId, s.ClientSecret, s.AccessToken, s.RefreshToken };
        if (File.Exists(m2mEnv))
        {
            foreach (var line in File.ReadLines(m2mEnv))
            {
                var eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().EndsWith("SECRET", StringComparison.Ordinal))
                    values.Add(line[(eq + 1)..].Trim().Trim('"', '\''));
            }
        }
        return values.Where(v => v.Length >= MinLength).Distinct().ToList();
    }
}
