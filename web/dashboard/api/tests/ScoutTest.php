<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;

/** The Scout page's data from uploaded setups.csv and orders.csv (dashboard.md §2.1), and the order pictures. */
final class ScoutTest extends TestCase
{
    private const SETUPS = 'time_utc,candle_utc,symbol,timeframe,event,side,level,far_side,setup_utc,pause_candles,pause_height_atr,move_atr,pullback,atr,higher_tf,higher_trend,with_higher,mode,reason';
    private const ORDERS = 'time_utc,symbol,timeframe,setup_utc,role,action,side,lots,price_req,stop_loss,expires_utc,order_id,position_id,price_fill,slip_pips,gross,net,result,detail,chart';
    private const CHART = 'brokerA/49123893/Scout-aaaaaa/2026-11-04/charts/GBPJPY-H1-202611041400-M-1001.svg';

    private TestKit $kit;
    private array $bearer;

    protected function setUp(): void
    {
        $this->kit = new TestKit();
        [$id, $secret] = $this->kit->machine('claude', 'viewer');
        $this->bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];

        // Day 1 (instance aaaaaa): GBPJPY main stop found and placed, its picture; a dry-run row without an order.
        $this->put('Scout-aaaaaa/2026-11-04/setups.csv', self::SETUPS, [
            '2026-11-04 16:00:05,2026-11-04 15:00:00,GBPJPY,H1,NewStop,Buy,212.686,212.474,2026-11-04 14:00:00,2,0.67,2.50,0.27,0.181,H4,up,with,Trade,',
        ]);
        $this->put('Scout-aaaaaa/2026-11-04/orders.csv', self::ORDERS, [
            '2026-11-04 16:00:06,GBPJPY,H1,2026-11-04 14:00:00,M,place,Buy,0.10,212.686,212.474,2026-11-05 08:00:06,1001,,,,,,ok,Scout-aaaaaa-H1-202611041400-M,' . self::CHART,
            '2026-11-04 16:00:06,EURUSD,H1,2026-11-04 14:00:00,M,place,Buy,0.10,1.1,1.0,,,,,,,,dry run,,',
        ]);
        $this->put('Scout-aaaaaa/2026-11-04/charts/GBPJPY-H1-202611041400-M-1001.svg', null, ['<svg xmlns="http://www.w3.org/2000/svg"/>']);
        // Day 2 (after a restart, instance bbbbbb): it fills and closes; XAUUSD found and cancelled; a cut-off line.
        $this->put('Scout-bbbbbb/2026-11-05/setups.csv', self::SETUPS, [
            '2026-11-05 09:00:04,2026-11-05 08:00:00,GBPJPY,H1,StopFilled,Buy,212.686,212.474,2026-11-04 14:00:00,,,,,0.181,H4,up,with,Trade,',
            '2026-11-05 10:00:04,2026-11-05 09:00:00,XAUUSD,H1,NewStop,Sell,4055.44,4070.46,2026-11-05 07:00:00,3,0.5,2.1,0.2,16.3,H4,up,against,Trade,',
            '2026-11-05 11:00:04,2026-11-05 10:00:00,XAUUSD,H1,Cancelled,Sell,4055.44,4070.46,2026-11-05 07:00:00,,,,,16.3,H4,up,against,Trade,trend turned',
            '2026-11-05 11:00:09,2026-11-05 10:00:00,XAUUSD',
        ]);
        $this->put('Scout-bbbbbb/2026-11-05/orders.csv', self::ORDERS, [
            '2026-11-05 08:41:00,GBPJPY,H1,2026-11-04 14:00:00,M,filled,Buy,0.10,212.686,212.474,,1001,692232259,212.688,0.2,,,ok,,',
            '2026-11-05 10:00:05,XAUUSD,H1,2026-11-05 07:00:00,M,place,Sell,0.01,4055.44,4070.46,2026-11-06 02:00:05,1002,,,,,,ok,Scout-bbbbbb-H1-202611050700-M,',
            '2026-11-05 10:16:00,GBPJPY,H1,2026-11-04 14:00:00,M,closed,Buy,0.10,212.686,212.474,,1001,692232259,,,12.90,12.40,ok,,',
            '2026-11-05 11:00:05,XAUUSD,H1,2026-11-05 07:00:00,M,cancel,Sell,0.01,4055.44,4070.46,,1002,,,,,,ok,trend turned,',
        ]);
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

