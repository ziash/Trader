#!/usr/bin/env bash
# Publishes the audit report tool (Release build) into a new folder ~/tradebot/bin/AuditReport-<git hash>,
# points the link ~/tradebot/bin/AuditReport at it (scripts/deploy-lib.sh), installs its systemd user service
# and daily timer (deploy/trader-auditreport.*), and enables the timer. Then makes any missing daily reports
# at once. Safe while the Audit Bot runs: the report only reads the logs.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.AuditReport AuditReport trader-auditreport

echo "Installing service and timer → $units/"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-auditreport.service" "$units/trader-auditreport.service"
install -m 644 "$repo/deploy/trader-auditreport.timer" "$units/trader-auditreport.timer"
systemctl --user daemon-reload
systemctl --user enable --now trader-auditreport.timer
systemctl --user list-timers trader-auditreport.timer --no-pager | head -2

echo "Making any missing daily reports now"
systemctl --user start trader-auditreport.service
journalctl --user -u trader-auditreport.service --no-pager -n 5 -o cat
