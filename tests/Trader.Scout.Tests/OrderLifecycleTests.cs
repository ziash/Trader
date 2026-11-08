using Serilog;
using Serilog.Core;
using Serilog.Events;
using Trader.Core.Csv;
using Trader.Core.Hosting;
using Trader.Core.MarketData;
using Trader.Core.Notifications;
using Trader.Core.OpenApi;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>Scout's orders against a broker that loses replies, refuses, disconnects and restarts (review P-01, P-04, P-12). The broker's state decides.</summary>
public sealed class OrderLifecycleTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long Eur = 1;
    private const string Key = "EURUSD|H1|202610011900|M";
    private const string Label = "Scout-abcdef-H1-202610011900-M";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scout-life-{Guid.NewGuid():N}");
    private readonly List<LogEvent> _logs = [];
    private readonly List<DailyCsvWriter> _writers = [];
    private readonly FakeGateway _broker;
    private DateTime _now = T0.AddHours(21).AddSeconds(10); // just after the 2nd pause candle closed
    private AccountScout _scout = null!;
    private ScoutState _state = null!;

    private sealed class Sink(List<LogEvent> logs) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => logs.Add(logEvent);
    }

    private static SymbolInfo Sym(long id, string name, long min = 100_000) =>
        new(id, name, name, "EUR", "USD", 2, 2, 100_000, true, ProtoOATradingMode.Enabled, TradingSchedule.AlwaysOpen, min, 100_000);

    /// <summary>16 quiet candles, a rally, and two pause candles: a Buy Stop at 163 with its stop-loss at 154 appears on the last.</summary>
    private static List<Candle> Setup()
    {
        var bars = new List<(double O, double H, double L, double C)>();
        for (var i = 0; i < 16; i++)
            bars.Add((100, 105, 95, 100));
        bars.AddRange([(100, 121, 99, 120), (120, 141, 119, 140), (140, 161, 139, 160), (160, 163, 155, 158), (158, 162, 154, 157)]);
        return bars.Select((b, i) => new Candle(T0.AddHours(i), b.O, b.H, b.L, b.C)).ToList();
    }

    public OrderLifecycleTests()
    {
        Directory.CreateDirectory(_dir);
        _broker = new FakeGateway(() => _now);
        _broker.Candles[Eur] = Setup();
        Start();
    }

    private void Start(ScoutConfig? config = null, params SymbolInfo[] more)
    {
        var symbols = new[] { Sym(Eur, "EURUSD") }.Concat(more).ToList();
        var account = new ScoutAccount("brokerT", 1, symbols.Select(s => new ScoutChart(s.Name, "H1", ScoutMode.Trade)).ToList());
        config ??= new ScoutConfig([account]);
        var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new Sink(_logs)).CreateLogger();
        var id = BotIdentity.Create("Scout");
        var registry = new InstanceRegistry(Path.Combine(_dir, "instances"), id, log);
        DailyCsvWriter Csv(string name, string[] header)
        {
            var w = new DailyCsvWriter(_ => Path.Combine(_dir, name), header);
            _writers.Add(w);
            return w;
        }
        _state = ScoutState.Load(Path.Combine(_dir, "state.json"));
        _scout = new AccountScout(account, config, id, log, new Notifier(log, "Scout", new NotificationConfig(60, [])), registry, _state,
            Csv("setups.csv", AccountScout.SetupsHeader), Csv("orders.csv", AccountScout.OrdersHeader), Path.Combine(_dir, "logs", "day"), false, () => _now, Path.Combine(_dir, "control"));
        _scout.UseGateway(_broker, symbols);
    }

    private void Restart(ScoutConfig? config = null)
    {
        foreach (var w in _writers)
            w.Dispose();
        _writers.Clear();
        Start(config);
    }

    private async Task Cycle(int seconds = 61)
    {
        _now = _now.AddSeconds(seconds);
        await Run();
    }

    private async Task Run()
    {
        try
        {
            await _scout.RunOnceAsync(_broker);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or CTraderApiException)
        {
            // the account loop logs these and retries
        }
    }

    private IReadOnlyList<CsvRow> Rows(string action)
    {
        var path = Path.Combine(_dir, "orders.csv");
        return File.Exists(path) ? CsvTable.Read(path).Rows.Where(r => r["action"] == action).ToList() : [];
    }

    private bool Logged(string text) => _logs.Any(e => e.RenderMessage().Contains(text, StringComparison.Ordinal));

    private void Control(string file)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "control"));
        File.WriteAllText(Path.Combine(_dir, "control", file), "");
    }

    private static ProtoOAExecutionEvent Event(ProtoOAExecutionType type, ProtoOAOrder? order, ProtoOAPosition? position = null, ProtoOADeal? deal = null)
    {
        var e = new ProtoOAExecutionEvent { CtidTraderAccountId = 1, ExecutionType = type };
        if (order is not null)
            e.Order = order;
        if (position is not null)
            e.Position = position;
        if (deal is not null)
            e.Deal = deal;
        return e;
    }

    private RoleRecord Role => _state.Roles[Key];

    // ── Placing ─────────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task An_order_is_placed_once_and_its_expiry_counts_from_the_candle_that_gave_the_level()
    {
        await Run();
        await Cycle();
        var o = Assert.Single(_broker.Orders);
        Assert.Equal(1, _broker.PlaceCalls);
        Assert.Equal(RoleState.Working, Role.State);
        Assert.Equal(T0.AddHours(21 + 16), Role.ExpiresUtc); // the level appeared on the candle that closed at T0 + 21 h
        Assert.Equal(163, o.StopPrice);
        Assert.Equal(154, o.StopLoss);
        Assert.Equal("ok", Assert.Single(Rows("place"))["result"]);
    }

    [Fact]
    public async Task A_request_that_never_reached_the_broker_is_not_lost_and_is_sent_again_once()
    {
        _broker.PlaceReplies.Enqueue(Reply.LostRequest);
        await Run();
        Assert.Equal(RoleState.Unknown, Role.State);
        Assert.Empty(_broker.Orders);

        await Cycle(); // the first reconcile after it: too early to judge
        Assert.Equal(1, _broker.PlaceCalls);
        await Cycle();
        await Cycle();
        Assert.Single(_broker.Orders);
        Assert.Equal(2, _broker.PlaceCalls);
        Assert.Equal(RoleState.Working, Role.State);
        Assert.Equal(2, _broker.ClientIds.Distinct().Count());
    }

    [Fact]
    public async Task A_lost_reply_finds_the_order_on_the_broker_and_never_sends_a_second_one()
    {
        _broker.PlaceReplies.Enqueue(Reply.LostReply);
        await Run();
        Assert.Equal(RoleState.Unknown, Role.State);
        await Cycle();
        await Cycle();

        Assert.Equal(1, _broker.PlaceCalls);
        Assert.Equal(RoleState.Working, Role.State);
        Assert.Equal(_broker.Orders[0].OrderId, Role.OrderId);
        Assert.Contains("recovered", Assert.Single(Rows("place"), r => r["result"] == "ok")["detail"]);
    }

    [Fact]
    public async Task After_two_sends_that_never_arrived_the_setup_is_abandoned()
    {
        _broker.PlaceReplies.Enqueue(Reply.LostRequest);
        _broker.PlaceReplies.Enqueue(Reply.LostRequest);
        await Run();
        for (var i = 0; i < 8; i++)
            await Cycle();
        Assert.Equal(2, _broker.PlaceCalls);
        Assert.Equal(RoleState.Abandoned, Role.State);
    }

    [Fact]
    public async Task A_request_lost_to_a_disconnect_is_resolved_after_the_reconnect()
    {
        _broker.PlaceReplies.Enqueue(Reply.Disconnect);
        await Run();
        Assert.Equal(RoleState.Unknown, Role.State);
        _broker.Offline = true;
        await Cycle();
        _broker.Offline = false;
        for (var i = 0; i < 4; i++)
            await Cycle();
        Assert.Single(_broker.Orders);
        Assert.Equal(RoleState.Working, Role.State);
    }

    [Fact]
    public async Task A_late_arrival_after_a_second_send_is_cancelled_as_a_duplicate()
    {
        await Run();
        var first = Assert.Single(_broker.Orders);
        _broker.AddOrder(Eur, ProtoOATradeSide.Buy, first.TradeData.Volume, 163, 154, Label);
        await Cycle();

        Assert.Equal(first.OrderId, Assert.Single(_broker.Orders).OrderId);
        Assert.True(Logged("two orders for one setup"));
        Assert.Equal(RoleState.Working, Role.State);
    }

    [Fact]
    public async Task A_broker_refusal_is_final_but_a_routing_failure_may_be_sent_again()
    {
        _broker.PlaceReplies.Enqueue(Reply.Refused);
        await Run();
        await Cycle();
        Assert.Equal(RoleState.Refused, Role.State);
        Assert.Equal(1, _broker.PlaceCalls);

        File.Delete(Path.Combine(_dir, "state.json"));
        Restart();
        _broker.RefuseCode = "CANT_ROUTE_REQUEST";
        _broker.PlaceReplies.Enqueue(Reply.Refused);
        await Cycle();
        await Cycle();
        Assert.Single(_broker.Orders);
    }

    [Fact]
    public async Task Restart_during_a_send_trusts_the_broker()
    {
        _broker.PlaceReplies.Enqueue(Reply.LostReply);
        await Run();
        Role.State = RoleState.Sending; // the process died with the request going out
        _state.Save(_now);

        Restart();
        await Cycle();
        await Cycle();
        Assert.Equal(1, _broker.PlaceCalls);
        Assert.Equal(RoleState.Working, Role.State);
    }

    // ── The checks before sending ───────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_level_the_price_has_already_passed_is_not_sent()
    {
        _broker.Ask = 163.5;
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.Equal(RoleState.Refused, Role.State);
        Assert.Contains("LEVEL_PASSED", Assert.Single(Rows("place"))["detail"]);
    }

    [Fact]
    public async Task Without_a_live_price_nothing_is_sent_until_one_arrives()
    {
        _broker.HasQuote = false;
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.False(_state.Roles.ContainsKey(Key));
        _broker.HasQuote = true;
        await Cycle();
        Assert.Single(_broker.Orders);
    }

    [Fact]
    public async Task The_broker_minimum_never_raises_a_size()
    {
        var account = new ScoutAccount("brokerT", 1, [new ScoutChart("EURUSD", "H1", ScoutMode.Trade)]);
        Restart(new ScoutConfig([account], Lots: 0.0001));
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.Equal("size below minimum", Assert.Single(Rows("skip"))["result"]);
    }

    [Fact]
    public async Task Candles_a_whole_candle_late_place_nothing()
    {
        _now = _now.AddHours(3);
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.Equal("stale candles", Assert.Single(Rows("skip"))["result"]);
    }

    [Fact]
    public async Task A_setup_too_old_for_its_expiry_is_not_placed()
    {
        var account = new ScoutAccount("brokerT", 1, [new ScoutChart("EURUSD", "H1", ScoutMode.Trade)]);
        Restart(new ScoutConfig([account], ExpiryCandles: 1)); // expires one candle after the level's candle closed: under a candle from now
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.Equal("setup too old", Assert.Single(Rows("skip"))["result"]);
    }

    [Fact]
    public async Task At_the_margin_limit_nothing_is_sent_and_the_setup_waits()
    {
        _broker.Equity = 50; // 50 % of equity = $25; the order needs 4 × $10
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.False(_state.Roles.ContainsKey(Key));
        _broker.MarginPerOrder = 1;
        await Cycle();
        Assert.Single(_broker.Orders);
    }

    [Fact]
    public async Task Setups_on_several_symbols_at_once_share_one_margin_budget()
    {
        _broker.Candles[2] = Setup();
        _broker.Candles[3] = Setup();
        _broker.Equity = 180; // 50 % of equity = $90: one order at 4 × $15 fits, a second does not
        _broker.MarginPerOrder = 15;
        foreach (var w in _writers)
            w.Dispose();
        _writers.Clear();
        Start(null, Sym(2, "GBPUSD"), Sym(3, "USDJPY"));
        await Run();
        Assert.Single(_broker.Orders);
        Assert.Equal(2, Rows("skip").Count(r => r["result"] == "no margin"));
    }

    // ── Cancelling ──────────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<ProtoOAOrder> Placed()
    {
        await Run();
        return Assert.Single(_broker.Orders);
    }

    [Fact]
    public async Task A_refused_cancel_is_retried_with_backoff_and_alerts_after_three_failures()
    {
        await Placed();
        Control("scout-halt");
        for (var i = 0; i < 3; i++)
            _broker.CancelReplies.Enqueue(Reply.Refused);
        await Cycle();
        Assert.Equal(1, _broker.CancelCalls);
        Assert.Equal(RoleState.CancelRequested, Role.State);

        await Cycle(1); // the backoff has not passed
        Assert.Equal(1, _broker.CancelCalls);
        await Cycle(6);
        Assert.Equal(2, _broker.CancelCalls);
        await Cycle(16);
        Assert.Equal(3, _broker.CancelCalls);
        Assert.True(Logged("cannot be cancelled"));
        await Cycle(61);
        Assert.Equal(4, _broker.CancelCalls);
        Assert.Empty(_broker.Orders);
        Assert.Equal(RoleState.Held, Role.State);
    }

    [Fact]
    public async Task A_cancel_that_times_out_and_never_arrived_is_sent_again()
    {
        await Placed();
        Control("scout-halt");
        _broker.CancelReplies.Enqueue(Reply.LostRequest);
        await Cycle();
        Assert.Equal(RoleState.CancelRequested, Role.State);
        Assert.Equal("no reply", Assert.Single(Rows("cancel"))["result"]);
        await Cycle(6);
        Assert.Empty(_broker.Orders);
    }

    [Fact]
    public async Task A_cancel_accepted_with_its_reply_lost_is_confirmed_from_the_broker()
    {
        await Placed();
        Control("scout-halt");
        _broker.CancelReplies.Enqueue(Reply.LostReply);
        await Cycle();
        await Cycle();
        Assert.Equal(1, _broker.CancelCalls);
        Assert.Equal(RoleState.Cancelled, Role.State);
    }

    [Fact]
    public async Task A_fill_during_a_cancel_wins_and_is_reported()
    {
        var o = await Placed();
        Control("scout-halt");
        _broker.CancelReplies.Enqueue(Reply.Refused);
        await Cycle();
        var p = _broker.Fill(o, 163.1);
        _scout.OnMessage(Event(ProtoOAExecutionType.OrderFilled, o, p, _broker.Deals[^1]));
        await Cycle(6);

        Assert.Equal(RoleState.Filled, Role.State);
        Assert.Equal(p.PositionId, Role.PositionId);
        Assert.True(Logged("filled while being cancelled"));
    }

    [Fact]
    public async Task Restart_during_a_cancel_carries_on_with_it()
    {
        await Placed();
        Control("scout-halt");
        _broker.CancelReplies.Enqueue(Reply.LostRequest);
        await Cycle();
        Restart();
        Assert.Equal(RoleState.CancelRequested, Role.State);
        await Cycle(6);
        Assert.Empty(_broker.Orders);
        Assert.Equal(RoleState.Held, Role.State);
    }

    [Fact]
    public async Task An_order_cancelled_by_TM_or_the_owner_is_recorded_as_such()
    {
        var o = await Placed();
        _broker.Remove(o, ProtoOAOrderStatus.OrderStatusCancelled);
        _scout.OnMessage(Event(ProtoOAExecutionType.OrderCancelled, o));
        await Cycle();
        Assert.Equal(RoleState.CancelledExternally, Role.State);
        Assert.Contains("not by Scout", Assert.Single(Rows("cancelled"))["detail"]);
    }

    // ── Events and recovery ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Duplicate_events_write_one_row_each()
    {
        var o = await Placed();
        var p = _broker.Fill(o, 163.1);
        var fill = Event(ProtoOAExecutionType.OrderFilled, o, p, _broker.Deals[^1]);
        _scout.OnMessage(fill);
        _scout.OnMessage(fill);
        await Cycle();
        var close = Event(ProtoOAExecutionType.OrderFilled, null, p, _broker.CloseDeal(p, p.TradeData.Volume, 165, 1000));
        _scout.OnMessage(close);
        _scout.OnMessage(close);
        await Cycle();
        Assert.Single(Rows("filled"));
        Assert.Single(Rows("closed"));
    }

    [Fact]
    public async Task A_partial_close_keeps_the_position_open_until_the_last_deal()
    {
        var o = await Placed();
        var p = _broker.Fill(o, 163.1);
        _scout.OnMessage(Event(ProtoOAExecutionType.OrderFilled, o, p, _broker.Deals[^1]));
        await Cycle();
        _scout.OnMessage(Event(ProtoOAExecutionType.OrderPartialFill, null, p, _broker.CloseDeal(p, p.TradeData.Volume / 2, 164, 500)));
        await Cycle();
        Assert.Equal(RoleState.Filled, Role.State);

        p.PositionStatus = ProtoOAPositionStatus.PositionStatusClosed;
        _broker.Positions.Remove(p);
        _scout.OnMessage(Event(ProtoOAExecutionType.OrderFilled, null, p, _broker.CloseDeal(p, p.TradeData.Volume / 2, 165, 700)));
        await Cycle();
        Assert.Equal(RoleState.Closed, Role.State);
        Assert.Equal(2, Rows("closed").Count);
    }

    [Fact]
    public async Task A_fill_and_close_while_Scout_was_down_are_recovered_once()
    {
        var o = await Placed();
        var p = _broker.Fill(o, 163.1);
        _now = _now.AddHours(1);
        _broker.Positions.Remove(p);
        _broker.CloseDeal(p, p.TradeData.Volume, 165, 1000);
        Restart();
        await Cycle();
        Assert.Equal(RoleState.Closed, Role.State);
        Assert.Equal(p.PositionId, Role.PositionId);
        Assert.Contains("recovered", Assert.Single(Rows("filled"))["detail"]);
        Assert.Single(Rows("closed"));

        Restart();
        await Cycle();
        Assert.Single(Rows("filled"));
        Assert.Single(Rows("closed"));
    }

    [Fact]
    public async Task An_expiry_while_Scout_was_down_is_recovered()
    {
        var o = await Placed();
        _broker.Remove(o, ProtoOAOrderStatus.OrderStatusExpired);
        Restart();
        await Cycle();
        Assert.Equal(RoleState.Expired, Role.State);
        Assert.Single(Rows("expired"));
    }

    [Fact]
    public async Task An_order_from_an_earlier_Scout_is_taken_up_by_its_label()
    {
        _broker.AddOrder(Eur, ProtoOATradeSide.Buy, 1_000_000, 163, 154, Label);
        await Run();
        Assert.Equal(0, _broker.PlaceCalls);
        Assert.Equal(RoleState.Working, Role.State);
    }

    [Fact]
    public async Task A_position_missing_from_memory_is_rebuilt_from_its_label()
    {
        var o = _broker.AddOrder(Eur, ProtoOATradeSide.Buy, 1_000_000, 163, 154, Label);
        var p = _broker.Fill(o, 163.1);
        await Run();
        Assert.Equal(p.PositionId, Role.PositionId);
        Assert.Equal(RoleState.Filled, Role.State);
        Assert.True(Logged("was not in memory"));
    }

    [Fact]
    public async Task A_broker_disconnection_changes_nothing()
    {
        var o = await Placed();
        _broker.Offline = true;
        await Cycle();
        Assert.Equal(RoleState.Working, Role.State);
        _broker.Offline = false;
        await Cycle();
        Assert.Equal(o.OrderId, Assert.Single(_broker.Orders).OrderId);
    }

    [Fact]
    public void Records_from_before_the_order_states_are_understood()
    {
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, """
            {"Roles":{
              "A|H1|202611031400|M":{"FirstSeenUtc":"2026-11-03T14:00:00Z","OrderId":5,"PositionId":null,"Result":"placed"},
              "B|H1|202611031400|M":{"FirstSeenUtc":"2026-11-03T14:00:00Z","OrderId":6,"PositionId":9,"Result":"filled"},
              "C|H1|202611031400|M":{"FirstSeenUtc":"2026-11-03T14:00:00Z","OrderId":0,"PositionId":null,"Result":""},
              "D|H1|202611031400|R":{"FirstSeenUtc":"2026-11-03T14:00:00Z","OrderId":0,"PositionId":null,"Result":"refused: X"},
              "E|H1|202611031400|R":{"FirstSeenUtc":"2026-11-03T14:00:00Z","OrderId":7,"PositionId":null,"Result":"held: skipped"}
            }}
            """);
        var s = ScoutState.Load(path);
        Assert.Equal(RoleState.Working, s.Roles["A|H1|202611031400|M"].State);
        Assert.Equal(RoleState.Filled, s.Roles["B|H1|202611031400|M"].State);
        Assert.Equal(RoleState.Unknown, s.Roles["C|H1|202611031400|M"].State);
        Assert.Equal(RoleState.Refused, s.Roles["D|H1|202611031400|R"].State);
        Assert.Equal(RoleState.Held, s.Roles["E|H1|202611031400|R"].State);
    }

    // ── Halts (scout.md §2.7) ───────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task The_owners_halt_cancels_pending_orders_and_leaves_open_positions_alone()
    {
        var filled = _broker.AddOrder(2, ProtoOATradeSide.Sell, 1_000_000, 100, 110, "Scout-abcdef-H1-202610010000-M");
        var open = _broker.Fill(filled, 99);
        await Run();
        Assert.Single(_broker.Orders);
        Control("scout-halt");
        await Cycle();
        Assert.Empty(_broker.Orders);
        Assert.Contains(open, _broker.Positions);
        Assert.True(Logged("new entries stopped"));

        File.Delete(Path.Combine(_dir, "control", "scout-halt"));
        await Cycle();
        Assert.Single(_broker.Orders); // released: the setup is still valid
        Assert.True(Logged("new entries allowed again"));
    }

    [Fact]
    public async Task A_daily_loss_stops_new_entries_until_the_next_UTC_day()
    {
        await Placed();
        _broker.Equity = 4800; // −4 %
        await Cycle();
        Assert.Empty(_broker.Orders);
        Assert.Equal(LossHalt.Daily, _state.Equity!.Halt);

        _broker.Equity = 4990; // a recovery does not end it
        await Cycle();
        Assert.Equal(LossHalt.Daily, _state.Equity!.Halt);

        Restart();                // nor does a restart
        Assert.Equal(LossHalt.Daily, _state.Equity!.Halt);
    }

    [Fact]
    public void A_daily_halt_ends_with_the_day_but_a_weekly_or_peak_one_waits_for_the_owner()
    {
        var limits = new RiskLimits();
        var monday = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        var m = new EquityMarks();
        EntryHalt.Update(m, 1000, monday, limits);
        EntryHalt.Update(m, 965, monday.AddHours(1), limits);
        Assert.Equal(LossHalt.Daily, m.Halt);
        EntryHalt.Update(m, 965, monday.AddDays(1), limits);
        Assert.Equal(LossHalt.None, m.Halt);

        EntryHalt.Update(m, 935, monday.AddDays(1).AddHours(1), limits); // −6.5 % on the week
        Assert.Equal(LossHalt.Weekly, m.Halt);
        EntryHalt.Update(m, 1000, monday.AddDays(9), limits);
        Assert.Equal(LossHalt.Weekly, m.Halt);
        EntryHalt.Reset(m, 1000, monday.AddDays(9));
        Assert.Equal(LossHalt.None, m.Halt);


        var peak = new EquityMarks();
        EntryHalt.Update(peak, 1000, monday, new RiskLimits(50, 50));
        EntryHalt.Update(peak, 840, monday.AddHours(1), new RiskLimits(50, 50)); // −16 % from the peak
        Assert.Equal(LossHalt.Peak, peak.Halt);
    }

    [Fact]
    public async Task The_owners_reset_ends_a_latched_halt()
    {
        await Placed();
        _broker.Equity = 4000; // −20 % from the peak
        await Cycle();
        Assert.Equal(LossHalt.Peak, _state.Equity!.Halt);
        Control("scout-reset-brokerT-1");
        await Cycle();
        Assert.Equal(LossHalt.None, _state.Equity!.Halt);
        Assert.False(File.Exists(Path.Combine(_dir, "control", "scout-reset-brokerT-1")));
        Assert.Single(_broker.Orders);
    }

    public void Dispose()
    {
        foreach (var w in _writers)
            w.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // an open log file on the way out; the temp folder is cleaned by the system
        }
    }
}
