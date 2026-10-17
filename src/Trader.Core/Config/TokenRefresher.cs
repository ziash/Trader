namespace Trader.Core.Config;

/// <summary>New token pair as returned by the Open API (ProtoOARefreshTokenRes).</summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, TimeSpan ExpiresIn);

/// <summary>
/// Renews the access token with the refresh token and saves the new pair.
///
/// Each refresh invalidates the old refresh token, so two instances must never refresh with the
/// same one. Under the secrets-file lock this re-reads the file first: if another instance has
/// already refreshed, its tokens are used and no request is sent.
/// </summary>
public static class TokenRefresher
{
    /// <summary>Refresh when the access token has less than this left (it lasts about 30 days).</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromDays(7);

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>True when the expiry is unknown (never refreshed by the bot) or within <see cref="RefreshMargin"/>.</summary>
    public static bool NeedsRefresh(TraderSecrets secrets, DateTime nowUtc) =>
        secrets.AccessTokenExpiresUtc is not { } expires || expires - nowUtc < RefreshMargin;

    /// <param name="current">The tokens this instance is using now.</param>
    /// <param name="refresh">Sends the refresh request (refresh token in, new pair out).</param>
    /// <returns>The tokens to use from now on, and whether this call refreshed them.</returns>
    public static async Task<(TraderSecrets Secrets, bool Refreshed)> RefreshAsync(
        string path,
        TraderSecrets current,
        Func<string, Task<TokenPair>> refresh,
        Func<DateTime> utcNow,
        CancellationToken ct = default)
    {
        using var _ = await SecretsFile.LockAsync(path, LockTimeout, ct);

        var onDisk = SecretsFile.Load(path);
        if (onDisk.RefreshToken != current.RefreshToken)
            return (onDisk, false); // another instance refreshed while we waited

        var pair = await refresh(onDisk.RefreshToken);
        var updated = onDisk with
        {
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            AccessTokenExpiresUtc = utcNow() + pair.ExpiresIn,
        };
        SecretsFile.WriteTokens(path, updated);
        return (updated, true);
    }
}
