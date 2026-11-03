using System.Globalization;
using Trader.Core.Csv;
using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>Candle files written by <c>--candles</c>: time_utc,open,high,low,close (Trader CSV rules), oldest first.</summary>
public static class CandleFile
{
    public static IReadOnlyList<Candle> Read(string path)
    {
        var table = CsvTable.Read(path);
        return table.Rows.Select(r => new Candle(r.Time("time_utc")!.Value, r.Num("open")!.Value, r.Num("high")!.Value, r.Num("low")!.Value, r.Num("close")!.Value))
            .ToList();
    }
}
