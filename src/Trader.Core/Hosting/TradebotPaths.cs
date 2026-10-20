namespace Trader.Core.Hosting;

/// <summary>
/// Runtime folders outside the repo (shared.md §5): ~/tradebot/{config,run/instances,logs,reports}.
/// TRADEBOT_HOME overrides the root (used by tests).
/// </summary>
public static class TradebotPaths
{
    public static string Root =>
        Environment.GetEnvironmentVariable("TRADEBOT_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "tradebot");

    public static string Config => Path.Combine(Root, "config");
    public static string Instances => Path.Combine(Root, "run", "instances");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Reports => Path.Combine(Root, "reports");
}
