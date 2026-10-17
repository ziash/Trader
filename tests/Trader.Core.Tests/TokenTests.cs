using Trader.Core.Config;
using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

public sealed class TokenTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-tests-").FullName;
    private string SecretsPath => Path.Combine(_dir, "secrets.env");

    public TokenTests()
    {
        File.WriteAllLines(SecretsPath,
        [
            "# my secrets",
            "CTRADER_CLIENT_ID=id_1234567",
            "CTRADER_CLIENT_SECRET=secret-abcdef",
            "export CTRADER_ACCESS_TOKEN=old-access",
            "CTRADER_REFRESH_TOKEN=old-refresh",
            "CTRADER_ENV=demo",
        ]);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(SecretsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteTokens_replaces_tokens_keeps_other_lines_and_adds_expiry()
    {
        var updated = SecretsFile.Load(SecretsPath) with
        {
            AccessToken = "new-access",
            RefreshToken = "new-refresh",
            AccessTokenExpiresUtc = Now.AddDays(30),
        };

        SecretsFile.WriteTokens(SecretsPath, updated);

        var lines = File.ReadAllLines(SecretsPath);
        Assert.Equal("# my secrets", lines[0]);
        Assert.Contains("export CTRADER_ACCESS_TOKEN=new-access", lines);
        Assert.Contains("CTRADER_REFRESH_TOKEN=new-refresh", lines);
        Assert.Contains("CTRADER_ACCESS_TOKEN_EXPIRES_UTC=2026-11-16T12:00:00Z", lines);
        Assert.DoesNotContain(lines, l => l.Contains("old-"));

        var reloaded = SecretsFile.Load(SecretsPath);
        Assert.Equal(updated, reloaded);
        Assert.Equal(DateTimeKind.Utc, reloaded.AccessTokenExpiresUtc!.Value.Kind);
        Assert.Single(Directory.GetFiles(_dir)); // no temp file left behind
    }

    [Fact]
    public void WriteTokens_keeps_file_private()
    {
        if (OperatingSystem.IsWindows())
            return;

        SecretsFile.WriteTokens(SecretsPath, SecretsFile.Load(SecretsPath) with { AccessToken = "x-access" });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SecretsPath));
    }

    [Fact]
    public void Bad_expiry_value_is_rejected() =>
        Assert.Throws<SecretsException>(() => SecretsFile.ParseExpiry("next month"));

    [Fact]
    public async Task Lock_is_exclusive_until_released()
    {
        using (await SecretsFile.LockAsync(SecretsPath, TimeSpan.FromSeconds(1)))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => SecretsFile.LockAsync(SecretsPath, TimeSpan.FromMilliseconds(300)));
        }

        using var again = await SecretsFile.LockAsync(SecretsPath, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(null, true)]   // expiry unknown
    [InlineData(30, false)]
    [InlineData(8, false)]
    [InlineData(6, true)]
    [InlineData(-1, true)]     // already expired
    public void NeedsRefresh_when_unknown_or_within_7_days(int? daysLeft, bool expected)
    {
        var s = SecretsFile.Load(SecretsPath) with { AccessTokenExpiresUtc = daysLeft is { } d ? Now.AddDays(d) : null };
        Assert.Equal(expected, TokenRefresher.NeedsRefresh(s, Now));
    }

    [Fact]
    public async Task Refresh_sends_current_refresh_token_and_saves_new_pair()
    {
        var current = SecretsFile.Load(SecretsPath);
        string? sent = null;

        var (result, refreshed) = await TokenRefresher.RefreshAsync(SecretsPath, current, rt =>
        {
            sent = rt;
            return Task.FromResult(new TokenPair("new-access", "new-refresh", TimeSpan.FromDays(30)));
        }, () => Now);

        Assert.True(refreshed);
        Assert.Equal("old-refresh", sent);
        Assert.Equal("new-access", result.AccessToken);
        Assert.Equal(Now.AddDays(30), result.AccessTokenExpiresUtc);
        Assert.Equal(result, SecretsFile.Load(SecretsPath));
    }

    [Fact]
    public async Task Refresh_reuses_tokens_already_refreshed_by_another_instance()
    {
        var stale = SecretsFile.Load(SecretsPath);
        var other = stale with { AccessToken = "other-access", RefreshToken = "other-refresh", AccessTokenExpiresUtc = Now.AddDays(30) };
        SecretsFile.WriteTokens(SecretsPath, other);

        var (result, refreshed) = await TokenRefresher.RefreshAsync(SecretsPath, stale,
            _ => throw new InvalidOperationException("must not send a refresh request"), () => Now);

        Assert.False(refreshed);
        Assert.Equal(other, result);
    }

    [Fact]
    public async Task Failed_refresh_leaves_file_unchanged()
    {
        var before = File.ReadAllText(SecretsPath);

        await Assert.ThrowsAsync<TimeoutException>(() => TokenRefresher.RefreshAsync(SecretsPath, SecretsFile.Load(SecretsPath),
            _ => throw new TimeoutException(), () => Now));

        Assert.Equal(before, File.ReadAllText(SecretsPath));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(50, 60)]
    public void Backoff_doubles_up_to_60_s_with_at_most_20_percent_jitter(int attempt, double baseSeconds)
    {
        var delay = Backoff.Delay(attempt, new Random(attempt)).TotalSeconds;
        Assert.InRange(delay, baseSeconds, baseSeconds * 1.2);
    }
}
