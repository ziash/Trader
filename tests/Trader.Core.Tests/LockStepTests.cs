using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

/// <summary>The switch --lockstep (open-code-pieces.md §3, Rewind spec §7.2).</summary>
public sealed class LockStepTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lockstep-{Guid.NewGuid():N}.env");

    public void Dispose() => File.Delete(_path);

    private void Secrets(string environment)
    {
        string[] lines = [
            "# my secrets",
            "CTRADER_CLIENT_ID=id_1234567",
            "CTRADER_CLIENT_SECRET=secret-abcdef",
            "export CTRADER_ACCESS_TOKEN=old-access",
            "CTRADER_REFRESH_TOKEN=old-refresh",
            "CTRADER_ENV=demo",
        ];
        File.WriteAllLines(_path, lines.Select(l => l.StartsWith("CTRADER_ENV=", StringComparison.Ordinal) ? $"CTRADER_ENV={environment}" : l).Concat(environment == "replay" ? ["CTRADER_HOST=localhost", "CTRADER_PORT=5037"] : []));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Without_the_switch_there_is_no_lock_step()
    {
        Secrets("demo");
        Assert.Null(LockStep.FromArgs(["--dry-run"], _path));
    }

    [Fact]
    public void The_switch_is_refused_on_a_demo_account()
    {
        Secrets("demo");
        var ex = Assert.Throws<InvalidOperationException>(() => LockStep.FromArgs([LockStep.Option], _path));
        Assert.Contains("replay", ex.Message);
    }

    [Fact]
    public void A_turn_gives_the_replays_time()
    {
        Secrets("replay");
        var step = LockStep.FromArgs([LockStep.Option], _path)!;
        var at = new DateTime(2025, 1, 6, 10, 5, 0, DateTimeKind.Utc);
        var waiting = step.NextTurnAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        step.Handle(new ProtoOAVersionRes { Version = "2.0" }); // an ordinary reply is not a turn
        Assert.False(waiting.IsCompleted);

        step.Handle(new ProtoOAVersionRes { Version = $"turn {at.Ticks}" });
        Assert.True(waiting.IsCompleted);
        Assert.Equal(at, step.UtcNow);
    }
}
