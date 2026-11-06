<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\HttpError;

/**
 * The Accounts and TM trades pages (dashboard.md §2.3, §2.4) from the uploaded snapshot (data/snapshot-<broker>-<ctid>.json),
 * the deals and account rows (data/logs/<broker>/<ctid>/Dashboard/<day>/deals.csv, account.csv), TM's actions
 * (TM-<id>/<day>/actions.csv) and the trade pictures (Dashboard/<day>/charts/trade-P<id>.svg). All made by the laptop (§3.2, §3.3).
 */
final class TradesData
{
    private const DAY = '/^\d{4}-\d{2}-\d{2}$/';
    /** A trade picture path, relative to the logs folder. */
    public const CHART = '#^[A-Za-z0-9]{1,32}/\d{1,12}/Dashboard/\d{4}-\d{2}-\d{2}/charts/trade-[PO]\d{1,20}\.svg$#';
    /** TM's margin rules (tm.md §5.1): the Accounts page colours the margin level by them. */
    public const RESCUE_LEVEL = 100;
    public const WARN_LEVEL = 300;
    /** Owners whose trades are listed unless asked otherwise; other bots' (Audit Bot probes, hundreds a day) are hidden. */
    public const SHOWN_OWNERS = ['Scout', 'manual', 'TM'];

    public function __construct(private readonly string $dataDir)
    {
    }

    // --- Accounts page ---------------------------------------------------------------------

    public function accounts(string $accountId, string $today): array
    {
        [$broker, $ctid] = ScoutData::account($accountId);
        $snapshot = $this->snapshot($broker, $ctid);
        $deals = $this->rows($broker, $ctid, 'deals.csv');
        $accountRows = $this->rows($broker, $ctid, 'account.csv');

        $perDay = [];
        $todayByOwner = [];
        $balance = [];
        foreach ($deals as $d) {
            $day = substr($d['time_utc'], 0, 10);
            $net = self::num($d['net_usd']) ?? 0.0;
            $close = $d['kind'] === 'close';
            $perDay[$day] ??= ['day' => $day, 'net_usd' => 0.0, 'closes' => 0];
            $perDay[$day]['net_usd'] += $net;
            $perDay[$day]['closes'] += $close ? 1 : 0;
            if ($day === $today) {
                $o = $d['owner'];
                $todayByOwner[$o] ??= ['owner' => $o, 'net_usd' => 0.0, 'closes' => 0];
                $todayByOwner[$o]['net_usd'] += $net;
                $todayByOwner[$o]['closes'] += $close ? 1 : 0;
            }
            if ($close && ($b = self::num($d['balance_usd'])) !== null) {
                $balance[] = ['time_utc' => $d['time_utc'], 'balance_usd' => $b];
            }
        }
        ksort($perDay);
        $round = fn (array $r) => ['net_usd' => round($r['net_usd'], 2)] + $r;

        $openByOwner = [];
        foreach ($snapshot['positions'] ?? [] as $p) {
            $o = $p['owner'];
            $openByOwner[$o] ??= ['owner' => $o, 'positions' => 0, 'orders' => 0, 'open_result_usd' => 0.0];
            $openByOwner[$o]['positions']++;
            $openByOwner[$o]['open_result_usd'] += (float) $p['open_result_usd'];
        }
        foreach ($snapshot['orders'] ?? [] as $p) {
            $o = $p['owner'];
            $openByOwner[$o] ??= ['owner' => $o, 'positions' => 0, 'orders' => 0, 'open_result_usd' => 0.0];
            $openByOwner[$o]['orders']++;
        }

        return [
            'account' => $accountId,
            'available' => $snapshot !== null,
            'snapshot_utc' => $snapshot['time_utc'] ?? null,
            'status' => $snapshot['status'] ?? null,
            'levels' => ['rescue_pct' => self::RESCUE_LEVEL, 'warn_pct' => self::WARN_LEVEL],
            'open_by_owner' => array_values(array_map(fn ($r) => ['open_result_usd' => round($r['open_result_usd'], 2)] + $r, $openByOwner)),
            'today' => [
                'day' => $today,
                'net_usd' => round(array_sum(array_column($todayByOwner, 'net_usd')), 2),
                'by_owner' => array_values(array_map($round, $todayByOwner)),
            ],
            'per_day' => array_values(array_map($round, $perDay)),
            'balance' => $balance,
            'equity' => array_map(fn ($r) => [
                'time_utc' => $r['time_utc'], 'balance_usd' => self::num($r['balance_usd']), 'equity_usd' => self::num($r['equity_usd']),
                'margin_level_pct' => self::num($r['margin_level_pct']),
            ], $accountRows),
        ];
    }

    // --- TM trades page --------------------------------------------------------------------

