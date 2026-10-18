#!/usr/bin/env bash
# Publishes the Audit Bot (Release build) to ~/tradebot/bin/AuditBot and installs or refreshes its
# systemd user service (deploy/trader-auditbot.service). Does not start, stop or restart the bot;
# it prints the command to use. Safe to run while the bot is running: the running process keeps
# its old files until it is restarted.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
bin="$HOME/tradebot/bin"
dest="$bin/AuditBot"
units="$HOME/.config/systemd/user"

echo "Publishing Trader.AuditBot (Release) → $dest"
mkdir -p "$bin"
rm -rf "${dest:?}.new"
dotnet publish "$repo/src/Trader.AuditBot" -c Release -o "$dest.new" --nologo -v quiet
rm -rf "${dest:?}.old"
if [ -d "$dest" ]; then mv "$dest" "$dest.old"; fi
mv "$dest.new" "$dest"
git -C "$repo" rev-parse --short HEAD > "$dest/VERSION"
echo "  version $(cat "$dest/VERSION")"

echo "Installing service → $units/trader-auditbot.service"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-auditbot.service" "$units/trader-auditbot.service"
systemctl --user daemon-reload

if systemctl --user is-active --quiet trader-auditbot; then
  echo "The bot is running the previous build. To switch: systemctl --user restart trader-auditbot"
else
  echo "Not running. To start (now and at every login): systemctl --user enable --now trader-auditbot"
fi
