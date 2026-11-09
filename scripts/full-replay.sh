#!/usr/bin/env bash
# Whole-account replay (review Phase 3; Rewind spec §7.2): the real Scout and the real TM trade one simulated account over all symbols, in step with Rewind (--lockstep).
# No connection to any broker. Everything is written under the run folder, including the bots' state and logs; ~/tradebot and the running bots are not touched.
# Symbol details come from ~/rewind/config/symbols.pepperstone.json (Trader.Scout --export-symbols), over Rewind's own symbols.json.
#
#   scripts/full-replay.sh 2025-01-06 2025-02-03 [run-folder]
set -euo pipefail

FROM="$1"; TO="$2"
OUT="${3:-$HOME/rewind/runs/$FROM-$TO}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
REWIND="${REWIND:-$HOME/code/Rewind}"
BALANCE="${BALANCE:-5000}"
PORT="${PORT:-5037}"
case "$OUT" in "$HOME"/rewind/runs/*) ;; *) echo "The run folder must be under ~/rewind/runs (it is emptied first)"; exit 2;; esac
rm -rf "$OUT"; mkdir -p "$OUT/home/config" "$OUT/config" "$OUT/bin"

echo "== Building"
dotnet build "$REWIND/src/Rewind.Lab" -c Release -o "$OUT/bin/lab" -v q -nologo | grep -E " error |rror\(s\)" || true
dotnet build "$REPO/src/Trader.Scout" -c Release -o "$OUT/bin/scout" -v q -nologo | grep -E " error |rror\(s\)" || true
dotnet build "$REPO/src/Trader.TM" -c Release -o "$OUT/bin/tm" -v q -nologo | grep -E " error |rror\(s\)" || true

echo "== Settings: the repository's rule values, on Rewind's account"
python3 - "$REPO" "$REWIND" "$OUT" <<'PY'
import json, re, sys, os
repo, rewind, out = sys.argv[1:]
strip = lambda text: re.sub(r'^\s*//.*$|(?<=[,\[{\s])//[^"\n]*$', '', text, flags=re.M)
scout = open(f'{repo}/config/scout.json', encoding='utf-8').read()
scout = re.sub(r'"broker":\s*"\w+",\s*"accountId":\s*\d+,', '"broker": "rewind", "accountId": 9000001,', scout)
scout = re.sub(r'^\s*"symbolNames":.*\n', '', scout, flags=re.M)
scout, n = re.subn(r'"requireTm":\s*true', '"requireTm": false', scout)  # TM's health report runs on the computer's clock, the replay on its own
assert n == 1
# A weekly or peak halt lasts until the owner resets it, and a replay has no owner: left in force it ends the run within weeks. The daily halt stays.
scout, n = re.subn(r'"weeklyLossPercent":\s*[\d.]+,\s*"peakDrawdownPercent":\s*[\d.]+', '"weeklyLossPercent": 100, "peakDrawdownPercent": 100', scout)
assert n == 1
open(f'{out}/home/config/scout.json', 'w', encoding='utf-8').write(scout)
tm = open(f'{repo}/config/tm.json', encoding='utf-8').read()
symbols = ["EURUSD","GBPUSD","USDJPY","GBPJPY","EURJPY","AUDJPY","XAUUSD","XAGUSD","XPTUSD","US500","USTEC","DE40","XTIUSD","XBRUSD","XNGUSD","BTCUSD","ETHUSD","SOLUSD"]
start = tm.index('"accounts": ['); end = re.search(r'^  \],', tm[start:], flags=re.M).end() + start
tm = tm[:start] + '"accounts": [ { "broker": "rewind", "accountId": 9000001, "symbols": ' + json.dumps(symbols) + ' } ],' + tm[end:]
open(f'{out}/home/config/tm.json', 'w', encoding='utf-8').write(tm)
open(f'{out}/home/config/notifications.json', 'w').write('{ "repeatSeconds": 60, "channels": [] }\n')
home = os.path.expanduser('~')
live = f'{home}/rewind/config/symbols.json'
base = open(live if os.path.exists(live) else f'{rewind}/config/symbols.json', encoding='utf-8').read()
specs = {s['name']: s for s in json.loads(strip(base))['symbols']}
broker = f'{home}/rewind/config/symbols.pepperstone.json'
over = json.load(open(broker))['symbols']
specs.update({s['name']: s for s in over})
json.dump({'symbols': list(specs.values())}, open(f'{out}/config/symbols.json', 'w'), indent=1)
print(f'{len(over)} symbols with Pepperstone details, {len(specs) - len(over)} with Rewind\'s')
PY
cp "$REWIND/config/rewind.json" "$OUT/config/rewind.json"

echo "== Running $FROM → $TO"
"$OUT/bin/lab/rewind-lab" run --from "$FROM" --to "$TO" --out "$OUT/result" --config "$OUT/config" --bots 2 --port "$PORT" --balance "$BALANCE" > "$OUT/lab.log" 2>&1 &
LAB=$!
trap 'kill "$LAB" ${SCOUT:-} ${TM:-} 2>/dev/null || true' EXIT
until [ -f "$OUT/result/bots/secrets.env" ] && grep -q "Run from\|Open API for TM\|localhost" "$OUT/lab.log" 2>/dev/null; do kill -0 "$LAB"; sleep 1; done
sleep 2
export TRADEBOT_HOME="$OUT/home" TRADER_SECRETS_FILE="$OUT/result/bots/secrets.env"
dotnet "$OUT/bin/scout/Trader.Scout.dll" --lockstep > "$OUT/scout.log" 2>&1 &
SCOUT=$!
until grep -q "joined the lock-step (1)" "$OUT/lab.log"; do kill -0 "$LAB"; kill -0 "$SCOUT"; sleep 1; done
dotnet "$OUT/bin/tm/Trader.TM.dll" --config-dir "$OUT/home/config" --lockstep > "$OUT/tm.log" 2>&1 &
TM=$!
wait "$LAB"
tail -2 "$OUT/lab.log"
