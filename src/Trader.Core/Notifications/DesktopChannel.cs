using System.Diagnostics;

namespace Trader.Core.Notifications;

/// <summary>
/// Desktop pop-ups on Ubuntu through <c>notify-send</c> (package libnotify-bin), which talks to GNOME's
/// notification service over the session bus. Works from a terminal and from a systemd user service, as long
/// as the owner is logged in to the desktop. Critical notifications stay on screen until dismissed.
/// </summary>
public sealed class DesktopChannel(Func<ProcessStartInfo, CancellationToken, Task<int>>? run = null) : INotificationChannel
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> _run = run ?? RunAsync;

    public string Name => "desktop";

    public async Task SendAsync(Notification n, CancellationToken ct)
    {
        var exit = await _run(StartInfo(n), ct);
        if (exit != 0)
            throw new InvalidOperationException($"notify-send exited with code {exit}");
    }

    /// <summary>The notify-send command for <paramref name="n"/> (separate for tests).</summary>
    internal static ProcessStartInfo StartInfo(Notification n)
    {
        var (urgency, icon) = n.Level switch
        {
            NotificationLevel.Critical => ("critical", "dialog-error"),
            NotificationLevel.Warning => ("normal", "dialog-warning"),
            _ => ("low", "dialog-information"),
        };
        var info = new ProcessStartInfo("notify-send") { UseShellExecute = false };
        foreach (var arg in new[] { $"--app-name=Trader {n.BotName}", $"--urgency={urgency}", $"--icon={icon}", $"{n.BotName}: {n.Title}", n.Message })
            info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<int> RunAsync(ProcessStartInfo info, CancellationToken ct)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException("could not start notify-send");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException("notify-send did not finish within 10 s");
        }
        return process.ExitCode;
    }
}
