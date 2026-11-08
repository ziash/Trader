namespace Trader.Core.OpenApi;

/// <summary>
/// Spaces requests at least <c>interval</c> apart, in the order they ask (open-code-pieces.md §3). Used for historical
/// candles: Spotware allows 5 such requests per second per connection; 2 per second keeps well clear of it, also on a
/// live account where breaking the limit could get the connection cut.
/// </summary>
public sealed class RequestPacer(TimeSpan interval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;

    public TimeSpan Interval => interval;

    /// <summary>Returns when the caller may send its request.</summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (_next > now)
                await Task.Delay(_next - now, ct);
            _next = DateTime.UtcNow + interval; // from the real moment: a late wake-up must not shorten the next gap
        }
        finally
        {
            _gate.Release();
        }
    }
}
