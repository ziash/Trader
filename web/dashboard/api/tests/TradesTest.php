<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;

/** The Accounts and TM trades pages' data (dashboard.md §2.3, §2.4) from the uploaded snapshot, deals, account rows and TM's actions. */
final class TradesTest extends TestCase
{
    private const DEALS = 'time_utc,deal_id,order_id,position_id,symbol,side,lots,price,kind,entry_price,gross_usd,swap_usd,commission_usd,net_usd,balance_usd,owner,trade,role,label';
    private const ACTIONS = 'time_utc,trade,symbol,role,action,side,lots,price_req,price_fill,latency_ms,result,detail';
    private const ACCOUNT = 'time_utc,balance_usd,equity_usd,margin_used_usd,free_margin_usd,margin_level_pct,leverage,positions,pending_orders';
    private const ORDERS = 'time_utc,symbol,timeframe,setup_utc,role,action,side,lots,price_req,stop_loss,expires_utc,order_id,position_id,price_fill,slip_pips,gross,net,result,detail,chart';
    private const CHART = 'brokerA/49123893/Dashboard/2026-11-05/charts/trade-P692692856.svg';

    private TestKit $kit;
    private array $bearer;

    protected function setUp(): void
    {
        $this->kit = new TestKit();
        [$id, $secret] = $this->kit->machine('claude', 'viewer');
        $this->bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];

