using Trader.Core.Csv;
using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>
/// One order Scout would place or cancel on a chart, worked out from past candles without a broker (scout-lens.md §8
/// step 1). <see cref="Action"/> is "place" or "cancel"; a place row carries the setup's numbers at that moment.
/// </summary>
public sealed record ReplayOrder(
    DateTime TimeUtc, string Key, string Action, int Dir, double Price, double? StopLoss, DateTime? ExpiresUtc,
    DateTime PauseStartUtc, bool Reversal, PauseInfo? Pause, double Atr, int HigherTrend, string? Reason,
    int? DailyTrend = null, string? DailyZone = null);

/// <summary>
/// Plays a chart's closed candles through the detector (with the next timeframe up for the tag) and lists the orders
/// Scout's Trade mode would place and cancel, from the same rules as the live bot (<see cref="OrderPlanner"/>, scout.md §2.3):
/// after each candle the planner's wanted orders are taken; each role is placed once, when first wanted; an order whose setup ended is cancelled. What happens to the orders (fills, one
/// cancels the other, expiry, refusals, the result in R) is left to the broker that plays them out (Rewind's
/// <c>rewind-lab outcomes</c>). No margin budget and no dashboard controls: every wanted order is placed.
/// </summary>
public static class ReplayOrders
{
    /// <param name="daily">Closed daily candles for the daily rule and its labels (scout.md §2.10); null on a replay without them.</param>
    /// <param name="dailyRule">Null or Enabled = false: every order is kept and only labelled.</param>
    public static List<ReplayOrder> Run(string symbol, string timeframe, IReadOnlyList<Candle> main, IReadOnlyList<Candle> higher,
        DetectorSettings settings, int expiryCandles, IReadOnlyList<Candle>? daily = null, DailySettings? dailyRule = null)
    {
        var rule = dailyRule ?? new DailySettings { Enabled = false };
        var di = 0;
        var tracker = new ChartTracker(new ScoutChart(symbol, timeframe, ScoutMode.Trade), settings);
        var len = tracker.Length;
        var hLen = tracker.HigherTimeframe is { } h && Timeframes.IsKnown(h) ? Timeframes.Length(h) : TimeSpan.Zero;
        var orders = new List<ReplayOrder>();
        var placed = new HashSet<string>();          // every role placed so far (at most one order per role, ever)
        var pending = new HashSet<string>();         // placed and not cancelled here (the broker may have filled them)
        var endReason = new Dictionary<string, string>();
        var lastEvent = new Dictionary<string, TaggedEvent>(); // newest event with the pause's numbers, per role
        var hi = 0;

        foreach (var candle in main)
        {
            var now = candle.OpenUtc + len;
            var newHigher = new List<Candle>();
            while (hi < higher.Count && higher[hi].OpenUtc + hLen <= now)
                newHigher.Add(higher[hi++]);
            var newDaily = new List<Candle>();
            while (daily is not null && di < daily.Count && daily[di].OpenUtc + TimeSpan.FromDays(1) <= now)
                newDaily.Add(daily[di++]);
            foreach (var te in tracker.Feed([candle], newHigher, now, newDaily))
            {
                var e = te.Event;
                if (e.PauseStartUtc is not { } ps)
                    continue;
                var key = ScoutState.Key(symbol, timeframe, ps, e.Reversal);
                if (e.Kind == SetupEventKind.Cancelled)
                    endReason[key] = e.Reason ?? "cancelled";
                else if (e.Kind is SetupEventKind.StopFilled or SetupEventKind.ReversalFilled)
                    endReason[key] = "filled";
                if (e.Pause is not null)
                    lastEvent[key] = te;
            }

            var d = tracker.Main;
            var wants = OrderPlanner.Wanted(d, symbol, timeframe).ToDictionary(i => i.Key);
            var dailyHeld = new HashSet<string>();
            foreach (var (key, w) in wants.ToList())
                if (OrderPlanner.DailyHold(tracker, w, rule) is { } why)
                {
                    wants.Remove(key);
                    dailyHeld.Add(key);
                    endReason[key] = why;
                }

            foreach (var key in pending.Where(k => !wants.ContainsKey(k)).Order(StringComparer.Ordinal).ToList())
            {
                pending.Remove(key);
                if (dailyHeld.Contains(key))
                    placed.Remove(key); // placed again if the direction allows it later, as the live bot does (scout.md §2.10)
                orders.Add(new ReplayOrder(now, key, "cancel", 0, 0, null, null, PauseStart(key), key[^1] == 'R', null, 0, 0,
                    endReason.GetValueOrDefault(key, "setup ended")));
            }

            foreach (var (key, w) in wants.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (OrderPlanner.StopLoss(w.Dir, w.Level, w.FarSide) is not { } sl)
                    continue; // as live: no order without a valid stop-loss, looked at again while the setup lasts
                if (!placed.Add(key))
                    continue;
                pending.Add(key);
                var ev = lastEvent.GetValueOrDefault(key);
                orders.Add(new ReplayOrder(now, key, "place", w.Dir, w.Level, sl, OrderPlanner.Expiry(w.SinceUtc, len, expiryCandles), w.PauseStartUtc, w.Reversal,
                    ev?.Event.Pause, ev?.Event.Atr ?? d.Atr ?? 0, tracker.Higher?.Trend ?? 0, null,
                    tracker.Daily?.Trend, tracker.Daily is { } dd ? DailyRule.ZoneInTheWay(w.Dir, w.Level, tracker.DailyZones(rule), dd.Atr, rule) : null));
            }
        }
        return orders;
    }

