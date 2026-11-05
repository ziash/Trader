using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trader.Core.Csv;

namespace Trader.DashboardExport;

// status.json (dashboard.md §2.6; schema Status in web/dashboard/openapi.yaml). Times are UTC "yyyy-MM-dd HH:mm:ss".

public sealed record StatusFile(
    string GeneratedUtc,
    IReadOnlyList<AccountInfo> Accounts,
    IReadOnlyList<UnitStatus> Units,
    IReadOnlyList<EventLine> Events,
    UploadInfo Upload,
    SyncInfo? Sync,
    IReadOnlyList<object> Controls);

public sealed record AccountInfo(string Id, string Broker, long Ctid, string Name, bool Scout, bool Tm);

public sealed record UnitStatus(
    string Name,
    string Kind,
    string State,
    string? SinceUtc = null,
    string? Build = null,
    string? LastLogUtc = null,
    int? ErrorsToday = null,
    int? WarningsToday = null,
    string? Schedule = null,
    string? LastRunUtc = null,
    string? LastResult = null,
    string? NextRunUtc = null);

public sealed record EventLine(string TimeUtc, string Bot, string Level, string Message);

public sealed record UploadInfo(string? LastUtc, double? Seconds, int? Files, string? NextUtc);

public sealed record SyncInfo(string? LastUtc, bool AckReceived, string? NextUtc);

/// <summary>A bot's systemd service and the prefix of its instance folders under ~/tradebot/logs/&lt;broker&gt;/&lt;ctid&gt;/.</summary>
public sealed record BotUnit(string Name, string Service, string LogPrefix);

/// <summary>A timer, its service and how often it runs, in words.</summary>
public sealed record TimerUnit(string Name, string Unit, string Schedule);

public sealed class StatusReport(string tradebotHome, ISystemd systemd)
{
    public static readonly BotUnit[] Bots =
    [
        new("Scout", "trader-scout", "Scout"),
        new("TM", "trader-tm", "TM"),
        new("Audit Bot", "trader-auditbot", "AuditBot"),
    ];

    public static readonly TimerUnit[] Timers =
    [
        new("Audit report", "trader-auditreport", "daily at 00:15 UTC"),
        new("Data upload", "trader-dashboard", "every 30 min, at :02 and :32"),
        new("Control sync", "trader-dashboard-sync", "every minute"),
    ];

