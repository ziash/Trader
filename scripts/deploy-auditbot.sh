#!/usr/bin/env bash
# Publishes the Audit Bot (Release build) into a new folder ~/tradebot/bin/AuditBot-<git hash>, points the link
# ~/tradebot/bin/AuditBot at it (scripts/deploy-lib.sh) and installs or refreshes its systemd user service
# (deploy/trader-auditbot.service). Does not start, stop or restart the bot; it prints the command to use.
# Safe to run while the bot is running: no file the running process uses is changed; it keeps its
# own folder until it is restarted.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.AuditBot AuditBot trader-auditbot

echo "Installing service → $units/trader-auditbot.service"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-auditbot.service" "$units/trader-auditbot.service"
systemctl --user daemon-reload

if [ "$switched" = no ]; then
  echo "Not switched yet: use the one-time command above."
elif systemctl --user is-active --quiet trader-auditbot; then
  echo "The bot is running the previous build. To switch: systemctl --user restart trader-auditbot"
else
  echo "Not running. To start (now and at every login): systemctl --user enable --now trader-auditbot"
fi
