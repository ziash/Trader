using Trader.AuditBot;
using Trader.Core.MarketData;

namespace Trader.AuditBot.Tests;

public sealed class ProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-probe-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private static SymbolInfo Symbol(string name, int digits, int pipPosition, long lotUnits, long minVolume, long stepVolume) =>
        new(1, name, name, name[..3], name[3..], digits, pipPosition, lotUnits, true, ProtoOATradingMode.Enabled,
            TradingSchedule.AlwaysOpen, minVolume, stepVolume);

    [Fact]
    public void Windows_follow_local_market_time_through_the_clock_changes()
    {
        var schedule = new ProbeSchedule([]);
        DateTime[] Day(int y, int m, int d) => schedule.WindowsOn(new DateOnly(y, m, d)).OrderBy(w => w.StartUtc).Select(w => w.StartUtc).ToArray();

        // 19-Oct: London BST, New York EDT → QuietHour 02:00, LondonOpen 07:00, NewYorkOpen 12:00 UTC
        Assert.Equal([Utc(2026, 10, 19, 2, 0), Utc(2026, 10, 19, 7, 0), Utc(2026, 10, 19, 12, 0)], Day(2026, 10, 19));
        // 26-Oct: London GMT (changed 25-Oct), New York still EDT
        Assert.Equal([Utc(2026, 10, 26, 3, 0), Utc(2026, 10, 26, 8, 0), Utc(2026, 10, 26, 12, 0)], Day(2026, 10, 26));
        // 2-Nov: both on winter time
        Assert.Equal([Utc(2026, 11, 2, 3, 0), Utc(2026, 11, 2, 8, 0), Utc(2026, 11, 2, 13, 0)], Day(2026, 11, 2));
        // Weekend: none
        Assert.Empty(Day(2026, 10, 24));
    }

    [Fact]
    public void Next_skips_the_weekend_and_names_the_window()
    {
        var schedule = new ProbeSchedule([]);
        var next = schedule.Next(Utc(2026, 10, 23, 20, 0)); // Friday evening
        Assert.Equal(new ProbeWindow("QuietHour", Utc(2026, 10, 26, 3, 0)), next);

        Assert.Equal("LondonOpen", schedule.Next(Utc(2026, 10, 19, 2, 0))!.Name); // strictly after the QuietHour start
    }

    [Fact]
    public async Task Wait_for_a_window_rereads_the_wall_clock_every_step()
    {
        var now = Utc(2026, 10, 26, 2, 58);
        var steps = new List<TimeSpan>();
        Task Delay(TimeSpan span, CancellationToken _) { steps.Add(span); now += span; return Task.CompletedTask; }

        await ProbeRunner.DelayUntilAsync(Utc(2026, 10, 26, 3, 0), () => now, Delay, CancellationToken.None);

        Assert.Equal(Utc(2026, 10, 26, 3, 0), now);
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.Equal(ProbeRunner.WaitStep, s));
    }

    [Fact]
    public async Task Wait_ends_one_step_after_waking_when_the_computer_slept_past_the_window()
    {
        // 24–26 Oct 2026: the bot waited from Friday evening for Monday's QuietHour; the laptop slept 65 h during the wait.
        var now = Utc(2026, 10, 23, 19, 58);
        var steps = 0;
        Task Delay(TimeSpan span, CancellationToken _)
        {
            now += ++steps == 1000 ? span + TimeSpan.FromHours(65) : span; // asleep during one step, 8 h 20 min in
            return Task.CompletedTask;
        }

        await ProbeRunner.DelayUntilAsync(Utc(2026, 10, 26, 3, 0), () => now, Delay, CancellationToken.None);

        Assert.Equal(1000, steps); // no further waiting after waking
        Assert.True(now > Utc(2026, 10, 26, 3, 0));
    }

    [Fact]
    public async Task Wait_for_a_past_window_returns_at_once()
    {
        var calls = 0;
        await ProbeRunner.DelayUntilAsync(Utc(2026, 10, 26, 3, 0), () => Utc(2026, 10, 26, 21, 17),
            (_, _) => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void News_release_gives_three_windows_around_08_30_New_York()
    {
        var schedule = new ProbeSchedule([new NewsRelease(new DateTime(2026, 11, 6, 8, 30, 0), "NFP")]);
        var news = schedule.WindowsOn(new DateOnly(2026, 11, 6)).Where(w => w.Name.StartsWith("NewsRelease")).ToList();

        // 6-Nov is winter time in New York: 08:30 EST = 13:30 UTC
        Assert.Equal(
            [
                new ProbeWindow("NewsRelease-2m", Utc(2026, 11, 6, 13, 28), "NFP"),
                new ProbeWindow("NewsRelease+5s", Utc(2026, 11, 6, 13, 30, 5), "NFP"),
                new ProbeWindow("NewsRelease+5m", Utc(2026, 11, 6, 13, 35), "NFP"),
            ], news);
        Assert.Equal("NewYorkOpen", schedule.Next(Utc(2026, 11, 6, 12, 0))!.Name); // 13:00 UTC comes first
    }

    [Fact]
    public void News_file_loads_and_reports_bad_times()
    {
        Assert.Empty(ProbeSchedule.LoadNews(Path.Combine(_dir, "missing.json")));

        var path = Path.Combine(_dir, "news-releases.json");
        File.WriteAllText(path, """{ "releases": [ { "time": "2026-11-10 08:30", "event": "CPI" }, { "time": "2026-11-06 08:30", "event": "NFP" } ] }""");
        Assert.Equal(["NFP", "CPI"], ProbeSchedule.LoadNews(path).Select(n => n.Event)); // sorted by time

        File.WriteAllText(path, """{ "releases": [ { "time": "6 Nov 2026", "event": "NFP" } ] }""");
        Assert.Contains("2026-11-06 08:30", Assert.Throws<AuditConfigException>(() => ProbeSchedule.LoadNews(path)).Message);
    }

    [Fact]
    public void Repo_news_file_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "news-releases.json");
        var news = ProbeSchedule.LoadNews(path);
        Assert.Contains(news, n => n.Event == "NFP" && n.NewYorkTime == new DateTime(2026, 11, 6, 8, 30, 0));
        Assert.Contains(news, n => n.Event == "CPI" && n.NewYorkTime == new DateTime(2026, 11, 10, 8, 30, 0));
    }

    [Theory]
    [InlineData(ProtoOATradeSide.Buy, 1.10010, 1.10012, -0.00002)]  // bought higher than seen: worse
    [InlineData(ProtoOATradeSide.Buy, 1.10010, 1.10008, 0.00002)]   // bought lower: better
    [InlineData(ProtoOATradeSide.Sell, 1.10000, 1.09998, -0.00002)] // sold lower: worse
    [InlineData(ProtoOATradeSide.Sell, 1.10000, 1.10003, 0.00003)]  // sold higher: better
    public void Slippage_is_negative_when_worse_for_us(ProtoOATradeSide side, double requested, double filled, double expected) =>
        Assert.Equal(expected, ProbeRunner.Slippage(side, requested, filled), 9);

    [Fact]
    public void Backup_stop_is_50_pips_for_forex_and_2_percent_otherwise()
    {
        var eurusd = Symbol("EURUSD", 5, 4, 100_000, 100_000, 100_000);
        var audjpy = Symbol("AUDJPY", 3, 2, 100_000, 100_000, 100_000);
        var btcusd = Symbol("BTCUSD", 2, 2, 1, 1, 1);

        Assert.Equal(500, ProbeRunner.Backstop(eurusd, 1.16));          // 0.0050 × 100,000
        Assert.Equal(50_000, ProbeRunner.Backstop(audjpy, 97.0));       // 0.50 × 100,000
        Assert.Equal(162_000_000, ProbeRunner.Backstop(btcusd, 81_000)); // 1,620 × 100,000 (2 %)
    }

    [Fact]
    public void Probe_volume_is_0_01_lot_in_api_units_within_broker_limits()
    {
        var eurusd = Symbol("EURUSD", 5, 4, 100_000, 100_000, 100_000);
        var btcusd = Symbol("BTCUSD", 2, 2, 1, 1, 1);

        Assert.Equal(100_000, eurusd.VolumeForLots(ProbeRunner.Lots)); // 1,000 units
        Assert.Equal(1, btcusd.VolumeForLots(ProbeRunner.Lots));       // 0.01 coin
        Assert.Equal(0.01, eurusd.LotsForVolume(100_000), 9);
        Assert.Equal(100_000, eurusd.VolumeForLots(0.001));            // never below the broker's minimum
        Assert.Equal(1_000_000, eurusd.VolumeForLots(0.105));          // rounded down to the step
    }
}