    public const int MaxEvents = 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Utc(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    private static string? UtcOrNull(DateTime? t) => t is null ? null : Utc(t);

    public StatusFile Build(IReadOnlyList<DashAccount> accounts, DateTime nowUtc, UploadInfo upload)
    {
        var today = DateOnly.FromDateTime(nowUtc);
        var events = new List<EventLine>();
        var units = new List<UnitStatus>();
        foreach (var bot in Bots)
        {
            var u = systemd.Show($"{bot.Service}.service", "LoadState", "ActiveState", "ActiveEnterTimestamp", "InactiveEnterTimestamp", "MainPID");
            var files = EventFiles(accounts, bot.LogPrefix).ToList();
            var todays = files.Where(f => f.Day == today).Select(f => f.Path).ToList();
            var botEvents = todays.SelectMany(f => ReadEvents(f, bot.Name)).ToList();
            events.AddRange(botEvents);
            if (u.GetValueOrDefault("LoadState") != "loaded" && files.Count == 0)
                continue; // never installed here and never ran: not shown
            var active = u.GetValueOrDefault("ActiveState") ?? "";
            var state = active switch
            {
                "active" or "reloading" => "running",
                "activating" => "waiting",
                "failed" => "failed",
                _ => "stopped",
            };
            var since = Systemctl.Time(u.GetValueOrDefault(state == "running" ? "ActiveEnterTimestamp" : "InactiveEnterTimestamp"));
            var pid = int.TryParse(u.GetValueOrDefault("MainPID"), out var p) ? p : 0;
            var build = BuildName((pid > 0 ? systemd.RunningFolder(pid) : null) ?? Path.Combine(tradebotHome, "bin", bot.LogPrefix), bot.LogPrefix);
            var lastLog = files.Count == 0 ? (DateTime?)null : files.Max(f => File.GetLastWriteTimeUtc(f.Path));
            units.Add(new UnitStatus(bot.Name, "bot", state, UtcOrNull(since), build, UtcOrNull(lastLog),
                botEvents.Count(e => e.Level == "error"), botEvents.Count(e => e.Level == "warning")));
        }

        DateTime? nextUpload = null;
        foreach (var timer in Timers)
        {
            var t = systemd.Show($"{timer.Unit}.timer", "LoadState", "ActiveState", "LastTriggerUSec", "NextElapseUSecRealtime");
            if (t.GetValueOrDefault("LoadState") != "loaded")
                continue; // not installed yet (the control sync arrives in build step 6)
            var s = systemd.Show($"{timer.Unit}.service", "ActiveState", "Result");
            var next = Systemctl.Time(t.GetValueOrDefault("NextElapseUSecRealtime"));
            var last = Systemctl.Time(t.GetValueOrDefault("LastTriggerUSec"));
            if (timer.Unit == "trader-dashboard")
                nextUpload = next;
            // status.json is written by the upload itself: that run has just sent the data, so it counts as done.
            var self = timer.Unit == "trader-dashboard" && upload.LastUtc is not null;
            var state = s.GetValueOrDefault("ActiveState") is "active" or "activating" && !self ? "running"
                : t.GetValueOrDefault("ActiveState") == "active" ? "waiting"
                : "stopped";
            var result = self ? "ok" : last is null ? null : s.GetValueOrDefault("Result") == "success" ? "ok" : "failed";
            units.Add(new UnitStatus(timer.Name, "timer", state, Schedule: timer.Schedule, LastRunUtc: self ? upload.LastUtc : UtcOrNull(last),
                LastResult: result, NextRunUtc: UtcOrNull(next)));
        }

        return new StatusFile(
            Utc(nowUtc),
            accounts.Select(a => new AccountInfo(a.Id, a.Broker, a.AccountId, AccountName(a), a.Scout, a.Tm)).ToList(),
            units,
            events.OrderByDescending(e => e.TimeUtc, StringComparer.Ordinal).Take(MaxEvents).ToList(),
            upload with { NextUtc = upload.NextUtc ?? UtcOrNull(nextUpload) },
            null,
            []);
    }

    /// <summary>"IC Markets demo", from the Audit Bot's broker settings (audit.&lt;broker&gt;.json "brokerName").</summary>
    public string AccountName(DashAccount a)
    {
        var file = Path.Combine(tradebotHome, "config", $"audit.{a.Broker}.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("brokerName", out var n) && n.GetString() is { Length: > 0 } name)
                return $"{name} demo";
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        return $"{a.Broker} {a.AccountId}";
    }

    /// <summary>
    /// "Scout-6711a92": the versioned folder's name (scripts/deploy-lib.sh), or for a plain folder from before versioned
    /// folders (or the ~/tradebot/bin/&lt;Bot&gt; link of a stopped bot) the bot's name and its VERSION file.
    /// </summary>
    internal static string? BuildName(string folder, string bot)
    {
        var dir = new DirectoryInfo(folder);
        if (dir.LinkTarget is { } target)
            dir = new DirectoryInfo(Path.Combine(dir.Parent!.FullName, target));
        if (dir.Name.StartsWith(bot + "-", StringComparison.Ordinal))
            return dir.Name;
        var version = Path.Combine(dir.FullName, "VERSION");
        return File.Exists(version) && File.ReadAllText(version).Trim() is { Length: > 0 and <= 40 } v ? $"{bot}-{v}" : null;
    }

    private IEnumerable<(string Path, DateOnly Day)> EventFiles(IEnumerable<DashAccount> accounts, string prefix)
    {
        foreach (var a in accounts)
        {
            var dir = Path.Combine(tradebotHome, "logs", a.Broker, a.AccountId.ToString());
            if (!Directory.Exists(dir))
                continue;
            foreach (var instance in Directory.EnumerateDirectories(dir, prefix + "-*"))
            foreach (var dayDir in Directory.EnumerateDirectories(instance))
            {
                var file = Path.Combine(dayDir, "events.csv");
                if (DateOnly.TryParseExact(Path.GetFileName(dayDir), "yyyy-MM-dd", out var day) && File.Exists(file))
                    yield return (file, day);
            }
        }
    }

    private static IEnumerable<EventLine> ReadEvents(string file, string bot)
    {
        CsvTable table;
        try
        {
            table = CsvTable.Read(file);
        }
        catch (IOException)
        {
            yield break;
        }
        if (!table.Has("time_utc") || !table.Has("level"))
            yield break;
        foreach (var row in table.Rows)
        {
            var level = row["level"] switch
            {
                "Warning" => "warning",
                "Error" or "Fatal" => "error",
                _ => null,
            };
            if (level is null)
                continue;
            var ev = table.Has("event") ? row["event"] : "";
            var detail = table.Has("detail") ? row["detail"] : "";
            var message = ev.Length > 0 && detail.Length > 0 ? $"{ev}: {detail}" : ev + detail;
            yield return new EventLine(row["time_utc"], bot, level, message.Length > 300 ? message[..299] + "…" : message);
        }
    }
}