    private function get(array $query): \TraderDash\Http\Response
    {
        return $this->kit->request('GET', '/api/scout', ['headers' => $this->bearer, 'query' => $query]);
    }

    public function testOrdersAreMergedAcrossDaysAndInstances(): void
    {
        $d = $this->get(['account' => 'brokerA-49123893', 'day' => '2026-11-04'])->data();
        self::assertSame(['2026-11-04', '2026-11-05'], $d['days']);
        $o = $d['orders'][0];
        self::assertCount(1, $d['orders'], 'the dry-run row has no order');
        self::assertSame(['1001', 'closed', '2026-11-04 16:00:06', '212.688', 0.2, 12.4, self::CHART],
            [$o['order_id'], $o['state'], $o['placed_utc'], $o['price_fill'], $o['slip_pips'], $o['net_usd'], $o['chart']]);
        self::assertSame(['orders' => 1, 'filled' => 1, 'closed' => 1, 'net_usd' => 12.4], $d['totals']);
    }

    public function testSetupsOfTheDayNewestFirstWithTheirOrderAndStory(): void
    {
        $d = $this->get(['account' => 'brokerA-49123893'])->data(); // default: the latest day
        self::assertSame('2026-11-05', $d['day']);
        self::assertSame(['XAUUSD-H1-202611050700', 'GBPJPY-H1-202611041400'], array_column($d['setups'], 'key'));

        [$xau, $gbp] = $d['setups'];
        self::assertSame(['Cancelled', '2026-11-05 11:00:00', 'trend turned', false, 'cancelled'],
            [$xau['event'], $xau['candle_close_utc'], $xau['reason'], $xau['with_higher'], $xau['order']['state']]);
        self::assertSame(['StopFilled', 'M', '212.686', '212.474', 0.1, 'closed', true],
            [$gbp['event'], $gbp['role'], $gbp['level'], $gbp['stop_loss'], $gbp['lots'], $gbp['order']['state'], $gbp['with_higher']]);
        self::assertSame(['found', 'placed', 'filled', 'filled', 'closed'], array_column($gbp['timeline'], 'kind'));
        self::assertSame('Position closed · net +$12.40', end($gbp['timeline'])['text']);
        self::assertSame(['1002'], array_column($d['orders'], 'order_id'));
    }

    public function testBadParametersAre400(): void
    {
        self::assertSame(400, $this->get([])->status);
        self::assertSame(400, $this->get(['account' => '../brokerA-1'])->status);
        self::assertSame(400, $this->get(['account' => 'brokerA-49123893', 'day' => '2026-11-5'])->status);
        self::assertSame([], $this->get(['account' => 'brokerB-1'])->data()['setups']);
    }

    public function testOrderPictureIsServedAsAnImageOnly(): void
    {
        $r = $this->kit->request('GET', '/api/scout/chart', ['headers' => $this->bearer, 'query' => ['file' => self::CHART]]);
        self::assertSame(200, $r->status);
        self::assertSame('image/svg+xml', $r->headers['Content-Type']);
        self::assertStringContainsString('sandbox', $r->headers['Content-Security-Policy']);
        self::assertSame('private, max-age=86400', $r->headers['Cache-Control']);
        self::assertStringStartsWith('<svg', $r->body);
    }

    public function testNothingButOrderPicturesIsServed(): void
    {
        file_put_contents("{$this->kit->root}/data/status.json", '{}');
        foreach (['../../status.json', 'brokerA/49123893/Scout-aaaaaa/2026-11-04/orders.csv',
            'brokerA/49123893/Scout-aaaaaa/2026-11-04/charts/../../../../../../config.php', '/etc/passwd'] as $file) {
            $r = $this->kit->request('GET', '/api/scout/chart', ['headers' => $this->bearer, 'query' => ['file' => $file]]);
            self::assertSame(400, $r->status, $file);
        }
        $missing = 'brokerA/49123893/Scout-aaaaaa/2026-11-04/charts/NOPE-H1-1-M-1.svg';
        self::assertSame(404, $this->kit->request('GET', '/api/scout/chart', ['headers' => $this->bearer, 'query' => ['file' => $missing]])->status);
    }

    public function testNotSignedInSeesNothing(): void
    {
        self::assertSame(401, $this->kit->request('GET', '/api/scout', ['query' => ['account' => 'brokerA-49123893']])->status);
        self::assertSame(401, $this->kit->request('GET', '/api/scout/chart', ['query' => ['file' => self::CHART]])->status);
    }
}
