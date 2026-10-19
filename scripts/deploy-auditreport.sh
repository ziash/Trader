#!/usr/bin/env bash
# Publishes the audit report tool (Release build) to ~/tradebot/bin/AuditReport, installs its systemd user
# service and daily timer (deploy/trader-auditreport.*), and enables the timer. Then makes any missing
# daily reports at once. Safe while the Audit Bot runs: the report only reads the logs.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
bin="$HOME/tradebot/bin"
dest="$bin/AuditReport"
units="$HOME/.config/systemd/user"

echo "Publishing Trader.AuditReport (Release) → $dest"
mkdir -p "$bin"
rm -rf "${dest:?}.new"
dotnet publish "$repo/src/Trader.AuditReport" -c Release -o "$dest.new" --nologo -v quiet
rm -rf "${dest:?}.old"
if [ -d "$dest" ]; then mv "$dest" "$dest.old"; fi
mv "$dest.new" "$dest"
git -C "$repo" rev-parse --short HEAD > "$dest/VERSION"
echo "  version $(cat "$dest/VERSION")"

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
