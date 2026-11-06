using System.Globalization;
using Trader.Core.OpenApi;

namespace Trader.Scout;

/// <summary>
/// How much more margin Scout may commit on an account (scout.md §2.5, issue-log.md #1). An order of Scout's holds no
/// margin while it waits, but once filled it holds its own margin × <see cref="ScoutConfig.MarginFactor"/> (TM mirrors it
/// and hedges both legs). So Scout counts what is in use now plus all its waiting orders as if filled, and places a new
/// order only while that stays within <see cref="ScoutConfig.MarginUsePercent"/> of equity.
/// </summary>
internal sealed class MarginBudget(AccountStatus status, ScoutConfig config, double waitingMargin)
{
    private double _committed = config.MarginFactor * waitingMargin;

    /// <summary>Margin still free for new orders (each counted × the factor), in the account currency; can be below 0.</summary>
    public double Left => status.Equity * config.MarginUsePercent / 100 - status.UsedMargin - _committed;

    /// <summary>True, and the order's share is booked, if an order holding <paramref name="margin"/> once filled still fits.</summary>
    public bool TryTake(double margin)
    {
        if (config.MarginFactor * margin > Left)
            return false;
        _committed += config.MarginFactor * margin;
        return true;
    }

    /// <summary>"needs $320.00 (4 × $80.00), $210.50 left of 50 % of equity $2,491.85 (in use $2,707.70, waiting orders $0.00)".</summary>
    public string Explain(double margin) =>
        $"needs {AccountStatus.Money(config.MarginFactor * margin)} ({config.MarginFactor.ToString("0.#", CultureInfo.InvariantCulture)} × {AccountStatus.Money(margin)}), "
        + $"{AccountStatus.Money(Left)} left of {config.MarginUsePercent.ToString("0.#", CultureInfo.InvariantCulture)} % of equity {AccountStatus.Money(status.Equity)} "
        + $"(in use {AccountStatus.Money(status.UsedMargin)}, waiting orders {AccountStatus.Money(_committed)})";
}
