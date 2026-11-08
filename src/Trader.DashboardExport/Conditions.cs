using System.Globalization;
using System.Text.Json;
using Trader.Core.Csv;
using Trader.Core.MarketData;
using Trader.Core.OpenApi;

namespace Trader.DashboardExport;

/// <summary>The broker's margin for a size (<see cref="Lots"/>), buy and sell, in USD: a point on the curve the "largest size" is read from.</summary>
public sealed record MarginPoint(double Lots, double Buy, double Sell);

/// <summary>One symbol's trading conditions (dashboard.md §2.5), per lot and in USD, as the broker stated them at the snapshot.</summary>
public sealed record ConditionsRow(
    string Symbol, bool MarketOpen, string AccountLeverage, string SymbolLeverage, IReadOnlyList<string> Tiers,
    double LotUnits, string LotAsset, double MinLots, double StepLots, double MaxLots,
    double? MarginBuyPerLotUsd, double? MarginSellPerLotUsd, double PipSize, int Digits, double? PipValuePerLotUsd,
    double? Bid, double? Ask, double? SpreadPips, double? SpreadPerLotUsd,
    double? CommissionPerLotUsd, string CommissionType, double CommissionRate,
    string SwapType, double SwapLong, double SwapShort, double? SwapLongPerLotUsd, double? SwapShortPerLotUsd, string TripleSwapDay,
    double? LargestLotsNow, IReadOnlyList<MarginPoint> Margin)
{
    public static readonly string[] Header =
    [
        "time_utc", "symbol", "account_leverage", "symbol_leverage", "lot_units", "lot_asset", "min_lots", "step_lots", "max_lots",
        "margin_buy_per_lot_usd", "margin_sell_per_lot_usd", "pip_size", "pip_value_per_lot_usd", "bid", "ask", "spread_pips", "spread_per_lot_usd",
        "commission_per_lot_usd", "commission_type", "commission_rate", "swap_type", "swap_long", "swap_short",
        "swap_long_per_lot_usd", "swap_short_per_lot_usd", "triple_swap_day", "largest_lots_now",
    ];

    /// <summary>The columns that are not price or spread: a change in any of them adds a row to conditions.csv (dashboard.md §3.3).</summary>
    public static readonly string[] Fixed =
        ["account_leverage", "symbol_leverage", "lot_units", "lot_asset", "min_lots", "step_lots", "max_lots", "commission_type", "commission_rate", "swap_type", "swap_long", "swap_short", "triple_swap_day"];

    public string[] Values(DateTime nowUtc)
    {
        static string L(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
        string P(double? v) => CsvFormat.Num(v, Digits);
        return
        [
            CsvFormat.Time(nowUtc), Symbol, AccountLeverage, SymbolLeverage, L(LotUnits), LotAsset, L(MinLots), L(StepLots), L(MaxLots),
            CsvFormat.Num(MarginBuyPerLotUsd, 2), CsvFormat.Num(MarginSellPerLotUsd, 2), L(PipSize), CsvFormat.Num(PipValuePerLotUsd, 4), P(Bid), P(Ask),
            CsvFormat.Num(SpreadPips, 2), CsvFormat.Num(SpreadPerLotUsd, 2), CsvFormat.Num(CommissionPerLotUsd, 2), CommissionType, L(CommissionRate),
            SwapType, L(SwapLong), L(SwapShort), CsvFormat.Num(SwapLongPerLotUsd, 2), CsvFormat.Num(SwapShortPerLotUsd, 2), TripleSwapDay,
            CsvFormat.Num(LargestLotsNow, 2),
        ];
    }
}

/// <summary>Works the conditions out from the broker's replies (dashboard.md §2.5). No connection here: the tests feed it directly.</summary>
public static class ConditionsMath
{
    /// <summary>The broker's commission and minimum-commission rates are integers × 10^8; a percentage of the value × 10^5.</summary>
    private const double Rate = 1e8, PercentRate = 1e5;

    /// <param name="price">Mid price now, or the last close when the market is closed.</param>
    /// <param name="usdPerQuote">USD for 1 unit of the quote currency (1 for USD); null if no symbol converts it.</param>
    public static ConditionsRow Compute(ProtoOASymbol f, string name, string baseAsset, string quoteAsset, long accountLeverageCents,
        ProtoOADynamicLeverage? leverage, Quote? quote, double? price, double? usdPerQuote, IReadOnlyList<ExpectedMargin> margins, double freeMargin)
    {
        var lotVolume = Math.Max(1, f.LotSize);                                // Open API volume of 1 lot
        double Lots(long volume) => (double)volume / lotVolume;
        var units = f.LotSize / 100.0;
        var pip = Math.Pow(10, -f.PipPosition);
        var pipValue = usdPerQuote * pip * units;
        var notional = price * units * usdPerQuote;                           // USD value of 1 lot

        double? spreadPips = quote is { } q ? (q.Ask - q.Bid) / pip : null;
        double? spreadUsd = quote is { } q2 ? (q2.Ask - q2.Bid) * units * usdPerQuote : null;

        var rate = f.HasPreciseTradingCommissionRate ? f.PreciseTradingCommissionRate : 0L;
        var commission = CommissionRule.From(f).RoundTripPerLotUsd(notional, usdPerQuote);

        double? Swap(double v) => f.SwapCalculationType switch
        {
            ProtoOASwapCalculationType.Pips => pipValue * v,
            ProtoOASwapCalculationType.Percentage => notional * v / 100 / 360,
            _ => null,
        };

        var points = margins.OrderBy(m => m.Volume).Select(m => new MarginPoint(Lots(m.Volume), m.Buy, m.Sell)).ToList();
        var perLot = margins.FirstOrDefault(m => m.Volume == lotVolume);
        var tiers = leverage?.Tiers.Select(t => $"1:{t.Leverage / 100}").ToList() ?? [];
        var accountLev = accountLeverageCents / 100;
        var symbolLev = leverage?.Tiers.Count > 0 ? leverage.Tiers[0].Leverage / 100 : (long?)null;

        static double? R(double? v, int d) => v is { } x ? Math.Round(x, d) : null;
        return new ConditionsRow(
            name, quote is not null, $"1:{accountLev}", symbolLev is { } sl ? $"1:{sl}" : "", tiers,
            units, baseAsset, Lots(f.MinVolume), Lots(Math.Max(1, f.StepVolume)), Lots(f.MaxVolume),
            perLot?.Buy, perLot?.Sell, pip, f.Digits, R(pipValue, 6),
            quote?.Bid, quote?.Ask, R(spreadPips, 2), R(spreadUsd, 4),
            R(commission, 4), f.CommissionType.ToString(), rate / (f.CommissionType == ProtoOACommissionType.PercentageOfValue ? PercentRate : Rate),
            f.SwapCalculationType.ToString(), f.SwapLong, f.SwapShort, R(Swap(f.SwapLong), 4), R(Swap(f.SwapShort), 4), f.SwapRollover3Days.ToString(),
            LargestLots(points, freeMargin, Lots(f.MinVolume), Lots(Math.Max(1, f.StepVolume)), Lots(f.MaxVolume)), points);
    }

    /// <summary>
    /// The most lots whose margin (the larger of buy and sell) fits in <paramref name="freeMargin"/>, read off the broker's margin curve
    /// (straight between its points and from zero), rounded down to the step and capped by the largest order; 0 when not even the smallest fits.
    /// </summary>
    public static double LargestLots(IReadOnlyList<MarginPoint> points, double freeMargin, double minLots, double stepLots, double maxLots)
    {
        if (freeMargin <= 0 || points.Count == 0)
            return 0;
        var (lots, margin) = (0.0, 0.0);
        double fit = maxLots;
        foreach (var p in points)
        {
            var m = Math.Max(p.Buy, p.Sell);
            if (m > freeMargin)
            {
                fit = lots + (p.Lots - lots) * (freeMargin - margin) / (m - margin);
                break;
            }
            (lots, margin) = (p.Lots, m);
            if (p == points[^1])
                fit = margin > 0 ? Math.Min(maxLots, p.Lots * freeMargin / margin) : maxLots; // past the last point: straight on
        }
        var stepped = Math.Floor(Math.Min(fit, maxLots) / stepLots + 1e-9) * stepLots;
        return stepped + 1e-9 < minLots ? 0 : Math.Round(stepped, 8);
    }

    /// <summary>Volumes the broker is asked the margin for: 1, 10 and 100 lots and the largest order, within the largest.</summary>
    public static List<long> MarginVolumes(ProtoOASymbol f)
    {
        var lot = Math.Max(1, f.LotSize);
        return new[] { lot, lot * 10, lot * 100, f.MaxVolume }.Where(v => v > 0 && v <= f.MaxVolume).Distinct().Order().ToList();
    }

    /// <summary>The symbols Scout and TM work on, per account (scout.json charts, tm.json symbols): the rows of the conditions page.</summary>
    public static Dictionary<string, List<string>> ConfiguredSymbols(string configDir)
    {
        var map = new Dictionary<string, List<string>>();
        void Add(string broker, long id, string symbol)
        {
            var key = $"{broker}-{id}";
            var list = map.TryGetValue(key, out var l) ? l : map[key] = [];
            if (!list.Contains(symbol))
                list.Add(symbol);
        }
        foreach (var (file, listName, nested) in new[] { ("scout.json", "charts", true), ("tm.json", "symbols", false) })
        {
            var path = Path.Combine(configDir, file);
            if (!File.Exists(path))
                continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var a in accounts.EnumerateArray())
            {
                if (!a.TryGetProperty("broker", out var b) || b.GetString() is not { } broker || !a.TryGetProperty("accountId", out var idEl) || !idEl.TryGetInt64(out var id)
                    || !a.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in list.EnumerateArray())
                {
                    var symbol = nested ? (item.TryGetProperty("symbol", out var sEl) ? sEl.GetString() : null) : item.GetString();
                    if (symbol is { Length: > 0 and <= 32 })
                        Add(broker, id, symbol);
                }
            }
        }
        return map;
    }

    /// <summary>Adds a row for each symbol seen for the first time today, or whose fixed values changed since its last row today.</summary>
    public static int AppendChanged(string dayFile, IEnumerable<ConditionsRow> rows, DateTime nowUtc)
    {
        var last = new Dictionary<string, CsvRow>();
        if (File.Exists(dayFile))
            foreach (var r in CsvTable.Read(dayFile).Rows)
                last[r["symbol"]] = r;
        var added = 0;
        using var writer = new DailyCsvWriter(_ => dayFile, ConditionsRow.Header);
        foreach (var row in rows)
        {
            var values = row.Values(nowUtc);
            if (last.TryGetValue(row.Symbol, out var prev)
                && ConditionsRow.Fixed.All(c => prev[c] == values[Array.IndexOf(ConditionsRow.Header, c)]))
                continue;
            writer.Write(nowUtc, values);
            added++;
        }
        return added;
    }
}
