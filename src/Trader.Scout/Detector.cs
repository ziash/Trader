using Trader.Core.MarketData;

namespace Trader.Scout;

/// <summary>Where the order level sits on the pause candles (Scout spec §2.1 rule 4, §3.2 "Level at").</summary>
public enum LevelMode { Smart, Wicks, Bodies }

/// <summary>The setup settings of Scout spec §3.2, with the same defaults as Scout_Pine.</summary>
public sealed record DetectorSettings
{
    public double PushBody { get; init; } = 0.6;        // strong candle: body ≥ × ATR
    public double MinMove { get; init; } = 1.5;         // move before a pause ≥ × ATR
    public int MinPause { get; init; } = 2;             // pause: min candles (the stop is placed from this one)
    public int MaxPause { get; init; } = 16;            // pause: max candles
    public double MaxPauseHeight { get; init; } = 3.0;  // pause: max height × ATR
    public double MaxRetrace { get; init; } = 0.6;      // pause: max pull-back of the move (0–1)
    public int ReversalAfter { get; init; } = 4;        // reversal stop: after this many pause candles
    public double FlipAtr { get; init; } = 3.0;         // trend turns: move against it ≥ × ATR ...
    public int Channel { get; init; } = 20;             // ... or a strong candle closes beyond the last N candles
    public int AtrLength { get; init; } = 14;
    public LevelMode Level { get; init; } = LevelMode.Smart;
    public double WickTolerance { get; init; } = 0.6;   // Smart: a wick longer than this × ATR counts at the body
    public double Offset { get; init; }                 // extra distance beyond the level, in price
}

public enum SetupEventKind
{
    NewStop,         // the stop in the trend's direction appears (2nd pause candle)
    StopMoved,       // its level changed on a later pause candle
    NewReversal,     // the pause rolls over: reversal stop on the other side
    ReversalMoved,
    StopFilled,      // a candle reached the stop
    ReversalFilled,  // a candle reached the reversal stop: the trend has turned
    Cancelled,       // a pending level ended without a fill (Reason says why)
    TrendTurned,     // the trend turned without a fill (step 3)
}

/// <summary>What the pause looked like when an event happened; null fields are not known yet.</summary>
public sealed record PauseInfo(int Candles, double High, double Low, double MoveAtr, double Pullback);

/// <summary>
/// One thing the detector saw on a closed candle. <see cref="Dir"/> is the side of the order it is about: +1 Buy Stop,
/// −1 Sell Stop (for TrendTurned: the new trend). <see cref="FarSide"/>: the level on the other side of the pause,
/// used as the order's stop-loss (Scout spec §4.2). <see cref="PauseStartUtc"/> identifies the setup.
/// </summary>
public sealed record SetupEvent(
    DateTime CandleUtc,
    SetupEventKind Kind,
    int Dir,
    double Level,
    double? FarSide,
    DateTime? PauseStartUtc,
    double Atr,
    PauseInfo? Pause,
    bool Reversal,
    string? Reason = null);

/// <summary>
/// Scout_Pine's rules (Scout spec §3.1, pine/Scout_Pine.pine v1.0) as a state machine fed one closed candle at a time,
/// oldest first. Same steps in the same order as the Pine script, so the levels match the chart; only closed candles
/// change the state. Never talks to a broker.
/// </summary>
public sealed class Detector(DetectorSettings? settings = null)
{
    private readonly DetectorSettings _p = settings ?? new DetectorSettings();
    private readonly List<Candle> _c = [];
    private readonly List<double?> _atr = [];   // ta.atr (RMA of the true range), per candle

    private int _trend;                // 1 up, -1 down, 0 not known yet
    private double _ext = double.NaN;  // trend's extreme since the current move began
    private double _moveStart = double.NaN;
    private int? _ps;                  // index of the first pause candle
    private double _pHi = double.NaN, _pLo = double.NaN;
    private double? _main, _mainFar;   // pending stop in the trend's direction (and its far side)
    private double? _ctr, _ctrFar;     // pending reversal stop
    private int? _mainAt, _ctrAt;      // index of the candle on which each level first appeared

