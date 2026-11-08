using System.Globalization;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary>How hard the margin stress test is (scout.md §2.5). Starting values, to be calibrated on replay.</summary>
/// <param name="MarginMultiple">Margin on <paramref name="Symbols"/> multiplied by this (brokers raise it on volatile instruments).</param>
/// <param name="EquityFallPercent">Equity assumed to fall by this much.</param>
/// <param name="SpreadMultiple">Every open position also loses this many spreads less one, as in a rollover or news spike.</param>
/// <param name="Symbols">Our symbol names; null = metals and crypto.</param>
public sealed record MarginStress(double MarginMultiple = 2, double EquityFallPercent = 10, double SpreadMultiple = 5, IReadOnlyList<string>? Symbols = null)
{
    private static readonly string[] MetalsAndCrypto = ["XAUUSD", "XAGUSD", "XPTUSD", "BTCUSD", "ETHUSD", "SOLUSD"];

    public bool Applies(string symbol) => (Symbols ?? MetalsAndCrypto).Contains(symbol);
}

/// <summary>
/// Margin the account could come to hold (scout.md §2.5, review P-05), by the broker's own rule for a symbol's two sides (<see cref="AccountStatus.Combine"/>).
/// Every symbol is worked out twice and the larger counts: with everything pending filled but no hedge yet, and with the hedges filled too. A trade of
/// Scout's is its order and TM's mirror, with hedges of the same size implied; the two orders of one pause count once, because only one can fill. A new
/// trade must fit within <c>usePercent</c> of equity, and the account must still carry everything after the stress.
/// </summary>
internal sealed class MarginBudget(ProtoOATotalMarginCalculationType how, double equity, double usePercent, MarginStress stress, Func<long, bool> stressed)
{
    private sealed class Sides
    {
        public double PositionBuy, PositionSell, HedgeBuy, HedgeSell;
    }

    private readonly Dictionary<long, Sides> _symbols = [];
    private readonly Dictionary<string, (long SymbolId, int Dir, double Margin)> _trades = [];
    private double _stressLoss;

    private Sides Of(long symbolId) => _symbols.TryGetValue(symbolId, out var s) ? s : _symbols[symbolId] = new Sides();

    public void AddPosition(long symbolId, int dir, double margin)
    {
        var s = Of(symbolId);
        if (dir > 0)
            s.PositionBuy += margin;
        else
            s.PositionSell += margin;
    }

    /// <summary>A hedge order of TM's that waits on the broker: it holds margin only if it fills.</summary>
    public void AddHedgeOrder(long symbolId, int dir, double margin)
    {
        var s = Of(symbolId);
        if (dir > 0)
            s.HedgeBuy += margin;
        else
            s.HedgeSell += margin;
    }

    /// <summary>A trade not yet filled. <paramref name="margin"/>: its order and mirror together. One <paramref name="group"/> counts once, at its largest.</summary>
    public void AddTrade(string group, long symbolId, int dir, double margin)
    {
        Of(symbolId);
        if (!_trades.TryGetValue(group, out var old) || margin > old.Margin)
            _trades[group] = (symbolId, dir, margin);
    }

    public void AddStressLoss(double usd) => _stressLoss += usd;

    private double Projected(Func<long, double> multiple)
    {
        var total = 0.0;
        foreach (var (id, s) in _symbols)
        {
            var trades = _trades.Values.Where(t => t.SymbolId == id).ToList();
            var (buys, sells) = (trades.Where(t => t.Dir > 0).Sum(t => t.Margin), trades.Where(t => t.Dir < 0).Sum(t => t.Margin));
            var m = multiple(id);
            var unhedged = AccountStatus.Combine(how, (s.PositionBuy + buys) * m, (s.PositionSell + sells) * m);
            var hedged = AccountStatus.Combine(how, (s.PositionBuy + buys + sells + s.HedgeBuy) * m, (s.PositionSell + sells + buys + s.HedgeSell) * m);
            total += Math.Max(unhedged, hedged);
        }
        return total;
    }

    /// <summary>Margin held if everything pending fills.</summary>
    public double ProjectedMargin => Projected(_ => 1);

    public double StressedMargin => Projected(id => stressed(id) ? stress.MarginMultiple : 1);

    public double StressedEquity => equity * (1 - stress.EquityFallPercent / 100) - _stressLoss;

    public double Limit => equity * usePercent / 100;

    /// <summary>The account, as it stands, could not carry what is on it after the stress.</summary>
    public bool StressBreached => StressedMargin > StressedEquity;

    /// <summary>True, and the trade is counted, if it still fits within the limit and the stress test.</summary>
    public bool TryTake(string group, long symbolId, int dir, double margin)
    {
        var had = _trades.TryGetValue(group, out var old);
        AddTrade(group, symbolId, dir, margin);
        if (ProjectedMargin <= Limit && !StressBreached)
            return true;
        if (had)
            _trades[group] = old;
        else
            _trades.Remove(group);
        return false;
    }

    /// <summary>Why a trade was refused, or the account's state: "projected $320.00 of $2,233.73 allowed (50 % of equity $4,467.46); under stress $640.00 of $3,900.00".</summary>
    public string Explain(string group, long symbolId, int dir, double margin)
    {
        var had = _trades.TryGetValue(group, out var old);
        AddTrade(group, symbolId, dir, margin);
        var text = Describe();
        if (had)
            _trades[group] = old;
        else
            _trades.Remove(group);
        return text;
    }

    public string Describe() =>
        $"projected margin {AccountStatus.Money(ProjectedMargin)} of {AccountStatus.Money(Limit)} allowed ({usePercent.ToString("0.#", CultureInfo.InvariantCulture)} % of equity {AccountStatus.Money(equity)}); "
        + $"under stress {AccountStatus.Money(StressedMargin)} against equity {AccountStatus.Money(StressedEquity)}";
}
