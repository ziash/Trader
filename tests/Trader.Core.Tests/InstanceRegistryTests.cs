using System.Text.Json;
using Serilog;
using Trader.Core.Hosting;

namespace Trader.Core.Tests;

public sealed class InstanceRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trader-reg-").FullName;
    private readonly ILogger _log = new LoggerConfiguration().CreateLogger();
    private DateTime _now = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    private readonly HashSet<int> _deadPids = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private InstanceRegistry Make(string id, params (string Broker, long Account)[] accounts)
    {
        var reg = new InstanceRegistry(_dir, new BotIdentity("AuditBot", id), _log, () => _now, pid: 1000, isProcessAlive: pid => !_deadPids.Contains(pid));
        foreach (var (broker, account) in accounts)
            reg.Register(broker, account, ["EURUSD"]);
        return reg;
    }

    [Fact]
    public void First_instance_is_Primary_second_is_Passive_and_both_see_each_other()
    {
        using var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        a.Check();
        _now = _now.AddSeconds(1);
        using var b = Make("AuditBot-bbbbbb", ("brokerA", 1));
        var view = b.Check().Single();

        Assert.Equal(InstanceRole.Primary, a.RoleOn(1));
        Assert.Equal(InstanceRole.Passive, view.Role);
        Assert.Equal(["AuditBot-aaaaaa", "AuditBot-bbbbbb"], view.Siblings.Select(s => s.InstanceId));
        Assert.Equal(1, view.Slot);
        Assert.Equal(0, a.Check().Single().Slot);
    }

    [Fact]
    public void Passive_takes_over_as_soon_as_the_Primary_stops()
    {
        var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        a.Check();
        using var b = Make("AuditBot-bbbbbb", ("brokerA", 1));
        var roles = new List<InstanceRole>();
        b.RoleChanged += (_, role) => roles.Add(role);
        b.Check();

        a.Dispose(); // Primary stops: lock released, status file removed
        var view = b.Check().Single();

        Assert.Equal(InstanceRole.Primary, view.Role);
        Assert.Equal([InstanceRole.Passive, InstanceRole.Primary], roles);
        Assert.Single(view.Siblings);
        Assert.Equal(InstanceRole.Primary, view.Siblings[0].Role); // status file rewritten at once with the new role
    }

    [Fact]
    public void Roles_are_per_account()
    {
        using var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        a.Check();
        using var b = Make("AuditBot-bbbbbb", ("brokerA", 1), ("brokerB", 2));
        b.Check();

        Assert.Equal(InstanceRole.Passive, b.RoleOn(1));
        Assert.Equal(InstanceRole.Primary, b.RoleOn(2));
    }

    [Fact]
    public void Status_file_has_plan_name_and_fields_and_no_temp_file_is_left()
    {
        using var a = Make("AuditBot-aaaaaa", ("brokerA", 49123893));
        a.Check();

        var file = Path.Combine(_dir, "brokerA-49123893-AuditBot-aaaaaa.json");
        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("Primary", json.RootElement.GetProperty("Role").GetString());
        Assert.Equal(1000, json.RootElement.GetProperty("Pid").GetInt32());
        Assert.Equal("brokerA", json.RootElement.GetProperty("Broker").GetString());

        a.Dispose();
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Status_files_older_than_30_s_are_removed_and_not_counted()
    {
        var dead = new InstanceRecord("AuditBot-dddddd", "AuditBot", 999, "brokerA", 1, ["EURUSD"], InstanceRole.Primary, _now.AddHours(-1), _now.AddSeconds(-31));
        var deadFile = Path.Combine(_dir, "brokerA-1-AuditBot-dddddd.json");
        File.WriteAllText(deadFile, JsonSerializer.Serialize(dead, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));

        using var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        var view = a.Check().Single();

        Assert.False(File.Exists(deadFile));
        Assert.Single(view.Siblings);
        Assert.Equal(InstanceRole.Primary, view.Role); // a dead instance's file does not hold the lock
    }

    [Fact]
    public void Status_file_of_a_process_that_is_gone_is_removed_at_once()
    {
        var killed = new InstanceRecord("AuditBot-kkkkkk", "AuditBot", 4242, "brokerA", 1, ["EURUSD"], InstanceRole.Primary, _now.AddMinutes(-5), _now.AddSeconds(-3));
        var file = Path.Combine(_dir, "brokerA-1-AuditBot-kkkkkk.json");
        File.WriteAllText(file, JsonSerializer.Serialize(killed, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        _deadPids.Add(4242); // e.g. killed with kill -9 three seconds ago

        using var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        var view = a.Check().Single();

        Assert.False(File.Exists(file));
        Assert.Single(view.Siblings); // no stale "Primary" left in the picture
    }

    [Fact]
    public void Heartbeat_is_rewritten_every_10_s_and_keeps_an_instance_alive()
    {
        using var a = Make("AuditBot-aaaaaa", ("brokerA", 1));
        using var b = Make("AuditBot-bbbbbb", ("brokerA", 1));
        for (var i = 0; i < 30; i++) // 60 s of 2-second checks
        {
            a.Check();
            b.Check();
            _now = _now.AddSeconds(2);
        }
        Assert.Equal(2, b.Check().Single().Siblings.Count);
    }

    [Fact]
    public async Task RunTogether_stops_the_other_jobs_when_one_fails_and_rethrows()
    {
        var stopped = false;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BotRunner.RunTogetherAsync(CancellationToken.None,
            async ct => { try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped = true; } },
            async _ => { await Task.Delay(50); throw new InvalidOperationException("disk full"); }));

        Assert.Equal("disk full", ex.Message);
        Assert.True(stopped);
    }

    [Fact]
    public async Task RunTogether_ends_with_cancellation_when_asked_to_stop()
    {
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BotRunner.RunTogetherAsync(cts.Token,
            ct => Task.Delay(Timeout.Infinite, ct),
            async ct => { while (!ct.IsCancellationRequested) await Task.Delay(10); })); // returns normally on stop
    }
}
