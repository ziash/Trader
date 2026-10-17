namespace Trader.Core.OpenApi;

/// <summary>Wait time before reconnect attempt n: 1, 2, 4, 8, 16, 32, then 60 s, plus up to 20 % random jitter
/// so that several instances do not all reconnect in the same second.</summary>
public static class Backoff
{
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    public static TimeSpan Delay(int attempt, Random? random = null)
    {
        var baseSeconds = Math.Min(Max.TotalSeconds, Math.Pow(2, Math.Clamp(attempt, 0, 10)));
        var jitter = (random ?? Random.Shared).NextDouble() * 0.2 * baseSeconds;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
