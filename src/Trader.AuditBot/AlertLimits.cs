using System.Globalization;
using Trader.Core.MarketData;

namespace Trader.AuditBot;

/// <summary>
/// Alert limits (audit-bot.md §2.4): a spread or a probe fill beyond these is logged as a warning (journal and events.csv).
/// Forex limits are in pips; everything that is not forex (crypto, metals, energies, indices) in USD per lot, because
/// their "pip" can differ by broker. The "crypto…" defaults apply to all non-forex symbols (the name dates from v1.0).
/// Optional "alerts" section of audit.&lt;broker&gt;.json; anything left out keeps its default:
/// <code>
/// "alerts": {
///   "forexSpreadPips": 2,     // average spread over one spreads.csv interval
///   "forexSlipPips": 1,       // one probe fill worse than requested by more than this
///   "cryptoSpreadUsd": 50,
///   "cryptoSlipUsd": 10,
///   "symbols": { "ETHUSD": { "spreadUsd": 10, "slipUsd": 3 } }   // per-symbol overrides
/// }
/// </code>
/// </summary>
public sealed record AlertLimits(
    double ForexSpreadPips = 2,
    double ForexSlipPips = 1,
    double CryptoSpreadUsd = 50,
    double CryptoSlipUsd = 10,
    IReadOnlyDictionary<string, SymbolLimits>? Symbols = null)
{
    public static readonly AlertLimits Default = new();

    /// <summary>The limits that apply to <paramref name="symbol"/>, in pips (forex) or USD per lot (others).</summary>
    public Limit For(SymbolInfo symbol)
    {
        var o = Symbols?.GetValueOrDefault(symbol.Name);
        return symbol.IsForex
            ? new Limit(InPips: true, o?.SpreadPips ?? ForexSpreadPips, o?.SlipPips ?? ForexSlipPips)
            : new Limit(InPips: false, o?.SpreadUsd ?? CryptoSpreadUsd, o?.SlipUsd ?? CryptoSlipUsd);
    }

    internal void Validate(string file, IReadOnlyList<string> symbols)
    {
        double?[] values =
        [
            ForexSpreadPips, ForexSlipPips, CryptoSpreadUsd, CryptoSlipUsd,
            .. (Symbols?.Values ?? []).SelectMany(s => new[] { s.SpreadPips, s.SlipPips, s.SpreadUsd, s.SlipUsd }),
        ];
        if (values.Any(v => v is <= 0 or double.NaN))
            throw new AuditConfigException($"{file}: every \"alerts\" limit must be a number above 0");
        foreach (var name in Symbols?.Keys ?? [])
            if (!symbols.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new AuditConfigException($"{file}: \"alerts.symbols\" has {name}, which is not in \"symbols\"");
    }
}

/// <summary>Per-symbol override; use the pips fields for forex, the USD fields for everything else.</summary>
public sealed record SymbolLimits(double? SpreadPips = null, double? SlipPips = null, double? SpreadUsd = null, double? SlipUsd = null);

/// <summary>Spread and slippage limits for one symbol, and how to print values in its unit.</summary>
public sealed record Limit(bool InPips, double Spread, double Slip)
{
    /// <summary>e.g. "0.30 pips" or "$6.91/lot".</summary>
    public string Show(double value) =>
        InPips ? Inv($"{value:0.00} pips") : Inv($"${value:0.00}/lot");

    /// <summary>With a sign, for slippage: "-0.30 pips", "+$14.64/lot".</summary>
    public string ShowSigned(double value) =>
        InPips ? Inv($"{value:+0.00;-0.00;0.00} pips") : (value < 0 ? "-" : value > 0 ? "+" : "") + Inv($"${Math.Abs(value):0.00}/lot");

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
