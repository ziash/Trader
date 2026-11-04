#!/usr/bin/env bash
# Publishes TM (Release build) into a new folder ~/tradebot/bin/TM-<git hash>, points the link
# ~/tradebot/bin/TM at it (scripts/deploy-lib.sh) and installs or refreshes its systemd user service
# (deploy/trader-tm.service). Does not start, stop or restart the bot; it prints the command to use.
# Safe to run while the bot is running: no file the running process uses is changed; it keeps its
# own folder until it is restarted.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
units="$HOME/.config/systemd/user"
. "$repo/scripts/deploy-lib.sh"

deploy_build Trader.TM TM trader-tm

echo "Installing service → $units/trader-tm.service"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-tm.service" "$units/trader-tm.service"
systemctl --user daemon-reload

if [ "$switched" = no ]; then
  echo "Not switched yet: use the one-time command above."
elif systemctl --user is-active --quiet trader-tm; then
  echo "TM is running the previous build. To switch: systemctl --user restart trader-tm"
else
  echo "Not running. To start (now and at every login): systemctl --user enable --now trader-tm"
fi
