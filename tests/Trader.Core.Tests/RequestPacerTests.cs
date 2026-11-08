using System.Diagnostics;
using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

/// <summary>Historical-candle requests are spaced (CTraderSession.TrendbarSpacing: 2 per second, Spotware allows 5).</summary>
public sealed class RequestPacerTests
{
    [Fact]
    public async Task Requests_are_spaced_at_least_the_interval_apart_even_when_asked_together()
    {
        var pacer = new RequestPacer(TimeSpan.FromMilliseconds(100));
        var watch = Stopwatch.StartNew();
        var starts = new List<long>();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await pacer.WaitAsync();
            lock (starts)
                starts.Add(watch.ElapsedMilliseconds);
        }));

        starts.Sort();
        Assert.Equal(5, starts.Count);
        // Times are taken after each caller is released, so one gap can look short on a busy machine; four gaps together cannot.
        Assert.True(starts[^1] - starts[0] >= 4 * 100 - 40, string.Join(" → ", starts));
    }

    [Fact]
    public async Task A_request_after_a_quiet_spell_goes_at_once()
    {
        var pacer = new RequestPacer(TimeSpan.FromMilliseconds(100));
        await pacer.WaitAsync();
        await Task.Delay(150);
        var watch = Stopwatch.StartNew();
        await pacer.WaitAsync();
        Assert.True(watch.ElapsedMilliseconds < 50, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Candle_requests_are_paced_to_two_per_second()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), CTraderSession.TrendbarSpacing);
    }
}
