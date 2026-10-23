using Trader.TM;

namespace Trader.TM.Tests;

/// <summary>Which labels TM takes over as mains: the "takeOver" list in tm.json (tm.md §2).</summary>
public sealed class TakeOverTests
{
    private static FakeBroker Broker(params string[]? takeOver) =>
        new() { Config = new TmConfig([new TmAccount("brokerA", 1, ["EURUSD"])], TakeOver: takeOver).Validated() };

    /// <summary>The labels of the mains TM took over (one trade each).</summary>
    private static List<string> TakenOver(FakeBroker b, params Pos[] opened) =>
        opened.Where(p => b.Book.Trades.Any(t => t.MainId == p.Id)).Select(p => p.Label).ToList();

    [Fact]
    public void Default_takes_over_only_the_owners_unlabelled_positions()
    {
        var b = new FakeBroker();
        var manual = b.OpenManual(-1, stopLoss: 1.16100);
        var bot = b.OpenManual(-1, stopLoss: 1.16100, label: "ABC-1");
        b.Run();

        Assert.Equal([""], TakenOver(b, manual, bot));
        Assert.Equal(1.16100, b.Positions.Single(p => p.Id == bot.Id).StopLoss); // never touched
    }

    [Fact]
    public void Empty_and_a_prefix_take_over_both()
    {
        var b = Broker("", "ABC");
        var manual = b.OpenManual(-1, stopLoss: 1.16100);
        var abc = b.OpenManual(-1, stopLoss: 1.16100, label: "ABC-77fbd8");
        var xyz = b.OpenManual(-1, stopLoss: 1.16100, label: "XYZ-1");
        b.Run();

        Assert.Equal(["", "ABC-77fbd8"], TakenOver(b, manual, abc, xyz));
        Assert.Equal(2, b.Positions.Count(p => p.Label.EndsWith("-" + TmLabel.Mirror)));
    }

    [Fact]
    public void A_prefix_alone_leaves_the_owners_trades_alone()
    {
        var b = Broker("ABC");
        var manual = b.OpenManual(-1, stopLoss: 1.16100);
        var abc = b.OpenManual(-1, stopLoss: 1.16100, label: "ABC");
        b.Run();

        Assert.Equal(["ABC"], TakenOver(b, manual, abc));
        Assert.Equal(1.16100, b.Positions.Single(p => p.Id == manual.Id).StopLoss);
    }

    [Fact]
    public void Pending_orders_follow_the_same_list()
    {
        var b = Broker("ABC");
        b.PlaceManualStop(-1, 1.15900, stopLoss: 1.16000);
        var abc = b.PlaceManual(false, -1, 1.15900, stopLoss: 1.16000, label: "ABC-1");
        b.Run();

        var mirror = Assert.Single(b.Orders, o => o.Label.EndsWith("-" + TmLabel.Mirror));
        Assert.Contains($"-O{abc.Id}-", mirror.Label);
    }

    [Fact]
    public void Matching_is_by_the_start_of_the_label_and_case_sensitive()
    {
        var c = new TmConfig([new TmAccount("brokerA", 1, ["EURUSD"])], TakeOver: ["AuditBot-"]);
        Assert.True(c.TakesOver("AuditBot-77fbd8"));
        Assert.False(c.TakesOver("auditbot-77fbd8"));
        Assert.False(c.TakesOver("MyAuditBot-1"));
        Assert.False(c.TakesOver(""));
    }

    [Theory]
    [InlineData("(none)")]
    [InlineData("TM")]
    [InlineData("TM-")]
    [InlineData("T")]
    [InlineData(",TM-abcdef")] // "" and "TM-abcdef"
    public void Refuses_an_empty_list_or_one_that_matches_TMs_own_labels(string list)
    {
        string[] takeOver = list == "(none)" ? [] : list.Split(',');
        var ex = Assert.Throws<TmConfigException>(() => Broker(takeOver));
        Assert.Contains("takeOver", ex.Message);
    }

    [Fact]
    public void Repo_config_takes_over_only_the_owners_trades()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", TmConfig.FileName);
        Assert.Equal([""], TmConfig.Load(path).TakeOverLabels);
    }
}
