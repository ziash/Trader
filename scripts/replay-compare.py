#!/usr/bin/env python3
"""A replay against what the broker really did (review P-06): the same window, order by order and trade by trade.

    scripts/replay-compare.py <real-account-log-folder> <replay-run-folder> "<from>" "<to>" > compare.md
    scripts/replay-compare.py --self-test

The real side is read from the bots' own logs and the dashboard's deals.csv and conditions.csv under ~/tradebot/logs/<broker>/<account>; nothing connects to a broker.
An order is the same order if symbol, setup time and role agree. R is the distance from entry to initial stop, for one leg at its own lots.
Python's standard library only.
"""
import csv, glob, json, os, statistics, sys
from collections import Counter, defaultdict
from datetime import datetime

# Fixed before the first comparison was run (09-Nov-2026).
TOL = {'orders': 0.90, 'median_level': 0.10, 'p90_level': 0.25, 'filled': 0.85, 'time_minutes': 5, 'time_share': 0.80, 'median_r': 0.25, 'sum_share': 0.10}


def t(text):
    return datetime.strptime(text[:19], '%Y-%m-%d %H:%M:%S')


def rows(path):
    with open(path, newline='', encoding='utf-8') as f:
        return list(csv.DictReader(f))


def read_orders(paths, start, end):
    """Key → the order's story. Only orders placed inside the window."""
    orders = {}
    records = sorted((r for p in paths for r in rows(p)), key=lambda r: r['time_utc'])
    for r in records:
        key = (r['symbol'], r['setup_utc'], r['role'])
        if r['action'] == 'place' and r['result'] == 'ok' and start <= t(r['time_utc']) <= end and key not in orders:
            orders[key] = {'place': r}
        elif key in orders and r['action'] != 'place' and r['action'] not in orders[key] and t(r['time_utc']) <= end:
            orders[key][r['action']] = r
    return orders


def percentile(values, share):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(share * len(ordered)))]


def level_gap(real, replay):
    distance = abs(float(real['price_req']) - float(real['stop_loss']))
    return abs(float(real['price_req']) - float(replay['price_req'])) / distance if distance else 0.0


def one_r(order, usd_per_price):
    return abs(float(order['price_req']) - float(order['stop_loss'])) * float(order['lots']) * usd_per_price


def real_trades(deal_rows, end):
    money, opened, closed = defaultdict(float), defaultdict(set), defaultdict(set)
    for d in deal_rows:
        if t(d['time_utc']) > end or not d['trade']:
            continue
        money[d['trade']] += float(d['net_usd'])
        (opened if d['kind'] == 'open' else closed)[d['trade']].add(d['position_id'])
    return {k: (v, opened[k] <= closed[k]) for k, v in money.items()}


def replay_trades(leg_rows, open_text):
    money, finished = defaultdict(float), {}
    for leg in leg_rows:
        money[leg['trade']] += float(leg['pnl_usd'])
        finished[leg['trade']] = finished.get(leg['trade'], True) and bool(leg['close_time_utc']) and f'"{leg["trade"]}"' not in open_text
    return {k: (v, finished[k]) for k, v in money.items()}


def verdict(ok):
    return 'within' if ok else '**outside**'


def compare(real, replay, money_real, money_replay, rates):
    both = sorted(set(real) & set(replay))
    out = {'real': len(real), 'replay': len(replay), 'both': len(both), 'only_real': sorted(set(real) - set(replay)), 'only_replay': sorted(set(replay) - set(real))}
    gaps = [level_gap(real[k]['place'], replay[k]['place']) for k in both]
    out['median_level'] = statistics.median(gaps) if gaps else None
    out['p90_level'] = percentile(gaps, 0.9) if gaps else None
    same = [k for k in both if ('filled' in real[k]) == ('filled' in replay[k])]
    out['filled_same'] = len(same)
    out['filled_diff'] = [k for k in both if k not in set(same)]
    fills = [k for k in both if 'filled' in real[k] and 'filled' in replay[k]]
    out['fills'] = len(fills)
    minutes = [abs((t(real[k]['filled']['time_utc']) - t(replay[k]['filled']['time_utc'])).total_seconds()) / 60 for k in fills]
    out['time_ok'] = sum(m <= TOL['time_minutes'] for m in minutes)
    trades = []
    for k in fills:
        rt, rp = f"P{real[k]['filled']['position_id']}", f"P{replay[k]['filled']['position_id']}"
        if rt not in money_real or rp not in money_replay or not money_real[rt][1] or not money_replay[rp][1]:
            continue
        usd = rates[k[0]]
        r1, r2 = one_r(real[k]['place'], usd), one_r(replay[k]['place'], usd)
        if r1 <= 0 or r2 <= 0:
            continue
        trades.append({'key': k, 'real_usd': money_real[rt][0], 'replay_usd': money_replay[rp][0], 'real_r': money_real[rt][0] / r1, 'replay_r': money_replay[rp][0] / r2, 'one_r_real': r1, 'one_r_replay': r2})
    out['trades'] = trades
    return out


