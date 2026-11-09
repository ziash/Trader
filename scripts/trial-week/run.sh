#!/usr/bin/env bash
# A real trial stretch against the replay (review P-06; report: docs/bots/scout/reviews/2026-11-09-phase3-real-week.md).
# IC Markets demo 49123893, 04-Nov-2026 18:15 to 06-Nov-2026 16:12 UTC, with the builds that really ran then (Scout 6711a92, TM 2b56e33), the lock-step switch added to throw-away copies.
#
#   WORK=~/rewind/work/trial-week scripts/trial-week/run.sh [candle-folder]
#
# Two replays: on Dukascopy's history, and on IC's own 5-minute candles. The candles are read from the demo account once, read-only, unless a folder of them is given.
# Writes under $WORK and ~/rewind/runs/trial-ic-*. No order reaches a broker: the bots talk to Rewind only (CTRADER_ENV=replay, localhost).
set -euo pipefail
WORK="${WORK:?set WORK to an empty working folder}"; REPO="$(cd "$(dirname "$0")/../.." && pwd)"; HERE="$REPO/scripts/trial-week"; REWIND="${REWIND:-$HOME/code/Rewind}"
ACCOUNT="$HOME/tradebot/logs/brokerA/49123893"; FROM=2026-11-04T18:15; TO=2026-11-06T16:12; BALANCE=4749.93
trap 'kill $(jobs -p) 2>/dev/null || true' EXIT
mkdir -p "$WORK/old/scout" "$WORK/old/tm" "$WORK/fetch/home/config"

git -C "$REPO" archive 6711a92 | tar -x -C "$WORK/old/scout"
git -C "$REPO" archive 2b56e33 | tar -x -C "$WORK/old/tm"
python3 "$HERE/add-lockstep.py" "$WORK/old" "$REPO/src/Trader.Core/OpenApi/LockStep.cs"
dotnet build "$WORK/old/scout/src/Trader.Scout" -c Release -o "$WORK/old/out/scout" -v q -nologo | grep -E " error |rror\(s\)"
dotnet build "$WORK/old/tm/src/Trader.TM" -c Release -o "$WORK/old/out/tm" -v q -nologo | grep -E " error |rror\(s\)"
python3 "$HERE/conditions-to-symbols.py" "$ACCOUNT/Dashboard/2026-11-06/conditions.csv" "$WORK/ic-symbols.json" > /dev/null

CANDLES="${1:-}"
if [ -z "$CANDLES" ]; then
  CANDLES="$WORK/fetch/candles"; mkdir -p "$CANDLES" "$WORK/fetch/src"
  git -C "$REPO" archive HEAD | tar -x -C "$WORK/fetch/src"
  sed -i 's|^        \["H1"\] = (ProtoOATrendbarPeriod.H1|        ["M5"] = (ProtoOATrendbarPeriod.M5, TimeSpan.FromMinutes(5), "H1"),\n&|' "$WORK/fetch/src/src/Trader.Scout/ScoutConfig.cs"
  dotnet build "$WORK/fetch/src/src/Trader.Scout" -c Release -o "$WORK/fetch/out" -v q -nologo | grep -E " error |rror\(s\)"
  git -C "$REPO" show 6711a92:config/scout.json > "$WORK/fetch/home/config/scout.json"
  echo '{ "repeatSeconds": 60, "channels": [] }' > "$WORK/fetch/home/config/notifications.json"
  for s in EURUSD GBPUSD USDJPY GBPJPY EURJPY AUDJPY EURGBP XAUUSD XAGUSD XPTUSD US500 USTEC DE40 XTIUSD XBRUSD XNGUSD BTCUSD ETHUSD SOLUSD; do
    TRADEBOT_HOME="$WORK/fetch/home" dotnet "$WORK/fetch/out/Trader.Scout.dll" --config-dir "$WORK/fetch/home/config" --candles "$s" M5 2026-10-01 "$CANDLES/$s.csv" > /dev/null
    sleep 1
  done
