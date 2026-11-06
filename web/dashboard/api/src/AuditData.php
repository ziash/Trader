<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\HttpError;

/**
 * The Audit page (dashboard.md §2.7) from the uploaded Audit Bot reports, data/reports/summary-<from>_<to>.csv and
 * daily-compare-<day>.csv (audit-bot.md §5.1): IC Markets and Pepperstone side by side per symbol, the better figure marked.
 */
final class AuditData
{
    private const SUMMARY = '/^summary-(\d{4}-\d{2}-\d{2})_(\d{4}-\d{2}-\d{2})\.csv$/';
    private const DAILY = '/^daily-compare-(\d{4}-\d{2}-\d{2})\.csv$/';

    /** For each compared figure: true when a lower value is better. Slippage is negative when worse for us, so higher is better. */
    private const LOWER_BETTER = [
        'spread_median' => true, 'spread_p95' => true, 'spread_max' => true, 'slip_avg' => false, 'slip_worst' => false,
        'latency_avg_ms' => true, 'latency_median_ms' => true, 'latency_max_ms' => true, 'rejected' => true, 'order_errors' => true,
        'commission_usd' => true,
    ];

    public function __construct(private readonly string $dataDir)
    {
    }

    /** $report: "summary" (the newest summary; default), or a day "2026-11-04" for its daily file. $period: day, main, offpeak or a probe window. */
    public function view(?string $report, ?string $period): array
    {
        $reports = $this->reports();
        if ($reports === []) {
            return ['available' => false, 'reports' => [], 'report' => null, 'periods' => [], 'period' => null, 'symbols' => [], 'totals' => [], 'brokers' => [], 'broker_choice' => null];
        }
        if ($report !== null && $report !== 'summary' && !preg_match('/^\d{4}-\d{2}-\d{2}$/', $report)) {
            throw new HttpError(400, 'bad_request', 'Parameter "report" must be "summary" or a day like 2026-11-04');
        }
        $chosen = null;
        foreach ($reports as $r) {
            if (($report === null || $report === 'summary') ? $r['kind'] === 'summary' : $r['id'] === $report) {
                $chosen = $r;
                break;
            }
        }
        $chosen ??= $report === null || $report === 'summary' ? $reports[0] : throw new HttpError(404, 'not_found', 'No report for that day');

        $rows = ScoutData::readCsv("{$this->dataDir}/reports/{$chosen['file']}");
        $brokers = [];
        foreach ($rows as $r) {
            $brokers[$r['broker']] = $r['broker_name'];
        }
        ksort($brokers);
        $periods = array_values(array_unique(array_map(fn ($r) => $r['period'], array_filter($rows, fn ($r) => $r['symbol'] !== 'ALL'))));
        $order = ['day' => 0, 'main' => 1, 'offpeak' => 2];
        usort($periods, fn ($a, $b) => ($order[$a] ?? 3) <=> ($order[$b] ?? 3) ?: strcmp($a, $b));
        $period ??= 'day';
        if (!in_array($period, $periods, true)) {
            throw new HttpError(400, 'bad_request', 'No such period in this report');
        }

        $bySymbol = [];
        foreach ($rows as $r) {
            if ($r['symbol'] !== 'ALL' && $r['period'] === $period) {
                $bySymbol[$r['symbol']][$r['broker']] = $r;
            }
        }
        ksort($bySymbol);
        $symbols = [];
        foreach ($bySymbol as $symbol => $pair) {
            $unit = self::byPips($symbol) ? 'pips' : 'usd';
            $figures = [];
            foreach ($pair as $broker => $r) {
                $figures[$broker] = self::figures($r, $unit);
            }
            $symbols[] = ['symbol' => $symbol, 'unit' => $unit, 'brokers' => $figures, 'better' => self::better($figures)];
        }

        $totals = [];
        foreach ($rows as $r) {
            if ($r['symbol'] === 'ALL') {
                $totals[$r['broker']] = [
                    'orders' => self::int($r['orders']), 'filled' => self::int($r['filled']), 'rejected' => self::int($r['rejected']),
                    'order_errors' => self::int($r['order_errors']), 'latency_avg_ms' => self::num($r['latency_avg_ms']),
                    'latency_median_ms' => self::num($r['latency_median_ms']), 'latency_max_ms' => self::num($r['latency_max_ms']),
                    'commission_usd' => self::num($r['commission_usd']), 'instances' => self::int($r['instances']),
                    'connection_drops' => self::int($r['connection_drops']), 'spread_alerts' => self::int($r['spread_alerts']),
                    'slippage_alerts' => self::int($r['slippage_alerts']), 'missed_windows' => self::int($r['missed_windows']),
                    'log_warnings' => self::int($r['log_warnings']), 'log_errors' => self::int($r['log_errors']),
                ];
            }
        }

        return [
            'available' => true,
            'reports' => array_map(fn ($r) => array_diff_key($r, ['file' => 0]), $reports),
            'report' => array_diff_key($chosen, ['file' => 0]),
            'periods' => $periods,
            'period' => $period,
            'brokers' => $brokers,
            'symbols' => $symbols,
            'totals' => $totals,
            // Recorded in the Audit Bot's document (audit-bot.md §9) and config/dashboard.json; the page shows it, it never makes it.
            'broker_choice' => $this->brokerChoice(),
        ];
    }

