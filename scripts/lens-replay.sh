#!/usr/bin/env bash
# Scout Lens dataset (docs/bots/scout/scout-lens.md §8 step 1): every setup Scout would have traded on past candles, with
# its result in R. Three steps, no broker connection, nothing touches the running bots:
#   1. Rewind exports 1H / 4H / daily candles from its Dukascopy history     (rewind-lab candles  → ~/rewind/lab/candles/)
#   2. Scout's detector replays them and writes the orders it would place     (Trader.Scout --replay … --orders → ~/tradebot/lens/orders/)
#   3. Rewind's simulated broker plays the orders out: fills, trailing stop,  (rewind-lab outcomes → ~/rewind/lab/outcomes.csv)
#      one cancels the other, expiry, refusals
# Setup settings and expiry come from ~/tradebot/config/scout.json. Rewind: ~/code/Rewind (its own repository).
#
#   scripts/lens-replay.sh                    all symbols Rewind has history for
#   scripts/lens-replay.sh EURUSD,BTCUSD      some of them
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
REWIND="${REWIND:-$HOME/code/Rewind}"
LAB="$HOME/rewind/lab"
ORDERS="$HOME/tradebot/lens/orders"
SYMBOLS="${1:-}"

echo "== Building rewind-lab and Scout"
dotnet build "$REWIND/src/Rewind.Lab" -c Release -o "$HOME/rewind/bin/lab" -v q -nologo | grep -E "error|Warn" || true
dotnet build "$REPO/src/Trader.Scout" -c Release -v q -nologo | grep -E "error|Warn" || true
SCOUT="$REPO/src/Trader.Scout/bin/Release/net10.0/Trader.Scout"

echo "== 1. Candles"
"$HOME/rewind/bin/lab/rewind-lab" candles ${SYMBOLS:+--symbols "$SYMBOLS"} --out "$LAB/candles"

echo "== 2. Scout's orders"
mkdir -p "$ORDERS"
rm -f "$ORDERS"/*.orders.csv
for h1 in "$LAB"/candles/*-H1.csv; do
  sym="$(basename "$h1" -H1.csv)"
  if [[ -n "$SYMBOLS" && ",$SYMBOLS," != *",$sym,"* ]]; then continue; fi
  "$SCOUT" --replay "$h1" --higher "$LAB/candles/$sym-H4.csv" --symbol "$sym" --orders "$ORDERS/$sym-H1.orders.csv"
done

echo "== 3. Outcomes"
"$HOME/rewind/bin/lab/rewind-lab" outcomes "$ORDERS"/*.orders.csv --out "$LAB/outcomes.csv"
