#!/usr/bin/env python3
"""Rewind's symbol file for IC Markets, from the broker conditions the dashboard exporter recorded on 06-Nov-2026 (read from the local log; no broker connection)."""
import csv, json, math, sys

src, out = sys.argv[1:]
DAY = {'Sunday': 0, 'Monday': 1, 'Tuesday': 2, 'Wednesday': 3, 'Thursday': 4, 'Friday': 5, 'Saturday': 6}
seen, rows = set(), []
with open(src, newline='', encoding='utf-8') as f:
    for r in csv.DictReader(f):
        if r['symbol'] in seen:
            continue
        seen.add(r['symbol'])
        name, lot_units = r['symbol'], float(r['lot_units'])
        bid, ask = float(r['bid']), float(r['ask'])
        digits = max(len(r['bid'].split('.')[1]) if '.' in r['bid'] else 0, len(r['ask'].split('.')[1]) if '.' in r['ask'] else 0)
        pip = round(-math.log10(float(r['pip_size'])))
        lot = int(lot_units * 100)
        # USD value of one lot, from the pip value the broker's conditions give: pip_value_per_lot / pip_size is USD per 1.0 of price for a lot.
        usd_per_price = float(r['pip_value_per_lot_usd']) / float(r['pip_size'])
        notional = (bid + ask) / 2 * usd_per_price
        margin = float(r['margin_buy_per_lot_usd'])
        commission = float(r['commission_per_lot_usd']) / 2 / notional * 1e6
        quote = {'EURUSD': 'USD', 'GBPUSD': 'USD', 'USDJPY': 'JPY', 'GBPJPY': 'JPY', 'EURJPY': 'JPY', 'AUDJPY': 'JPY', 'EURGBP': 'GBP', 'DE40': 'EUR'}.get(name, 'USD')
        rows.append({
            'name': name, 'base': r['lot_asset'], 'quote': quote, 'digits': digits, 'pipPosition': pip, 'lotSize': lot,
            'minVolume': int(round(float(r['min_lots']) * lot)), 'stepVolume': int(round(float(r['step_lots']) * lot)), 'maxVolume': int(round(float(r['max_lots']) * lot)),
            'commissionPerMillionUsd': round(commission, 4), 'leverage': round(notional / margin, 2) if margin > 0 else 0,
            'swapLong': float(r['swap_long']), 'swapShort': float(r['swap_short']), 'swapPercent': r['swap_type'].startswith('Percent'),
            'swapTripleDay': DAY.get(r['triple_swap_day'], -1),
        })
json.dump({'symbols': rows}, open(out, 'w'), indent=1)
for r in rows:
    print(r['name'], r['digits'], r['pipPosition'], r['lotSize'], r['minVolume'], r['stepVolume'], r['commissionPerMillionUsd'], r['leverage'], r['swapLong'], r['swapShort'], r['swapPercent'], r['swapTripleDay'])