    /** "IC Markets (chosen 2026-11-06)" from the uploaded status.json, or null while not chosen. */
    private function brokerChoice(): ?string
    {
        $file = "{$this->dataDir}/status.json";
        $s = is_file($file) ? json_decode((string) file_get_contents($file), true) : null;
        $c = is_array($s) ? ($s['broker_choice'] ?? null) : null;
        if (!is_array($c) || !is_string($c['name'] ?? null) || $c['name'] === '') {
            return null;
        }
        return $c['name'] . (is_string($c['decided'] ?? null) && $c['decided'] !== '' ? " (chosen {$c['decided']})" : '');
    }

    /** The reports on the server: summaries first (newest first), then the daily files (newest first). */
    private function reports(): array
    {
        $found = [];
        foreach (glob("{$this->dataDir}/reports/*.csv") ?: [] as $file) {
            $name = basename($file);
            if (preg_match(self::SUMMARY, $name, $m)) {
                $found[] = ['id' => 'summary', 'kind' => 'summary', 'from' => $m[1], 'to' => $m[2], 'file' => $name];
            } elseif (preg_match(self::DAILY, $name, $m)) {
                $found[] = ['id' => $m[1], 'kind' => 'day', 'from' => $m[1], 'to' => $m[1], 'file' => $name];
            }
        }
        usort($found, fn ($a, $b) => ($b['kind'] === 'summary') <=> ($a['kind'] === 'summary') ?: strcmp($b['to'] . $b['from'], $a['to'] . $a['from']));
        return $found;
    }

    /** Forex is compared by pips; every other symbol by USD per lot, except XAGUSD (audit-bot.md §2.1, §5.1). */
    public static function byPips(string $symbol): bool
    {
        $ccy = '(USD|EUR|GBP|JPY|AUD|NZD|CAD|CHF|SEK|NOK|DKK|SGD|HKD|ZAR|MXN|PLN|TRY|CNH)';
        return $symbol === 'XAGUSD' || preg_match("/^$ccy$ccy$/", $symbol) === 1;
    }

    private static function figures(array $r, string $unit): array
    {
        $u = $unit === 'pips' ? 'pips' : 'usd';
        return [
            'spread_median' => self::num($r["spread_median_$u"]), 'spread_p95' => self::num($r["spread_p95_$u"]), 'spread_max' => self::num($r["spread_max_$u"]),
            'slip_avg' => self::num($r["slip_avg_$u"]), 'slip_worst' => self::num($r["slip_worst_$u"]),
            'latency_avg_ms' => self::num($r['latency_avg_ms']), 'latency_median_ms' => self::num($r['latency_median_ms']), 'latency_max_ms' => self::num($r['latency_max_ms']),
            'orders' => self::int($r['orders']), 'filled' => self::int($r['filled']), 'rejected' => self::int($r['rejected']), 'order_errors' => self::int($r['order_errors']),
            'commission_usd' => self::num($r['commission_usd']), 'ticks' => self::int($r['ticks']), 'logged_min' => self::int($r['logged_min']), 'gap_rows' => self::int($r['gap_rows']),
        ];
    }

    /** For each figure both brokers have: the broker with the better value, or "equal". */
    private static function better(array $figures): array
    {
        if (count($figures) !== 2) {
            return [];
        }
        [$a, $b] = array_keys($figures);
        $out = [];
        foreach (self::LOWER_BETTER as $k => $lower) {
            [$x, $y] = [$figures[$a][$k], $figures[$b][$k]];
            if ($x === null || $y === null) {
                continue;
            }
            $out[$k] = $x == $y ? 'equal' : (($x < $y) === $lower ? $a : $b);
        }
        return $out;
    }

    private static function num(?string $v): ?float
    {
        return $v === null || $v === '' || !is_numeric($v) ? null : (float) $v;
    }

    private static function int(?string $v): ?int
    {
        return $v === null || $v === '' || !is_numeric($v) ? null : (int) $v;
    }
}