        // The SOLUSD trade of 05–06 Nov 2026 (issue-log #1), shortened: main and mirror, one hedge pair, the rescue close.
        $this->put('Dashboard/2026-11-05/deals.csv', self::DEALS, [
            '2026-11-05 07:04:52,957264074,1035938797,692692856,SOLUSD,buy,20,96.359,open,,0.00,0.00,0.00,0.00,,Scout,P692692856,MAIN,Scout-6d48a6-H1-202611050300-M',
            '2026-11-05 07:04:52,957264075,1035938800,692692858,SOLUSD,buy,20,96.359,open,,0.00,0.00,0.00,0.00,,TM,P692692856,MIR,TM-38ade9-O1035938797-MIR',
            '2026-11-05 12:25:50,957355895,1036004021,692729521,SOLUSD,sell,20,95.722,open,,0.00,0.00,0.00,0.00,,TM,P692692856,HMAIN,TM-38ade9-P692692856-HMAIN',
            '2026-11-05 13:00:00,957300001,1036000001,692800001,EURUSD,buy,0.01,1.1,open,,0.00,0.00,-0.05,-0.05,,AuditBot,P692800001,MAIN,AuditBot-77fbd8',
            '2026-11-05 13:00:01,957300002,1036000002,692800001,EURUSD,sell,0.01,1.1,close,1.1,-0.01,0.00,-0.05,-0.06,4637.78,AuditBot,P692800001,MAIN,AuditBot-77fbd8',
        ]);
        $this->put('Dashboard/2026-11-06/deals.csv', self::DEALS, [
            '2026-11-06 16:14:28,958029716,1036852200,692692856,SOLUSD,sell,20,89.832,close,96.359,-130.54,-0.98,0.00,-131.52,3294.95,Scout,P692692856,MAIN,Scout-6d48a6-H1-202611050300-M',
            '2026-11-06 16:14:29,958029720,1036852203,692692858,SOLUSD,sell,20,89.842,close,96.359,-130.34,-0.98,0.00,-131.32,3163.63,TM,P692692856,MIR,TM-38ade9-O1035938797-MIR',
            '2026-11-06 16:14:29,958029722,1036852206,692729521,SOLUSD,buy,20,89.954,close,95.722,115.36,0.00,0.00,115.36,3278.99,TM,P692692856,HMAIN,TM-38ade9-P692692856-HMAIN',
        ]);
        $this->put('Dashboard/2026-11-06/account.csv', self::ACCOUNT, [
            '2026-11-06 16:14:56,3394.35,2278.93,1868.02,410.91,122.0,1:500,39,15',
        ]);
        $this->put('Dashboard/2026-11-05/charts/trade-P692692856.svg', null, ['<svg xmlns="http://www.w3.org/2000/svg"/>']);
        $this->put('TM-38ade9/2026-11-05/actions.csv', self::ACTIONS, [
            '2026-11-05 05:00:16,O1035938797,SOLUSD,MIR,PlaceMirrorOrder,buy,20.00,buy-stop 96.3520 SL 95.7220 TP none,,,ok,',
            '2026-11-05 07:04:53,P692692856,SOLUSD,HMAIN,PlaceHedge,sell,20.00,95.7220,,,ok,',
            '2026-11-05 09:12:56,P692692856,SOLUSD,HMAIN,PlaceHedge,sell,20.00,,,,NOT_ENOUGH_MONEY,',
            '2026-11-05 09:12:57,P692692856,SOLUSD,HMAIN,PlaceHedge,sell,20.00,,,,NOT_ENOUGH_MONEY,',
            '2026-11-05 09:12:58,P692692856,SOLUSD,HMAIN,PlaceHedge,sell,20.00,,,,NOT_ENOUGH_MONEY,',
            '2026-11-05 09:40:00,P692692856,SOLUSD,HMAIN,PlaceHedge,sell,20.00,,,,NOT_ENOUGH_MONEY,', // 27 min later: a new episode
        ]);
        $this->put('TM-f0c8e9/2026-11-06/actions.csv', self::ACTIONS, [
            '2026-11-06 16:14:28,P692692856,SOLUSD,MAIN,ClosePosition,,20.00,,89.8320,540,ok,position 692692856',
        ]);
        $this->put('Scout-6d48a6/2026-11-05/orders.csv', self::ORDERS, [
            '2026-11-05 05:00:15,SOLUSD,H1,2026-11-05 03:00:00,M,place,Buy,20.00,96.3520,95.7220,2026-11-05 21:00:15,1035938797,,,,,,ok,Scout-6d48a6-H1-202611050300-M,brokerA/49123893/Scout-6d48a6/2026-11-05/charts/SOLUSD-H1-202611050300-M-1035938797.svg',
        ]);
        file_put_contents("{$this->kit->root}/data/snapshot-brokerA-49123893.json", json_encode([
            'account' => 'brokerA-49123893', 'broker' => 'brokerA', 'ctid' => 49123893, 'time_utc' => '2026-11-06 16:14:56',
            'status' => ['balance' => 3394.35, 'equity' => 2278.93, 'margin_used' => 1868.02, 'free_margin' => 410.91, 'margin_level_pct' => 122.0,
                'leverage' => '1:500', 'positions' => 2, 'pending_orders' => 1],
            'positions' => [
                ['position_id' => 692619890, 'owner' => 'Scout', 'trade' => 'P692619890', 'role' => 'MAIN', 'symbol' => 'XNGUSD', 'side' => 'sell', 'lots' => 0.5,
                    'entry' => 2.738, 'stop_loss' => 2.897, 'take_profit' => null, 'open_result_usd' => -344.8, 'margin_usd' => 27.5, 'opened_utc' => '2026-11-04 22:10:00', 'label' => 'Scout-x'],
                ['position_id' => 692619895, 'owner' => 'TM', 'trade' => 'P692619890', 'role' => 'MIR', 'symbol' => 'XNGUSD', 'side' => 'sell', 'lots' => 0.5,
                    'entry' => 2.738, 'stop_loss' => 2.897, 'take_profit' => null, 'open_result_usd' => -344.8, 'margin_usd' => 27.5, 'opened_utc' => '2026-11-04 22:10:00', 'label' => 'TM-x'],
            ],
            'orders' => [
                ['order_id' => 1036848429, 'owner' => 'TM', 'trade' => 'P692619890', 'role' => 'HMAIN', 'symbol' => 'XNGUSD', 'type' => 'stop', 'side' => 'buy', 'lots' => 0.5,
                    'level' => 2.791, 'stop_loss' => null, 'placed_utc' => '2026-11-06 16:05:40', 'label' => 'TM-x'],
            ],
        ]));
    }

    protected function tearDown(): void
    {
        $this->kit->cleanUp();
    }

    private function put(string $relative, ?string $header, array $rows): void
    {
        $path = "{$this->kit->root}/data/logs/brokerA/49123893/$relative";
        @mkdir(dirname($path), 0700, true);
        file_put_contents($path, ($header !== null ? "$header\n" : '') . implode("\n", $rows) . "\n");
    }

    private function get(string $path, array $query): \TraderDash\Http\Response
    {
        return $this->kit->request('GET', $path, ['headers' => $this->bearer, 'query' => $query]);
    }

    public function testATradeIsEveryLegWithItsWholeResultAndStory(): void
    {
        $d = $this->get('/api/trades', ['account' => 'brokerA-49123893', 'day' => '2026-11-06'])->data();
        self::assertSame(['2026-11-05', '2026-11-06'], $d['days']);
        $sol = $d['trades'][array_search('P692692856', array_column($d['trades'], 'trade'), true)];
        self::assertSame(['Scout', 'SOLUSD', false, -147.48, 3], [$sol['owner'], $sol['symbol'], $sol['open'], $sol['result_usd'], $sol['legs']]);
        self::assertSame(self::CHART, $sol['chart']);
        self::assertSame(['1035938797', '2026-11-05'], [$sol['scout_order']['order_id'], $sol['scout_order']['day']]);
        // The mirror order logged under the pending order's name (O1035938797) belongs to this trade.
        self::assertSame('PlaceMirrorOrder buy buy-stop 96.3520 SL 95.7220 TP none', $sol['timeline'][0]['what']);
        // TM's refusals within 10 minutes are one line with a count; a later one starts a new line.
        $refused = array_values(array_filter($sol['timeline'], fn ($e) => !$e['ok']));
        self::assertSame([[3, 'NOT_ENOUGH_MONEY', '2026-11-05 09:12:58'], [1, 'NOT_ENOUGH_MONEY', null]],
            array_map(fn ($e) => [$e['count'], $e['result'], $e['until_utc'] ?? null], $refused));
        self::assertSame(4, $sol['refused']);
        self::assertSame(-131.52, array_values(array_filter($sol['timeline'], fn ($e) => $e['source'] === 'fill' && $e['role'] === 'MAIN' && $e['net_usd'] !== null))[0]['net_usd']);
    }

    public function testOpenTradesCountTheirOpenResultAndOtherBotsAreMarked(): void
    {
        $d = $this->get('/api/trades', ['account' => 'brokerA-49123893', 'day' => '2026-11-05'])->data();
        self::assertSame(['Scout', 'manual', 'TM'], $d['shown_owners']);
        self::assertContains('AuditBot', array_column($d['trades'], 'owner'));
        self::assertCount(2, $d['open_positions']);
        self::assertSame('P692619890', $d['open_orders'][0]['trade']);
        self::assertNotContains('P692619890', array_column($d['trades'], 'trade'), 'no fill or TM action on the day');
        $today = $this->get('/api/trades', ['account' => 'brokerA-49123893'])->data();
        self::assertSame('2026-11-06', $today['day']);
    }

    public function testAccountsShowMoneyTodayByOwnerAndTheLines(): void
    {
        $d = $this->get('/api/accounts', ['account' => 'brokerA-49123893'])->data();
        self::assertTrue($d['available']);
        self::assertEquals([122.0, 100, 300], [$d['status']['margin_level_pct'], $d['levels']['rescue_pct'], $d['levels']['warn_pct']]);
        self::assertSame([['open_result_usd' => -344.8, 'owner' => 'Scout', 'positions' => 1, 'orders' => 0], ['open_result_usd' => -344.8, 'owner' => 'TM', 'positions' => 1, 'orders' => 1]],
            $d['open_by_owner']);
        self::assertSame([['day' => '2026-11-05', 'net_usd' => -0.11, 'closes' => 1], ['day' => '2026-11-06', 'net_usd' => -147.48, 'closes' => 3]],
            array_map(fn ($r) => ['day' => $r['day'], 'net_usd' => $r['net_usd'], 'closes' => $r['closes']], $d['per_day']));
        self::assertSame([4637.78, 3294.95, 3163.63, 3278.99], array_column($d['balance'], 'balance_usd'));
        self::assertSame(2278.93, $d['equity'][0]['equity_usd']);
    }

    public function testBeforeTheFirstSnapshotThePageSaysSo(): void
    {
        $d = $this->get('/api/accounts', ['account' => 'brokerB-49124666'])->data();
        self::assertFalse($d['available']);
        self::assertSame([], $d['per_day']);
        self::assertSame(400, $this->get('/api/accounts', ['account' => '../x'])->status);
        self::assertSame(400, $this->get('/api/trades', ['account' => 'brokerA-49123893', 'day' => '6-11-2026'])->status);
    }

    public function testOnlyTradePicturesAreServedAsImages(): void
    {
        $r = $this->get('/api/trades/chart', ['file' => self::CHART]);
        self::assertSame([200, 'image/svg+xml'], [$r->status, $r->headers['Content-Type']]);
        self::assertStringContainsString('sandbox', $r->headers['Content-Security-Policy']);
        foreach (['brokerA/49123893/Dashboard/2026-11-06/deals.csv', 'brokerA/49123893/Dashboard/2026-11-05/charts/../../../../../config.php',
            'brokerA/49123893/Scout-6d48a6/2026-11-05/charts/SOLUSD-H1-202611050300-M-1035938797.svg'] as $file) {
            self::assertSame(400, $this->get('/api/trades/chart', ['file' => $file])->status, $file);
        }
        self::assertSame(404, $this->get('/api/trades/chart', ['file' => 'brokerA/49123893/Dashboard/2026-11-05/charts/trade-P1.svg'])->status);
        self::assertSame(401, $this->kit->request('GET', '/api/trades', ['query' => ['account' => 'brokerA-49123893']])->status);
    }
}