    /** The trades with a fill or a TM action on $day (default: the latest such day), and what is open now. */
    public function trades(string $accountId, ?string $day): array
    {
        [$broker, $ctid] = ScoutData::account($accountId);
        if ($day !== null && !preg_match(self::DAY, $day)) {
            throw new HttpError(400, 'bad_request', 'Parameter "day" must look like 2026-11-05');
        }
        $snapshot = $this->snapshot($broker, $ctid);
        $deals = $this->rows($broker, $ctid, 'deals.csv');
        $actions = $this->actions($broker, $ctid);
        $scoutOrders = (new ScoutData("{$this->dataDir}/logs"))->ordersById($broker, $ctid);
        $charts = $this->charts($broker, $ctid);

        // The order that opened each main: TM logs a trade as O<orderId> until it fills (tm.md §7).
        $tradeOfOrder = [];
        foreach ($deals as $d) {
            if ($d['kind'] === 'open' && $d['role'] === 'MAIN') {
                $tradeOfOrder['O' . $d['order_id']] = $d['trade'];
            }
        }
        $tradeKey = fn (string $t) => $tradeOfOrder[$t] ?? $t;

        $trades = [];
        $new = fn (string $key) => [
            'trade' => $key, 'owner' => null, 'symbol' => null, 'open' => false, 'result_usd' => 0.0, 'closed_result_usd' => 0.0,
            'open_result_usd' => 0.0, 'start_utc' => null, 'last_utc' => null, 'days' => [], 'legs' => [], 'timeline' => [],
            'chart' => null, 'scout_order' => null, 'refused' => 0, 'repeat' => [],
        ];
        foreach ($deals as $d) {
            $key = $d['trade'];
            $t = $trades[$key] ?? $new($key);
            $t['symbol'] ??= $d['symbol'];
            if ($d['role'] === 'MAIN' || $t['owner'] === null) {
                $t['owner'] = $d['role'] === 'MAIN' ? $d['owner'] : ($t['owner'] ?? $d['owner']);
            }
            $net = self::num($d['net_usd']) ?? 0.0;
            $t['closed_result_usd'] += $net;
            $t['legs'][$d['position_id']] = $d['role'];
            $t['timeline'][] = [
                'time_utc' => $d['time_utc'], 'source' => 'fill', 'role' => $d['role'],
                'what' => $d['kind'] === 'open' ? ucfirst($d['side']) . ' filled' : 'Closed (' . $d['side'] . ')',
                'price' => $d['price'], 'lots' => self::num($d['lots']), 'net_usd' => $d['kind'] === 'close' ? round($net, 2) : null,
                'position_id' => $d['position_id'], 'ok' => true, 'count' => 1,
            ];
            if ($d['kind'] === 'open' && $d['role'] === 'MAIN' && isset($scoutOrders[$d['order_id']])) {
                $o = $scoutOrders[$d['order_id']];
                $t['scout_order'] = ['order_id' => $d['order_id'], 'day' => $o['_day'], 'chart' => $o['chart']];
            }
            $t['days'][substr($d['time_utc'], 0, 10)] = true;
            $trades[$key] = $t;
        }
        foreach ($snapshot['positions'] ?? [] as $p) {
            $t = $trades[$p['trade']] ?? $new($p['trade']);
            $t['symbol'] ??= $p['symbol'];
            if ($p['role'] === 'MAIN' || $t['owner'] === null) {
                $t['owner'] = $p['owner'];
            }
            $t['open'] = true;
            $t['open_result_usd'] += (float) $p['open_result_usd'];
            $trades[$p['trade']] = $t;
        }
        foreach ($actions as $a) {
            $key = $tradeKey($a['trade']);
            $t = $trades[$key] ?? $new($key);
            $t['symbol'] ??= $a['symbol'];
            if ($t['owner'] === null && str_starts_with($key, 'O')) {
                $t['owner'] = isset($scoutOrders[substr($key, 1)]) ? 'Scout' : 'manual';
            }
            $ok = $a['result'] === 'ok';
            $t['refused'] += $ok ? 0 : 1;
            $what = trim("{$a['action']} {$a['side']} {$a['price_req']}");
            // TM plans every second, so a refused action repeats until something changes (issue-log #1: 5,448 times). The same
            // action of the same leg with the same result, again within 10 minutes of the last one, adds to a count instead of a line.
            $same = "{$a['role']}|$what|{$a['result']}";
            $i = $t['repeat'][$same] ?? null;
            $at = strtotime($a['time_utc'] . ' UTC');
            if ($i !== null && $at - strtotime(($t['timeline'][$i]['until_utc'] ?? $t['timeline'][$i]['time_utc']) . ' UTC') <= 600) {
                $t['timeline'][$i]['count']++;
                $t['timeline'][$i]['until_utc'] = $a['time_utc'];
            } else {
                $t['repeat'][$same] = count($t['timeline']);
                $t['timeline'][] = [
                    'time_utc' => $a['time_utc'], 'source' => 'tm', 'role' => $a['role'], 'what' => $what, 'price' => $a['price_fill'] ?: null,
                    'lots' => self::num($a['lots']), 'net_usd' => null, 'latency_ms' => self::num($a['latency_ms']), 'ok' => $ok,
                    'result' => $ok ? null : $a['result'], 'detail' => $a['detail'] ?: null, 'count' => 1,
                ];
            }
            $t['days'][substr($a['time_utc'], 0, 10)] = true;
            $trades[$key] = $t;
        }

        $allDays = [];
        foreach ($trades as $t) {
            $allDays += $t['days'];
        }
        $allDays = array_keys($allDays);
        sort($allDays);
        $day ??= end($allDays) ?: gmdate('Y-m-d');

        $list = [];
        foreach ($trades as $key => $t) {
            if (!isset($t['days'][$day])) {
                continue;
            }
            usort($t['timeline'], fn ($a, $b) => strcmp($a['time_utc'], $b['time_utc']) ?: strcmp($a['source'], $b['source']));
            $t['owner'] ??= 'unknown';
            $t['start_utc'] = $t['timeline'][0]['time_utc'] ?? null;
            $t['last_utc'] = $t['timeline'] === [] ? null : end($t['timeline'])['time_utc'];
            $t['result_usd'] = round($t['closed_result_usd'] + $t['open_result_usd'], 2);
            $t['closed_result_usd'] = round($t['closed_result_usd'], 2);
            $t['open_result_usd'] = round($t['open_result_usd'], 2);
            $t['chart'] = $charts[$key] ?? null;
            $t['legs'] = count($t['legs']);
            unset($t['days'], $t['repeat']);
            $list[] = $t;
        }
        usort($list, fn ($a, $b) => strcmp($b['last_utc'] ?? '', $a['last_utc'] ?? ''));

        return [
            'account' => $accountId,
            'day' => $day,
            'days' => $allDays,
            'snapshot_utc' => $snapshot['time_utc'] ?? null,
            'shown_owners' => self::SHOWN_OWNERS,
            'open_positions' => $snapshot['positions'] ?? [],
            'open_orders' => $snapshot['orders'] ?? [],
            'trades' => $list,
        ];
    }

