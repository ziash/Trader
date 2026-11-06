<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;
use TraderDash\AuditData;

/** The Trading conditions and Audit pages' data (dashboard.md §2.5, §2.7). */
final class ConditionsAuditTest extends TestCase
{
    private const REPORT = 'date_from,date_to,broker,broker_name,symbol,period,spread_rows,logged_min,ticks,gap_rows,spread_median_pips,spread_p95_pips,spread_max_pips,spread_median_usd,spread_p95_usd,spread_max_usd,orders,filled,rejected,order_errors,slip_avg_pips,slip_worst_pips,slip_avg_usd,slip_worst_usd,latency_avg_ms,latency_median_ms,latency_max_ms,commission_usd,instances,connection_drops,spread_alerts,slippage_alerts,missed_windows,log_warnings,log_errors';

    private TestKit $kit;
    private array $bearer;

    protected function setUp(): void
    {
        $this->kit = new TestKit();
        [$id, $secret] = $this->kit->machine('claude', 'viewer');
        $this->bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        @mkdir("{$this->kit->root}/data/reports", 0700, true);
        // From the final summary of 18 Oct – 4 Nov 2026.
        file_put_contents("{$this->kit->root}/data/reports/summary-2026-10-18_2026-11-04.csv", implode("\n", [self::REPORT,
            '2026-10-18,2026-11-04,brokerA,IC Markets,AUDJPY,day,6499,17837,650872,2722,0.28,1.60,30.00,1.80,10.11,189.87,72,72,0,0,0.04,-0.60,0.25,-3.80,618,568,1479,1.44,,,,,,,',
            '2026-10-18,2026-11-04,brokerB,Pepperstone,AUDJPY,day,6496,17828,1252379,2725,0.72,1.14,19.90,4.56,7.26,126.29,72,72,0,0,0.00,-1.40,0.01,-8.93,632,566,2310,2.16,,,,,,,',
            '2026-10-18,2026-11-04,brokerA,IC Markets,BTCUSD,day,1,1,1,0,510.69,623.32,9487,5.11,6.23,94.87,72,72,0,0,-1.17,-1200,-0.01,-12.00,515,470,1123,0,,,,,,,',
            '2026-10-18,2026-11-04,brokerB,Pepperstone,BTCUSD,day,1,1,1,0,150.99,194.08,1510.3,15.10,19.41,151.03,72,72,0,0,-10.23,-420.8,-0.10,-4.21,554,536,918,0,,,,,,,',
            '2026-10-18,2026-11-04,brokerA,IC Markets,AUDJPY,QuietHour,,,,,,,,,,,24,24,0,0,0.00,-0.20,0.00,-1.27,600,560,900,0.48,,,,,,,',
            '2026-10-18,2026-11-04,brokerB,Pepperstone,AUDJPY,QuietHour,,,,,,,,,,,24,24,0,0,0.00,-0.50,0.00,-3.17,640,590,1200,0.72,,,,,,,',
            '2026-10-18,2026-11-04,brokerA,IC Markets,ALL,day,,,,,,,,,,,912,912,0,0,,,,,521,482,1479,18.76,9,649,326,5,7,999,1',
            '2026-10-18,2026-11-04,brokerB,Pepperstone,ALL,day,,,,,,,,,,,912,912,0,0,,,,,563,535,2310,12.64,9,649,55,10,7,728,1',
        ]) . "\n");
        file_put_contents("{$this->kit->root}/data/reports/daily-compare-2026-11-04.csv", self::REPORT . "\n"
            . "2026-11-04,2026-11-04,brokerA,IC Markets,AUDJPY,day,402,1095,100274,0,0.29,0.35,12.50,1.82,2.23,78.97,12,12,0,0,0.10,-0.20,0.63,-1.27,552,538,732,0.24,,,,,,,\n");
        file_put_contents("{$this->kit->root}/data/snapshot-brokerA-49123893.json", json_encode([
            'time_utc' => '2026-11-06 17:16:58', 'status' => ['free_margin' => 276.74, 'margin_level_pct' => 114.8, 'leverage' => '1:500'],
            'positions' => [], 'orders' => [],
            'conditions' => [['symbol' => 'EURUSD', 'largest_lots_now' => 1.24, 'margin' => [['lots' => 1, 'buy' => 222.99, 'sell' => 222.99]]]],
        ]));
    }

