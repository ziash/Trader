using System.Globalization;
using System.Security;
using System.Text;
using Trader.Core.MarketData;

namespace Trader.DashboardExport;

/// <summary>A level that changes over time: from each point's time on, the price (null = none, e.g. stop-loss removed).</summary>
public sealed record Step(DateTime Utc, double? Price);

/// <summary>What one trade picture shows (dashboard.md §2.3, §3.3).</summary>
public sealed record TradePicture(
    string Broker, long AccountId, string Trade, string Owner, string Symbol, int Digits, string Timeframe,
    DateTime StartUtc, DateTime? ClosedUtc, double? EntryLevel, IReadOnlyList<Step> StopLoss, IReadOnlyList<Step> Hedge,
    IReadOnlyList<DealRow> Deals, double ResultUsd, bool Open);

/// <summary>
/// Draws one trade from shortly before its order to its close (or now): candles, the main's entry level, its stop-loss and the
/// hedge level H as they changed, and a marker at every fill. Same look as Scout's order pictures (Scout OrderChart): SVG,
/// '.' as decimal point whatever the machine's language, white background so it reads in both themes.
/// </summary>
public static class TradeChart
{
    public const int Before = 12, After = 6;

    private const double W = 900, H = 440, Left = 8, Right = 92, Top = 52, Bottom = 22;
    private const string Up = "#26a69a", Down = "#ef5350", Entry = "#1e66f5", Stop = "#e11d48", Hedge = "#f59e0b", Grid = "#e5e7eb", Muted = "#6b7280", Ink = "#111827";

    public static string FileName(string trade) => $"trade-{trade}.svg";

    /// <summary>1H, or 4H for a trade that spans more than 4 days.</summary>
    public static string TimeframeFor(DateTime startUtc, DateTime endUtc) => endUtc - startUtc > TimeSpan.FromDays(4) ? "H4" : "H1";

    public static TimeSpan Length(string timeframe) => timeframe == "H4" ? TimeSpan.FromHours(4) : TimeSpan.FromHours(1);

    public static string Svg(TradePicture p, IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
            throw new ArgumentException($"No {p.Symbol} {p.Timeframe} candles for {p.Trade}");
        var len = Length(p.Timeframe);
        var slot = (W - Left - Right) / candles.Count;

        double X(DateTime t)
        {
            var i = candles.Count - 1;
            while (i > 0 && candles[i].OpenUtc > t)
                i--;
            var frac = Math.Clamp((t - candles[i].OpenUtc) / len, 0, 1);
            return Left + (i + frac) * slot;
        }

        var prices = candles.SelectMany(c => new[] { c.High, c.Low })
            .Concat(p.Deals.Select(d => d.Price))
            .Concat(p.StopLoss.Concat(p.Hedge).Where(s => s.Price is not null).Select(s => s.Price!.Value))
            .Concat(p.EntryLevel is { } e ? [e] : []).ToList();
        var (hi, lo) = (prices.Max(), prices.Min());
        var pad = hi > lo ? (hi - lo) * 0.06 : Math.Max(Math.Abs(hi) * 0.001, 1e-5);
        hi += pad;
        lo -= pad;
        double Y(double price) => Top + (hi - price) / (hi - lo) * (H - Top - Bottom);
        var endX = Left + candles.Count * slot;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" viewBox=\"0 0 {W} {H}\" font-family=\"DejaVu Sans, Arial, sans-serif\">\n");
        sb.Append($"<title>{Esc($"{p.Symbol} {p.Timeframe} trade {p.Trade} — {p.Broker} {p.AccountId}")}</title>\n");
        sb.Append($"<rect width=\"{W}\" height=\"{H}\" fill=\"#ffffff\"/>\n");

        // The trade's time on the chart, shaded: from the order (or first fill) to the close, or to the right edge while open.
        var (fromX, toX) = (X(p.StartUtc), p.ClosedUtc is { } closed ? X(closed) : endX);
        sb.Append($"<rect x=\"{N(fromX)}\" y=\"{N(Top)}\" width=\"{N(Math.Max(1, toX - fromX))}\" height=\"{N(H - Top - Bottom)}\" fill=\"#f3f4f6\"/>\n");

        var step = NiceStep((hi - lo) / 7);
        for (var v = Math.Ceiling(lo / step) * step; v <= hi; v += step)
        {
            var y = Y(v);
            sb.Append($"<line x1=\"{N(Left)}\" x2=\"{N(endX)}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{Grid}\"/>\n");
            sb.Append($"<text x=\"{N(endX + 6)}\" y=\"{N(y + 4)}\" font-size=\"11\" fill=\"{Muted}\">{Price(v, p)}</text>\n");
        }

