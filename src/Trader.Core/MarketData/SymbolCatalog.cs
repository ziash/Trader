using Trader.Core.OpenApi;

namespace Trader.Core.MarketData;

/// <summary>What a bot needs to know about one tradable symbol at one broker.</summary>
/// <param name="Name">The name used in our config and logs (e.g. "AVAXUSD").</param>
/// <param name="BrokerName">The broker's own name for it (e.g. "AVXUSD" at IC Markets).</param>
/// <param name="LotUnits">Units in 1 lot: 100,000 for forex, 1 coin for crypto (from ProtoOASymbol.LotSize, which is in hundredths).</param>
/// <param name="MinVolume">Smallest order volume, in Open API units (hundredths of a unit).</param>
/// <param name="StepVolume">Order volumes must be a multiple of this, in Open API units.</param>
public sealed record SymbolInfo(
    long SymbolId,
    string Name,
    string BrokerName,
    string BaseAsset,
    string QuoteAsset,
    int Digits,
    int PipPosition,
    long LotUnits,
    bool Enabled,
    ProtoOATradingMode TradingMode,
    TradingSchedule Schedule,
    long MinVolume = 1,
    long StepVolume = 1)
{
    /// <summary>1 pip as a price: 0.0001 for EURUSD, 0.01 for JPY pairs; for crypto the broker decides.</summary>
    public double PipSize => Math.Pow(10, -PipPosition);

    public bool IsTradable => Enabled && TradingMode == ProtoOATradingMode.Enabled;

    /// <summary>Forex pairs have 100,000 units per lot; crypto (1 coin per lot) and others do not.</summary>
    public bool IsForex => LotUnits == 100_000;

    /// <summary>Order volume in Open API units for <paramref name="lots"/>, rounded down to the step; at least the minimum.</summary>
    public long VolumeForLots(double lots)
    {
        var raw = (long)Math.Round(lots * LotUnits * 100);
        var step = Math.Max(1, StepVolume);
        return Math.Max(MinVolume, raw / step * step);
    }

    /// <summary>Lots for an Open API volume (e.g. 100,000 on EURUSD = 0.01 lot).</summary>
    public double LotsForVolume(long volume) => volume / 100.0 / LotUnits;

    /// <summary>A price distance in Open API relative units (1/100,000), rounded to the symbol's price digits.</summary>
    public long RelativeDistance(double priceDistance)
    {
        var step = (long)Math.Pow(10, Math.Max(0, 5 - Digits));
        return Math.Max(step, (long)Math.Round(priceDistance * 100_000 / step) * step);
    }

    /// <summary>Open API prices are integers in units of 0.00001, whatever the symbol's digits.</summary>
    public static double PriceFromRaw(ulong raw) => raw / 100_000.0;
}

/// <summary>A currency conversion: price of <see cref="Symbol"/> converts by multiplying, or dividing when <see cref="Invert"/>.</summary>
public sealed record Conversion(string FromAsset, string ToAsset, string Symbol, bool Invert);

/// <summary>
/// One account's symbol list and asset names, loaded from the server. Resolves names to IDs and details,
/// and finds the symbol needed to convert one currency into another (e.g. JPY → USD via USDJPY).
/// </summary>
public sealed class SymbolCatalog
{
    private readonly Dictionary<string, ProtoOALightSymbol> _byName;
    private readonly Dictionary<long, string> _assets;

    private SymbolCatalog(long ctid, IEnumerable<ProtoOALightSymbol> symbols, Dictionary<long, string> assets)
    {
        CtidTraderAccountId = ctid;
        _byName = symbols.GroupBy(s => s.SymbolName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _assets = assets;
    }

    public long CtidTraderAccountId { get; }

    public int Count => _byName.Count;

    public static async Task<SymbolCatalog> LoadAsync(CTraderSession session, long ctid)
    {
        var symbols = await session.GetSymbolsAsync(ctid);
        var assets = await session.GetAssetsAsync(ctid);
        return new SymbolCatalog(ctid, symbols, assets.ToDictionary(a => a.AssetId, a => a.Name));
    }

    /// <summary>For tests.</summary>
    internal static SymbolCatalog Create(long ctid, IEnumerable<ProtoOALightSymbol> symbols, Dictionary<long, string> assets) =>
        new(ctid, symbols, assets);

    public ProtoOALightSymbol? Find(string brokerName) => _byName.GetValueOrDefault(brokerName);

    public string AssetName(long assetId) => _assets.GetValueOrDefault(assetId, $"asset {assetId}");

    /// <summary>The symbol pricing <paramref name="from"/> in <paramref name="to"/>: base=from/quote=to (multiply) or base=to/quote=from (divide). Null if the broker has neither.</summary>
    public Conversion? FindConversion(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var s in _byName.Values)
        {
            var b = AssetName(s.BaseAssetId);
            var q = AssetName(s.QuoteAssetId);
            if (b.Equals(from, StringComparison.OrdinalIgnoreCase) && q.Equals(to, StringComparison.OrdinalIgnoreCase))
                return new Conversion(from, to, s.SymbolName, Invert: false);
            if (b.Equals(to, StringComparison.OrdinalIgnoreCase) && q.Equals(from, StringComparison.OrdinalIgnoreCase))
                return new Conversion(from, to, s.SymbolName, Invert: true);
        }
        return null;
    }

    /// <summary>Full details for the given symbols. <paramref name="names"/> maps our name → broker's name; names the broker lacks are skipped.</summary>
    public async Task<IReadOnlyList<SymbolInfo>> GetDetailsAsync(CTraderSession session, IReadOnlyDictionary<string, string> names)
    {
        var light = names
            .Select(n => (Name: n.Key, Light: Find(n.Value)))
            .Where(x => x.Light is not null)
            .ToList();
        if (light.Count == 0)
            return [];

        var full = (await session.GetSymbolDetailsAsync(CtidTraderAccountId, light.Select(x => x.Light!.SymbolId)))
            .ToDictionary(s => s.SymbolId);

        return light
            .Where(x => full.ContainsKey(x.Light!.SymbolId))
            .Select(x =>
            {
                var l = x.Light!;
                var f = full[l.SymbolId];
                return new SymbolInfo(l.SymbolId, x.Name, l.SymbolName, AssetName(l.BaseAssetId), AssetName(l.QuoteAssetId),
                    f.Digits, f.PipPosition, f.LotSize / 100, l.Enabled, f.TradingMode, TradingSchedule.From(f), f.MinVolume, f.StepVolume);
            })
            .ToList();
    }
}
