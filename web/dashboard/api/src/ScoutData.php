<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\HttpError;

/**
 * Scout's uploaded logs for the Scout page (dashboard.md §2.1; the files: scout.md §3): data/logs/<broker>/<ctid>/Scout-<id>/<day>/
 * setups.csv, orders.csv and charts/*.svg. One order's rows are spread over days and instance folders (a restart makes a new
 * instance), so all of the account's Scout files are read and each order is merged from its rows.
 */
final class ScoutData
{
    private const DAY = '/^\d{4}-\d{2}-\d{2}$/';
    /** A chart path as in orders.csv's `chart` column (relative to the logs folder). */
    public const CHART = '#^[A-Za-z0-9]{1,32}/\d{1,12}/Scout-[A-Za-z0-9]{1,32}/\d{4}-\d{2}-\d{2}/charts/[A-Za-z0-9._-]{1,120}\.svg$#';

    private const TF_SECONDS = ['M1' => 60, 'M5' => 300, 'M15' => 900, 'M30' => 1800, 'H1' => 3600, 'H4' => 14400, 'D1' => 86400, 'W1' => 604800];

    public function __construct(private readonly string $logsDir)
    {
    }

    /** "brokerA-49123893" → [broker, ctid]; anything else is a 400. */
    public static function account(string $id): array
    {
        if (!preg_match('/^([A-Za-z0-9]{1,32})-(\d{1,12})$/', $id, $m)) {
            throw new HttpError(400, 'bad_request', 'Parameter "account" must look like brokerA-49123893');
        }
        return [$m[1], $m[2]];
    }

    /** The Scout page's data for one account and UTC day (default: the latest day with Scout files). */
    public function day(string $accountId, ?string $day): array
    {
        [$broker, $ctid] = self::account($accountId);
        if ($day !== null && !preg_match(self::DAY, $day)) {
            throw new HttpError(400, 'bad_request', 'Parameter "day" must look like 2026-11-05');
        }
        $folders = $this->dayFolders($broker, $ctid);
        $days = array_values(array_unique(array_map(fn ($f) => $f[1], $folders)));
        sort($days);
        $day ??= end($days) ?: gmdate('Y-m-d');

        $setupRows = $orderRows = [];
        foreach ($folders as [$dir, $d]) {
            foreach (self::readCsv("$dir/setups.csv") as $r) {
                $setupRows[] = $r + ['_day' => $d];
            }
            foreach (self::readCsv("$dir/orders.csv") as $r) {
                $orderRows[] = $r + ['_day' => $d];
            }
        }
        $byTime = fn ($a, $b) => strcmp($a['time_utc'] ?? '', $b['time_utc'] ?? '');
        usort($setupRows, $byTime);
        usort($orderRows, $byTime);

        $orders = self::mergeOrders($orderRows);
        $setups = self::setups($setupRows, $orderRows, $orders, $day);

        $dayOrders = array_values(array_filter($orders, fn ($o) => $o['_day'] === $day));
        usort($dayOrders, fn ($a, $b) => strcmp($b['placed_utc'] ?? '', $a['placed_utc'] ?? ''));
        $net = 0.0;
        $filled = $closed = 0;
        foreach ($dayOrders as $o) {
            $filled += in_array($o['state'], ['filled', 'closed'], true) ? 1 : 0;
            $closed += $o['state'] === 'closed' ? 1 : 0;
            $net += $o['net_usd'] ?? 0.0;
        }

        return [
            'account' => $accountId,
            'day' => $day,
            'days' => $days,
            'setups' => $setups,
            'orders' => array_map(fn ($o) => array_diff_key($o, ['_day' => 0]), $dayOrders),
            'totals' => ['orders' => count($dayOrders), 'filled' => $filled, 'closed' => $closed, 'net_usd' => round($net, 2)],
        ];
    }

