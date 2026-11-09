using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>A setup event with the higher timeframe's trend at that moment (Scout spec §4.4 I1): +1 up, −1 down, 0 unknown.</summary>
public sealed record TaggedEvent(SetupEvent Event, int HigherTrend)
{
    /// <summary>"with", "against" or "unknown": the order's side compared with the higher timeframe's trend.</summary>
    public string WithHigher => Compare(HigherTrend, Event.Dir);

    /// <summary>"with", "against" or "unknown" for an order side (+1 buy, −1 sell) and a higher-timeframe trend.</summary>
    public static string Compare(int higherTrend, int dir) => higherTrend == 0 ? "unknown" : higherTrend == dir ? "with" : "against";
}

/// <summary>
/// One chart (symbol × timeframe): the detector on its own candles and one on the next timeframe up for the tag.
/// Candles are fed in the order they closed; a higher-timeframe candle that closes at the same moment goes first,
/// so the tag never looks ahead.
/// </summary>
public sealed class ChartTracker(ScoutChart chart, DetectorSettings settings)
{
    public ScoutChart Chart { get; } = chart;
    public Detector Main { get; } = new(settings);
    public Detector? Higher { get; } = Timeframes.Higher(chart.Timeframe) is null ? null : new Detector(settings);
    public string? HigherTimeframe => Timeframes.Higher(Chart.Timeframe);

    private readonly Detector? _ownDaily = chart.Timeframe == "H1" ? new Detector(settings) : null;
    private DateTime? _zonesAt;
    private IReadOnlyList<DailyZone> _zones = [];

    /// <summary>True when this chart needs daily candles of its own (a 1H chart; a 4H chart has them as its higher timeframe).</summary>
    public bool ReadsDaily => _ownDaily is not null;

    /// <summary>The detector on closed daily candles (scout.md §2.10), or null on a chart the daily rule does not apply to.</summary>
    public Detector? Daily => Chart.Timeframe switch { "H1" => _ownDaily, "H4" => Higher, _ => null };

    /// <summary>The daily zones as of the last closed daily candle; worked out once per daily candle.</summary>
    public IReadOnlyList<DailyZone> DailyZones(DailySettings s)
    {
        if (Daily is not { } d)
            return [];
        if (_zonesAt != d.LastCandleUtc)
            (_zonesAt, _zones) = (d.LastCandleUtc, DailyRule.Zones(d.Candles, d.Atr, s));
        return _zones;
    }

    public TimeSpan Length => Timeframes.Length(Chart.Timeframe);

    /// <summary>
    /// The higher timeframe's trend as it stood at <paramref name="utc"/>, worked out again from its candles closed by
    /// then (for pictures of earlier orders whose tag was not logged, scout.md §3.1). 0 when unknown.
    /// </summary>
    public int HigherTrendAt(DateTime utc)
    {
        if (Higher is null || HigherTimeframe is not { } h)
            return 0;
        var replay = new Detector(settings);
        foreach (var c in Higher.Candles.Where(c => c.OpenUtc + Timeframes.Length(h) <= utc))
            replay.Add(c);
        return replay.Trend;
    }

    /// <summary>
    /// Adds the closed candles that are newer than those already seen (the lists may overlap earlier ones and may
    /// include the candle still forming, which is skipped). Returns the main timeframe's events, oldest first.
    /// </summary>
    public IReadOnlyList<TaggedEvent> Feed(IReadOnlyList<Candle> main, IReadOnlyList<Candle> higher, DateTime nowUtc, IReadOnlyList<Candle>? daily = null)
    {
        var len = Length;
        var hLen = HigherTimeframe is { } h ? Timeframes.Length(h) : TimeSpan.Zero;
        var items = main.Where(c => c.OpenUtc + len <= nowUtc && (Main.LastCandleUtc is not { } last || c.OpenUtc > last))
            .Select(c => (Close: c.OpenUtc + len, Feed: 2, Candle: c))
            .ToList();
        if (Higher is not null)
            items.AddRange(higher.Where(c => c.OpenUtc + hLen <= nowUtc && (Higher.LastCandleUtc is not { } last || c.OpenUtc > last))
                .Select(c => (Close: c.OpenUtc + hLen, Feed: 1, Candle: c)));
        if (_ownDaily is not null && daily is not null)
            items.AddRange(daily.Where(c => c.OpenUtc + TimeSpan.FromDays(1) <= nowUtc && (_ownDaily.LastCandleUtc is not { } last || c.OpenUtc > last))
                .Select(c => (Close: c.OpenUtc + TimeSpan.FromDays(1), Feed: 0, Candle: c)));

        var events = new List<TaggedEvent>();
        foreach (var item in items.OrderBy(x => x.Close).ThenBy(x => x.Feed).ThenBy(x => x.Candle.OpenUtc))
        {
            if (item.Feed == 0)
                _ownDaily!.Add(item.Candle);
            else if (item.Feed == 1)
                Higher!.Add(item.Candle);
            else
                events.AddRange(Main.Add(item.Candle).Select(e => new TaggedEvent(e, Higher?.Trend ?? 0)));
        }
        return events;
    }
}
