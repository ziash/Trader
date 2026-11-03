using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>A setup event with the higher timeframe's trend at that moment (Scout spec §4.4 I1): +1 up, −1 down, 0 unknown.</summary>
public sealed record TaggedEvent(SetupEvent Event, int HigherTrend)
{
    /// <summary>"with", "against" or "unknown": the order's side compared with the higher timeframe's trend.</summary>
    public string WithHigher => HigherTrend == 0 ? "unknown" : HigherTrend == Event.Dir ? "with" : "against";
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

    public TimeSpan Length => Timeframes.Length(Chart.Timeframe);

    /// <summary>
    /// Adds the closed candles that are newer than those already seen (the lists may overlap earlier ones and may
    /// include the candle still forming, which is skipped). Returns the main timeframe's events, oldest first.
    /// </summary>
    public IReadOnlyList<TaggedEvent> Feed(IReadOnlyList<Candle> main, IReadOnlyList<Candle> higher, DateTime nowUtc)
    {
        var len = Length;
        var hLen = HigherTimeframe is { } h ? Timeframes.Length(h) : TimeSpan.Zero;
        var items = main.Where(c => c.OpenUtc + len <= nowUtc && (Main.LastCandleUtc is not { } last || c.OpenUtc > last))
            .Select(c => (Close: c.OpenUtc + len, Higher: false, Candle: c))
            .ToList();
        if (Higher is not null)
            items.AddRange(higher.Where(c => c.OpenUtc + hLen <= nowUtc && (Higher.LastCandleUtc is not { } last || c.OpenUtc > last))
                .Select(c => (Close: c.OpenUtc + hLen, Higher: true, Candle: c)));

        var events = new List<TaggedEvent>();
        foreach (var item in items.OrderBy(x => x.Close).ThenBy(x => x.Higher ? 0 : 1).ThenBy(x => x.Candle.OpenUtc))
        {
            if (item.Higher)
                Higher!.Add(item.Candle);
            else
                events.AddRange(Main.Add(item.Candle).Select(e => new TaggedEvent(e, Higher?.Trend ?? 0)));
        }
        return events;
    }
}
