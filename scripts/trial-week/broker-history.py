#!/usr/bin/env python3
"""Rewind history from IC Markets' own 5-minute bid candles. The broker's history has no ask, so the ask is bid + Dukascopy's spread of that 5-minute bar
(the same bar, or the symbol's median when Dukascopy has none); SOLUSD, which Dukascopy stopped on 22-Aug-2026, gets IC's quoted spread. With --spike, the one ask
the broker's deals recorded in issue #2 is written into its bar: GBPUSD 05-Nov 22:00, ask high 1.34644."""
import csv, glob, os, statistics, sys

candles, dukas, out = sys.argv[1:4]
spike = '--spike' in sys.argv
IC_SPREAD = {'SOLUSD': 0.122}
END = '2026-11-07 00:00:00'
for path in sorted(glob.glob(f'{candles}/*.csv')):
    symbol = os.path.basename(path)[:-4]
    spreads = {}
    for f in sorted(glob.glob(f'{dukas}/{symbol}/2026-1*.m5.csv')):
        for r in csv.DictReader(open(f, newline='')):
            spreads[r['time_utc']] = tuple(float(r[f'ask_{k}']) - float(r[f'bid_{k}']) for k in ('open', 'high', 'low', 'close'))
    typical = (0.0,) * 4
    if spreads:
        values = list(spreads.values())
        typical = tuple(statistics.median(v[i] for v in values) for i in range(4))
    elif symbol in IC_SPREAD:
        typical = (IC_SPREAD[symbol],) * 4
    else:
        raise SystemExit(f'{symbol}: no spread')
    rows = list(csv.DictReader(open(path, newline='')))
    digits = max(len(rows[0][k].split('.')[1]) if '.' in rows[0][k] else 0 for k in ('open', 'high', 'low', 'close'))
    by_month = {}
    hit = missed = 0
    for r in rows:
        if r['time_utc'] >= END:
            continue
        s = spreads.get(r['time_utc'])
        hit += s is not None
        missed += s is None
        s = s or typical
        o, h, l, c = float(r['open']), float(r['high']), float(r['low']), float(r['close'])
        ask = [o + s[0], h + s[1], l + s[2], c + s[3]]
        if spike and symbol == 'GBPUSD' and r['time_utc'] == '2026-11-05 22:00:00':
            ask[1] = max(ask[1], 1.34644)
        ask[1] = max(ask)
        ask[2] = min(ask)
        line = [r['time_utc']] + [f'{v:.{digits}f}' for v in (o, h, l, c, *ask)] + ['1']
        by_month.setdefault(r['time_utc'][:7], []).append(line)
    os.makedirs(f'{out}/{symbol}', exist_ok=True)
    for month, lines in by_month.items():
        with open(f'{out}/{symbol}/{month}.m5.csv', 'w', newline='') as f:
            w = csv.writer(f, lineterminator='\n')
            w.writerow('time_utc,bid_open,bid_high,bid_low,bid_close,ask_open,ask_high,ask_low,ask_close,volume'.split(','))
            w.writerows(lines)
    print(symbol, 'bars', sum(map(len, by_month.values())), 'spread from same bar', hit, 'median', missed)