fi
python3 "$HERE/broker-history.py" "$CANDLES" "$HOME/rewind/history" "$WORK/ic-history" --spike > /dev/null

replay() {  # name, port, extra options for Rewind
  local OUT="$HOME/rewind/runs/$1" PORT="$2"; shift 2
  rm -rf "$OUT"; mkdir -p "$OUT/home/config" "$OUT/config" "$OUT/bin"
  dotnet build "$REWIND/src/Rewind.Lab" -c Release -o "$OUT/bin/lab" -v q -nologo | grep -E " error |rror\(s\)"
  python3 - "$REPO" "$OUT" <<'PY'
import json, re, subprocess, sys
repo, out = sys.argv[1:]
show = lambda c, f: subprocess.check_output(['git', '-C', repo, 'show', f'{c}:{f}'], text=True)
scout = show('6711a92', 'config/scout.json')
scout, n = re.subn(r'"broker":\s*"brokerA",\s*"accountId":\s*49123893,', '"broker": "rewind", "accountId": 9000001,', scout); assert n == 1
open(f'{out}/home/config/scout.json', 'w').write(scout)
tm = show('2b56e33', 'config/tm.json')
symbols = ["EURUSD","GBPUSD","USDJPY","GBPJPY","EURJPY","AUDJPY","EURGBP","XAUUSD","XAGUSD","XPTUSD","US500","USTEC","DE40","XTIUSD","XBRUSD","XNGUSD","BTCUSD","ETHUSD","SOLUSD"]
start = tm.index('"accounts": ['); end = re.search(r'^  \],', tm[start:], flags=re.M).end() + start
open(f'{out}/home/config/tm.json', 'w').write(tm[:start] + '"accounts": [ { "broker": "rewind", "accountId": 9000001, "symbols": ' + json.dumps(symbols) + ' } ],' + tm[end:])
open(f'{out}/home/config/notifications.json', 'w').write('{ "repeatSeconds": 60, "channels": [] }\n')
PY
  cp "$WORK/ic-symbols.json" "$OUT/config/symbols.json"; cp "$REWIND/config/rewind.json" "$OUT/config/rewind.json"
  "$OUT/bin/lab/rewind-lab" run --from "$FROM" --to "$TO" --out "$OUT/result" --config "$OUT/config" --bots 2 --port "$PORT" --balance "$BALANCE" "$@" > "$OUT/lab.log" 2>&1 &
  local LAB=$! SCOUT TM
  until [ -f "$OUT/result/bots/secrets.env" ] && grep -q "Run from\|Open API for TM\|localhost" "$OUT/lab.log" 2>/dev/null; do kill -0 "$LAB"; sleep 1; done
  sleep 2
  export TRADEBOT_HOME="$OUT/home" TRADER_SECRETS_FILE="$OUT/result/bots/secrets.env"
  dotnet "$WORK/old/out/scout/Trader.Scout.dll" --config-dir "$OUT/home/config" --lockstep > "$OUT/scout.log" 2>&1 &
  SCOUT=$!
  until grep -q "joined the lock-step (1)" "$OUT/lab.log"; do kill -0 "$LAB"; kill -0 "$SCOUT"; sleep 1; done
  dotnet "$WORK/old/out/tm/Trader.TM.dll" --config-dir "$OUT/home/config" --lockstep > "$OUT/tm.log" 2>&1 &
  TM=$!
  wait "$LAB"
  kill "$SCOUT" "$TM" 2>/dev/null || true
  unset TRADEBOT_HOME TRADER_SECRETS_FILE
  tail -1 "$OUT/lab.log"
  python3 "$REPO/scripts/replay-compare.py" "$ACCOUNT" "$OUT" "2026-11-04 18:15:00" "2026-11-06 16:12:00" 2>/dev/null > "$WORK/compare-$(basename "$OUT").md"
}
replay trial-ic-2026-11-04 5041
replay trial-ic-2026-11-04-broker 5042 --history "$WORK/ic-history"
echo "Results: $WORK/compare-*.md"
