using System.Diagnostics;
using System.Text.Json;
using Serilog;
using Trader.Core.Logging;

namespace Trader.Core.Hosting;

/// <summary>What one bot instance may do on one account (plan 6.4).</summary>
public enum InstanceRole
{
    /// <summary>The one instance of this bot on this account that may place orders.</summary>
    Primary,

    /// <summary>Records data only; becomes Primary if the Primary stops.</summary>
    Passive,
}

/// <summary>Contents of one status file in ~/tradebot/run/instances/.</summary>
public sealed record InstanceRecord(
    string InstanceId,
    string BotName,
    int Pid,
    string Broker,
    long AccountId,
    IReadOnlyList<string> Symbols,
    InstanceRole Role,
    DateTime StartedUtc,
    DateTime LastSeenUtc);

/// <summary>How this instance sees one account at one moment.</summary>
/// <param name="Siblings">Live instances of the same bot on the same account, this one included, oldest first.</param>
/// <param name="Slot">This instance's position in <paramref name="Siblings"/> (0 = oldest); used to stagger orders.</param>
/// <param name="TotalInstances">Live instances of any bot on any account (counted per account registration).</param>
public sealed record AccountView(string Broker, long AccountId, InstanceRole Role, IReadOnlyList<InstanceRecord> Siblings, int Slot, int TotalInstances);

