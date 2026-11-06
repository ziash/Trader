#!/usr/bin/env bash
# The 1-min control sync (dashboard.md §3.4, §6). Fetches the control files the site wrote, ~/trader-dashboard/controls/
# scout-<broker>-<ctid>.json, into ~/tradebot/control/ where Scout reads them (scout.md §2.6), and sends Scout's
# acknowledgements (scout-<broker>-<ctid>.ack.json) back up. Only those names, only that folder, both ways; a file is
# replaced only when its content changed, so Scout sees a new time only for a real change.
# Run by trader-dashboard-sync.timer every minute (deploy/trader-dashboard-sync.*), or by hand.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
. "$here/dashboard-lib.sh"

ctl="$HOME/tradebot/control"
state="$HOME/tradebot/dashboard"
mkdir -p "$ctl" "$state"
chmod 700 "$ctl"
exec 9>"$state/sync.lock"
flock -n 9 || { echo "Another sync is running; skipped."; exit 0; }

name='^scout-[A-Za-z0-9]{1,32}-[0-9]{1,12}\.json$'
tmp=$(mktemp -d "$state/sync-in.XXXXXX")
trap 'rm -rf "$tmp"' EXIT

# Down: the control files only (never an acknowledgement, never anything else), at most 64 KB each.
dash_rsync -rt --max-size=64k --exclude='*.ack.json' --include='scout-*.json' --exclude='*' \
  "$dash_remote:$dash_home/controls/" "$tmp/" 2>/dev/null || echo "Controls not fetched (none on the server yet, or no connection)."
got=0
for f in "$tmp"/*; do
  [ -f "$f" ] && [ ! -L "$f" ] || continue
  base=$(basename "$f")
  if ! grep -Eq "$name" <<<"$base"; then
    echo "Ignored $base (not a control file name)"
    continue
  fi
  if ! cmp -s "$f" "$ctl/$base"; then
    install -m 600 "$f" "$ctl/$base.tmp" && mv -f "$ctl/$base.tmp" "$ctl/$base"
    echo "New controls: $base"
    got=$((got + 1))
  fi
done

# Up: Scout's acknowledgements.
shopt -s nullglob
acks=("$ctl"/scout-*.ack.json)
if [ ${#acks[@]} -gt 0 ]; then
  dash_rsync -t --chmod=F600 "${acks[@]}" "$dash_remote:$dash_home/controls/"
fi
echo "Sync done: ${got} new control file(s), ${#acks[@]} acknowledgement(s) sent"
