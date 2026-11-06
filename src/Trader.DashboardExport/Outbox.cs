using System.Text;
using System.Text.RegularExpressions;

namespace Trader.DashboardExport;

/// <summary>One demo account the dashboard shows: the folder names under ~/tradebot/logs, and which bots list it.</summary>
public sealed record DashAccount(string Broker, long AccountId, bool Scout = false, bool Tm = false)
{
    public string Id => $"{Broker}-{AccountId}";
}

/// <summary>A file the secrets check refused to stage; <see cref="Reason"/> never contains the secret itself.</summary>
public sealed record Refusal(string RelativePath, string Reason);

public sealed record StageResult(int Allowed, int Copied, IReadOnlyList<Refusal> Refused);

/// <summary>
/// The upload's staging folder (dashboard.md §3.1, §6): ~/tradebot/dashboard/outbox holds a copy of every allowlisted
/// file, in the layout of the server's data folder. Only this folder is sent. A file is copied when its size or time
/// changed, keeps its time (so rsync sends only what changed), and is checked for secrets before it lands; a file that
/// holds one is not staged at all. Nothing under ~/tradebot/logs or ~/tradebot/reports is changed.
/// </summary>
public sealed class Outbox(string tradebotHome, string outboxDir, IReadOnlyCollection<string> secrets)
{
    private const string Day = @"\d{4}-\d{2}-\d{2}";
    private const string Account = @"[A-Za-z0-9]{1,32}/\d{1,12}";
    private const string Instance = @"[A-Za-z0-9]{1,32}";

    /// <summary>
    /// Every file that may leave the laptop, as paths relative to the outbox (= relative to ~/tradebot, except status.json
    /// which the exporter writes). scripts/dashboard-push.sh checks the outbox against the same list before sending.
    /// </summary>
    public static readonly Regex[] Allowlist =
    [
        new($@"^logs/{Account}/Scout-{Instance}/{Day}/(orders|setups|events)\.csv$"),
        new($@"^logs/{Account}/Scout-{Instance}/{Day}/charts/[A-Za-z0-9._-]{{1,120}}\.svg$"),
        new($@"^logs/{Account}/TM-{Instance}/{Day}/(actions|events)\.csv$"),
        new($@"^logs/{Account}/Dashboard/{Day}/(account|deals|conditions)\.csv$"),
        new($@"^logs/{Account}/Dashboard/{Day}/charts/trade-[PO]\d{{1,20}}\.svg$"),
        new($@"^reports/(daily-compare-{Day}|summary-{Day}_{Day})\.csv$"),
        new(@"^status\.json$"),
        new(@"^snapshot-[A-Za-z0-9]{1,32}-\d{1,12}\.json$"),
    ];

    /// <summary>Text that must never be sent even if it is not in the secrets files (Google client secrets, private keys).</summary>
    private static readonly Regex SecretShapes = new(@"GOCSPX-[A-Za-z0-9_-]{10}|BEGIN [A-Z ]*PRIVATE KEY");

    public static bool IsAllowed(string relativePath) => Allowlist.Any(r => r.IsMatch(relativePath));

    public string Dir => outboxDir;

    /// <summary>Copies the allowlisted files of the last <paramref name="days"/> UTC days (and all reports) into the outbox.</summary>
    public StageResult Stage(IEnumerable<DashAccount> accounts, int days, DateOnly today)
    {
        var first = today.AddDays(1 - days);
        var candidates = new List<string>();
        foreach (var account in accounts)
        {
            var accountDir = Path.Combine(tradebotHome, "logs", account.Broker, account.AccountId.ToString());
            if (!Directory.Exists(accountDir))
                continue;
            foreach (var instance in Directory.EnumerateDirectories(accountDir))
            {
                var name = Path.GetFileName(instance);
                if (!name.StartsWith("Scout-", StringComparison.Ordinal) && !name.StartsWith("TM-", StringComparison.Ordinal) && name != "Dashboard")
                    continue;
                foreach (var dayDir in Directory.EnumerateDirectories(instance))
                {
                    if (!DateOnly.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", out var d) || d < first)
                        continue;
                    candidates.AddRange(Directory.EnumerateFiles(dayDir));
                    var charts = Path.Combine(dayDir, "charts");
                    if (Directory.Exists(charts))
                        candidates.AddRange(Directory.EnumerateFiles(charts));
                }
            }
        }
        var reports = Path.Combine(tradebotHome, "reports");
        if (Directory.Exists(reports))
            candidates.AddRange(Directory.EnumerateFiles(reports));

        int allowed = 0, copied = 0;
        var refused = new List<Refusal>();
        foreach (var source in candidates)
        {
            var rel = Path.GetRelativePath(tradebotHome, source).Replace('\\', '/');
            if (!IsAllowed(rel) || new FileInfo(source).LinkTarget is not null)
                continue; // not on the allowlist (or a link that could point anywhere): stays on the laptop
            allowed++;
            switch (CopyIn(source, rel))
            {
                case true: copied++; break;
                case null: refused.Add(new Refusal(rel, "contains a secret value")); break;
            }
        }
        return new StageResult(allowed, copied, refused);
    }

    /// <summary>Writes a file the exporter made itself (status.json, the snapshots) into the outbox, through the same secrets check.</summary>
    public bool Write(string relativePath, string content)
    {
        if (!IsAllowed(relativePath))
            throw new InvalidOperationException($"{relativePath} is not on the upload allowlist");
        var dest = Path.Combine(outboxDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (ContainsSecret(Encoding.UTF8.GetBytes(content)))
        {
            File.Delete(dest);
            return false;
        }
        var tmp = dest + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, dest, overwrite: true);
        return true;
    }

    /// <summary>true = copied, false = unchanged, null = refused (holds a secret; any older copy is removed too).</summary>
    private bool? CopyIn(string source, string rel)
    {
        var dest = Path.Combine(outboxDir, rel);
        var src = new FileInfo(source);
        var old = new FileInfo(dest);
        if (old.Exists && old.Length == src.Length && old.LastWriteTimeUtc == src.LastWriteTimeUtc)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var bytes = File.ReadAllBytes(source); // one read: what is checked is exactly what is staged
        if (ContainsSecret(bytes))
        {
            File.Delete(dest);
            return null;
        }
        var tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.SetLastWriteTimeUtc(tmp, src.LastWriteTimeUtc);
        File.Move(tmp, dest, overwrite: true);
        return true;
    }

    internal bool ContainsSecret(byte[] content)
    {
        var text = Encoding.UTF8.GetString(content);
        return secrets.Any(s => text.Contains(s, StringComparison.Ordinal)) || SecretShapes.IsMatch(text);
    }
}
