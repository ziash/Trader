using System.Globalization;
using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>
/// Sizing from the money at risk (scout.md §2.8, review P-02). Off by default: <c>"sizing": { "enabled": true }</c> in scout.json replaces the fixed
/// lots. Starting values, to be calibrated on replay.
/// </summary>
/// <param name="RiskPercent">Cash risk of the whole trade at its initial stop, all legs together, in % of equity.</param>
/// <param name="Legs">Legs the trade will have: 2 = Scout's order and TM's mirror.</param>
/// <param name="SlippageSpreads">Allowance for spread and slippage on entry and exit, in spreads.</param>
/// <param name="MinStopSpreads">A stop nearer than this many spreads is refused: its size would be large and mostly spread.</param>
/// <param name="LimitR">TM's loss limit in R (<c>closeR</c> in tm.json).</param>
/// <param name="MaxLossPercent">Refused if the whole trade at <paramref name="LimitR"/> would lose more than this % of equity.</param>
public sealed record SizingSettings(
    bool Enabled = false,
    double RiskPercent = 0.5,
    int Legs = 2,
    double SlippageSpreads = 1,
    double MinStopSpreads = 2,
    double LimitR = 3,
    double MaxLossPercent = 2);

/// <summary>The size for one order, or why there is none. <see cref="RiskCash"/>: the whole trade's loss at the initial stop, costs included, in USD.</summary>
public sealed record SizeDecision(long? Volume, double RiskCash, string? Refusal);

/// <summary>
/// Volume = permitted cash risk ÷ (legs × the loss of one lot at the stop, with its costs). Always rounds down, and refuses rather than raising a size
/// to the broker's minimum. A pure calculation: no broker, no clock. Money is in USD, the currency of the demo accounts.
/// </summary>
public static class PositionSizer
{
    /// <summary>One leg's loss per lot at the stop, in USD: the distance, the allowance for spread and slippage, and the commission opened and closed.</summary>
    public static double LossPerLot(SymbolInfo s, SizingSettings p, double entry, double distance, double spread, double usdPerQuote) =>
        (distance + p.SlippageSpreads * spread) * s.LotUnits * usdPerQuote + (s.Commission?.RoundTripPerLotUsd(entry * s.LotUnits * usdPerQuote, usdPerQuote) ?? 0);

    /// <param name="usdPerQuote">USD for 1 unit of the symbol's quote currency (1 for USD); null when no price converts it.</param>
    public static SizeDecision Decide(SymbolInfo s, SizingSettings p, double equity, double entry, double? stopLoss, double spread, double? usdPerQuote)
    {
        static SizeDecision No(string why) => new(null, 0, why);
        static string N(double v, string f = "0.00") => v.ToString(f, CultureInfo.InvariantCulture);

        if (equity <= 0)
            return No("no equity to size from");
        if (stopLoss is not { } sl || Math.Abs(entry - sl) <= 0)
            return No("no valid stop-loss");
        if (usdPerQuote is not { } rate || rate <= 0)
            return No($"no price converts {s.QuoteAsset} to USD");

        var distance = Math.Abs(entry - sl);
        if (distance < p.MinStopSpreads * spread)
            return No($"stop is only {N(spread > 0 ? distance / spread : 0, "0.0")} spreads away (minimum {N(p.MinStopSpreads, "0.0")})");

        var lossPerLot = LossPerLot(s, p, entry, distance, spread, rate);
        var budget = equity * p.RiskPercent / 100;
        var lots = Math.Min(budget / (p.Legs * lossPerLot), ScoutConfig.MaxLots);

        if (s.VolumeNotAbove(lots) is not { } volume)
            return No($"permitted risk ${N(budget)} buys {N(lots, "0.0000")} lot, below the broker's minimum {N(s.LotsForVolume(s.MinVolume), "0.####")}");
        volume = Math.Min(volume, s.MaxVolume / Math.Max(1, s.StepVolume) * Math.Max(1, s.StepVolume));

        var risk = p.Legs * s.LotsForVolume(volume) * lossPerLot;
        var atLimit = p.LimitR * risk;
        if (atLimit > equity * p.MaxLossPercent / 100)
            return No($"${N(atLimit)} at the {N(p.LimitR, "0.#")}R limit is above {N(p.MaxLossPercent, "0.##")} % of equity");
        return new SizeDecision(volume, risk, null);
    }
}