    /** The full path of an order picture, or a 404; never anything outside the logs folder. */
    public function chartPath(string $file): string
    {
        if (!preg_match(self::CHART, $file) || str_contains($file, '..')) {
            throw new HttpError(400, 'bad_request', 'Parameter "file" is not an order picture');
        }
        $path = realpath("{$this->logsDir}/$file");
        $root = realpath($this->logsDir);
        if ($path === false || $root === false || !str_starts_with($path, $root . '/') || !is_file($path)) {
            throw new HttpError(404, 'not_found', 'No such picture (yet)');
        }
        return $path;
    }

    // --- Reading ---------------------------------------------------------------------------

    /** @return list<array{0: string, 1: string}> [folder, day] for every Scout-<id>/<day> folder of the account */
    private function dayFolders(string $broker, string $ctid): array
    {
        $found = [];
        foreach (glob("{$this->logsDir}/$broker/$ctid/Scout-*/*", GLOB_ONLYDIR) ?: [] as $dir) {
            $d = basename($dir);
            if (preg_match(self::DAY, $d)) {
                $found[] = [$dir, $d];
            }
        }
        return $found;
    }

    /** Rows of a CSV file in the shared format (shared.md §5) by column name; a cut-off line (wrong field count) is skipped. */
    public static function readCsv(string $file): array
    {
        if (!is_file($file) || ($h = fopen($file, 'r')) === false) {
            return [];
        }
        $rows = [];
        $header = fgetcsv($h, null, ',', '"', '');
        while (is_array($header) && ($fields = fgetcsv($h, null, ',', '"', '')) !== false) {
            if (count($fields) === count($header)) {
                $rows[] = array_combine($header, $fields);
            }
        }
        fclose($h);
        return $rows;
    }

    // --- Orders ----------------------------------------------------------------------------

    private static function num(?string $v): ?float
    {
        return $v === null || $v === '' || !is_numeric($v) ? null : (float) $v;
    }

    /** A price as Scout logged it ("1.12300"), so the broker's digits are kept; null if empty or not a number. */
    private static function price(?string $v): ?string
    {
        return $v === null || $v === '' || !is_numeric($v) ? null : $v;
    }

    /** One entry per order ID, its rows merged in time order (scout.md §3: place, filled, closed, cancel, expired …). */
    private static function mergeOrders(array $rows): array
    {
        $orders = [];
        foreach ($rows as $r) {
            $id = $r['order_id'] ?? '';
            if ($id === '') {
                continue; // dry-run and alert rows have no order
            }
            $o = $orders[$id] ?? [
                'order_id' => $id, 'placed_utc' => null, 'symbol' => $r['symbol'], 'timeframe' => $r['timeframe'],
                'setup_key' => self::setupKey($r), 'role' => $r['role'], 'side' => $r['side'], 'lots' => self::num($r['lots']),
                'level' => self::price($r['price_req']), 'stop_loss' => self::price($r['stop_loss']), 'expires_utc' => null,
                'state' => 'pending', 'state_utc' => $r['time_utc'], 'reason' => null, 'price_fill' => null, 'slip_pips' => null,
                'position_id' => null, 'gross_usd' => null, 'net_usd' => null, 'chart' => null, '_day' => $r['_day'],
            ];
            $t = $r['time_utc'];
            switch ($r['action']) {
                case 'place':
                    $o['placed_utc'] = $t;
                    $o['_day'] = $r['_day'];
                    $o['expires_utc'] = $r['expires_utc'] ?: null;
                    $o['level'] = self::price($r['price_req']);
                    $o['stop_loss'] = self::price($r['stop_loss']);
                    $o['lots'] = self::num($r['lots']);
                    if (($r['result'] ?? 'ok') !== 'ok') {
                        [$o['state'], $o['state_utc'], $o['reason']] = ['refused', $t, $r['result'] . ($r['detail'] !== '' ? ": {$r['detail']}" : '')];
                    }
                    break;
                case 'move':
                case 'amend':
                    $o['level'] = self::price($r['price_req']) ?? $o['level'];
                    $o['stop_loss'] = self::price($r['stop_loss']) ?? $o['stop_loss'];
                    break;
                case 'filled':
                    [$o['state'], $o['state_utc']] = ['filled', $t];
                    $o['price_fill'] = self::price($r['price_fill']);
                    $o['slip_pips'] = self::num($r['slip_pips']);
                    $o['position_id'] = $r['position_id'] ?: null;
                    break;
                case 'closed':
                    [$o['state'], $o['state_utc']] = ['closed', $t];
                    $o['gross_usd'] = round(($o['gross_usd'] ?? 0) + (self::num($r['gross']) ?? 0), 2);
                    $o['net_usd'] = round(($o['net_usd'] ?? 0) + (self::num($r['net']) ?? 0), 2);
                    break;
                case 'cancel':
                case 'cancelled':
                    [$o['state'], $o['state_utc']] = ['cancelled', $t];
                    $o['reason'] = $r['detail'] !== '' ? $r['detail'] : ($r['action'] === 'cancelled' ? 'cancelled outside Scout' : null);
                    break;
                case 'expired':
                case 'rejected':
                    [$o['state'], $o['state_utc'], $o['reason']] = [$r['action'], $t, $r['detail'] ?: null];
                    break;
            }
            if (($r['chart'] ?? '') !== '' && preg_match(self::CHART, $r['chart'])) {
                $o['chart'] = $r['chart']; // the latest chart row wins (scout.md §3.1)
            }
            $orders[$id] = $o;
        }
        return $orders;
    }

