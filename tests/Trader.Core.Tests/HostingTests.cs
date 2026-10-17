using Trader.Core.Hosting;
using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

public class HostingTests
{
    private static readonly ProtoOACtidTraderAccount[] Granted =
    [
        new() { CtidTraderAccountId = 49123893, TraderLogin = 10124393, IsLive = false },
        new() { CtidTraderAccountId = 49124666, TraderLogin = 5357135, IsLive = false },
        new() { CtidTraderAccountId = 70000001, TraderLogin = 999, IsLive = true },
    ];

    [Fact]
    public void SelectAccounts_empty_list_means_all_demo_accounts()
    {
        var selected = ConnectionManager.SelectAccounts(Granted, []);
        Assert.Equal([49123893L, 49124666L], selected.Select(a => a.CtidTraderAccountId));
    }

    [Fact]
    public void SelectAccounts_narrows_to_wanted_accounts()
    {
        var selected = ConnectionManager.SelectAccounts(Granted, [49124666]);
        Assert.Equal(new TradingAccount(49124666, 5357135), Assert.Single(selected));
    }

    [Theory]
    [InlineData(12345L)]     // not granted at all
    [InlineData(70000001L)]  // live: never selected
    public void SelectAccounts_rejects_account_not_granted_for_demo(long wanted)
    {
        var ex = Assert.Throws<FatalConnectionException>(() => ConnectionManager.SelectAccounts(Granted, [wanted]));
        Assert.Contains(wanted.ToString(), ex.Message);
    }

    [Fact]
    public void Identity_has_name_plus_six_hex_and_is_unique()
    {
        var a = BotIdentity.Create("AuditBot");
        var b = BotIdentity.Create("AuditBot");

        Assert.Matches("^AuditBot-[0-9a-f]{6}$", a.InstanceId);
        Assert.NotEqual(a.InstanceId, b.InstanceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1Bot")]
    [InlineData("Audit Bot")]
    [InlineData("Audit-Bot")]
    [InlineData("ABotNameThatIsFarTooLong1")]
    public void Identity_rejects_bad_names(string name) =>
        Assert.Throws<ArgumentException>(() => BotIdentity.Create(name));

    [Fact]
    public void Label_round_trips_with_and_without_leg()
    {
        var id = new BotIdentity("TrendBot", "TrendBot-3f9a1c");

        Assert.Equal("TrendBot-3f9a1c", id.Label());
        Assert.Equal("TrendBot-3f9a1c-T1-S1", id.Label("T1-S1"));

        Assert.True(BotIdentity.TryParseLabel(id.Label("T1-S1"), out var bot, out var instance, out var leg));
        Assert.Equal(("TrendBot", "TrendBot-3f9a1c", "T1-S1"), (bot, instance, leg));

        Assert.True(BotIdentity.TryParseLabel(id.Label(), out _, out _, out var noLeg));
        Assert.Null(noLeg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("manual trade")]
    [InlineData("TrendBot-XYZ123")]
    public void TryParseLabel_rejects_foreign_labels(string? label) =>
        Assert.False(BotIdentity.TryParseLabel(label, out _, out _, out _));

    [Fact]
    public void Label_rejects_bad_leg_names() =>
        Assert.Throws<ArgumentException>(() => new BotIdentity("TrendBot", "TrendBot-3f9a1c").Label("T1 S1"));

    [Fact]
    public void BotArgs_parses_common_options_and_keeps_the_rest()
    {
        var a = BotArgs.Parse(["-v", "--account", "49123893", "--symbol", "EURUSD", "--account", "49124666", "--test-drop-after", "20"]);

        Assert.True(a.Verbose);
        Assert.Equal([49123893L, 49124666L], a.AccountIds);
        Assert.Equal(TimeSpan.FromSeconds(20), a.TestDropAfter);
        Assert.Equal(["--symbol", "EURUSD"], a.Rest);
    }

    [Theory]
    [InlineData("--account")]
    [InlineData("--account", "abc")]
    [InlineData("--account", "-5")]
    [InlineData("--test-drop-after", "0")]
    public void BotArgs_rejects_bad_values(params string[] args) =>
        Assert.Throws<ArgumentException>(() => BotArgs.Parse(args));
}
