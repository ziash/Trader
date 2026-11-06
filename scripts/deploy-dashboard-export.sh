#!/usr/bin/env bash
# Publishes the dashboard export (Release build) into a new folder ~/tradebot/bin/DashboardExport-<git hash> with
# dashboard-push.sh and dashboard-lib.sh beside it, points the link ~/tradebot/bin/DashboardExport at it
# (scripts/deploy-lib.sh), installs the upload's and the control sync's systemd user services and timers
# (deploy/trader-dashboard*.*) and enables the timers (dashboard.md §3.1, §3.4, §7). Touches no bot.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.DashboardExport DashboardExport trader-dashboard
install -m 755 "$repo/scripts/dashboard-push.sh" "$repo/scripts/dashboard-sync.sh" "$repo/scripts/dashboard-lib.sh" "$bin/DashboardExport/"

echo "Installing service and timer → $units/"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-dashboard.service" "$units/trader-dashboard.service"
install -m 644 "$repo/deploy/trader-dashboard.timer" "$units/trader-dashboard.timer"
install -m 644 "$repo/deploy/trader-dashboard-sync.service" "$units/trader-dashboard-sync.service"
install -m 644 "$repo/deploy/trader-dashboard-sync.timer" "$units/trader-dashboard-sync.timer"
systemctl --user daemon-reload
systemctl --user enable --now trader-dashboard.timer trader-dashboard-sync.timer
systemctl --user list-timers 'trader-dashboard*' --no-pager | head -3