    private static DateTime PauseStart(string key) =>
        DateTime.SpecifyKind(DateTime.ParseExact(key.Split('|')[2], "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    public const string Header =
        "time_utc,setup_id,symbol,timeframe,role,action,side,price,stop_loss,expires_utc,setup_utc,pause_candles,pause_height_atr,move_atr,pullback,atr,higher_tf,higher_trend,with_higher,reason,daily_trend,with_daily,daily_zone";

    /// <summary>Writes the orders as CSV (Trader CSV rules, shared.md §5); prices with 6 decimals (the broker rounds them).</summary>
    public static void Write(string path, string symbol, string timeframe, IEnumerable<ReplayOrder> orders)
    {
        var higherTf = Timeframes.Higher(timeframe) ?? "";
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        w.WriteLine(Header);
        foreach (var o in orders)
        {
            var place = o.Action == "place";
            var p = o.Pause;
            w.Write(CsvFormat.Line(
            [
                CsvFormat.Time(o.TimeUtc), o.Key, symbol, timeframe, o.Reversal ? "R" : "M", o.Action,
                place ? (o.Dir > 0 ? "Buy" : "Sell") : "", place ? CsvFormat.Num(o.Price, 6) : "", CsvFormat.Num(o.StopLoss, 6),
                o.ExpiresUtc is { } x ? CsvFormat.Time(x) : "", CsvFormat.Time(o.PauseStartUtc),
                p?.Candles.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                p is null || o.Atr <= 0 ? "" : CsvFormat.Num((p.High - p.Low) / o.Atr, 2), CsvFormat.Num(p?.MoveAtr, 2), CsvFormat.Num(p?.Pullback, 2),
                place ? CsvFormat.Num(o.Atr, 6) : "", place ? higherTf : "",
                place ? (o.HigherTrend > 0 ? "up" : o.HigherTrend < 0 ? "down" : "unknown") : "",
                place ? TaggedEvent.Compare(o.HigherTrend, o.Dir) : "", o.Reason ?? "",
                place && o.DailyTrend is { } dt ? (dt > 0 ? "up" : dt < 0 ? "down" : "unknown") : "",
                place && o.DailyTrend is { } dw ? TaggedEvent.Compare(dw, o.Dir) : "",
                place ? o.DailyZone ?? "" : "",
            ]));
        }
    }
}