        var days = Enumerable.Range(1, Math.Max(0, candles.Count - 1)).Where(i => candles[i].OpenUtc.Date != candles[i - 1].OpenUtc.Date).ToList();
        for (var k = 0; k < days.Count; k++)
        {
            var x = Left + days[k] * slot;
            sb.Append($"<line x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"{N(Top)}\" y2=\"{N(H - Bottom)}\" stroke=\"{Grid}\"/>\n");
            if (k + 1 < days.Count && (days[k + 1] - days[k]) * slot < 48)
                continue;
            sb.Append($"<text x=\"{N(x + 3)}\" y=\"{N(H - 7)}\" font-size=\"11\" fill=\"{Muted}\">{candles[days[k]].OpenUtc.ToString("dd MMM", CultureInfo.InvariantCulture)}</text>\n");
        }

        var body = Math.Max(1, slot * 0.66);
        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            var x = Left + (i + 0.5) * slot;
            var color = c.Close >= c.Open ? Up : Down;
            var (bt, bb) = (Y(Math.Max(c.Open, c.Close)), Y(Math.Min(c.Open, c.Close)));
            sb.Append($"<g class=\"candle\" fill=\"{color}\" stroke=\"{color}\"><line x1=\"{N(x)}\" x2=\"{N(x)}\" y1=\"{N(Y(c.High))}\" y2=\"{N(Y(c.Low))}\"/>"
                + $"<rect x=\"{N(x - body / 2)}\" y=\"{N(bt)}\" width=\"{N(body)}\" height=\"{N(Math.Max(1, bb - bt))}\" stroke=\"none\"/></g>\n");
        }

        // Levels: the order's entry level until the first fill, then the stop-loss and H as steps.
        var tags = new List<(double Y, string Color, string Text)>();
        var firstFill = p.Deals.Where(d => !d.Close).Select(d => d.TimeUtc).DefaultIfEmpty(p.StartUtc).Min();
        if (p.EntryLevel is { } entry)
        {
            var y = Y(entry);
            sb.Append($"<line class=\"entry\" x1=\"{N(fromX)}\" x2=\"{N(Math.Max(X(firstFill), fromX + 4))}\" y1=\"{N(y)}\" y2=\"{N(y)}\" stroke=\"{Entry}\" stroke-width=\"1.5\" stroke-dasharray=\"6 3\"/>\n");
            tags.Add((y, Entry, $"Entry {Price(entry, p)}"));
        }
        StepLine(sb, "sl", p.StopLoss, Stop, "SL", toX, X, Y, tags, p);
        StepLine(sb, "hedge", p.Hedge, Hedge, "H", toX, X, Y, tags, p);

        // Fills of the same kind within a minute and a few pixels of each other (main and mirror fill together, as do both
        // hedges) share one marker, tagged with all their roles.
        var groups = new List<List<DealRow>>();
        foreach (var d in p.Deals.OrderBy(d => d.TimeUtc))
        {
            var g = groups.LastOrDefault(g => g[0].Close == d.Close && g[0].Side == d.Side && d.TimeUtc - g[0].TimeUtc < TimeSpan.FromMinutes(1)
                && Math.Abs(Y(g[0].Price) - Y(d.Price)) < 4);
            if (g is null)
                groups.Add([d]);
            else
                g.Add(d);
        }
        foreach (var g in groups)
        {
            var d = g[0];
            var (x, y) = (X(d.TimeUtc), Y(d.Price));
            var color = d.Close ? Ink : d.Side == "buy" ? Up : Down;
            var shape = d.Close
                ? $"<path d=\"M{N(x - 4)} {N(y - 4)} L{N(x + 4)} {N(y + 4)} M{N(x - 4)} {N(y + 4)} L{N(x + 4)} {N(y - 4)}\" stroke=\"{color}\" stroke-width=\"2\"/>"
                : d.Side == "buy"
                    ? $"<path d=\"M{N(x)} {N(y - 6)} L{N(x + 5)} {N(y + 3)} L{N(x - 5)} {N(y + 3)} Z\" fill=\"{color}\" stroke=\"#ffffff\"/>"
                    : $"<path d=\"M{N(x)} {N(y + 6)} L{N(x + 5)} {N(y - 3)} L{N(x - 5)} {N(y - 3)} Z\" fill=\"{color}\" stroke=\"#ffffff\"/>";
            var what = string.Join("\n", g.Select(f =>
                $"{f.Link.Role} {(f.Close ? "close" : f.Side)} {f.Lots.ToString("0.##", CultureInfo.InvariantCulture)} lot at {Price(f.Price, p)}, {f.TimeUtc.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture)}Z"
                + (f.Close ? $", net {Usd(f.Net)}" : "")));
            var roles = string.Join("+", g.Select(f => f.Link.Role).Distinct());
            sb.Append($"<g class=\"fill\"><title>{Esc(what)}</title>{shape}<text x=\"{N(x + 6)}\" y=\"{N(d.Close || d.Side == "sell" ? y + 14 : y - 8)}\" font-size=\"9\" fill=\"{Muted}\">{Esc(roles)}</text></g>\n");
        }

        // Price tags in the right margin, nudged apart so none hides another.
        double last = double.NegativeInfinity;
        foreach (var (ty, color, text) in tags.OrderBy(t => t.Y))
        {
            var y = Math.Max(ty, last + 17);
            last = y;
            sb.Append($"<rect x=\"{N(endX + 2)}\" y=\"{N(y - 8)}\" width=\"{N(Right - 4)}\" height=\"16\" rx=\"2\" fill=\"{color}\"/>\n");
            sb.Append($"<text x=\"{N(endX + 5)}\" y=\"{N(y + 4)}\" font-size=\"10\" fill=\"#ffffff\">{Esc(text)}</text>\n");
        }

        var state = p.Open ? "open" : "closed " + p.ClosedUtc!.Value.ToString("dd MMM HH:mm", CultureInfo.InvariantCulture) + "Z";
        var title = $"{p.Symbol} {p.Timeframe} · trade {p.Trade} ({p.Owner}) · {(p.Open ? "result so far" : "result")} {Usd(p.ResultUsd)}";
        var sub = $"{p.Broker} {p.AccountId} · from {p.StartUtc.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)}Z · {state} · "
            + "▲ buy ▼ sell ✕ close · blue entry, red stop-loss, amber hedge level H";
        sb.Append($"<text x=\"{N(Left + 4)}\" y=\"20\" font-size=\"15\" font-weight=\"bold\" fill=\"{Ink}\">{Esc(title)}</text>\n");
        sb.Append($"<text x=\"{N(Left + 4)}\" y=\"38\" font-size=\"12\" fill=\"{Muted}\">{Esc(sub)}</text>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void StepLine(StringBuilder sb, string cls, IReadOnlyList<Step> steps, string color, string name, double endX,
        Func<DateTime, double> x, Func<double, double> y, List<(double, string, string)> tags, TradePicture p)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Price is not { } price)
                continue;
            var x1 = x(steps[i].Utc);
            var x2 = i + 1 < steps.Count ? x(steps[i + 1].Utc) : endX;
            sb.Append($"<line class=\"{cls}\" x1=\"{N(x1)}\" x2=\"{N(Math.Max(x2, x1 + 2))}\" y1=\"{N(y(price))}\" y2=\"{N(y(price))}\" stroke=\"{color}\" stroke-width=\"1.5\" stroke-dasharray=\"6 3\"/>\n");
        }
        // A tag for the level as it stood at the end; none when it was removed (e.g. the stop-loss once a hedge took over).
        if (steps.Count > 0 && steps[^1].Price is { } lastPrice)
            tags.Add((y(lastPrice), color, $"{name} {Price(lastPrice, p)}"));
    }

    /// <summary>Removes points that repeat the level before them, so a hedge placed again at the same H is one line.</summary>
    public static List<Step> Compact(IEnumerable<Step> steps)
    {
        var list = new List<Step>();
        foreach (var s in steps.OrderBy(s => s.Utc))
            if (list.Count == 0 || list[^1].Price != s.Price)
                list.Add(s);
        return list;
    }

    private static double NiceStep(double raw)
    {
        if (!(raw > 0))
            return 1;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var f = raw / mag;
        return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
    }

    public static string Usd(double v) => (v < 0 ? "−$" : "+$") + Math.Abs(v).ToString("N2", CultureInfo.InvariantCulture);
    private static string Price(double v, TradePicture p) => v.ToString("F" + p.Digits, CultureInfo.InvariantCulture);
    private static string N(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    private static string Esc(string s) => SecurityElement.Escape(s);
}
