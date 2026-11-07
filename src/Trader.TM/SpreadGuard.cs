using Trader.Core.MarketData;

namespace Trader.TM;

/// <summary>A change of a symbol's spread guard worth a log line: "on", "off", or "long" (it ran out after the maximum time).</summary>
internal sealed record GuardChange(string Kind, string Reason);

/// <summary>
/// The spread guard (tm.md §5.2): per symbol, the usual spread — the median of the spreads seen in the last 2 hours,
/// one every 10 s — and whether TM's own reactions to the price wait. They wait while the spread is more than
/// <c>spreadGuardFactor</c> × the usual one (for at most <c>spreadGuardMaxMinutes</c>), and always inside the rollover
/// window: from <c>rolloverBeforeMinutes</c> before a close to <c>rolloverAfterMinutes</c> after a reopening in the
/// broker's trading hours (the daily break, the weekend). Symbols traded around the clock have no window.
/// </summary>
internal sealed class SpreadGuard(TmConfig config)
{
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(2);
    private static readonly TimeSpan WindowEvery = TimeSpan.FromSeconds(30); // the rollover window is worked out this often
    private const int MinSamples = 30;                                        // 5 minutes of samples before a usual spread is known

    private sealed class State
    {
        public readonly Queue<(DateTime Time, double Spread)> Samples = new();
        public DateTime LastSample = DateTime.MinValue, WindowChecked = DateTime.MinValue;
        public bool InWindow, Guarded, Expired;
        public DateTime? WideSince;
    }

    private readonly Dictionary<long, State> _states = [];

    public bool IsGuarded(long symbolId) => _states.TryGetValue(symbolId, out var s) && s.Guarded;

    /// <summary>The median spread of the last 2 hours; null until 5 minutes of prices were seen.</summary>
    public double? Usual(long symbolId)
    {
        if (!_states.TryGetValue(symbolId, out var s) || s.Samples.Count < MinSamples)
            return null;
        var sorted = s.Samples.Select(x => x.Spread).Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>Takes the latest price and works out the symbol's guard; returns a change to log, or null.</summary>
    public GuardChange? Update(long symbolId, Quote? quote, TradingSchedule schedule, DateTime now)
    {
        if (!_states.TryGetValue(symbolId, out var s))
            _states[symbolId] = s = new State();

        if (quote is { Spread: > 0 } q && now - s.LastSample >= SampleEvery)
        {
            s.Samples.Enqueue((now, q.Spread));
            s.LastSample = now;
            while (s.Samples.Count > 0 && now - s.Samples.Peek().Time > KeepFor)
                s.Samples.Dequeue();
        }
        if (now - s.WindowChecked >= WindowEvery)
        {
            s.WindowChecked = now;
            s.InWindow = !schedule.IsOpenThroughout(now - TimeSpan.FromMinutes(config.RolloverAfterMinutes), now + TimeSpan.FromMinutes(config.RolloverBeforeMinutes));
        }

        var usual = Usual(symbolId);
        var wide = quote is { } p && usual is { } u && u > 0 && p.Spread > config.SpreadGuardFactor * u;
        if (wide)
            s.WideSince ??= now;
        else
            (s.WideSince, s.Expired) = (null, false);
        var wideGuard = s.WideSince is { } since && now - since < TimeSpan.FromMinutes(config.SpreadGuardMaxMinutes);

        var was = s.Guarded;
        s.Guarded = s.InWindow || wideGuard;
        var reason = s.InWindow
            ? $"rollover window ({config.RolloverBeforeMinutes} min before a close to {config.RolloverAfterMinutes} min after a reopening)"
            : $"spread {quote?.Spread:0.#####} is {(quote?.Spread ?? 0) / (usual ?? 1):0.#}× the usual {usual:0.#####}";
        if (s.Guarded && !was)
            return new GuardChange("on", reason);
        if (!s.Guarded && was)
        {
            if (wide && !s.Expired)
            {
                s.Expired = true;
                return new GuardChange("long", $"spread still {(quote?.Spread ?? 0) / (usual ?? 1):0.#}× the usual after {config.SpreadGuardMaxMinutes} min");
            }
            return new GuardChange("off", "spread back to normal");
        }
        return null;
    }
}
