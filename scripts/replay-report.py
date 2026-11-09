#!/usr/bin/env python3
"""Validation numbers of a whole-account replay (review P-07; scripts/full-replay.sh makes the run).

    scripts/replay-report.py <run-folder> [<run-folder> ...] > report.md
    scripts/replay-report.py --self-test

A trade is Scout's or the owner's main with its mirror and hedges. Its result is the money it made after commission and swap.
"R" here is that result divided by the budget of a trade when it opened (riskPercent of equity), so trades of any size compare.
Intervals are day-block bootstrap: whole days are resampled, because trades on one day are not independent. Python's standard library only; a fixed seed, so a report repeats.
"""
import csv, glob, os, random, statistics, sys
from collections import Counter, defaultdict
from datetime import datetime

RISK_PERCENT = 0.5
GROUPS = {
    'USD': ['EURUSD', 'GBPUSD', 'USDJPY'], 'JPY': ['GBPJPY', 'EURJPY', 'AUDJPY'], 'Metals': ['XAUUSD', 'XAGUSD', 'XPTUSD'],
    'Indices': ['US500', 'USTEC', 'DE40'], 'Energy': ['XTIUSD', 'XBRUSD', 'XNGUSD'], 'Crypto': ['BTCUSD', 'ETHUSD', 'SOLUSD'],
}
GROUP_OF = {s: g for g, members in GROUPS.items() for s in members}


def t(text):
    return datetime.strptime(text, '%Y-%m-%d %H:%M:%S')


def rows(path):
    with open(path, newline='', encoding='utf-8') as f:
        return list(csv.DictReader(f))


def profit_factor(values):
    won, lost = sum(v for v in values if v > 0), -sum(v for v in values if v < 0)
    return won / lost if lost else float('inf')


def max_drawdown(curve):
    """Largest fall from a peak, as a fraction of that peak."""
    peak, worst = None, 0.0
    for v in curve:
        peak = v if peak is None or v > peak else peak
        if peak > 0:
            worst = max(worst, (peak - v) / peak)
    return worst


def longest_losing_run(values):
    run = best = 0
    for v in values:
        run = run + 1 if v < 0 else 0
        best = max(best, run)
    return best


def day_bootstrap(days, n=5000, seed=42):
    """95 % interval of the mean per trade. days: a list of (sum of R on the day, trades on the day)."""
    if not days:
        return (0.0, 0.0)
    rng = random.Random(seed)
    means = []
    for _ in range(n):
        pick = [days[rng.randrange(len(days))] for _ in days]
        count = sum(p[1] for p in pick)
        means.append(sum(p[0] for p in pick) / count if count else 0.0)
    means.sort()
    return (means[int(0.025 * n)], means[int(0.975 * n)])


def load(run):
    res = os.path.join(run, 'result')
    curve = [(t(r['time_utc']), float(r['equity_usd']), float(r['balance_usd']), float(r['margin_usd']), int(r['positions']), int(r['orders'])) for r in rows(f'{res}/equity.csv')]
    legs = rows(f'{res}/tm-trades.csv')
    fills = rows(f'{res}/trades.csv')
    events = rows(f'{res}/events.csv')
    orders = [r for f in sorted(glob.glob(f'{run}/home/logs/rewind/*/Scout-*/*/orders.csv')) for r in rows(f)]
    return curve, legs, fills, events, orders


def trades_of(legs, curve):
    """One row per trade, with its R from the equity when it opened."""
    grouped = defaultdict(list)
    for leg in legs:
        grouped[leg['trade']].append(leg)
    out = []
    i = 0
    for name, ls in sorted(grouped.items(), key=lambda x: min(l['open_time_utc'] for l in x[1])):
        opened = t(min(l['open_time_utc'] for l in ls))
        while i + 1 < len(curve) and curve[i + 1][0] <= opened:
            i += 1
        usd = sum(float(l['pnl_usd']) for l in ls)
        budget = curve[i][1] * RISK_PERCENT / 100
        out.append({'symbol': ls[0]['symbol'], 'opened': opened, 'closed': t(max(l['close_time_utc'] for l in ls)), 'usd': usd, 'r': usd / budget if budget > 0 else 0.0})
    return out


def line(name, group):
    r = [x['r'] for x in group]
    return f"| {name} | {len(r)} | {sum(r) / len(r):+.3f} | {100 * sum(1 for v in r if v > 0) / len(r):.0f} % | {min(profit_factor(r), 99):.2f} | {sum(x['usd'] for x in group):+,.2f} |" if r else f'| {name} | 0 | | | | |'


