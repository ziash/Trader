using Serilog;
using Serilog.Core;
using Serilog.Events;
using Trader.AuditBot;
using Trader.Core.Csv;
using Trader.Core.MarketData;

namespace Trader.AuditBot.Tests;

public sealed class AuditBotTests : IDisposable
{
    private const long Ctid = 49123893;
    private static readonly DateTime Start = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc); // Monday, main hours
    private static readonly DateTime End = Start.AddMinutes(2);
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-audit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Tick T(int second, double bid, double ask) => new(Start.AddSeconds(second), bid, ask);

    private static SymbolInfo Symbol(long id, string name, string quote, int digits, int pipPosition, long lotUnits, TradingSchedule? schedule = null) =>
        new(id, name, name, name[..3], quote, digits, pipPosition, lotUnits, true, ProtoOATradingMode.Enabled, schedule ?? TradingSchedule.AlwaysOpen);

    [Fact]
    public void Median_and_p95_use_standard_definitions()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(5.5, SpreadStats.Median(sorted));
        Assert.Equal(10, SpreadStats.Percentile(sorted, 0.95)); // nearest rank: ceil(9.5) = 10th value
        Assert.Equal(3, SpreadStats.Median([1, 3, 9]));
        Assert.Equal(7, SpreadStats.Percentile([7], 0.95));
    }

    [Fact]
    public void Summary_has_stats_last_prices_and_longest_gap_including_edges()
    {
        var s = SpreadStats.Summarize([T(10, 1.10000, 1.10010), T(70, 1.10005, 1.10025), T(75, 1.10001, 1.10031)], Start, End);

        Assert.Equal(3, s.Ticks);
        Assert.Equal((1.10001, 1.10031), (s.Bid, s.Ask));
        Assert.Equal(0.00010, s.Min!.Value, 9);
        Assert.Equal(0.00020, s.Median!.Value, 9);
        Assert.Equal(0.00030, s.Max!.Value, 9);
        Assert.Equal(60, s.MaxGapSeconds, 3); // 10 s → 70 s; the edge gaps are 10 s and 45 s
    }

    [Fact]
    public void Summary_without_ticks_has_gap_equal_to_interval()
    {
        var s = SpreadStats.Summarize([], Start, End);
        Assert.Equal(0, s.Ticks);
        Assert.Null(s.Avg);
        Assert.Equal(120, s.MaxGapSeconds);
    }

    [Fact]
    public void Recorder_writes_pips_and_usd_per_lot_and_handles_closed_markets()
    {
        var feed = new PriceFeed();
        var recorder = new SpreadRecorder(new LoggerConfiguration().CreateLogger(), feed);
        var config = new AuditConfig("brokerA", "IC Markets", Ctid, ["EURUSD", "AUDJPY", "BTCUSD", "GBPJPY"]);
        var file = Path.Combine(_dir, "spreads.csv");
        var writer = new DailyCsvWriter(_ => file, SpreadRecorder.Header);
        recorder.AddAccount(config, writer);

        var closed = new TradingSchedule([(0, 1)], TimeZoneInfo.Utc);     // open only during Sunday's first second
        var eurusd = Symbol(1, "EURUSD", "USD", 5, 4, 100_000);
        var audjpy = Symbol(11, "AUDJPY", "JPY", 3, 2, 100_000);
        var btcusd = Symbol(10026, "BTCUSD", "USD", 2, 2, 1);
        var gbpjpy = Symbol(7, "GBPJPY", "JPY", 3, 2, 100_000, closed);  // market closed, no ticks → no row
        var usdjpy = Symbol(4, "USDJPY", "JPY", 3, 2, 100_000);
        recorder.Configure(Ctid, [eurusd, audjpy, btcusd, gbpjpy],
            new Dictionary<string, ConversionSymbol> { ["JPY"] = new(usdjpy, Invert: true) });

        // USDJPY at 150.000 / 150.010 → mid 150.005, used to turn JPY into USD.
        feed.Handle(new ProtoOASpotEvent { CtidTraderAccountId = Ctid, SymbolId = 4, Bid = 150_00000, Ask = 150_01000 });

        void Q(long id, int sec, double bid, double ask) => recorder.OnQuote(new Quote(Ctid, id, bid, ask, Start.AddSeconds(sec)));
        Q(1, 5, 1.16000, 1.16002);    // EURUSD 0.2 pip
        Q(1, 30, 1.16001, 1.16005);   // 0.4 pip
        Q(11, 5, 97.000, 97.012);     // AUDJPY 1.2 pips
        Q(1, 125, 1.2, 1.3);          // next interval: must not count here
        // BTCUSD: no ticks while open → row with 0 ticks (feed gap)

        Assert.Equal(3, recorder.FlushInterval(Start, End));
        writer.Dispose();

        var lines = File.ReadAllLines(file);
        Assert.Equal(string.Join(",", SpreadRecorder.Header), lines[0]);
        Assert.Equal(4, lines.Length);
        // rows are sorted by symbol name: AUDJPY, BTCUSD, EURUSD
        Assert.Equal("2026-10-19 09:02:00,120,main,AUDJPY,1,97.000,97.012,1.20,1.20,1.20,1.20,1.20,8.00,8.00,115.0", lines[1]);
        Assert.Equal("2026-10-19 09:02:00,120,main,BTCUSD,0,,,,,,,,,,120.0", lines[2]);
        Assert.Equal("2026-10-19 09:02:00,120,main,EURUSD,2,1.16001,1.16005,0.20,0.30,0.30,0.40,0.40,3.00,4.00,90.0", lines[3]);

        // The tick at 125 s belongs to the next interval.
        Assert.Equal(3, recorder.FlushInterval(End, End.AddMinutes(2)));
    }

    [Fact]
    public void Recorder_ignores_stale_snapshot_quotes_so_closed_markets_get_no_row()
    {
        var recorder = new SpreadRecorder(new LoggerConfiguration().CreateLogger(), new PriceFeed());
        var file = Path.Combine(_dir, "stale.csv");
        var writer = new DailyCsvWriter(_ => file, SpreadRecorder.Header);
        recorder.AddAccount(new AuditConfig("brokerA", "IC Markets", Ctid, ["EURUSD", "BTCUSD"]), writer);
        var closed = new TradingSchedule([(0, 1)], TimeZoneInfo.Utc);
        recorder.Configure(Ctid, [Symbol(1, "EURUSD", "USD", 5, 4, 100_000, closed), Symbol(10026, "BTCUSD", "USD", 2, 2, 1)],
            new Dictionary<string, ConversionSymbol>());

        // On (re)subscribe the server re-sends the last known price, stamped with its original time (e.g. Friday close).
        recorder.OnQuote(new Quote(Ctid, 1, 1.16, 1.17, Start.AddDays(-2)));
        recorder.OnQuote(new Quote(Ctid, 10026, 81000, 81005, Start.AddSeconds(-30)));
        recorder.OnQuote(new Quote(Ctid, 10026, 81001, 81006, Start.AddSeconds(10)));

        Assert.Equal(1, recorder.FlushInterval(Start, End)); // EURUSD: closed, no fresh ticks → no row
        writer.Dispose();
        var row = File.ReadAllLines(file)[1];
        Assert.Contains(",BTCUSD,1,", row);                  // only the fresh BTCUSD tick counts
    }

    [Fact]
    public void Config_loads_with_comments_and_maps_broker_names()
    {
        File.WriteAllText(Path.Combine(_dir, "audit.brokerA.json"), """
            {
              // comment
              "broker": "brokerA",
              "brokerName": "IC Markets",
              "accountId": 49123893,
              "symbols": ["EURUSD", "AVAXUSD"],
              "symbolNames": { "AVAXUSD": "AVXUSD" },
            }
            """);

        var c = Assert.Single(AuditConfig.LoadAll(_dir));
        Assert.Equal(49123893, c.AccountId);
        Assert.Equal("AVXUSD", c.BrokerSymbolNames["AVAXUSD"]);
        Assert.Equal("EURUSD", c.BrokerSymbolNames["EURUSD"]);
    }

    [Theory]
    [InlineData("""{ "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbol": ["EURUSD"] }""", "symbol")]         // typo in field name
    [InlineData("""{ "broker": "broker A", "brokerName": "X", "accountId": 1, "symbols": ["EURUSD"] }""", "broker")]
    [InlineData("""{ "broker": "brokerA", "brokerName": "X", "accountId": 0, "symbols": ["EURUSD"] }""", "accountId")]
    [InlineData("""{ "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbols": [] }""", "symbols")]
    [InlineData("""{ "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbols": ["EURUSD", "eurusd"] }""", "duplicate")]
    public void Config_problems_are_reported_clearly(string json, string expectedInMessage)
    {
        File.WriteAllText(Path.Combine(_dir, "audit.bad.json"), json);
        var ex = Assert.Throws<AuditConfigException>(() => AuditConfig.LoadAll(_dir));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void Config_rejects_same_account_twice_and_empty_folder()
    {
        Assert.Throws<AuditConfigException>(() => AuditConfig.LoadAll(_dir));

        const string a = """{ "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbols": ["EURUSD"] }""";
        const string b = """{ "broker": "brokerB", "brokerName": "Y", "accountId": 1, "symbols": ["EURUSD"] }""";
        File.WriteAllText(Path.Combine(_dir, "audit.a.json"), a);
        File.WriteAllText(Path.Combine(_dir, "audit.b.json"), b);
        Assert.Contains("more than one", Assert.Throws<AuditConfigException>(() => AuditConfig.LoadAll(_dir)).Message);
    }

    [Fact]
    public void Repo_config_files_are_valid()
    {
        var repoConfig = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config");
        var configs = AuditConfig.LoadAll(repoConfig);
        Assert.Equal([49123893L, 49124666L], configs.Select(c => c.AccountId));
        Assert.All(configs, c => Assert.Equal(["AUDJPY", "EURGBP", "EURUSD", "GBPJPY", "BTCUSD", "ETHUSD"], c.Symbols));
    }
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent e) => Events.Add(e);
        public IEnumerable<string> Named(string name) =>
            Events.Where(e => e.Properties.TryGetValue("Event", out var v) && v.ToString() == $"\"{name}\"").Select(Render);

        private static string Render(LogEvent e) // as the journal shows it ({Message:lj}: strings without quotes)
        {
            var w = new StringWriter();
            new Serilog.Formatting.Display.MessageTemplateTextFormatter("{Message:lj}").Format(e, w);
            return w.ToString();
        }
    }

    [Fact]
    public void Alert_limits_default_per_asset_class_with_symbol_overrides()
    {
        var eurusd = Symbol(1, "EURUSD", "USD", 5, 4, 100_000);
        var btcusd = Symbol(2, "BTCUSD", "USD", 2, 2, 1);
        var ethusd = Symbol(3, "ETHUSD", "USD", 2, 2, 1);
        var limits = new AlertLimits(Symbols: new Dictionary<string, SymbolLimits> { ["ETHUSD"] = new(SpreadUsd: 10) });

        Assert.Equal(new Limit(true, 2, 1), limits.For(eurusd));
        Assert.Equal(new Limit(false, 50, 10), limits.For(btcusd));
        Assert.Equal(new Limit(false, 10, 10), limits.For(ethusd)); // only the spread overridden
        Assert.Equal("-0.30 pips", limits.For(eurusd).ShowSigned(-0.3));
        Assert.Equal("+$14.64/lot", limits.For(btcusd).ShowSigned(14.64));
        Assert.Equal("-$6.91/lot", limits.For(btcusd).ShowSigned(-6.91));
    }

    [Fact]
    public void Config_alerts_load_and_are_optional()
    {
        File.WriteAllText(Path.Combine(_dir, "audit.a.json"), """
            { "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbols": ["EURUSD", "ETHUSD"],
              "alerts": { "forexSlipPips": 0.5, "symbols": { "ETHUSD": { "slipUsd": 3 } } } }
            """);
        File.WriteAllText(Path.Combine(_dir, "audit.b.json"), """{ "broker": "brokerB", "brokerName": "Y", "accountId": 2, "symbols": ["EURUSD"] }""");

        var configs = AuditConfig.LoadAll(_dir);
        Assert.Equal(new AlertLimits(ForexSlipPips: 0.5).ForexSlipPips, configs[0].Limits.ForexSlipPips);
        Assert.Equal(2, configs[0].Limits.ForexSpreadPips);             // left out → default
        Assert.Equal(3, configs[0].Limits.Symbols!["ETHUSD"].SlipUsd);
        Assert.Same(AlertLimits.Default, configs[1].Limits);
    }

    [Theory]
    [InlineData("""{ "forexSpreadPips": -1 }""", "above 0")]
    [InlineData("""{ "symbols": { "XRPUSD": { "spreadUsd": 1 } } }""", "XRPUSD")]
    [InlineData("""{ "forexSpread": 2 }""", "forexSpread")] // typo in field name
    public void Config_alert_problems_are_reported(string alerts, string expectedInMessage)
    {
        File.WriteAllText(Path.Combine(_dir, "audit.bad.json"),
            $$"""{ "broker": "brokerA", "brokerName": "X", "accountId": 1, "symbols": ["EURUSD"], "alerts": {{alerts}} }""");
        Assert.Contains(expectedInMessage, Assert.Throws<AuditConfigException>(() => AuditConfig.LoadAll(_dir)).Message);
    }

    [Fact]
    public void Wide_spread_raises_one_warning_and_one_clear_with_duration_and_peak()
    {
        var sink = new ListSink();
        var recorder = new SpreadRecorder(new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger(), new PriceFeed());
        var writer = new DailyCsvWriter(_ => Path.Combine(_dir, "wide.csv"), SpreadRecorder.Header);
        recorder.AddAccount(new AuditConfig("brokerA", "IC Markets", Ctid, ["EURUSD"]), writer);
        recorder.Configure(Ctid, [Symbol(1, "EURUSD", "USD", 5, 4, 100_000)], new Dictionary<string, ConversionSymbol>());

        void Interval(int i, double spreadPips)
        {
            var from = Start.AddMinutes(2 * i);
            recorder.OnQuote(new Quote(Ctid, 1, 1.16, 1.16 + spreadPips * 0.0001, from.AddSeconds(10)));
            recorder.FlushInterval(from, from.AddMinutes(2));
        }

        Interval(0, 0.2);  // normal
        Interval(1, 3.0);  // wide → warning
        Interval(2, 5.0);  // still wide → nothing, peak 5
        Interval(3, 0.3);  // normal → clear
        writer.Dispose();

        Assert.Equal(["brokerA EURUSD: spread wide, avg 3.00 pips in 09:02-09:04Z, max 3.00 pips (limit 2.00 pips, main)"], sink.Named("SpreadWide"));
        Assert.Equal(["brokerA EURUSD: spread back to normal (avg 0.30 pips) after 4 min wide since 09:02Z; peak avg 5.00 pips"], sink.Named("SpreadNormal"));
        Assert.All(sink.Events.Where(e => e.Level == LogEventLevel.Warning), e => Assert.Equal(Ctid.ToString(), e.Properties["Account"].ToString()));
    }

    [Fact]
    public void Slippage_summary_is_per_asset_class_and_names_the_worst_fill()
    {
        var fx = new Limit(true, 2, 1);
        var crypto = new Limit(false, 50, 10);
        ProbeRunner.ProbeResult R(string symbol, string action, string side, Limit limit, double slip) =>
            new("filled", 500, symbol, action, side, limit, slip);

        var lines = ProbeRunner.SlippageSummary(
        [
            R("EURUSD", "open", "buy", fx, 0.1), R("AUDJPY", "close", "buy", fx, -0.3), R("GBPJPY", "open", "sell", fx, -1.2),
            R("BTCUSD", "open", "sell", crypto, 14.64), R("BTCUSD", "open", "buy", crypto, -6.91),
        ]).ToList();

        Assert.Equal(
        [
            "forex: slippage avg -0.47 pips, worst -1.20 pips (GBPJPY open sell); 1 of 3 fills over the alert limit",
            "crypto: slippage avg +$3.87/lot, worst -$6.91/lot (BTCUSD open buy); 0 of 2 fills over the alert limit",
        ], lines);
        Assert.Equal(["forex: slippage avg +0.10 pips, no fill worse than requested; 0 of 1 fills over the alert limit"],
            ProbeRunner.SlippageSummary([R("EURUSD", "open", "buy", fx, 0.1)]));
    }
}