    /** "GBPJPY-H1-202611041400": the setup's part of Scout's order label (dashboard.md §3.4). */
    private static function setupKey(array $r): string
    {
        return $r['symbol'] . '-' . $r['timeframe'] . '-' . preg_replace('/\D/', '', substr($r['setup_utc'] ?? '', 0, 16));
    }

    // --- Setups ----------------------------------------------------------------------------

    /** Each setup with an event on $day, newest activity first, with its current order and its whole story. */
    private static function setups(array $setupRows, array $orderRows, array $orders, string $day): array
    {
        $byKey = [];
        foreach ($setupRows as $r) {
            $byKey[self::setupKey($r)][] = $r;
        }
        $ordersByKey = $orderRowsByKey = [];
        foreach ($orders as $o) {
            $ordersByKey[$o['setup_key']][] = $o;
        }
        foreach ($orderRows as $r) {
            if (($r['order_id'] ?? '') !== '' && $r['action'] !== 'chart') {
                $orderRowsByKey[self::setupKey($r)][] = $r;
            }
        }

        $out = [];
        foreach ($byKey as $key => $rows) {
            $today = array_values(array_filter($rows, fn ($r) => $r['_day'] === $day));
            if ($today === []) {
                continue;
            }
            $last = end($today);
            $found = null; // the latest row that set the order's level (new or moved)
            foreach ($rows as $r) {
                if (in_array($r['event'], ['NewStop', 'NewReversal', 'StopMoved', 'ReversalMoved'], true)) {
                    $found = $r;
                }
            }
            $found ??= $last;
            $role = self::roleOf($last['event']) ?? self::roleOf($found['event']);
            $candidates = $ordersByKey[$key] ?? [];
            usort($candidates, fn ($a, $b) => strcmp($a['placed_utc'] ?? '', $b['placed_utc'] ?? ''));
            $order = null;
            foreach ($candidates as $o) {
                if ($role === null || $o['role'] === $role) {
                    $order = $o;
                }
            }
            $order ??= end($candidates) ?: null;
            $close = strtotime($last['candle_utc'] . ' UTC') + (self::TF_SECONDS[$last['timeframe']] ?? 3600);

            $out[] = [
                'key' => $key,
                'symbol' => $last['symbol'],
                'timeframe' => $last['timeframe'],
                'setup_utc' => $last['setup_utc'],
                'event' => $last['event'],
                'event_utc' => $last['time_utc'],
                'candle_close_utc' => gmdate('Y-m-d H:i:s', $close),
                'reason' => $last['reason'] !== '' ? $last['reason'] : null,
                'role' => $order['role'] ?? $role,
                'side' => $order['side'] ?? $found['side'],
                'level' => $order['level'] ?? self::price($found['level']),
                'stop_loss' => $order['stop_loss'] ?? self::price($found['far_side']),
                'lots' => $order['lots'] ?? null,
                'higher_tf' => $found['higher_tf'] ?: null,
                'higher_trend' => $found['higher_trend'] ?: null,
                'with_higher' => $found['with_higher'] === 'with' ? true : ($found['with_higher'] === 'against' ? false : null),
                'mode' => $found['mode'] ?: null,
                'pause_candles' => self::num($found['pause_candles']),
                'pause_height_atr' => self::num($found['pause_height_atr']),
                'move_atr' => self::num($found['move_atr']),
                'atr' => self::price($found['atr']),
                'order' => $order === null ? null : array_diff_key($order, ['_day' => 0]),
                'orders' => array_map(fn ($o) => $o['order_id'], $candidates),
                'timeline' => self::timeline($rows, $orderRowsByKey[$key] ?? []),
            ];
        }
        usort($out, fn ($a, $b) => strcmp($b['event_utc'], $a['event_utc']));
        return $out;
    }

