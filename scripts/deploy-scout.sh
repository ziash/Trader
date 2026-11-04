#!/usr/bin/env bash
# Publishes Scout (Release build) into a new folder ~/tradebot/bin/Scout-<git hash>, points the link
# ~/tradebot/bin/Scout at it (scripts/deploy-lib.sh) and installs or refreshes its systemd user service
# (deploy/trader-scout.service). Does not start, stop or restart the bot; it prints the command to use.
# Safe to run while the bot is running: no file the running process uses is changed; it keeps its
# own folder until it is restarted.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.Scout Scout trader-scout

if [ ! -f "$HOME/tradebot/config/scout.json" ]; then
  echo "Copying config/scout.json → ~/tradebot/config/scout.json (first deploy)"
  install -m 644 "$repo/config/scout.json" "$HOME/tradebot/config/scout.json"
fi

echo "Installing service → $units/trader-scout.service"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-scout.service" "$units/trader-scout.service"
systemctl --user daemon-reload

if [ "$switched" = no ]; then
  echo "Not switched yet: use the one-time command above."
elif systemctl --user is-active --quiet trader-scout; then
  echo "Scout is running the previous build. To switch: systemctl --user restart trader-scout"
else
  echo "Not running. To start (now and at every login): systemctl --user enable --now trader-scout"
fi
