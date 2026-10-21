namespace Trader.Core.Config;

/// <summary>Which cTrader Open API host to use. Live is refused until a go-live is decided (shared.md §4).</summary>
public enum ApiEnvironment
{
    Demo,

    /// <summary>A replay broker on this computer (Rewind, github.com/ziash/Rewind): host and port from the secrets
    /// file, localhost only (open-code-pieces.md §3). Never a real broker.</summary>
    Replay,
}

/// <summary>
/// Credentials read from ~/.config/trader/secrets.env. Never log these values;
/// use <see cref="Mask"/> when something must be shown.
/// </summary>
public sealed record TraderSecrets(
    string ClientId,
    string ClientSecret,
    string AccessToken,
    string RefreshToken,
    ApiEnvironment Environment,
    DateTime? AccessTokenExpiresUtc = null,
    string? Host = null,
    int Port = 0)
{
    /// <summary>The only hosts a <see cref="ApiEnvironment.Replay"/> connection may use.</summary>
    public static bool IsLocalHost(string? host) => host is "localhost" or "127.0.0.1" or "::1";

    /// <summary>First 4 characters followed by "…"; never the whole value.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "(not set)" : value.Length <= 4 ? "…" : value[..4] + "…";

    // Keep secrets out of logs if a record is ever formatted by accident.
    public override string ToString() =>
        $"TraderSecrets {{ ClientId = {Mask(ClientId)}, ClientSecret = {Mask(ClientSecret)}, " +
        $"AccessToken = {Mask(AccessToken)}, RefreshToken = {Mask(RefreshToken)}, Environment = {Environment}, " +
        $"AccessTokenExpiresUtc = {AccessTokenExpiresUtc?.ToString("u") ?? "unknown"}" +
        (Host is null ? "" : $", Host = {Host}:{Port}") + " }";
}