    private static function roleOf(string $event): ?string
    {
        return str_contains($event, 'Reversal') ? 'R' : (str_contains($event, 'Stop') ? 'M' : null);
    }

    private const EVENTS = [
        'NewStop' => ['found', 'Found · main stop'],
        'NewReversal' => ['found', 'Reversal stop'],
        'StopMoved' => ['moved', 'Main stop moved'],
        'ReversalMoved' => ['moved', 'Reversal stop moved'],
        'StopFilled' => ['filled', 'Main stop filled'],
        'ReversalFilled' => ['filled', 'Reversal stop filled'],
        'Cancelled' => ['cancelled', 'Setup ended'],
        'TrendTurned' => ['info', 'Higher-timeframe trend turned'],
    ];

    /** The setup's story: Scout's detector events and its orders' results, in time order. */
    private static function timeline(array $setupRows, array $orderRows): array
    {
        $items = [];
        foreach ($setupRows as $r) {
            [$kind, $text] = self::EVENTS[$r['event']] ?? ['info', $r['event']];
            if (in_array($r['event'], ['NewStop', 'NewReversal', 'StopMoved', 'ReversalMoved'], true)) {
                $text .= " · {$r['side']} {$r['level']}";
            }
            if ($r['reason'] !== '') {
                $text .= " · {$r['reason']}";
            }
            $items[] = ['time_utc' => $r['time_utc'], 'kind' => $kind, 'text' => $text];
        }
        foreach ($orderRows as $r) {
            $role = $r['role'] === 'R' ? 'reversal' : 'main';
            $text = match ($r['action']) {
                'place' => ($r['result'] === 'ok' ? "Order placed · $role · {$r['side']} stop {$r['price_req']} · SL {$r['stop_loss']} · {$r['lots']} lot"
                    : "Order not placed ({$r['result']})"),
                'filled' => "Order filled at {$r['price_fill']}" . ($r['slip_pips'] !== '' ? " · slippage {$r['slip_pips']} pips" : ''),
                'closed' => 'Position closed · net ' . self::usd(self::num($r['net'])),
                'cancel' => 'Order cancelled by Scout' . ($r['detail'] !== '' ? " · {$r['detail']}" : ''),
                'cancelled' => 'Order cancelled outside Scout',
                'expired' => 'Order expired',
                'rejected' => 'Order rejected by the broker',
                default => null,
            };
            if ($text !== null) {
                $kind = ['place' => 'placed', 'filled' => 'filled', 'closed' => 'closed', 'cancel' => 'cancelled', 'cancelled' => 'cancelled'][$r['action']] ?? 'info';
                $items[] = ['time_utc' => $r['time_utc'], 'kind' => $kind, 'text' => $text];
            }
        }
        usort($items, fn ($a, $b) => strcmp($a['time_utc'], $b['time_utc']));
        return $items;
    }

    private static function usd(?float $v): string
    {
        return $v === null ? '—' : ($v < 0 ? '−$' : '+$') . number_format(abs($v), 2);
    }
}
