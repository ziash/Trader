#!/usr/bin/env bash
# The 30-min data upload (dashboard.md §3.1, §6). Trader.DashboardExport copies the allowlisted files into
# ~/tradebot/dashboard/outbox and checks them for secrets; this script checks the outbox against the allowlist
# once more, sends it by rsync over SSH to ~/trader-dashboard/data/ on Hostinger, then writes and sends status.json
# with this upload's numbers. Nothing on the laptop is changed but the outbox; the server keeps what it has.
# Run by trader-dashboard.timer at :02 and :32 (deploy/trader-dashboard.*), or by hand:
#
#   scripts/dashboard-push.sh             upload now
#   scripts/dashboard-push.sh --dry-run   stage and check, list what would be sent, send nothing
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
. "$here/dashboard-lib.sh"

dry=no
case "${1:-}" in
  --dry-run) dry=yes ;;
  "") ;;
  *) echo "Usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

state="$HOME/tradebot/dashboard"
outbox="$state/outbox"
mkdir -p "$outbox"
exec 9>"$state/push.lock"
flock -n 9 || { echo "Another upload is running; skipped."; exit 0; }

# The exporter sits next to this script once deployed (~/tradebot/bin/DashboardExport); from the repo, the deployed
# one, or (before the first deploy) a build of the repo's code.
if [ -f "$here/Trader.DashboardExport.dll" ]; then
  exporter=(dotnet "$here/Trader.DashboardExport.dll")
elif [ -f "$HOME/tradebot/bin/DashboardExport/Trader.DashboardExport.dll" ]; then
  exporter=(dotnet "$HOME/tradebot/bin/DashboardExport/Trader.DashboardExport.dll")
else
  exporter=(dotnet run --project "$here/../src/Trader.DashboardExport" --)
fi
export_() { "${exporter[@]}" "$@" --outbox "$outbox"; }

find "$outbox" -name '*.tmp' -type f -delete   # left by an interrupted run
export_ collect                               # exit 3 = a file held a secret: nothing is sent

# Second guard: every file in the outbox must be on the allowlist (the same list as Outbox.Allowlist).
day='[0-9]{4}-[0-9]{2}-[0-9]{2}'
acct='logs/[A-Za-z0-9]{1,32}/[0-9]{1,12}'
inst='[A-Za-z0-9]{1,32}'
allow="^($acct/Scout-$inst/$day/((orders|setups|events)\.csv|charts/[A-Za-z0-9._-]{1,120}\.svg)"
allow+="|$acct/TM-$inst/$day/(actions|events)\.csv"
allow+="|reports/(daily-compare-$day|summary-${day}_$day)\.csv|status\.json)$"
odd=$(cd "$outbox" && find . -mindepth 1 \( -type f -o -type l \) -printf '%P\n' | grep -Ev "$allow" || true)
odd+=$(cd "$outbox" && find . -mindepth 1 -type l -printf '%P\n')
if [ -n "$odd" ]; then
  first=$(head -1 <<<"$odd")
  export_ refuse "a file in the outbox is not on the allowlist: $first" || true
  exit 3
fi

if [ "$dry" = yes ]; then
  export_ status
  echo "Dry run: would send to $dash_remote:$dash_home/data/ —"
  dash_rsync -rtn --itemize-changes "$outbox/" "$dash_remote:$dash_home/data/" | grep -v '/$' || true
  echo "Nothing sent."
  exit 0
fi

start=$(date -u +%s.%N)
stats=$(dash_rsync -rt --chmod=D700,F600 --exclude=status.json --stats "$outbox/" "$dash_remote:$dash_home/data/")
files=$(sed -n 's/^Number of regular files transferred: *\([0-9,]*\).*/\1/p' <<<"$stats" | tr -d ,)
seconds=$(awk -v a="$start" -v b="$(date -u +%s.%N)" 'BEGIN { printf "%.1f", b - a }')

export_ status --upload-utc "$(date -u -d "@${start%.*}" '+%Y-%m-%d %H:%M:%S')" --upload-seconds "$seconds" --upload-files "${files:-0}"
dash_rsync -t --chmod=F600 "$outbox/status.json" "$dash_remote:$dash_home/data/status.json"
echo "Sent ${files:-0} changed file(s) in ${seconds} s to $dash_site"