    protected function tearDown(): void
    {
        $this->kit->cleanUp();
    }

    private function get(string $path, array $query): \TraderDash\Http\Response
    {
        return $this->kit->request('GET', $path, ['headers' => $this->bearer, 'query' => $query]);
    }

    public function testTheFinalSummaryComesFirstWithTheBetterBrokerPerFigure(): void
    {
        $d = $this->get('/api/audit', [])->data();
        self::assertSame(['summary', '2026-11-04'], array_column($d['reports'], 'id'));
        self::assertSame(['2026-10-18', '2026-11-04'], [$d['report']['from'], $d['report']['to']]);
        self::assertSame(['day', 'QuietHour'], $d['periods']);
        self::assertSame(['brokerA' => 'IC Markets', 'brokerB' => 'Pepperstone'], $d['brokers']);
        [$aud, $btc] = $d['symbols'];
        self::assertSame(['pips', 0.28, 0.72], [$aud['unit'], $aud['brokers']['brokerA']['spread_median'], $aud['brokers']['brokerB']['spread_median']]);
        self::assertSame(['brokerA', 'brokerB', 'brokerA', 'equal'],
            [$aud['better']['spread_median'], $aud['better']['spread_p95'], $aud['better']['slip_worst'], $aud['better']['rejected']]);
        // Crypto is compared by USD per lot, not by the brokers' differing pips (audit-bot.md §5.1).
        self::assertSame(['usd', 5.11, 'brokerA'], [$btc['unit'], $btc['brokers']['brokerA']['spread_median'], $btc['better']['spread_median']]);
        self::assertSame([649, 326, 55], [$d['totals']['brokerA']['connection_drops'], $d['totals']['brokerA']['spread_alerts'], $d['totals']['brokerB']['spread_alerts']]);
        self::assertNull($d['broker_choice']);
        file_put_contents("{$this->kit->root}/data/status.json", json_encode(['broker_choice' => ['broker' => 'brokerA', 'name' => 'IC Markets', 'decided' => '2026-11-06']]));
        self::assertSame('IC Markets (chosen 2026-11-06)', $this->get('/api/audit', [])->data()['broker_choice']);
    }

    public function testAProbeWindowAndADailyReport(): void
    {
        $q = $this->get('/api/audit', ['period' => 'QuietHour'])->data();
        self::assertSame(['AUDJPY'], array_column($q['symbols'], 'symbol'));
        self::assertSame('brokerA', $q['symbols'][0]['better']['latency_avg_ms']);
        $day = $this->get('/api/audit', ['report' => '2026-11-04'])->data();
        self::assertSame('day', $day['report']['kind']);
        self::assertSame([], $day['symbols'][0]['better'], 'one broker only: nothing to compare');
        self::assertSame(404, $this->get('/api/audit', ['report' => '2026-11-05'])->status);
        self::assertSame(400, $this->get('/api/audit', ['report' => '../x'])->status);
        self::assertSame(400, $this->get('/api/audit', ['period' => 'Nope'])->status);
    }

    public function testForexIsComparedByPipsAndEverythingElseByUsdExceptSilver(): void
    {
        self::assertSame([true, true, true, false, false, false, false],
            array_map([AuditData::class, 'byPips'], ['EURUSD', 'GBPJPY', 'XAGUSD', 'XAUUSD', 'BTCUSD', 'US500', 'XNGUSD']));
    }

    public function testConditionsComeFromTheLatestSnapshot(): void
    {
        $d = $this->get('/api/conditions', ['account' => 'brokerA-49123893'])->data();
        self::assertTrue($d['available']);
        self::assertSame([276.74, '1:500', 'EURUSD', 1.24], [$d['free_margin'], $d['leverage'], $d['symbols'][0]['symbol'], $d['symbols'][0]['largest_lots_now']]);
        self::assertFalse($this->get('/api/conditions', ['account' => 'brokerB-49124666'])->data()['available']);
        self::assertSame(401, $this->kit->request('GET', '/api/audit', [])->status);
    }
}