    public DetectorSettings Settings => _p;
    public int Trend => _trend;
    public double? MainLevel => _main;
    public double? MainFarSide => _mainFar;
    public double? ReversalLevel => _ctr;
    public double? ReversalFarSide => _ctrFar;
    public DateTime? PauseStartUtc => _ps is { } ps ? _c[ps].OpenUtc : null;
    /// <summary>Open time of the candle on which the main level first appeared; its order's expiry counts from that candle's close.</summary>
    public DateTime? MainSinceUtc => _mainAt is { } i ? _c[i].OpenUtc : null;
    /// <summary>Open time of the candle on which the reversal level first appeared.</summary>
    public DateTime? ReversalSinceUtc => _ctrAt is { } i ? _c[i].OpenUtc : null;
    public DateTime? LastCandleUtc => _c.Count > 0 ? _c[^1].OpenUtc : null;
    public int Count => _c.Count;
    /// <summary>Every closed candle added so far, oldest first (for the order pictures, scout.md §3.1).</summary>
    public IReadOnlyList<Candle> Candles => _c;

    /// <summary>ATR of the last closed candle, or null while there are fewer than ATR-length candles.</summary>
    public double? Atr => _atr.Count > 0 ? _atr[^1] : null;

    /// <summary>Adds one closed candle (newer than the last one) and returns what happened on it.</summary>
    public IReadOnlyList<SetupEvent> Add(Candle candle)
    {
        if (_c.Count > 0 && candle.OpenUtc <= _c[^1].OpenUtc)
            throw new ArgumentException($"Candle {candle.OpenUtc:u} is not newer than {_c[^1].OpenUtc:u}");
        _c.Add(candle);
        _atr.Add(NextAtr());
        var events = new List<SetupEvent>();
        var i = _c.Count - 1;
        if (i == 0 || _atr[i - 1] is not { } atrPrev)
            return events;
        Step(i, atrPrev, events);
        return events;
    }

    // ta.atr(n) = ta.rma(true range, n): the first value is the plain average of the first n true ranges, then
    // rma = (previous × (n − 1) + true range) / n. The first candle's true range is its high − low.
    private double? NextAtr()
    {
        var i = _c.Count - 1;
        var n = _p.AtrLength;
        if (i < n - 1)
            return null;
        if (i == n - 1)
            return Enumerable.Range(0, n).Average(TrueRange);
        return (_atr[i - 1]!.Value * (n - 1) + TrueRange(i)) / n;
    }

    private double TrueRange(int i)
    {
        var c = _c[i];
        if (i == 0)
            return c.High - c.Low;
        var prev = _c[i - 1].Close;
        return Math.Max(c.High, prev) - Math.Min(c.Low, prev);
    }

    // pushUp / pushDn of candle j, each with that candle's own ATR of the previous candle (as the Pine series).
    private bool PushUp(int j) => j >= 1 && _atr[j - 1] is { } a && _c[j].Close - _c[j].Open >= _p.PushBody * a;
    private bool PushDn(int j) => j >= 1 && _atr[j - 1] is { } a && _c[j].Open - _c[j].Close >= _p.PushBody * a;