def report(run):
    curve, legs, fills, events, orders = load(run)
    trades = trades_of(legs, curve)
    r = [x['r'] for x in trades]
    days = defaultdict(lambda: [0.0, 0])
    for x in trades:
        d = days[x['closed'].date()]
        d[0] += x['r']
        d[1] += 1
    low, high = day_bootstrap(list(map(tuple, days.values())))
    equity = [c[1] for c in curve]
    daily = defaultdict(list)
    for c in curve:
        daily[c[0].date()].append(c[1])
    closes = [v[-1] for _, v in sorted(daily.items())]
    day_change = [(b - a) / a for a, b in zip(closes, closes[1:]) if a > 0]
    margin_use = [100 * c[3] / c[1] for c in curve if c[1] > 0]
    events_of = Counter(e['event'] for e in events)
    reasons = Counter(f['close_reason'] for f in fills)
    commission = sum(float(f['commission_usd']) for f in fills)
    placed = sum(1 for o in orders if o['action'] == 'place' and o['result'] == 'ok')
    filled = sum(1 for o in orders if o['action'] == 'filled')
    print(f"## {os.path.basename(run.rstrip('/'))}: {curve[0][0]:%d %b %Y} to {curve[-1][0]:%d %b %Y}\n")
    print('| Measure | Value |\n|---|---|')
    print(f"| Equity | ${curve[0][1]:,.2f} → ${curve[-1][1]:,.2f} ({100 * (curve[-1][1] / curve[0][1] - 1):+.1f} %) |")
    print(f'| Largest fall from a peak | {100 * max_drawdown(equity):.1f} % |')
    print(f"| Worst day; best day | {100 * min(day_change):+.1f} %; {100 * max(day_change):+.1f} % |" if day_change else '| Worst day | – |')
    print(f'| Whole trades finished | {len(trades)} (and {curve[-1][4]} positions still open at the end) |')
    print(f'| Mean result per trade, after costs | **{statistics.mean(r):+.3f} R** (95 % interval {low:+.3f} to {high:+.3f}) |' if r else '| Mean | – |')
    print(f'| Trades won | {100 * sum(1 for v in r if v > 0) / max(1, len(r)):.1f} % |')
    print(f'| Profit factor | {min(profit_factor(r), 99):.2f} |')
    print(f'| Longest losing run | {longest_losing_run(r)} trades |')
    print(f'| Worst trade; best trade | {min(r):+.2f} R; {max(r):+.2f} R |' if r else '| Worst trade | – |')
    finished = sum(x['usd'] for x in trades)
    print(f"| Money check | balance changed {curve[-1][2] - curve[0][2]:+,.2f}: finished trades {finished:+,.2f}, parts of trades still open {curve[-1][2] - curve[0][2] - finished:+,.2f} |")
    print(f'| Commission paid | ${commission:,.2f} |')
    print(f"| Margin use: typical; highest | {statistics.median(margin_use):.0f} %; {max(margin_use):.0f} % |")
    print(f"| Stop-outs by the broker | {reasons.get('StopOut', 0)} |")
    print(f"| Orders refused at their price for lack of margin | {events_of.get('OrderRejected', 0)} |")
    print()
    print('| Scout\'s orders | Count |\n|---|---|')
    print(f'| Placed | {placed} |\n| Filled | {filled} |')
    for (result, n) in Counter(o['result'] for o in orders if o['action'] == 'skip').most_common():
        print(f'| Not placed: {result} | {n} |')
    print(f"| Refused because the level had passed | {sum(1 for o in orders if o['action'] == 'place' and o['result'] == 'refused')} |")
    print()
    table = '| {} | Trades | Mean R | Won | Profit factor | USD |\n|---|---|---|---|---|---|'
    print(table.format('Symbol'))
    for s in sorted({x['symbol'] for x in trades}):
        print(line(s, [x for x in trades if x['symbol'] == s]))
    print()
    print(table.format('Group'))
    for g in GROUPS:
        print(line(g, [x for x in trades if GROUP_OF.get(x['symbol']) == g]))
    print()
    print(table.format('Half-year'))
    for h in sorted({(x['closed'].year, 1 if x['closed'].month <= 6 else 2) for x in trades}):
        print(line(f'{h[0]}-H{h[1]}', [x for x in trades if (x['closed'].year, 1 if x['closed'].month <= 6 else 2) == h]))
    print()


def self_test():
    assert profit_factor([2, -1, 1, -1]) == 1.5
    assert abs(max_drawdown([100, 120, 90, 130, 117]) - 0.25) < 1e-9
    assert longest_losing_run([1, -1, -1, 2, -1, -1, -1, 0]) == 3
    assert day_bootstrap([(1.0, 1), (1.0, 1)]) == (1.0, 1.0)
    low, high = day_bootstrap([(-1.0, 1), (1.0, 1)] * 50)
    assert low < 0 < high and day_bootstrap([(-1.0, 1), (1.0, 1)] * 50) == (low, high)
    curve = [(datetime(2025, 1, 1), 1000.0, 1000.0, 0.0, 0, 0), (datetime(2025, 1, 2), 2000.0, 2000.0, 0.0, 0, 0)]
    legs = [{'trade': 'P1', 'symbol': 'EURUSD', 'open_time_utc': '2025-01-01 10:00:00', 'close_time_utc': '2025-01-01 12:00:00', 'pnl_usd': '10'},
            {'trade': 'P1', 'symbol': 'EURUSD', 'open_time_utc': '2025-01-01 10:00:00', 'close_time_utc': '2025-01-01 13:00:00', 'pnl_usd': '-5'},
            {'trade': 'P2', 'symbol': 'XAUUSD', 'open_time_utc': '2025-01-02 10:00:00', 'close_time_utc': '2025-01-02 12:00:00', 'pnl_usd': '-20'}]
    got = trades_of(legs, curve)
    assert [round(x['r'], 6) for x in got] == [1.0, -2.0], got  # $5 of a $5 budget; -$20 of a $10 budget
    assert got[0]['closed'] == datetime(2025, 1, 1, 13)
    print('ok')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        for folder in sys.argv[1:]:
            report(folder)
