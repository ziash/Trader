#!/usr/bin/env bash
# Publishes the dashboard export (Release build) into a new folder ~/tradebot/bin/DashboardExport-<git hash> with
# dashboard-push.sh and dashboard-lib.sh beside it, points the link ~/tradebot/bin/DashboardExport at it
# (scripts/deploy-lib.sh), installs the upload's systemd user service and timer (deploy/trader-dashboard.*) and
# enables the timer (dashboard.md §3.1, §7). Touches no bot.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.DashboardExport DashboardExport trader-dashboard
install -m 755 "$repo/scripts/dashboard-push.sh" "$repo/scripts/dashboard-lib.sh" "$bin/DashboardExport/"

echo "Installing service and timer → $units/"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-dashboard.service" "$units/trader-dashboard.service"
install -m 644 "$repo/deploy/trader-dashboard.timer" "$units/trader-dashboard.timer"
systemctl --user daemon-reload
systemctl --user enable --now trader-dashboard.timer
systemctl --user list-timers trader-dashboard.timer --no-pager | head -2
