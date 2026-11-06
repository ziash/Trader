using System.Text.Json;
using Trader.Scout;

namespace Trader.Scout.Tests;

/// <summary>Controls from the dashboard (scout.md §2.6): each entry checked against the catalogue and scout.json, bad ones refused alone.</summary>
public sealed class ControlsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc);

    private static readonly ScoutAccount Account = new("brokerA", 49123893,
    [
        new ScoutChart("EURUSD", "H1", ScoutMode.Trade), new ScoutChart("XNGUSD", "H1", ScoutMode.Trade), new ScoutChart("SOLUSD", "H1", ScoutMode.Trade),
        new ScoutChart("GBPJPY", "H1", ScoutMode.Trade), new ScoutChart("GBPJPY", "H4", ScoutMode.Watch),
    ]);

    private static readonly ControlCatalogue Catalogue = new(1,
    [
        new("enabled", "symbol", "switch", null),
        new("timeframe", "symbol", "choice", ["H1", "H4", "D1"]),
        new("decision", "setup", "choice", ["keep", "skip"]),
        new("lots", "symbol", "number", null),                               // in the catalogue, but this Scout does not know it
    ]);

    private static ControlFile File(string json) => JsonSerializer.Deserialize<ControlFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static readonly ControlFile Sample = File("""
        { "version": 14, "updated_utc": "2026-11-06 17:55:00", "account": "brokerA-49123893",
          "symbols": {
            "XNGUSD": { "enabled": false },
            "SOLUSD": { "timeframe": "D1" },
            "EURUSD": { "timeframe": "M5", "lots": 2 },
            "GBPJPY": { "timeframe": "D1" },
            "AUDCAD": { "enabled": false }
          },
          "setups": {
            "EURUSD-H1-202611061400": { "decision": "skip", "utc": "2026-11-06 17:50:00", "by": "zia.zeeshan@gmail.com" },
            "EURUSD-H1-202610201400": { "decision": "skip", "utc": "2026-10-20 15:00:00" },
            "nonsense": { "decision": "skip", "utc": "2026-11-06 17:50:00" }
          } }
        """);

    [Fact]
    public void Good_entries_apply_and_each_bad_one_is_refused_on_its_own()
    {
        var c = Controls.Evaluate(Sample, Catalogue, Account, Now);
        Assert.Equal(14, c.Version);
        Assert.Equal(["XNGUSD"], c.Disabled);
        Assert.Equal(new Dictionary<string, string> { ["SOLUSD"] = "D1" }, c.Timeframes);
        Assert.Equal(["EURUSD-H1-202611061400"], c.Skipped);

        string? Why(string target, string key) => c.Entries.Single(e => e.Target == target && e.Key == key).Reason;
        Assert.Equal("\"M5\" is not one of H1, H4, D1", Why("EURUSD", "timeframe"));
        Assert.Equal("needs a newer Scout", Why("EURUSD", "lots"));
        Assert.StartsWith("GBPJPY has 2 charts", Why("GBPJPY", "timeframe"));
        Assert.Equal("AUDCAD is not in scout.json for this account", Why("AUDCAD", "enabled"));
        Assert.StartsWith("not a setup key", Why("nonsense", "decision"));
        Assert.DoesNotContain(c.Entries, e => e.Target == "EURUSD-H1-202610201400"); // older than 7 days: dropped
        Assert.Equal(3, c.Entries.Count(e => e.Applied));
    }

    [Fact]
    public void Switched_off_and_skipped_setups_are_held_back_and_their_orders_cancelled()
    {
        var c = Controls.Evaluate(Sample, Catalogue, Account, Now);
        Assert.Equal("symbol switched off on the dashboard", c.HoldBack("XNGUSD|H1|202611061200|M"));
        Assert.Equal("skipped on the dashboard", c.HoldBack("EURUSD|H1|202611061400|R"));   // one key covers main and reversal
        Assert.Null(c.HoldBack("EURUSD|H1|202611061500|M"));

        Assert.Equal("skipped on the dashboard", c.CancelReason("EURUSD|H1|202611061400|M", siblingFilled: false, chartActive: true, wanted: true, null));
        Assert.Equal("the other order of the pair filled", c.CancelReason("EURUSD|H1|202611061400|M", true, true, true, null));
        Assert.Null(c.CancelReason("EURUSD|H1|202611061500|M", false, true, wanted: true, null));
        Assert.Equal("trend turned", c.CancelReason("EURUSD|H1|202611061500|M", false, true, wanted: false, "trend turned"));
    }

    [Fact]
    public void After_a_timeframe_change_the_old_charts_orders_stay_until_filled_or_expired()
    {
        var c = Controls.Evaluate(Sample, Catalogue, Account, Now);
        Assert.Equal("D1", c.TimeframeFor("SOLUSD", "H1"));
        Assert.Equal("H1", c.TimeframeFor("EURUSD", "H1"));
        Assert.Null(c.CancelReason("SOLUSD|H1|202611061000|M", siblingFilled: false, chartActive: false, wanted: false, "setup ended"));
        Assert.Equal("the other order of the pair filled", c.CancelReason("SOLUSD|H1|202611061000|M", true, false, false, null));
    }

    [Fact]
    public void Nothing_applies_without_a_catalogue_or_for_another_account_and_no_file_means_as_configured()
    {
        Assert.All(Controls.Evaluate(Sample, null, Account, Now).Entries, e => Assert.False(e.Applied));
        var other = Controls.Evaluate(File("""{ "version": 1, "account": "brokerB-49124666", "symbols": { "EURUSD": { "enabled": false } } }"""), Catalogue, Account, Now);
        Assert.Empty(other.Disabled);
        Assert.Equal("the file is for brokerB-49124666, not brokerA-49123893", other.Entries.Single().Reason);
        Assert.Same(Controls.None, Controls.Evaluate(null, Catalogue, Account, Now));
        Assert.Equal("EURUSD-H1-202611061400", Controls.SetupOf("EURUSD|H1|202611061400|R"));
    }

    [Fact]
    public void Switches_are_acknowledged_as_json_and_controls_put_back_are_reported_as_reset()
    {
        var off = Controls.Evaluate(File("""{ "version": 1, "account": "brokerA-49123893", "symbols": { "EURUSD": { "enabled": false }, "SOLUSD": { "timeframe": "D1" } } }"""), Catalogue, Account, Now);
        Assert.Equal("false", off.Entries.Single(e => e.Target == "EURUSD").Value);

        var back = Controls.Evaluate(File("""{ "version": 2, "account": "brokerA-49123893", "symbols": { "SOLUSD": { "timeframe": "D1" } } }"""), Catalogue, Account, Now);
        Assert.Equal([("EURUSD", "enabled")], Controls.Reset(off, back).Select(e => (e.Target, e.Key)));
        Assert.Equal(2, Controls.Reset(off, Controls.None).Count());
        Assert.Empty(Controls.Reset(Controls.None, off));
    }

    [Fact]
    public void The_acknowledgement_remembers_when_each_version_was_applied()
    {
        var h = ControlAck.History(null, 1, "2026-11-06 19:31:49");
        h = ControlAck.History(h, 2, "2026-11-06 19:34:22");
        h = ControlAck.History(h, 2, "2026-11-06 20:00:00");                  // the same file again after a restart: first time kept
        Assert.Equal([new AppliedVersion(1, "2026-11-06 19:31:49"), new AppliedVersion(2, "2026-11-06 19:34:22")], h);
        Assert.Equal([new AppliedVersion(1, "2026-11-07 08:00:00")], ControlAck.History(h, 1, "2026-11-07 08:00:00")); // the file started again
        Assert.Equal(ControlAck.VersionsKept, Enumerable.Range(1, 80).Aggregate((IReadOnlyList<AppliedVersion>)[], (l, v) => ControlAck.History(l, v, "t")).Count);

        var path = Path.Combine(Path.GetTempPath(), $"ack-{Guid.NewGuid():N}.json");
        try
        {
            new ControlAck(2, "2026-11-06 19:34:22", "Scout-x", Controls.Known, [], null, h).Write(path);
            Assert.Equal(h, ControlAck.Load(path)!.Applied);
            System.IO.File.WriteAllText(path, "{ not json");
            Assert.Null(ControlAck.Load(path));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void The_repository_catalogue_lists_exactly_the_options_this_scout_knows()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config", ControlCatalogue.FileName));
        var catalogue = ControlCatalogue.Load(path)!;
        Assert.Equal(Controls.Known.Order(), catalogue.Options.Select(o => $"{o.Scope}.{o.Key}").Order());
        Assert.Equal(["H1", "H4", "D1"], catalogue.Find("symbol", "timeframe")!.Values);
    }
}
