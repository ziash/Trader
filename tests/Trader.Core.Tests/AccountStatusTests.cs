using Google.Protobuf;
using OpenAPI.Net.Helpers;
using Trader.Core.OpenApi;

namespace Trader.Core.Tests;

/// <summary>The account status the bots read before adding exposure (issue-log.md #1).</summary>
public sealed class AccountStatusTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 14, 47, 0, DateTimeKind.Utc);

    private static ProtoOAPosition Position(long id, long symbolId, ProtoOATradeSide side, ulong usedMarginCents) => new()
    {
        PositionId = id,
        TradeData = new ProtoOATradeData { SymbolId = symbolId, TradeSide = side, Volume = 100_000 },
        UsedMargin = usedMarginCents,
        MoneyDigits = 2,
        PositionStatus = ProtoOAPositionStatus.PositionStatusOpen,
    };

    private static (ProtoOATrader, ProtoOAReconcileRes, ProtoOAGetPositionUnrealizedPnLRes) Account(ProtoOATotalMarginCalculationType how)
    {
        var trader = new ProtoOATrader { CtidTraderAccountId = 49123893, Balance = 381_094, MoneyDigits = 2, TotalMarginCalculationType = how };
        var reconcile = new ProtoOAReconcileRes { CtidTraderAccountId = 49123893 };
        // SOLUSD: a long main and mirror, and their two short hedges; XNGUSD: one short.
        reconcile.Position.Add(Position(1, 10, ProtoOATradeSide.Buy, 100_000));
        reconcile.Position.Add(Position(2, 10, ProtoOATradeSide.Buy, 100_000));
        reconcile.Position.Add(Position(3, 10, ProtoOATradeSide.Sell, 90_000));
        reconcile.Position.Add(Position(4, 10, ProtoOATradeSide.Sell, 90_000));
        reconcile.Position.Add(Position(5, 20, ProtoOATradeSide.Sell, 16_486));
        reconcile.Order.Add(new ProtoOAOrder { OrderId = 9 });
        var pnl = new ProtoOAGetPositionUnrealizedPnLRes { CtidTraderAccountId = 49123893, MoneyDigits = 2 };
        pnl.Positions.AddRange([new(1, -5_000, -5_100), new(2, -5_000, -5_100), new(3, 2_000, 1_900), new(4, 2_000, 1_900), new(5, -99_000, -99_450)]);
        return (trader, reconcile, pnl);
    }

    [Fact]
    public void Equity_is_balance_plus_the_net_open_result_and_margin_follows_the_brokers_hedged_rule()
    {
        var (trader, reconcile, pnl) = Account(ProtoOATotalMarginCalculationType.Max);
        var s = AccountStatus.From(trader, reconcile, pnl, Now);

        Assert.Equal(3810.94, s.Balance, 2);
        Assert.Equal(3810.94 - 1058.50, s.Equity, 2);
        Assert.Equal(2000 + 164.86, s.UsedMargin, 2);          // MAX: only the bigger side of SOLUSD counts
        Assert.Equal(s.Equity - s.UsedMargin, s.FreeMargin, 6);
        Assert.Equal(s.Equity / s.UsedMargin * 100, s.MarginLevel!.Value, 6);
        Assert.Equal((5, 1), (s.Positions, s.Orders));
        Assert.Equal("balance $3,810.94, equity $2,752.44, margin $2,164.86 used, $587.58 free, level 127 %", s.Describe());

        (trader, reconcile, pnl) = Account(ProtoOATotalMarginCalculationType.Sum);
        Assert.Equal(3800 + 164.86, AccountStatus.From(trader, reconcile, pnl, Now).UsedMargin, 2); // SUM: both sides
        (trader, reconcile, pnl) = Account(ProtoOATotalMarginCalculationType.Net);
        Assert.Equal(200 + 164.86, AccountStatus.From(trader, reconcile, pnl, Now).UsedMargin, 2);  // NET: the difference
    }

    [Fact]
    public void Without_positions_there_is_no_margin_level()
    {
        var s = AccountStatus.From(new ProtoOATrader { Balance = 500_000, MoneyDigits = 2 }, new ProtoOAReconcileRes(), new ProtoOAGetPositionUnrealizedPnLRes(), Now);
        Assert.Equal((5000, 5000, 0), (s.Balance, s.Equity, s.UsedMargin));
        Assert.Null(s.MarginLevel);
        Assert.EndsWith("level – (no positions)", s.Describe());
    }

    [Fact]
    public void The_hand_written_unrealized_pnl_messages_go_out_and_come_back_through_the_decoder()
    {
        var req = new ProtoOAGetPositionUnrealizedPnLReq { CtidTraderAccountId = 49123893 };
        var envelope = MessageFactory.GetMessage(req.GetPayloadType(), req.ToByteString(), "abc");
        Assert.Equal(2187u, envelope.PayloadType);
        Assert.Equal(req.CalculateSize(), envelope.Payload.Length);

        var res = new ProtoOAGetPositionUnrealizedPnLRes { CtidTraderAccountId = 49123893, MoneyDigits = 2 };
        res.Positions.AddRange([new(693034636, -4_217, -4_250), new(692992966, 1_000_000_000_000, -1)]);
        var reply = MessageFactory.GetMessage(res.GetPayloadType(), res.ToByteString(), "abc");
        Assert.Equal(res.CalculateSize(), reply.Payload.Length);
        var decoded = Assert.IsType<ProtoOAGetPositionUnrealizedPnLRes>(PayloadDecoder.Decode(reply));
        Assert.True(res.Equals(decoded));
        Assert.True(PayloadDecoder.LibraryCannotDecode(reply)); // so CTraderSession hands it to the waiting request itself
    }

    [Fact]
    public void Expected_margin_picks_the_side()
    {
        var m = new ExpectedMargin(2_000_000, 81.20, 80.90);
        Assert.Equal((81.20, 80.90), (m.For(1), m.For(-1)));
    }
}
