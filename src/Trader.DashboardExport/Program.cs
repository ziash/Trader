// Dashboard export (dashboard.md §3.1, build step 4): stages the allowlisted files for the 30-min upload into
// ~/tradebot/dashboard/outbox and writes status.json there; scripts/dashboard-push.sh then sends that folder.
// Read-only on the bots' files and never connects to cTrader (the account snapshot is step 5). Never prints secrets.

using System.Globalization;
using System.Text.Json;
using Trader.Core.Config;
using Trader.Core.Hosting;
using Trader.Core.Logging;
using Trader.Core.Notifications;
using Trader.DashboardExport;

const string Usage = """
    Usage: Trader.DashboardExport collect [--days N]
           Trader.DashboardExport status [--upload-utc "yyyy-MM-dd HH:mm:ss" --upload-seconds S --upload-files N]
           Trader.DashboardExport refuse "<reason>"
           (all: [--home <dir>] [--outbox <dir>])

      collect   copy the allowlisted files of the last N days (default 14) into the outbox, checked for secrets;
                exit 3 and a Critical notification if any file holds a secret (that file is not staged)
      status    write status.json into the outbox (bots and timers from systemctl, today's warnings and errors)
      refuse    send the Critical notification for an upload refused by dashboard-push.sh, and exit 3
      --home    the tradebot folder (default ~/tradebot); --outbox (default <home>/dashboard/outbox)
    """;

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var command = args.FirstOrDefault();
if (command is null or "--help" or "-h" || command is not ("collect" or "status" or "refuse"))
{
    Console.WriteLine(Usage);
    return command is null or "--help" or "-h" ? 0 : 2;
}

var home = Option("--home") ?? TradebotPaths.Root;
var outboxDir = Option("--outbox") ?? Path.Combine(home, "dashboard", "outbox");
var configDir = Path.Combine(home, "config");
var log = BotLogging.Create(null, verbose: false);

async Task<int> Refuse(string reason)
{
    log.Error("Upload refused: {Reason}", reason);
    await using var notify = Notifier.FromConfigDir(log, "Dashboard", configDir);
    notify.Critical("DashboardUploadRefused", "Dashboard upload refused", reason + " — nothing was sent (dashboard.md §6)");
    return 3;
}

if (command == "refuse")
    return await Refuse(args.ElementAtOrDefault(1) ?? "no reason given");

IReadOnlyList<string> secrets;
try
{
    secrets = SecretValues.Load(SecretsFile.DefaultPath, SecretValues.M2mFile);
}
catch (SecretsException e)
{
    return await Refuse($"the secrets file could not be read, so nothing can be checked ({e.Message})");
}
var accounts = DashAccounts.Load(configDir);
var outbox = new Outbox(home, outboxDir, secrets);

if (command == "collect")
{
    var days = int.TryParse(Option("--days"), out var n) && n is > 0 and <= 400 ? n : 14;
    var result = outbox.Stage(accounts, days, DateOnly.FromDateTime(DateTime.UtcNow));
    log.Information("Staged {Copied} changed of {Allowed} allowlisted files (last {Days} days; accounts {Accounts}) in {Outbox}",
        result.Copied, result.Allowed, days, string.Join(", ", accounts.Select(a => a.Id)), outboxDir);
    if (result.Refused.Count > 0)
        return await Refuse($"{result.Refused.Count} file(s) contain a secret value, e.g. {result.Refused[0].RelativePath}");
    return 0;
}

// status
DateTime? uploadUtc = DateTime.TryParseExact(Option("--upload-utc"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u) ? u : null;
var upload = new UploadInfo(
    uploadUtc is null ? null : StatusReport.Utc(uploadUtc),
    double.TryParse(Option("--upload-seconds"), CultureInfo.InvariantCulture, out var secs) ? secs : null,
    int.TryParse(Option("--upload-files"), out var files) ? files : null,
    null);
var status = new StatusReport(home, new Systemctl()).Build(accounts, DateTime.UtcNow, upload);
if (!outbox.Write("status.json", JsonSerializer.Serialize(status, StatusReport.Json)))
    return await Refuse("status.json would contain a secret value");
log.Information("Wrote status.json: {Units} units, {Events} warnings/errors today", status.Units.Count, status.Events.Count);
return 0;