/// <summary>
/// The "know your siblings" registry (plan 6.4), shared by all bots.
///
/// - Each instance writes a status file per account every 10 s: &lt;broker&gt;-&lt;account&gt;-&lt;instanceId&gt;.json
///   (written to a temp file, then renamed, so readers never see half a file). A file belongs to a dead
///   instance when it was not updated for 30 s, or at once when its process (PID) is no longer running on
///   this machine; whoever notices deletes it.
/// - Primary: the instance holding the exclusive lock file &lt;BotName&gt;-&lt;account&gt;.primary.lock. Linux releases
///   the lock the moment the process ends — even on kill -9 or a crash — so a Passive instance takes over
///   within one check (2 s), and there can never be two Primaries on this machine (plan v1.12).
/// - Roles are per account: one process can be Primary on one account and Passive on another.
/// </summary>
public sealed class InstanceRegistry : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatusLogInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly BotIdentity _identity;
    private readonly ILogger _log;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly DateTime _startedUtc;
    private readonly int _pid;
    private readonly Lock _gate = new();
    private readonly List<Registration> _registrations = [];

    private sealed class Registration(string broker, long accountId, IReadOnlyList<string> symbols)
    {
        public string Broker { get; } = broker;
        public long AccountId { get; } = accountId;
        public IReadOnlyList<string> Symbols { get; } = symbols;
        public FileStream? PrimaryLock { get; set; }
        public DateTime LastHeartbeat { get; set; } = DateTime.MinValue;
        public DateTime LastStatusLog { get; set; } = DateTime.MinValue;
        public string? LastSiblingKey { get; set; }
        public AccountView? View { get; set; }
        public InstanceRole Role => PrimaryLock is null ? InstanceRole.Passive : InstanceRole.Primary;
    }

    public InstanceRegistry(string dir, BotIdentity identity, ILogger log, Func<DateTime>? utcNow = null, int? pid = null,
        Func<int, bool>? isProcessAlive = null)
    {
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _dir = dir;
        _identity = identity;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _startedUtc = _utcNow();
        _pid = pid ?? Environment.ProcessId;
        Directory.CreateDirectory(dir);
    }

    /// <summary>Raised when this instance becomes Primary or Passive on an account (after the first check).</summary>
    public event Action<long, InstanceRole>? RoleChanged;

    /// <summary>Registers an account. Call before <see cref="RunAsync"/> or <see cref="Check"/>.</summary>
    public void Register(string broker, long accountId, IReadOnlyList<string> symbols)
    {
        lock (_gate)
            _registrations.Add(new Registration(broker, accountId, symbols));
    }

    /// <summary>The latest view of an account; null before the first check.</summary>
    public AccountView? View(long accountId)
    {
        lock (_gate)
            return _registrations.FirstOrDefault(r => r.AccountId == accountId)?.View;
    }

    /// <summary>Current role on an account (Passive until the first check has run).</summary>
    public InstanceRole RoleOn(long accountId) => View(accountId)?.Role ?? InstanceRole.Passive;

    /// <summary>Checks every 2 s until cancelled. Removes this instance's files and releases its locks when it ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                Check();
                await Task.Delay(CheckInterval, ct);
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>One round: try to become Primary, write status files when due, read and clean the registry, log changes.</summary>
    public IReadOnlyList<AccountView> Check()
    {
        lock (_gate)
        {
            var now = _utcNow();
            foreach (var r in _registrations)
            {
                var before = r.View?.Role;
                if (r.PrimaryLock is null && TryLock(r) is { } stream)
                    r.PrimaryLock = stream;

                if (before is not null && before != r.Role || now - r.LastHeartbeat >= HeartbeatInterval)
                {
                    WriteStatus(r, now);
                    r.LastHeartbeat = now;
                }
            }

            var live = ReadLive(now);
            var views = new List<AccountView>();
            foreach (var r in _registrations)
            {
                var siblings = live
                    .Where(x => x.AccountId == r.AccountId && x.BotName == _identity.BotName)
                    .OrderBy(x => x.StartedUtc).ThenBy(x => x.InstanceId, StringComparer.Ordinal)
                    .ToList();
                var slot = Math.Max(0, siblings.FindIndex(x => x.InstanceId == _identity.InstanceId));
                var previous = r.View;
                r.View = new AccountView(r.Broker, r.AccountId, r.Role, siblings, slot, live.Count);
                views.Add(r.View);
                Report(r, previous, now);
            }
            return views;
        }
    }

    private void Report(Registration r, AccountView? previous, DateTime now)
    {
        var view = r.View!;
        var log = _log.ForAccount(r.AccountId);

        if (previous is not null && previous.Role != view.Role)
        {
            log.Event(view.Role == InstanceRole.Primary ? "Promoted" : "Demoted").Warning(
                "{Broker} account {Ctid}: this instance is now {Role}", r.Broker, r.AccountId, view.Role.ToString());
            RoleChanged?.Invoke(r.AccountId, view.Role);
        }
        else if (previous is null)
        {
            RoleChanged?.Invoke(r.AccountId, view.Role);
        }

        // Log the sibling picture on start, whenever it changes, and hourly (plan 6.4 "Visibility").
        var key = string.Join(",", view.Siblings.Select(s => $"{s.InstanceId}:{s.Role}")) + $"|{view.Role}";
        if (key == r.LastSiblingKey && now - r.LastStatusLog < StatusLogInterval)
            return;

        log.Event("Siblings").Information(
            "{Broker} account {Ctid}: {Count} {Bot} instance(s) [{List}]; this one {Role}, slot {Slot}; {Total} instance registration(s) in total",
            r.Broker, r.AccountId, view.Siblings.Count, _identity.BotName,
            string.Join(", ", view.Siblings.Select(s => $"{s.InstanceId} {s.Role}")), view.Role.ToString(), view.Slot, view.TotalInstances);
        r.LastSiblingKey = key;
        r.LastStatusLog = now;
    }

    private FileStream? TryLock(Registration r)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        try
        {
            return new FileStream(Path.Combine(_dir, $"{_identity.BotName}-{r.AccountId}.primary.lock"), options);
        }
        catch (IOException)
        {
            return null; // another live instance holds it
        }
    }

    private string StatusPath(Registration r) => Path.Combine(_dir, $"{r.Broker}-{r.AccountId}-{_identity.InstanceId}.json");

    private void WriteStatus(Registration r, DateTime now)
    {
        var record = new InstanceRecord(_identity.InstanceId, _identity.BotName, _pid, r.Broker, r.AccountId, r.Symbols, r.Role, _startedUtc, now);
        var path = StatusPath(r);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(record, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>All live status records; deletes files of instances not seen for 30 s.</summary>
    private List<InstanceRecord> ReadLive(DateTime now)
    {
        var live = new List<InstanceRecord>();
        foreach (var file in Directory.GetFiles(_dir, "*.json"))
        {
            InstanceRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(file), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // unreadable: judged by its file time below
            }

            var lastSeen = record?.LastSeenUtc ?? File.GetLastWriteTimeUtc(file);
            var stale = now - lastSeen > DeadAfter;
            var processGone = record is not null && record.InstanceId != _identity.InstanceId && !_isProcessAlive(record.Pid);
            if (stale || processGone)
            {
                TryDelete(file);
                if (record is not null)
                    _log.Event("InstanceGone").ForAccount(record.AccountId).Information(
                        "{Broker} account {Ctid}: removed status file of {Instance} (PID {Pid}): {Why}",
                        record.Broker, record.AccountId, record.InstanceId, record.Pid,
                        processGone ? "process no longer running" : $"not seen since {lastSeen:HH:mm:ss}Z");
                continue;
            }
            if (record is not null)
                live.Add(record);
        }
        return live;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // another instance deleted it first
        }
    }

    /// <summary>Releases the Primary locks and removes this instance's status files.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var r in _registrations)
            {
                r.PrimaryLock?.Dispose();
                r.PrimaryLock = null;
                TryDelete(StatusPath(r));
            }
        }
    }
}
