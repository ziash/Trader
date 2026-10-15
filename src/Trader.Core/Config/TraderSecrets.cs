namespace Trader.Core.Config;

/// <summary>Which cTrader Open API host to use. Only Demo exists until Phase 5 (plan Section 8).</summary>
public enum ApiEnvironment
{
    Demo,
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
    ApiEnvironment Environment)
{
    /// <summary>First 4 characters followed by "…"; never the whole value.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "(not set)" : value.Length <= 4 ? "…" : value[..4] + "…";

    // Keep secrets out of logs if a record is ever formatted by accident.
    public override string ToString() =>
        $"TraderSecrets {{ ClientId = {Mask(ClientId)}, ClientSecret = {Mask(ClientSecret)}, " +
        $"AccessToken = {Mask(AccessToken)}, RefreshToken = {Mask(RefreshToken)}, Environment = {Environment} }}";
}
