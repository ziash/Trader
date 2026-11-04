using System.Globalization;
using System.Security;
using System.Text;
using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>What one order picture shows (scout.md §3.1). <see cref="WithHigher"/>: "with", "against" or "unknown".</summary>
public sealed record OrderPicture(
    string Symbol, string Timeframe, int Digits, double PipSize, string Broker, long AccountId, long OrderId,
    int Dir, bool Reversal, double Level, double? StopLoss, DateTime PlacedUtc, DateTime ExpiresUtc, DateTime PauseStartUtc,
    string? HigherTimeframe, string WithHigher);

/// <summary>
/// Draws one Scout order as it stood when it was placed (scout.md §3.1): the last 60 closed candles, the pause, the
/// stop level, the stop-loss and the room up to the expiry (the right edge). Only candles that had closed by then are used, so a
/// picture drawn later from the same candles is the same picture. SVG: small (≈ 15 KB), sharp at any size, opens
/// in any browser; numbers are always written with '.' whatever the machine's language.
/// </summary>
public static class OrderChart
{
    public const int CandlesShown = 60;

    private const double W = 720, H = 400, Left = 8, Right = 84, Top = 50, Bottom = 22;
    private const string Up = "#26a69a", Down = "#ef5350", Entry = "#1e66f5", Stop = "#e11d48", Pause = "#f59e0b", Grid = "#e5e7eb", Muted = "#6b7280";

    /// <summary>"GBPJPY-H1-202611031500-M-1035054512.svg": chart, pause start, main/reversal, order ID.</summary>
    public static string FileName(OrderPicture p) =>
        $"{p.Symbol}-{p.Timeframe}-{p.PauseStartUtc:yyyyMMddHHmm}-{(p.Reversal ? "R" : "M")}-{p.OrderId}.svg";

    public static string Svg(OrderPicture p, IReadOnlyList<Candle> candles)
    {
        var len = Timeframes.Length(p.Timeframe);
        var shown = candles.Where(c => c.OpenUtc + len <= p.PlacedUtc).TakeLast(CandlesShown).ToList();
        if (shown.Count == 0)
            throw new ArgumentException($"No closed {p.Symbol} {p.Timeframe} candles before {p.PlacedUtc:u}");

        var after = Math.Max(1, (int)Math.Ceiling((p.ExpiresUtc - p.PlacedUtc) / len));
        var slot = (W - Left - Right) / (shown.Count + after);
        var placedX = Left + shown.Count * slot;
        var endX = Left + (shown.Count + after) * slot;

        var hi = new[] { shown.Max(c => c.High), p.Level, p.StopLoss ?? p.Level }.Max();
        var lo = new[] { shown.Min(c => c.Low), p.Level, p.StopLoss ?? p.Level }.Min();
        var pad = hi > lo ? (hi - lo) * 0.06 : p.PipSize * 10;
        hi += pad;
        lo -= pad;
        double Y(double price) => Top + (hi - price) / (hi - lo) * (H - Top - Bottom);

        var side = $"{(p.Dir == 1 ? "Buy" : "Sell")} Stop";
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\" font-family=\"DejaVu Sans, Arial, sans-serif\">\n");
        sb.Append($"<title>{Esc($"{p.Symbol} {p.Timeframe} {side} {Price(p.Level, p)} — {p.Broker} order {p.OrderId}")}</title>\n");
        sb.Append($"<rect width=\"{W}\" height=\"{H}\" fill=\"#ffffff\"/>\n");
        sb.Append($"<rect x=\"{N(placedX)}\" y=\"{N(Top)}\" width=\"{N(endX - placedX)}\" height=\"{N(H - Top - Bottom)}\" fill=\"#f3f4f6\"/>\n");

        // Price grid with labels in the right margin; labels that would sit on the order's own tags are left out.
        var tags = new List<double> { Y(p.Level) };
        if (p.StopLoss is { } slTag)
            tags.Add(Y(slTag));
        var step = NiceStep((hi - lo) / 7);
        for (var v = Math.Ceiling(lo / step) * step; v <= hi; v += step)
        {
            var y = Y(v);
            sb.Append($"<line x1=\"{N(Left)}\" x2=\"{N(endX)}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{Grid}\"/>\n");
            if (tags.All(t => Math.Abs(t - y) > 11))
                sb.Append($"<text x=\"{N(endX + 6)}\" y=\"{N(y + 4)}\" font-size=\"11\" fill=\"{Muted}\">{Price(v, p)}</text>\n");
        }

        // A line and the date at each new day; a date too close to the next one (a short day before a weekend gap) is left out.
        var days = Enumerable.Range(1, Math.Max(0, shown.Count - 1)).Where(i => shown[i].OpenUtc.Date != shown[i - 1].OpenUtc.Date).ToList();
        for (var k = 0; k < days.Count; k++)
        {
            var x = Left + days[k] * slot;
            sb.Append($"<line x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"{N(Top)}\" y2=\"{N(H - Bottom)}\" stroke=\"{Grid}\"/>\n");
            if (k + 1 < days.Count && (days[k + 1] - days[k]) * slot < 48)
                continue;
            sb.Append($"<text x=\"{N(x + 3)}\" y=\"{N(H - 7)}\" font-size=\"11\" fill=\"{Muted}\">{shown[days[k]].OpenUtc.ToString("dd MMM", CultureInfo.InvariantCulture)}</text>\n");
        }

