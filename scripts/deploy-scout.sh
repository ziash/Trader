#!/usr/bin/env bash
# Publishes Scout (Release build) to ~/tradebot/bin/Scout and installs or refreshes its
# systemd user service (deploy/trader-scout.service). Does not start, stop or restart the bot;
# it prints the command to use. Safe to run while the bot is running: the running process keeps
# its old files until it is restarted.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
bin="$HOME/tradebot/bin"
dest="$bin/Scout"
units="$HOME/.config/systemd/user"

echo "Publishing Trader.Scout (Release) → $dest"
mkdir -p "$bin"
rm -rf "${dest:?}.new"
dotnet publish "$repo/src/Trader.Scout" -c Release -o "$dest.new" --nologo -v quiet
rm -rf "${dest:?}.old"
if [ -d "$dest" ]; then mv "$dest" "$dest.old"; fi
mv "$dest.new" "$dest"
git -C "$repo" rev-parse --short HEAD > "$dest/VERSION"
echo "  version $(cat "$dest/VERSION")"

if [ ! -f "$HOME/tradebot/config/scout.json" ]; then
  echo "Copying config/scout.json → ~/tradebot/config/scout.json (first deploy)"
  install -m 644 "$repo/config/scout.json" "$HOME/tradebot/config/scout.json"
fi

echo "Installing service → $units/trader-scout.service"
mkdir -p "$units"
install -m 644 "$repo/deploy/trader-scout.service" "$units/trader-scout.service"
systemctl --user daemon-reload

if systemctl --user is-active --quiet trader-scout; then
  echo "Scout is running the previous build. To switch: systemctl --user restart trader-scout"
else
  echo "Not running. To start (now and at every login): systemctl --user enable --now trader-scout"
fi
