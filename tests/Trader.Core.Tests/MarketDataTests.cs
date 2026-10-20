using Google.Protobuf;
using Serilog;
using Trader.Core.Csv;
using Trader.Core.Logging;
using Trader.Core.MarketData;

namespace Trader.Core.Tests;

public sealed class MarketDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-md-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static DateTime Utc(int y, int mo, int d, int h, int mi, int s = 0) => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    // Forex schedule as both brokers send it: Sun 17:01 – Fri 16:59 New York, with a 2-minute break at 17:00 each day.
    private static readonly TradingSchedule Forex = new(
        [(61260, 147540), (147660, 233940), (234060, 320340), (320460, 406740), (406860, 493140)],
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

    [Theory]
    // Week of 19-Oct-2026: London BST (UTC+1), New York EDT (UTC−4) → main 07:00–21:00 UTC
    [InlineData(2026, 10, 19, 6, 59, false)]
    [InlineData(2026, 10, 19, 7, 0, true)]
    [InlineData(2026, 10, 19, 20, 59, true)]
    [InlineData(2026, 10, 19, 21, 0, false)]
    // Week of 26-Oct-2026: London GMT (UK changed 25 Oct), New York still EDT → main 08:00–21:00 UTC
    [InlineData(2026, 10, 26, 7, 30, false)]
    [InlineData(2026, 10, 26, 8, 0, true)]
    [InlineData(2026, 10, 26, 21, 0, false)]
    // Week of 2-Nov-2026: London GMT, New York EST (US changed 1 Nov) → main 08:00–22:00 UTC
    [InlineData(2026, 11, 2, 21, 30, true)]
    [InlineData(2026, 11, 2, 22, 0, false)]
    // Weekend and Friday afternoon
    [InlineData(2026, 10, 24, 12, 0, false)]
    [InlineData(2026, 10, 23, 20, 0, true)]
    public void MainHours_follow_London_open_to_New_York_close(int y, int mo, int d, int h, int mi, bool main) =>
        Assert.Equal(main, MarketHours.IsMainHours(Utc(y, mo, d, h, mi)));

    [Theory]
    [InlineData(2026, 10, 30, 12)] // New York on summer time (EDT, UTC−4)
    [InlineData(2026, 11, 6, 13)]  // winter time (EST, UTC−5) from 1-Nov-2026
    public void News_release_time_converts_from_New_York(int y, int mo, int d, int utcHour) =>
        Assert.Equal(Utc(y, mo, d, utcHour, 30), new NewsRelease(new DateTime(y, mo, d, 8, 30, 0), "NFP").Utc);

    [Fact]
    public void News_calendar_loads_sorted_and_reports_bad_entries()
    {
        Assert.Empty(NewsCalendar.Load(Path.Combine(_dir, "missing.json")));

        var path = Path.Combine(_dir, NewsCalendar.FileName);
        File.WriteAllText(path, """{ "releases": [ { "time": "2026-11-10 08:30", "event": "CPI" }, { "time": "2026-11-06 08:30", "event": "NFP" } ] }""");
        Assert.Equal(["NFP", "CPI"], NewsCalendar.Load(path).Select(n => n.Event));

        File.WriteAllText(path, """{ "releases": [ { "time": "2026-11-06 08:30", "event": "" } ] }""");
        Assert.Throws<NewsCalendarException>(() => NewsCalendar.Load(path));
    }

    [Fact]
    public void NextBoundary_uses_2_min_in_main_hours_and_5_min_off_peak()
    {
        Assert.Equal(Utc(2026, 10, 19, 8, 2), MarketHours.NextBoundary(Utc(2026, 10, 19, 8, 1, 30)));
        Assert.Equal(Utc(2026, 10, 19, 8, 4), MarketHours.NextBoundary(Utc(2026, 10, 19, 8, 2)));
        Assert.Equal(Utc(2026, 10, 19, 7, 0), MarketHours.NextBoundary(Utc(2026, 10, 19, 6, 58)));   // off-peak → lands on main start
        Assert.Equal(Utc(2026, 10, 19, 21, 5), MarketHours.NextBoundary(Utc(2026, 10, 19, 21, 0)));  // off-peak after NY close
        Assert.Equal(Utc(2026, 10, 24, 12, 5), MarketHours.NextBoundary(Utc(2026, 10, 24, 12, 3)));  // Saturday
    }

    [Fact]
    public void Schedule_knows_daily_break_and_weekend()
    {
        Assert.True(Forex.IsOpen(Utc(2026, 10, 19, 20, 58)));    // Mon 16:58 New York
        Assert.False(Forex.IsOpen(Utc(2026, 10, 19, 20, 59, 30))); // 16:59:30 — daily break
        Assert.True(Forex.IsOpen(Utc(2026, 10, 19, 21, 1, 30))); // 17:01:30
        Assert.False(Forex.IsOpen(Utc(2026, 10, 24, 12, 0)));    // Saturday
        Assert.True(Forex.IsOpen(Utc(2026, 10, 25, 21, 2)));     // Sunday 17:02 New York: week opens

        Assert.True(Forex.IsOpenThroughout(Utc(2026, 10, 19, 20, 50), Utc(2026, 10, 19, 20, 55)));
        Assert.False(Forex.IsOpenThroughout(Utc(2026, 10, 19, 20, 58), Utc(2026, 10, 19, 21, 0)));
        Assert.True(TradingSchedule.AlwaysOpen.IsOpenThroughout(Utc(2026, 10, 24, 12, 0), Utc(2026, 10, 24, 12, 5)));
    }

    [Fact]
    public void PriceFeed_merges_partial_spot_events_and_uses_server_time()
    {
        var feed = new PriceFeed(() => Utc(2026, 1, 1, 0, 0));
        var quotes = new List<Quote>();
        feed.QuoteReceived += quotes.Add;

        feed.Handle(new ProtoOASpotEvent { CtidTraderAccountId = 1, SymbolId = 7, Bid = 115_00000 }); // ask unknown: no quote yet
        feed.Handle(new ProtoOASpotEvent { CtidTraderAccountId = 1, SymbolId = 7, Ask = 115_00500, Timestamp = 1_760_000_000_000 });
        feed.Handle(new ProtoOASpotEvent { CtidTraderAccountId = 1, SymbolId = 7, Bid = 115_00100 });   // only bid changed

        Assert.Equal(2, quotes.Count);
        Assert.Equal((115.0, 115.005), (quotes[0].Bid, quotes[0].Ask));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000).UtcDateTime, quotes[0].TimeUtc);
        Assert.Equal((115.001, 115.005), (quotes[1].Bid, quotes[1].Ask));
        Assert.Equal(Utc(2026, 1, 1, 0, 0), quotes[1].TimeUtc); // no server timestamp → receive time
        Assert.Equal(0.004, quotes[1].Spread, 9);

        feed.Reset();
        Assert.Null(feed.Last(1, 7));
    }

    [Fact]
    public void Catalog_finds_conversion_symbols_either_way_round()
    {
        Dictionary<long, string> assets = new() { [1] = "USD", [2] = "JPY", [3] = "GBP", [4] = "EUR" };
        var catalog = SymbolCatalog.Create(1,
        [
            new ProtoOALightSymbol { SymbolId = 4, SymbolName = "USDJPY", BaseAssetId = 1, QuoteAssetId = 2 },
            new ProtoOALightSymbol { SymbolId = 2, SymbolName = "GBPUSD", BaseAssetId = 3, QuoteAssetId = 1 },
        ], assets);

        Assert.Equal(new Conversion("JPY", "USD", "USDJPY", Invert: true), catalog.FindConversion("JPY", "USD"));
        Assert.Equal(new Conversion("GBP", "USD", "GBPUSD", Invert: false), catalog.FindConversion("GBP", "USD"));
        Assert.Null(catalog.FindConversion("EUR", "USD"));
        Assert.Null(catalog.FindConversion("USD", "USD"));
        Assert.NotNull(catalog.Find("usdjpy")); // names are case-insensitive
    }

    [Fact]
    public void DailyCsvWriter_writes_header_once_and_starts_a_new_file_each_day()
    {
        var w = new DailyCsvWriter(d => Path.Combine(_dir, d.ToString("yyyy-MM-dd"), "x.csv"), ["time_utc", "note"]);
        w.Write(Utc(2026, 10, 18, 23, 58), [CsvFormat.Time(Utc(2026, 10, 18, 23, 58)), "a,b"]);
        w.Write(Utc(2026, 10, 18, 23, 59), [CsvFormat.Time(Utc(2026, 10, 18, 23, 59)), "say \"hi\""]);
        w.Write(Utc(2026, 10, 19, 0, 1), [CsvFormat.Time(Utc(2026, 10, 19, 0, 1)), "next day"]);
        w.Dispose();

        Assert.Equal(
            ["time_utc,note", "2026-10-18 23:58:00,\"a,b\"", "2026-10-18 23:59:00,\"say \"\"hi\"\"\""],
            File.ReadAllLines(Path.Combine(_dir, "2026-10-18", "x.csv")));
        Assert.Equal(["time_utc,note", "2026-10-19 00:01:00,next day"], File.ReadAllLines(Path.Combine(_dir, "2026-10-19", "x.csv")));

        // Reopening an existing file appends without a second header.
        var again = new DailyCsvWriter(d => Path.Combine(_dir, d.ToString("yyyy-MM-dd"), "x.csv"), ["time_utc", "note"]);
        again.Write(Utc(2026, 10, 19, 0, 2), ["2026-10-19 00:02:00", "more"]);
        again.Dispose();
        Assert.Equal(3, File.ReadAllLines(Path.Combine(_dir, "2026-10-19", "x.csv")).Length);

        Assert.Throws<ArgumentException>(() => again.Write(Utc(2026, 10, 19, 0, 3), ["only one value"]));
    }

    [Fact]
    public void CsvFormat_numbers_use_dot_and_no_separators_and_unspecified_times_are_not_shifted()
    {
        Assert.Equal("1234567.89", CsvFormat.Num(1234567.891, 2));
        Assert.Equal("", CsvFormat.Num(null, 2));
        Assert.Equal("", CsvFormat.Num(double.NaN, 2));
        Assert.Equal("2026-10-18 08:00:00", CsvFormat.Time(new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void CsvEventSink_routes_account_events_and_skips_debug()
    {
        var sink = new CsvEventSink();
        string Path1(DateOnly d) => Path.Combine(_dir, "a1", "events.csv");
        string Path2(DateOnly d) => Path.Combine(_dir, "a2", "events.csv");
        sink.AddAccount(1, new DailyCsvWriter(Path1, CsvEventSink.Header));
        sink.AddAccount(2, new DailyCsvWriter(Path2, CsvEventSink.Header));
        var log = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();

        log.Event("Connected").Information("Connected to {Host}", "demo");     // both accounts
        log.Event("SymbolMissing").ForAccount(2).Warning("Symbol {Symbol} missing", "XYZ"); // account 2 only
        log.Debug("noise");                                                     // not written
        log.Information("plain line");                                         // event = Log
        sink.Dispose();

        var a1 = File.ReadAllLines(Path1(default));
        var a2 = File.ReadAllLines(Path2(default));
        Assert.Equal("time_utc,level,event,detail", a1[0]);
        Assert.Equal(3, a1.Length);
        Assert.EndsWith(",Information,Connected,Connected to demo", a1[1]);
        Assert.EndsWith(",Information,Log,plain line", a1[2]);
        Assert.Equal(4, a2.Length);
        Assert.EndsWith(",Warning,SymbolMissing,Symbol XYZ missing", a2[2]);
    }
}

public class PayloadDecoderTests
{
    [Fact]
    public void Decodes_messages_the_library_factory_does_not_know()
    {
        // ProtoOASubscribeLiveTrendbarRes: the 2022 library's MessageFactory returns null for it (Phase 1 step 3 finding).
        var res = new ProtoOASubscribeLiveTrendbarRes { CtidTraderAccountId = 49123893 };
        var envelope = new ProtoMessage { PayloadType = (uint)res.PayloadType, Payload = res.ToByteString(), ClientMsgId = "x" };

        Assert.True(Trader.Core.OpenApi.PayloadDecoder.LibraryCannotDecode(envelope));
        var decoded = Assert.IsType<ProtoOASubscribeLiveTrendbarRes>(Trader.Core.OpenApi.PayloadDecoder.Decode(envelope));
        Assert.Equal(49123893, decoded.CtidTraderAccountId);
    }

    [Fact]
    public void Still_decodes_ordinary_messages_and_ignores_unknown_types()
    {
        var auth = new ProtoOAApplicationAuthRes();
        var ok = new ProtoMessage { PayloadType = (uint)auth.PayloadType, Payload = auth.ToByteString() };
        Assert.IsType<ProtoOAApplicationAuthRes>(Trader.Core.OpenApi.PayloadDecoder.Decode(ok));

        Assert.Null(Trader.Core.OpenApi.PayloadDecoder.Decode(new ProtoMessage { PayloadType = 999_999 }));
        Assert.True(Trader.Core.OpenApi.PayloadDecoder.KnownPayloadTypes >= 80); // 84 message classes in the 2022 library
    }
}

public class OrderCommentTests
{
    [Fact]
    public void Sent_comment_has_utc_time_with_milliseconds() =>
        Assert.Equal("sent 2026-10-19 08:00:01.234Z",
            Trader.Core.OpenApi.CTraderSession.SentComment(new DateTime(2026, 10, 19, 8, 0, 1, 234, DateTimeKind.Utc)));
}