def scaled_sum(trades):
    """The replay's results at the real order's size, so a run with different lots compares in money."""
    return sum(x['replay_r'] * x['one_r_real'] for x in trades)


def report(c):
    print('| Check | Tolerance | Result | |')
    print('|---|---|---|---|')
    a, b = c['both'] / c['real'] if c['real'] else 0, c['both'] / c['replay'] if c['replay'] else 0
    print(f"| Real orders found in the replay | ≥ {TOL['orders']:.0%} | {a:.0%} ({c['both']} of {c['real']}) | {verdict(a >= TOL['orders'])} |")
    print(f"| Replay orders found in the real log | ≥ {TOL['orders']:.0%} | {b:.0%} ({c['both']} of {c['replay']}) | {verdict(b >= TOL['orders'])} |")
    if c['both']:
        print(f"| Entry level, median difference (share of stop distance) | ≤ {TOL['median_level']:.0%} | {c['median_level']:.1%} | {verdict(c['median_level'] <= TOL['median_level'])} |")
        print(f"| Entry level, 90 % of orders within | ≤ {TOL['p90_level']:.0%} | {c['p90_level']:.1%} | {verdict(c['p90_level'] <= TOL['p90_level'])} |")
        share = c['filled_same'] / c['both']
        print(f"| Same answer on filled or not | ≥ {TOL['filled']:.0%} | {share:.0%} ({c['filled_same']} of {c['both']}) | {verdict(share >= TOL['filled'])} |")
    if c['fills']:
        share = c['time_ok'] / c['fills']
        print(f"| Filled within {TOL['time_minutes']} minutes of the real fill | ≥ {TOL['time_share']:.0%} | {share:.0%} ({c['time_ok']} of {c['fills']}) | {verdict(share >= TOL['time_share'])} |")
    trades = c['trades']
    if trades:
        median = statistics.median(abs(x['real_r'] - x['replay_r']) for x in trades)
        print(f"| Whole trade, median difference ({len(trades)} trades finished on both sides) | ≤ {TOL['median_r']} R | {median:.2f} R | {verdict(median <= TOL['median_r'])} |")
        real_sum, replay_sum, absolute = sum(x['real_usd'] for x in trades), scaled_sum(trades), sum(abs(x['real_usd']) for x in trades)
        share = abs(real_sum - replay_sum) / absolute if absolute else 0
        print(f"| Sum of whole-trade results: real ${real_sum:,.2f}, replay at the real sizes ${replay_sum:,.2f} | ≤ {TOL['sum_share']:.0%} of ${absolute:,.2f} | {share:.0%} | {verdict(share <= TOL['sum_share'])} |")
    print()


def details(c):
    print('### Differences\n')
    print(f"Real orders the replay did not place ({len(c['only_real'])}): " + (', '.join(f'{s} {u[5:16]} {r}' for s, u, r in c['only_real']) or 'none') + '.\n')
    print(f"Replay orders with no real order ({len(c['only_replay'])}): " + (', '.join(f'{s} {u[5:16]} {r}' for s, u, r in c['only_replay']) or 'none') + '.\n')
    print(f"Filled on one side only ({len(c['filled_diff'])}): " + (', '.join(f'{s} {u[5:16]} {r}' for s, u, r in c['filled_diff']) or 'none') + '.\n')
    worst = sorted(c['trades'], key=lambda x: -abs(x['real_r'] - x['replay_r']))[:12]
    if worst:
        print('| Trade | Real R | Replay R | Real $ | Replay $ |\n|---|---|---|---|---|')
        for x in worst:
            s, u, r = x['key']
            print(f"| {s} {u[5:16]} {r} | {x['real_r']:+.2f} | {x['replay_r']:+.2f} | {x['real_usd']:+.2f} | {x['replay_usd']:+.2f} |")
        print()