    /** The full path of a trade picture, or a 404; never anything outside the logs folder. */
    public function chartPath(string $file): string
    {
        if (!preg_match(self::CHART, $file) || str_contains($file, '..')) {
            throw new HttpError(400, 'bad_request', 'Parameter "file" is not a trade picture');
        }
        $logs = "{$this->dataDir}/logs";
        $path = realpath("$logs/$file");
        $root = realpath($logs);
        if ($path === false || $root === false || !str_starts_with($path, $root . '/') || !is_file($path)) {
            throw new HttpError(404, 'not_found', 'No such picture (yet)');
        }
        return $path;
    }

    // --- Reading ---------------------------------------------------------------------------

    private function snapshot(string $broker, string $ctid): ?array
    {
        $file = "{$this->dataDir}/snapshot-$broker-$ctid.json";
        $s = is_file($file) ? json_decode((string) file_get_contents($file), true) : null;
        return is_array($s) ? $s : null;
    }

    /** All rows of one Dashboard file kind, oldest first. */
    private function rows(string $broker, string $ctid, string $name): array
    {
        $rows = [];
        foreach (glob("{$this->dataDir}/logs/$broker/$ctid/Dashboard/*/$name") ?: [] as $file) {
            if (preg_match(self::DAY, basename(dirname($file)))) {
                array_push($rows, ...ScoutData::readCsv($file));
            }
        }
        usort($rows, fn ($a, $b) => strcmp($a['time_utc'], $b['time_utc']) ?: (int) ($a['deal_id'] ?? 0) <=> (int) ($b['deal_id'] ?? 0));
        return $rows;
    }

    private function actions(string $broker, string $ctid): array
    {
        $rows = [];
        foreach (glob("{$this->dataDir}/logs/$broker/$ctid/TM-*/*/actions.csv") ?: [] as $file) {
            if (preg_match(self::DAY, basename(dirname($file)))) {
                array_push($rows, ...ScoutData::readCsv($file));
            }
        }
        usort($rows, fn ($a, $b) => strcmp($a['time_utc'], $b['time_utc']));
        return array_values(array_filter($rows, fn ($r) => ($r['trade'] ?? '') !== ''));
    }

    /** trade → its picture's path relative to the logs folder (the newest, if a trade was drawn under two days). */
    private function charts(string $broker, string $ctid): array
    {
        $found = [];
        foreach (glob("{$this->dataDir}/logs/$broker/$ctid/Dashboard/*/charts/trade-*.svg") ?: [] as $file) {
            $rel = substr($file, strlen("{$this->dataDir}/logs/"));
            if (preg_match(self::CHART, $rel) && preg_match('/trade-([PO]\d+)\.svg$/', $file, $m)) {
                $found[$m[1]] = $rel;
            }
        }
        return $found;
    }

    private static function num(?string $v): ?float
    {
        return $v === null || $v === '' || !is_numeric($v) ? null : (float) $v;
    }
}