        // The pause: from its first candle to the moment of the order, between its high and low.
        var ps = shown.FindIndex(c => c.OpenUtc >= p.PauseStartUtc);
        var pauseX = ps < 0 ? placedX : Left + ps * slot;
        if (ps >= 0)
        {
            var pause = shown.Skip(ps).ToList();
            var (top, bottom) = (Y(pause.Max(c => c.High)), Y(pause.Min(c => c.Low)));
            sb.Append($"<rect x=\"{N(pauseX)}\" y=\"{N(top)}\" width=\"{N(placedX - pauseX)}\" height=\"{N(Math.Max(1, bottom - top))}\" fill=\"{Pause}\" fill-opacity=\"0.15\" stroke=\"{Pause}\" stroke-opacity=\"0.6\"/>\n");
        }

        var body = Math.Max(1, slot * 0.66);
        for (var i = 0; i < shown.Count; i++)
        {
            var c = shown[i];
            var x = Left + (i + 0.5) * slot;
            var color = c.Close >= c.Open ? Up : Down;
            var (bt, bb) = (Y(Math.Max(c.Open, c.Close)), Y(Math.Min(c.Open, c.Close)));
            sb.Append($"<g class=\"candle\" fill=\"{color}\" stroke=\"{color}\"><line x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"{N(Y(c.High))}\" y2=\"{N(Y(c.Low))}\"/>"
                + $"<rect x=\"{N(x - body / 2)}\" y=\"{N(bt)}\" width=\"{N(body)}\" height=\"{N(Math.Max(1, bb - bt))}\" stroke=\"none\"/></g>\n");
        }

        // The moment of the order and its expiry.
        sb.Append($"<line x1=\"{N(placedX)}\" x2=\"{N(placedX)}\" y1=\"{N(Top)}\" y2=\"{N(H - Bottom)}\" stroke=\"{Muted}\" stroke-dasharray=\"2 3\"/>\n");
        sb.Append($"<text x=\"{N(placedX + 3)}\" y=\"{N(H - 7)}\" font-size=\"11\" fill=\"{Muted}\">placed {p.PlacedUtc.ToString("HH:mm", CultureInfo.InvariantCulture)}Z</text>\n");

        // The order: stop level, stop-loss, tags with their prices, and an arrow for the side.
        Level(sb, p.Level, pauseX, endX, Entry, $"{(p.Dir == 1 ? "Buy" : "Sell")} {Price(p.Level, p)}", Y(p.Level));
        if (p.StopLoss is { } sl)
        {
            var y = Y(sl);
            if (Math.Abs(y - Y(p.Level)) < 18)
                y = Y(p.Level) + (y >= Y(p.Level) ? 18 : -18);
            Level(sb, sl, pauseX, endX, Stop, $"SL {Price(sl, p)}", Y(sl), y);
        }
        var ay = Y(p.Level);
        var tip = p.Dir == 1 ? ay - 9 : ay + 9;
        sb.Append($"<path d=\"M{N(placedX + 4)} {N(ay)} L{N(placedX + 14)} {N(ay)} L{N(placedX + 9)} {N(tip)} Z\" fill=\"{Entry}\"/>\n");

        var risk = p.StopLoss is { } r ? $" · SL {Price(r, p)} ({(Math.Abs(p.Level - r) / p.PipSize).ToString("0.#", CultureInfo.InvariantCulture)} pips)" : " · no stop-loss";
        var title = $"{p.Symbol} {p.Timeframe} · {side} {Price(p.Level, p)}{risk} · {(p.Reversal ? "reversal stop" : "main stop")}";
        var higher = p.HigherTimeframe is null ? "" : $" · {p.HigherTimeframe} trend: {p.WithHigher}";
        var sub = $"{p.Broker} {p.AccountId} · placed {p.PlacedUtc.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)}Z · expires {p.ExpiresUtc.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)}Z{higher} · order {p.OrderId}";
        sb.Append($"<text x=\"{N(Left + 4)}\" y=\"20\" font-size=\"15\" font-weight=\"bold\" fill=\"#111827\">{Esc(title)}</text>\n");
        sb.Append($"<text x=\"{N(Left + 4)}\" y=\"38\" font-size=\"12\" fill=\"{Muted}\">{Esc(sub)}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void Level(StringBuilder sb, double price, double fromX, double toX, string color, string text, double y, double? tagY = null)
    {
        var ty = tagY ?? y;
        sb.Append($"<line x1=\"{N(fromX)}\" x2=\"{N(toX)}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{color}\" stroke-width=\"1.5\" stroke-dasharray=\"6 3\"/>\n");
        sb.Append($"<rect x=\"{N(toX + 2)}\" y=\"{N(ty - 8)}\" width=\"{N(Right - 4)}\" height=\"16\" rx=\"2\" fill=\"{color}\"/>\n");
        sb.Append($"<text x=\"{N(toX + 5)}\" y=\"{N(ty + 4)}\" font-size=\"10\" fill=\"#ffffff\">{Esc(text)}</text>\n");
    }

    // 1, 2, 2.5 or 5 × a power of ten: grid steps a person reads easily.
    private static double NiceStep(double raw)
    {
        if (!(raw > 0))
            return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
    }

    private static string Price(double v, OrderPicture p) => v.ToString("F" + p.Digits, CultureInfo.InvariantCulture);
    private static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    private static string Esc(string s) => SecurityElement.Escape(s);
}