def issue_checks(real_dir, run, start, end):
    print('### Refusals for lack of money, and the rollover\n')
    def refusals(paths):
        found = []
        for p in paths:
            for r in rows(p):
                if start <= t(r['time_utc']) <= end and 'NOT_ENOUGH_MONEY' in ' '.join(r.values()):
                    found.append(r)
        return sorted(found, key=lambda r: r['time_utc'])
    real = refusals(sorted(glob.glob(f'{real_dir}/TM-*/*/actions.csv')))
    replay = refusals(sorted(glob.glob(f'{run}/home/logs/rewind/*/TM-*/*/actions.csv')))
    def line(name, found):
        first = found[0]['time_utc'] if found else 'none'
        symbols = Counter(r.get('symbol', '') for r in found)
        print(f'- {name}: {len(found)} refused, first {first}; ' + ', '.join(f'{s} {n}' for s, n in symbols.most_common(6)) + '.')
    line('Real', real)
    line('Replay', replay)
    equity = [r for r in rows(f'{run}/result/equity.csv') if float(r['margin_usd']) > 0]
    if equity:
        low = min(equity, key=lambda r: float(r['equity_usd']) / float(r['margin_usd']))
        print(f"- Replay's lowest margin level: {float(low['equity_usd']) / float(low['margin_usd']):.0%} at {low['time_utc']} (hourly).")
    print()
    def limits(paths):
        found = []
        for p in paths:
            for r in rows(p):
                when = t(r['time_utc'])
                if start <= when <= end and r.get('event') == 'LossLimit' and (when.hour == 22 and when.minute <= 10):
                    found.append(r)
        return sorted(found, key=lambda r: r['time_utc'])
    def limit_line(name, found):
        print(f'- {name}: ' + ('; '.join(f"{r['time_utc'][5:16]} {r['detail'][:60]}" for r in found) or 'none') + '.')
    print('Loss-limit closes in the 10 minutes after 22:00 UTC:\n')
    limit_line('Real', limits(sorted(glob.glob(f'{real_dir}/TM-*/*/events.csv'))))
    limit_line('Replay', limits(sorted(glob.glob(f'{run}/home/logs/rewind/*/TM-*/*/events.csv'))))
    print()


def main(real_dir, run, start_text, end_text):
    start, end = t(start_text), t(end_text)
    real = read_orders(sorted(glob.glob(f'{real_dir}/Scout-*/*/orders.csv')), start, end)
    replay = read_orders(sorted(glob.glob(f'{run}/home/logs/rewind/*/Scout-*/*/orders.csv')), start, end)
    deals = [r for p in sorted(glob.glob(f'{real_dir}/Dashboard/*/deals.csv')) for r in rows(p)]
    conditions = sorted(glob.glob(f'{real_dir}/Dashboard/*/conditions.csv'))[0]
    rates = {}
    for r in rows(conditions):
        rates.setdefault(r['symbol'], float(r['pip_value_per_lot_usd']) / float(r['pip_size']))
    open_file = f'{run}/result/tm-open.json'
    open_text = open(open_file, encoding='utf-8').read() if os.path.exists(open_file) else ''
    c = compare(real, replay, real_trades(deals, end), replay_trades(rows(f'{run}/result/tm-trades.csv'), open_text), rates)
    report(c)
    details(c)
    issue_checks(real_dir, run, start, end)
    json.dump({k: v for k, v in c.items() if k != 'trades'} | {'trades': len(c['trades'])}, sys.stderr, default=str)


def self_test():
    place = lambda price, stop, lots='0.1': {'place': {'price_req': price, 'stop_loss': stop, 'lots': lots}}
    assert abs(level_gap(place('100', '90')['place'], place('101', '90')['place']) - 0.1) < 1e-9
    a = {('X', 's', 'M'): place('100', '90') | {'filled': {'time_utc': '2026-01-01 10:00:00', 'position_id': '1'}}, ('Y', 's', 'M'): place('100', '90')}
    b = {('X', 's', 'M'): place('100', '90') | {'filled': {'time_utc': '2026-01-01 10:03:00', 'position_id': '9'}}, ('Z', 's', 'M'): place('100', '90')}
    c = compare(a, b, {'P1': (-10.0, True)}, {'P9': (-20.0, True)}, {'X': 1.0})
    assert c['both'] == 1 and c['only_real'] == [('Y', 's', 'M')] and c['only_replay'] == [('Z', 's', 'M')] and c['time_ok'] == 1
    assert len(c['trades']) == 1 and abs(c['trades'][0]['real_r'] + 10) < 1e-9 and abs(c['trades'][0]['replay_r'] + 20) < 1e-9
    open_only = compare(a, b, {'P1': (-10.0, False)}, {'P9': (-20.0, True)}, {'X': 1.0})
    assert open_only['trades'] == []
    done = real_trades([{'time_utc': '2026-01-01 10:00:00', 'trade': 'P1', 'kind': 'open', 'position_id': '1', 'net_usd': '-1'}, {'time_utc': '2026-01-01 11:00:00', 'trade': 'P1', 'kind': 'close', 'position_id': '1', 'net_usd': '5'}], t('2026-01-01 12:00:00'))
    assert done['P1'] == (4.0, True)
    before = real_trades([{'time_utc': '2026-01-01 10:00:00', 'trade': 'P1', 'kind': 'open', 'position_id': '1', 'net_usd': '-1'}, {'time_utc': '2026-01-01 13:00:00', 'trade': 'P1', 'kind': 'close', 'position_id': '1', 'net_usd': '5'}], t('2026-01-01 12:00:00'))
    assert before['P1'] == (-1.0, False)
    print('self-test passed')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        main(*sys.argv[1:5])