    private void Step(int i, double atr, List<SetupEvent> events)
    {
        var bar = _c[i];
        var (open, high, low, close) = (bar.Open, bar.High, bar.Low, bar.Close);

        SetupEvent Ev(SetupEventKind kind, int dir, double level, double? far, bool reversal, string? reason = null, PauseInfo? pause = null) =>
            new(bar.OpenUtc, kind, dir, level, far, PauseStartUtc, atr, pause, reversal, reason);

        // Ends both pending levels without a fill (Scout_Pine's finish(0, d) plus its "cancelled" alert).
        void CancelBoth(int dir, string reason)
        {
            if (_main is { } m)
                events.Add(Ev(SetupEventKind.Cancelled, dir, m, _mainFar, false, reason));
            if (_ctr is { } r)
                events.Add(Ev(SetupEventKind.Cancelled, -dir, r, _ctrFar, true, reason));
        }

        void Reset()
        {
            _ps = null;
            _main = _mainFar = _ctr = _ctrFar = null;
            _mainAt = _ctrAt = null;
        }

        var d = _trend;
        var hitMain = d != 0 && _main is { } mainLvl && (d == 1 ? high >= mainLvl : low <= mainLvl);
        var hitCtr = d != 0 && _ctr is { } ctrLvl && (d == 1 ? low <= ctrLvl : high >= ctrLvl);

        // 1. Pending orders filled on this candle (the main one first, as in the Pine script)
        if (hitMain)
        {
            events.Add(Ev(SetupEventKind.StopFilled, d, _main!.Value, _mainFar, false));
            if (_ctr is { } r)
                events.Add(Ev(SetupEventKind.Cancelled, -d, r, _ctrFar, true, "stop filled"));
            Reset();
        }
        else if (hitCtr)
        {
            events.Add(Ev(SetupEventKind.ReversalFilled, -d, _ctr!.Value, _ctrFar, true));
            if (_main is { } m)
                events.Add(Ev(SetupEventKind.Cancelled, d, m, _mainFar, false, "reversal stop filled"));
            _trend = -d;
            d = -d;
            _ext = d == 1 ? high : low;
            _moveStart = d == 1 ? _pLo : _pHi;
            Reset();
        }

        var body = close - open;
        var push = Math.Abs(body) >= _p.PushBody * atr;

        // 2. First trend: the first strong candle
        if (_trend == 0)
        {
            if (push)
            {
                _trend = body > 0 ? 1 : -1;
                _ext = _trend == 1 ? high : low;
                _moveStart = _trend == 1 ? low : high;
            }
            return;
        }

        d = _trend;
        _ext = d == 1 ? Math.Max(_ext, high) : Math.Min(_ext, low);
        var flipped = false;

        // 3. Trend turns: a strong move against it, or a strong candle breaking the recent range
        if (!(hitMain || hitCtr))
        {
            var against = 0;
            for (var j = Math.Max(0, i - 2); j <= i; j++)
                against += (d == 1 ? PushDn(j) : PushUp(j)) ? 1 : 0;
            var brk = false;
            if (i >= _p.Channel)
            {
                double chanHi = double.MinValue, chanLo = double.MaxValue;
                for (var j = i - _p.Channel; j < i; j++)
                {
                    chanHi = Math.Max(chanHi, _c[j].High);
                    chanLo = Math.Min(chanLo, _c[j].Low);
                }
                brk = d == 1 ? PushDn(i) && close < chanLo : PushUp(i) && close > chanHi;
            }
            if (((_ext - close) * d >= _p.FlipAtr * atr && against >= 2) || brk)
            {
                CancelBoth(d, "trend turned");
                events.Add(Ev(SetupEventKind.TrendTurned, -d, close, null, false));
                _trend = -d;
                d = -d;
                _moveStart = _ext;
                _ext = d == 1 ? high : low;
                Reset();
                flipped = true;
            }
        }

        // 4. The pause (the candle that filled an order belongs to the move, not to a pause)
        if (flipped || hitMain || hitCtr)
            return;

        // A new extreme before any order exists: the move is still going, restart the pause here.
        if (_ps is { } start && _main is null && i - start >= 1)
        {
            double prevHi = double.MinValue, prevLo = double.MaxValue;
            for (var j = start; j < i; j++)
            {
                prevHi = Math.Max(prevHi, _c[j].High);
                prevLo = Math.Min(prevLo, _c[j].Low);
            }
            if (d == 1 ? high > prevHi : low < prevLo)
                _ps = null;
        }

        if (_ps is null)
        {
            var withTrendPush = push && Math.Sign(body) == d;
            if (!withTrendPush && (_ext - _moveStart) * d >= _p.MinMove * atr)
                _ps = i;
        }
        if (_ps is not { } ps)
            return;

        var n = i - ps + 1;
        var half = n / 2;
        double wHi = high, wLo = low;
        double? upL = null, dnL = null, recHi = null, recLo = null, oldHi = null, oldLo = null;
        for (var k = 0; k < n; k++)
        {
            var c = _c[i - k];
            var bt = Math.Max(c.Open, c.Close);
            var bb = Math.Min(c.Open, c.Close);
            var u = _p.Level switch
            {
                LevelMode.Wicks => c.High,
                LevelMode.Bodies => bt,
                _ => c.High - bt > _p.WickTolerance * atr ? bt : c.High,
            };
            var w = _p.Level switch
            {
                LevelMode.Wicks => c.Low,
                LevelMode.Bodies => bb,
                _ => bb - c.Low > _p.WickTolerance * atr ? bb : c.Low,
            };
            wHi = Math.Max(wHi, c.High);
            wLo = Math.Min(wLo, c.Low);
            upL = upL is null ? u : Math.Max(upL.Value, u);
            dnL = dnL is null ? w : Math.Min(dnL.Value, w);
            if (k < half)
            {
                recHi = recHi is null ? c.High : Math.Max(recHi.Value, c.High);
                recLo = recLo is null ? c.Low : Math.Min(recLo.Value, c.Low);
            }
            else
            {
                oldHi = oldHi is null ? c.High : Math.Max(oldHi.Value, c.High);
                oldLo = oldLo is null ? c.Low : Math.Min(oldLo.Value, c.Low);
            }
        }

        var moveSize = Math.Max((_ext - _moveStart) * d, 1e-12);
        var retr = (_ext - (d == 1 ? wLo : wHi)) * d / moveSize;
        var info = new PauseInfo(n, wHi, wLo, moveSize / atr, retr);
        if (n > _p.MaxPause || wHi - wLo > _p.MaxPauseHeight * atr || retr > _p.MaxRetrace)
        {
            // Not a pause any more: cancel, and let the pull-back be the base of the next move.
            CancelBoth(d, "pause too long or too deep");
            _moveStart = d == 1 ? wLo : wHi;
            _ext = d == 1 ? high : low;
            Reset();
            return;
        }

        _pHi = wHi;
        _pLo = wLo;
        var above = upL!.Value + _p.Offset;
        var below = dnL!.Value - _p.Offset;

        // Stop in the trend's direction; its far side (the stop-loss) is the other side of the pause.
        if (n >= _p.MinPause)
        {
            var lvl = d == 1 ? above : below;
            if (_main is null)
                events.Add(Ev(SetupEventKind.NewStop, d, lvl, d == 1 ? below : above, false, pause: info));
            else if (lvl != _main)
                events.Add(Ev(SetupEventKind.StopMoved, d, lvl, d == 1 ? below : above, false, pause: info));
            _main = lvl;
            _mainAt ??= i;
            _mainFar = d == 1 ? below : above;
        }

        // Reversal stop once the pause rolls over (lower highs / higher lows)
        if (n >= _p.ReversalAfter && (d == 1 ? recHi < oldHi : recLo > oldLo))
        {
            var rl = d == 1 ? below : above;
            if (_ctr is null)
                events.Add(Ev(SetupEventKind.NewReversal, -d, rl, d == 1 ? above : below, true, pause: info));
            else if (rl != _ctr)
                events.Add(Ev(SetupEventKind.ReversalMoved, -d, rl, d == 1 ? above : below, true, pause: info));
            _ctr = rl;
            _ctrAt ??= i;
            _ctrFar = d == 1 ? above : below;
        }
    }
}
