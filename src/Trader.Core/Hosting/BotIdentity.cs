using System.Text.RegularExpressions;

namespace Trader.Core.Hosting;

/// <summary>
/// Who is running: the bot's name (e.g. "AuditBot") and a per-process instance ID ("AuditBot-3f9a1c").
/// The instance ID appears on every log line and in every order label, so with many bots running
/// each log line and each fill can be traced to the process that caused it (plan 6.4, 10.8.2).
/// </summary>
public sealed partial record BotIdentity(string BotName, string InstanceId)
{
    /// <summary>Spotware limits order labels to 100 characters.</summary>
    public const int MaxLabelLength = 100;

    public static BotIdentity Create(string botName)
    {
        if (!NamePattern().IsMatch(botName))
            throw new ArgumentException("Bot name must be 1–24 letters/digits, starting with a letter (e.g. AuditBot)", nameof(botName));
        return new BotIdentity(botName, $"{botName}-{Guid.NewGuid():N}"[..(botName.Length + 7)]);
    }

    /// <summary>Order label: the instance ID, optionally followed by a leg name — "AuditBot-3f9a1c" or "TrendBot-3f9a1c-T1-S1".</summary>
    public string Label(string? leg = null)
    {
        if (leg is not null && !LegPattern().IsMatch(leg))
            throw new ArgumentException("Leg name may contain only letters, digits and '-'", nameof(leg));
        var label = leg is null ? InstanceId : $"{InstanceId}-{leg}";
        return label.Length <= MaxLabelLength ? label : throw new ArgumentException($"Label longer than {MaxLabelLength} characters");
    }

    /// <summary>Splits a label made by <see cref="Label"/> back into its parts (used when reconciling positions after a restart).</summary>
    public static bool TryParseLabel(string? label, out string botName, out string instanceId, out string? leg)
    {
        botName = instanceId = "";
        leg = null;
        var m = label is null ? null : LabelPattern().Match(label);
        if (m is not { Success: true })
            return false;

        botName = m.Groups["bot"].Value;
        instanceId = $"{botName}-{m.Groups["id"].Value}";
        leg = m.Groups["leg"].Success ? m.Groups["leg"].Value : null;
        return true;
    }

    public override string ToString() => InstanceId;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,23}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex LegPattern();

    [GeneratedRegex("^(?<bot>[A-Za-z][A-Za-z0-9]{0,23})-(?<id>[0-9a-f]{6})(-(?<leg>[A-Za-z0-9-]+))?$")]
    private static partial Regex LabelPattern();
}
