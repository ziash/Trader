using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Trader.DashboardExport;

/// <summary>Reads unit properties from systemd; a fake in tests. Only asks, never starts or stops anything (dashboard.md §6.6).</summary>
public interface ISystemd
{
    /// <summary>Property → value for one user unit; timestamps as "@unix-seconds" or empty.</summary>
    IReadOnlyDictionary<string, string> Show(string unit, params string[] properties);

    /// <summary>The folder under ~/tradebot/bin (e.g. ".../bin/Scout-6711a92") that process <paramref name="pid"/> runs from, if known.</summary>
    string? RunningFolder(int pid);
}

public sealed partial class Systemctl : ISystemd
{
    public IReadOnlyDictionary<string, string> Show(string unit, params string[] properties)
    {
        var info = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--user", "show", unit, "--timestamp=unix" }.Concat(properties.SelectMany(p => new[] { "-p", p })))
            info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                values[line[..eq]] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    // The .NET host maps every assembly it loads by its real path, so the build folder shows in /proc/<pid>/maps
    // even though the service starts through the ~/tradebot/bin/<Bot> link (scripts/deploy-lib.sh).
    public string? RunningFolder(int pid)
    {
        try
        {
            var m = AssemblyPath().Match(File.ReadAllText($"/proc/{pid}/maps"));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(/\S*/tradebot/bin/[^/\s]+)/Trader\.[A-Za-z]+\.dll")]
    private static partial Regex AssemblyPath();

    /// <summary>"@1793816641" → UTC time; empty or "0" → null.</summary>
    public static DateTime? Time(string? value) =>
        value is { Length: > 1 } && value[0] == '@' && long.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime
            : null;
}
