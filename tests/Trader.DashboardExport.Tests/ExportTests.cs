using System.Text.Json;
using Trader.DashboardExport;

namespace Trader.DashboardExport.Tests;

/// <summary>What the upload collects and what it never does (dashboard.md §3.1, §6).</summary>
public sealed class ExportTests : IDisposable
{
    private const string Secret = "refresh-token-value-1234567890";
    private static readonly DateOnly Today = new(2026, 11, 5);
    private static readonly DashAccount A = new("brokerA", 49123893, Scout: true, Tm: true);

    private readonly string _dir = Directory.CreateTempSubdirectory("trader-dash-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Home => Path.Combine(_dir, "tradebot");
    private string OutDir => Path.Combine(_dir, "outbox");
    private Outbox NewOutbox() => new(Home, OutDir, [Secret]);

    private string Put(string relative, string content = "time_utc,level,event,detail\n")
    {
        var path = Path.Combine(Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private bool Staged(string relative) => File.Exists(Path.Combine(OutDir, relative));

    [Fact]
    public void Stages_only_allowlisted_files_in_the_server_layout()
    {
        const string scout = "logs/brokerA/49123893/Scout-6d48a6/2026-11-05";
        const string tm = "logs/brokerA/49123893/TM-0d4d6c/2026-11-05";
        Put($"{scout}/orders.csv");
        Put($"{scout}/setups.csv");
        Put($"{scout}/events.csv");
        Put($"{scout}/charts/AUDJPY-H1-202611042200-M-1035821352.svg", "<svg/>");
        Put($"{scout}/memory.json", "{}");                           // not on the list
        Put($"{tm}/actions.csv");
        Put($"{tm}/events.csv");
        Put($"{tm}/state.json", "{}");                               // not on the list
        Put("logs/brokerA/49123893/AuditBot-0abef1/2026-11-05/events.csv"); // the Audit Bot's raw logs stay home
        Put("logs/rewind/9000001/TM-78e76c/2026-11-05/actions.csv"); // not a dashboard account
        Put("reports/daily-compare-2026-11-04.csv");
        Put("reports/summary-2026-10-19_2026-11-03.csv");
        Put("reports/notes.txt");
        Put("config/scout.json", "{}");
        Put("run/tm/state.json", "{}");
        Put("secrets.env", "X=1");

        var r = NewOutbox().Stage([A], 14, Today);

        string[] expected =
        [
            $"{scout}/orders.csv", $"{scout}/setups.csv", $"{scout}/events.csv", $"{scout}/charts/AUDJPY-H1-202611042200-M-1035821352.svg",
            $"{tm}/actions.csv", $"{tm}/events.csv",
            "reports/daily-compare-2026-11-04.csv", "reports/summary-2026-10-19_2026-11-03.csv",
        ];
        var all = Directory.EnumerateFiles(OutDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(OutDir, f)).Order().ToList();
        Assert.Equal(expected.Order().ToList(), all);
        Assert.Equal(8, r.Copied);
        Assert.Empty(r.Refused);
    }

    [Fact]
    public void Only_the_last_days_are_staged_and_unchanged_files_are_not_copied_again()
    {
        Put("logs/brokerA/49123893/Scout-a/2026-10-20/orders.csv");
        var today = Put("logs/brokerA/49123893/Scout-a/2026-11-05/orders.csv");
        var outbox = NewOutbox();

        Assert.Equal(1, outbox.Stage([A], 14, Today).Copied);
        Assert.False(Staged("logs/brokerA/49123893/Scout-a/2026-10-20/orders.csv"));
        Assert.Equal(0, outbox.Stage([A], 14, Today).Copied);

        File.AppendAllText(today, "2026-11-05 10:00:00,x\n");
        Assert.Equal(1, outbox.Stage([A], 14, Today).Copied);
        Assert.Equal(File.ReadAllText(today), File.ReadAllText(Path.Combine(OutDir, "logs/brokerA/49123893/Scout-a/2026-11-05/orders.csv")));
        Assert.Equal(File.GetLastWriteTimeUtc(today), File.GetLastWriteTimeUtc(Path.Combine(OutDir, "logs/brokerA/49123893/Scout-a/2026-11-05/orders.csv")));
    }

    [Fact]
    public void A_file_holding_a_secret_is_refused_and_any_older_copy_removed()
    {
        const string rel = "logs/brokerA/49123893/Scout-a/2026-11-05/events.csv";
        var path = Put(rel);
        var outbox = NewOutbox();
        outbox.Stage([A], 14, Today);
        Assert.True(Staged(rel));

        File.AppendAllText(path, $"2026-11-05 10:00:00,Error,Oops,token {Secret}\n");
        var r = outbox.Stage([A], 14, Today);

        Assert.Equal(rel, Assert.Single(r.Refused).RelativePath);
        Assert.DoesNotContain(Secret, r.Refused[0].Reason);
        Assert.False(Staged(rel));
    }

    [Fact]
    public void Google_secrets_and_private_keys_are_refused_even_when_not_in_the_secrets_files()
    {
        Put("logs/brokerA/49123893/Scout-a/2026-11-05/events.csv", "client GOCSPX" + "-abcdefghijklmnop\n");
        Put("logs/brokerA/49123893/Scout-a/2026-11-05/orders.csv", "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        Assert.Equal(2, NewOutbox().Stage([A], 14, Today).Refused.Count);
    }

    [Fact]
    public void Links_are_never_followed()
    {
        Put("config/secret-copy.txt", "x");
        var dir = Path.Combine(Home, "logs/brokerA/49123893/Scout-a/2026-11-05");
        Directory.CreateDirectory(dir);
        File.CreateSymbolicLink(Path.Combine(dir, "orders.csv"), Path.Combine(Home, "config/secret-copy.txt"));
        Assert.Equal(0, NewOutbox().Stage([A], 14, Today).Allowed);
    }

    [Theory]
    [InlineData("status.json", true)]
    [InlineData("logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/X-H1-1-M-2.svg", true)]
    [InlineData("logs/brokerA/49123893/Scout-6d48a6/2026-11-05/charts/../../../../../../config/scout.json", false)]
    [InlineData("logs/brokerA/49123893/TM-0d4d6c/2026-11-05/orders.csv", false)]
    [InlineData("config/scout.json", false)]
    [InlineData("dashboard/outbox/status.json", false)]
    public void The_allowlist(string path, bool allowed) => Assert.Equal(allowed, Outbox.IsAllowed(path));

    [Fact]
    public void Status_json_goes_through_the_same_check()
    {
        var outbox = NewOutbox();
        Assert.True(outbox.Write("status.json", "{}"));
        Assert.False(outbox.Write("status.json", $"{{\"x\":\"{Secret}\"}}"));
        Assert.False(Staged("status.json"));
        Assert.Throws<InvalidOperationException>(() => outbox.Write("notes.txt", "x"));
    }

    [Fact]
    public void Accounts_come_from_scout_and_tm_config()
    {
        Put("config/scout.json", """{ /* c */ "accounts": [ { "broker": "brokerA", "accountId": 49123893, "charts": [] } ] }""");
        Put("config/tm.json", """{ "accounts": [ { "broker": "brokerA", "accountId": 49123893 }, { "broker": "brokerB", "accountId": 49124666 }, { "broker": "../x", "accountId": 1 } ] }""");
        var accounts = DashAccounts.Load(Path.Combine(Home, "config"));
        Assert.Equal([new DashAccount("brokerA", 49123893, true, true), new DashAccount("brokerB", 49124666, false, true)], accounts);
    }

    private sealed class FakeSystemd(Dictionary<string, Dictionary<string, string>> units) : ISystemd
    {
        public Dictionary<string, Dictionary<string, string>> Units => units;

        public IReadOnlyDictionary<string, string> Show(string unit, params string[] properties) =>
            units.TryGetValue(unit, out var v) ? v : new Dictionary<string, string> { ["LoadState"] = "not-found" };

        public string? RunningFolder(int pid) => pid == 42 ? "/home/x/tradebot/bin/Scout-6711a92" : null;
    }

    [Fact]
    public void Build_names_from_versioned_folders_links_and_VERSION_files()
    {
        Put("bin/Scout-6711a92/VERSION", "6711a92\n");
        Directory.CreateSymbolicLink(Path.Combine(Home, "bin/Scout"), "Scout-6711a92");
        Put("bin/TM/VERSION", "41ca4d0\n");
        Assert.Equal("Scout-6711a92", StatusReport.BuildName(Path.Combine(Home, "bin/Scout"), "Scout"));
        Assert.Equal("TM-41ca4d0", StatusReport.BuildName(Path.Combine(Home, "bin/TM"), "TM"));
        Assert.Null(StatusReport.BuildName(Path.Combine(Home, "bin/AuditBot"), "AuditBot"));
    }

    [Fact]
    public void Status_reports_bots_timers_and_todays_warnings()
    {
        Put("logs/brokerA/49123893/Scout-a/2026-11-05/events.csv",
            "time_utc,level,event,detail\n2026-11-05 09:00:00,Information,ScoutSetup,x\n2026-11-05 09:10:00,Warning,ScoutRefused,\"order refused, margin\"\n2026-11-05 09:20:00,Error,ScoutChartError,y\n");
        Put("logs/brokerA/49123893/Scout-a/2026-11-04/events.csv", "time_utc,level,event,detail\n2026-11-04 09:10:00,Warning,Old,z\n");
        Put("config/audit.brokerA.json", """{ "broker": "brokerA", "brokerName": "IC Markets" }""");
        var systemd = new FakeSystemd(new()
        {
            ["trader-scout.service"] = new() { ["LoadState"] = "loaded", ["ActiveState"] = "active", ["ActiveEnterTimestamp"] = "@1793816641", ["MainPID"] = "42" },
            ["trader-tm.service"] = new() { ["LoadState"] = "loaded", ["ActiveState"] = "failed", ["InactiveEnterTimestamp"] = "@1793816641", ["MainPID"] = "0" },
            ["trader-dashboard.timer"] = new() { ["LoadState"] = "loaded", ["ActiveState"] = "active", ["LastTriggerUSec"] = "@1793900000", ["NextElapseUSecRealtime"] = "@1793901720" },
            ["trader-dashboard.service"] = new() { ["ActiveState"] = "inactive", ["Result"] = "exit-code" },
        });

        var s = new StatusReport(Home, systemd).Build([A], new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc), new UploadInfo("2026-11-05 18:00:00", 3.5, 12, null));

        var scout = s.Units.Single(u => u.Name == "Scout");
        Assert.Equal(("running", "Scout-6711a92", "2026-11-04 18:24:01", 1, 1), (scout.State, scout.Build, scout.SinceUtc, scout.ErrorsToday, scout.WarningsToday));
        Assert.Equal("failed", s.Units.Single(u => u.Name == "TM").State);
        Assert.DoesNotContain(s.Units, u => u.Name == "Audit Bot" || u.Name == "Control sync" || u.Name == "Audit report");
        var upload = s.Units.Single(u => u.Name == "Data upload");
        Assert.Equal(("waiting", "ok", "2026-11-05 18:00:00", "2026-11-05 18:02:00"), (upload.State, upload.LastResult, upload.LastRunUtc, upload.NextRunUtc));

        // Without an upload (a dry run): the timer's last run and its result as systemd has them.
        var dry = new StatusReport(Home, systemd).Build([A], new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc), new UploadInfo(null, null, null, null))
            .Units.Single(u => u.Name == "Data upload");
        Assert.Equal(("waiting", "failed", "2026-11-05 17:33:20"), (dry.State, dry.LastResult, dry.LastRunUtc));

        // Written during an upload (the service is running): that upload is the last run, and it worked.
        systemd.Units["trader-dashboard.service"] = new() { ["ActiveState"] = "activating", ["Result"] = "success" };
        var during = new StatusReport(Home, systemd).Build([A], new DateTime(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc), new UploadInfo("2026-11-05 18:00:01", 3.5, 12, null))
            .Units.Single(u => u.Name == "Data upload");
        Assert.Equal(("waiting", "ok", "2026-11-05 18:00:01"), (during.State, during.LastResult, during.LastRunUtc));
        Assert.Equal("2026-11-05 18:02:00", s.Upload.NextUtc);
        Assert.Equal(["error", "warning"], s.Events.Select(e => e.Level));
        Assert.Equal("ScoutRefused: order refused, margin", s.Events[1].Message);
        Assert.Equal("IC Markets demo", s.Accounts.Single().Name);

        var json = JsonSerializer.Serialize(s, StatusReport.Json);
        Assert.Contains("\"generated_utc\": \"2026-11-05 18:00:00\"", json);
        Assert.Contains("\"errors_today\": 1", json);
        Assert.Contains("\"controls\": []", json);
    }
}
