using Serilog;
using Serilog.Core;
using Serilog.Events;
using Trader.Core.Hosting;
using Trader.Core.Notifications;

namespace Trader.Core.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-notify-").FullName;
    private readonly ListSink _sink = new();
    private DateTime _now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent e) => Events.Add(e);
        public List<LogEvent> Named(string name) =>
            Events.Where(e => e.Properties.TryGetValue("Event", out var v) && v.ToString() == $"\"{name}\"").ToList();
    }

    private sealed class FakeChannel(string name, bool fail = false) : INotificationChannel
    {
        public bool Fail { get; set; } = fail;
        public List<Notification> Sent { get; } = [];
        public string Name => name;

        public Task SendAsync(Notification n, CancellationToken ct)
        {
            if (Fail)
                throw new InvalidOperationException("no display");
            Sent.Add(n);
            return Task.CompletedTask;
        }
    }

    private (Notifier Notifier, Dictionary<string, FakeChannel> Channels) Make(string botName, params ChannelConfig[] channels)
    {
        var fakes = new Dictionary<string, FakeChannel>();
        var notifier = new Notifier(new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_sink).CreateLogger(), botName,
            new NotificationConfig(60, channels),
            c => fakes[c.Type] = new FakeChannel(c.Type), () => _now);
        return (notifier, fakes);
    }

    [Fact]
    public async Task Channels_get_only_what_their_filter_accepts()
    {
        var (n, ch) = Make("TM",
            new ChannelConfig("all"),                                                      // Warning and above (default)
            new ChannelConfig("info-tm", NotificationLevel.Info, Bots: ["TM"], Events: ["TradeTakenOver"]),
            new ChannelConfig("critical", NotificationLevel.Critical, ExcludeEvents: ["Stopped"]),
            new ChannelConfig("off", NotificationLevel.Info, Enabled: false));

        n.Info("TradeTakenOver", "AUDJPY short taken over");
        n.Info("HedgePlaced", "Hedge placed");
        n.Warning("LossWarning", "AUDJPY short at -2R");
        n.Critical("LossLimit", "AUDJPY short closed at 3R");
        n.Critical("Stopped", "TM stopped");
        await n.DisposeAsync();

        Assert.Equal(["LossWarning", "LossLimit", "Stopped"], ch["all"].Sent.Select(x => x.Event));
        Assert.Equal(["TradeTakenOver"], ch["info-tm"].Sent.Select(x => x.Event));
        Assert.Equal(["LossLimit"], ch["critical"].Sent.Select(x => x.Event));
        Assert.False(ch.ContainsKey("off")); // a disabled channel is not even created
    }

    [Fact]
    public async Task Every_notification_is_logged_with_its_event_level_and_account_even_when_not_sent()
    {
        var (n, ch) = Make("TM", new ChannelConfig("desk", NotificationLevel.Critical));
        n.Info("TradeTakenOver", "AUDJPY short taken over", "position 123", accountId: 49123893);
        n.Warning("LossWarning", "AUDJPY short at -2R");
        n.Critical("LossLimit", "Closed at 3R");
        await n.DisposeAsync();

        Assert.Single(ch["desk"].Sent);
        var info = Assert.Single(_sink.Named("TradeTakenOver"));
        Assert.Equal(LogEventLevel.Information, info.Level);
        Assert.Equal("49123893", info.Properties["Account"].ToString());
        Assert.Contains("position 123", info.RenderMessage());
        Assert.Equal(LogEventLevel.Warning, Assert.Single(_sink.Named("LossWarning")).Level);
        Assert.Equal(LogEventLevel.Error, Assert.Single(_sink.Named("LossLimit")).Level);
    }

    [Fact]
    public async Task The_same_notification_within_repeat_seconds_is_logged_but_sent_once()
    {
        var (n, ch) = Make("TM", new ChannelConfig("desk"));
        n.Warning("Disconnected", "Connection lost");
        _now = _now.AddSeconds(30);
        n.Warning("Disconnected", "Connection lost");             // repeat: not sent
        n.Warning("Disconnected", "Connection lost", "again");    // different text: sent
        _now = _now.AddSeconds(31);
        n.Warning("Disconnected", "Connection lost");             // 61 s after the first: sent
        await n.DisposeAsync();

        Assert.Equal(3, ch["desk"].Sent.Count);
        Assert.Equal(4, _sink.Named("Disconnected").Count);
    }

    [Fact]
    public async Task A_failing_channel_is_logged_once_and_does_not_stop_the_others()
    {
        var broken = new FakeChannel("telegram", fail: true);
        var desk = new FakeChannel("desktop");
        var n = new Notifier(new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger(), "TM",
            new NotificationConfig(0, [new ChannelConfig("telegram"), new ChannelConfig("desktop")]),
            c => c.Type == "telegram" ? broken : desk, () => _now);

        n.Warning("A", "one");
        n.Warning("B", "two");
        await Task.Delay(200);
        broken.Fail = false;
        n.Warning("C", "three");
        await n.DisposeAsync();

        Assert.Equal(["A", "B", "C"], desk.Sent.Select(x => x.Event));
        Assert.Equal(["C"], broken.Sent.Select(x => x.Event));
        Assert.Single(_sink.Named("NotifyChannelFailed"));
        Assert.Single(_sink.Named("NotifyChannelOk"));
    }

    [Fact]
    public void Config_file_loads_with_comments_and_defaults_and_reports_mistakes()
    {
        Assert.Same(NotificationConfig.Default, NotificationConfig.Load(Path.Combine(_dir, "missing.json")));

        var path = Path.Combine(_dir, NotificationConfig.FileName);
        File.WriteAllText(path, """
            { // comment
              "repeatSeconds": 30,
              "channels": [ { "type": "desktop", "minLevel": "Critical", "bots": ["TM"] }, ]
            }
            """);
        var c = NotificationConfig.Load(path);
        Assert.Equal(30, c.RepeatSeconds);
        Assert.Equal(new[] { "TM" }, c.Channels![0].Bots);
        Assert.Equal(NotificationLevel.Critical, c.Channels[0].MinLevel);

        File.WriteAllText(path, """{ "channels": [ { "type": "pager" } ] }""");
        Assert.Contains("pager", Assert.Throws<NotificationConfigException>(() => NotificationConfig.Load(path)).Message);
        File.WriteAllText(path, """{ "channel": [] }""");
        Assert.Contains("channel", Assert.Throws<NotificationConfigException>(() => NotificationConfig.Load(path)).Message);
        File.WriteAllText(path, """{ "channels": [ { "type": "desktop", "minLevel": "Loud" } ] }""");
        Assert.Throws<NotificationConfigException>(() => NotificationConfig.Load(path));
    }

    [Fact]
    public void Repo_notification_config_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", NotificationConfig.FileName);
        var c = NotificationConfig.Load(path);
        Assert.Equal(60, c.RepeatSeconds);
        Assert.Equal("desktop", Assert.Single(c.Channels!).Type);
    }

    [Theory]
    [InlineData(NotificationLevel.Info, "--urgency=low")]
    [InlineData(NotificationLevel.Warning, "--urgency=normal")]
    [InlineData(NotificationLevel.Critical, "--urgency=critical")]
    public void Desktop_popup_uses_notify_send_with_urgency_by_level(NotificationLevel level, string urgency)
    {
        var info = DesktopChannel.StartInfo(new Notification(_now, level, "TM", "LossLimit", "AUDJPY short closed at 3R", "Trade P123"));
        Assert.Equal("notify-send", info.FileName);
        Assert.Equal(["--app-name=Trader TM", urgency], info.ArgumentList.Take(2));
        Assert.Equal(["TM: AUDJPY short closed at 3R", "Trade P123"], info.ArgumentList.TakeLast(2));
    }

    [Fact]
    public async Task Desktop_channel_reports_a_failed_notify_send()
    {
        var channel = new DesktopChannel((_, _) => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendAsync(new Notification(_now, NotificationLevel.Warning, "TM", "X", "t", "m"), CancellationToken.None));
    }

    [Fact]
    public void Test_notification_is_a_common_option()
    {
        Assert.True(BotArgs.Parse(["--test-notification"]).TestNotification);
        Assert.False(BotArgs.Parse(["--verbose"]).TestNotification);
    }
}
