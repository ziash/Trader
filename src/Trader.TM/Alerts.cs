using Trader.Core.MarketData;
using Trader.Core.Notifications;

namespace Trader.TM;

/// <summary>A pop-up <see cref="Alerts"/> wants sent.</summary>
internal sealed record Alert(NotificationLevel Level, string Event, string Title, string Message);

/// <summary>
/// TM's time and connection pop-ups (tm.md §6), beside the account loops: a warning <c>AlertMinutesBefore</c> before
/// each NFP/CPI release (news-releases.json) and before the Friday close (17:00 New York), listing the open TM trades;
/// a critical pop-up when the connection has been down for <c>DisconnectAlertSeconds</c>, and a warning when it is
/// back. No automatic action. Each news / Friday pop-up is sent once, also when TM starts inside the lead time.
/// </summary>
internal sealed class Alerts(TmConfig config, IReadOnlyList<NewsRelease> news, Func<DateTime?> downSince, Func<IReadOnlyList<string>> openTrades)
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeSpan FridayClose = TimeSpan.FromHours(17);

    private readonly TimeSpan _lead = TimeSpan.FromMinutes(config.AlertMinutesBefore);
    private readonly TimeSpan _downAfter = TimeSpan.FromSeconds(config.DisconnectAlertSeconds);
    private readonly HashSet<DateTime> _sent = [];
    private DateTime? _downAlerted;

    public async Task RunAsync(Notifier notify, CancellationToken ct)
    {
        while (true)
        {
            foreach (var a in Due(DateTime.UtcNow))
                notify.Send(a.Level, a.Event, a.Title, a.Message);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>The pop-ups due at <paramref name="now"/> (UTC); each is returned once.</summary>
    public List<Alert> Due(DateTime now)
    {
        var due = new List<Alert>();

        foreach (var r in news)
        {
            var at = r.Utc;
            if (now >= at - _lead && now < at && _sent.Add(at))
                due.Add(new Alert(NotificationLevel.Warning, "NewsSoon",
                    $"{r.Event} in {Minutes(at - now)} min ({r.NewYorkTime:HH:mm} New York)", Trades("A jump at the release")));
        }

        var ny = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), NewYork);
        if (ny.DayOfWeek == DayOfWeek.Friday)
        {
            var close = TimeZoneInfo.ConvertTimeToUtc(ny.Date + FridayClose, NewYork);
            if (now >= close - _lead && now < close && _sent.Add(close))
                due.Add(new Alert(NotificationLevel.Warning, "WeekendSoon",
                    $"Friday close in {Minutes(close - now)} min (17:00 New York)", Trades("A weekend gap")));
        }

        var down = downSince();
        if (down is { } since && now - since >= _downAfter && _downAlerted is null)
        {
            _downAlerted = since;
            due.Add(new Alert(NotificationLevel.Critical, "Disconnected", "TM disconnected",
                $"No connection to cTrader since {since:HH:mm:ss}Z; TM keeps trying. Positions keep their hedges and stops " +
                "on the server, but nothing is managed (no mirrors, re-hedges, break-even or 3R close) until it is back."));
        }
        else if (down is null && _downAlerted is { } was)
        {
            _downAlerted = null;
            due.Add(new Alert(NotificationLevel.Warning, "Reconnected", "TM connected again",
                $"Down since {was:HH:mm:ss}Z ({Duration(now - was)}); managing again."));
        }

        return due;
    }

    private string Trades(string what)
    {
        var trades = openTrades();
        return trades.Count == 0
            ? "No open TM trades."
            : $"Open TM trades:\n{string.Join("\n", trades)}\n{what} can fill hedges beyond H (slippage is logged); TM takes no action.";
    }

    private static int Minutes(TimeSpan t) => (int)Math.Ceiling(t.TotalMinutes);

    private static string Duration(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{Minutes(t)} min";
}
