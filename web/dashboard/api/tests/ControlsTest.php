<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;

/** Scout's controls on the site (dashboard.md §3.4): only the catalogue's options and values, written for the laptop, acknowledged by Scout. */
final class ControlsTest extends TestCase
{
    private TestKit $kit;
    private array $admin;
    private array $viewer;

    protected function setUp(): void
    {
        $this->kit = new TestKit();
        [$id, $secret] = $this->kit->machine('claude', 'admin');
        $this->admin = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        [$id, $secret] = $this->kit->machine('watcher', 'viewer');
        $this->viewer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        @mkdir("{$this->kit->root}/data", 0700, true);
        $catalogue = json_decode((string) preg_replace('#^\s*//.*$#m', '', (string) file_get_contents(__DIR__ . '/../../../../config/scout-controls.json')), true);
        file_put_contents("{$this->kit->root}/data/scout-options.json", json_encode(['catalogue' => $catalogue, 'accounts' => ['brokerA-49123893' => [
            ['symbol' => 'EURUSD', 'timeframe' => 'H1', 'mode' => 'trade'], ['symbol' => 'XNGUSD', 'timeframe' => 'H1', 'mode' => 'trade'],
        ]]]));
    }

    protected function tearDown(): void
    {
        $this->kit->cleanUp();
    }

    private function set(array $body, ?array $as = null): \TraderDash\Http\Response
    {
        return $this->kit->request('POST', '/api/controls', ['headers' => $as ?? $this->admin, 'json' => ['account' => 'brokerA-49123893'] + $body]);
    }

    private function controlFile(): array
    {
        return json_decode((string) file_get_contents("{$this->kit->root}/controls/scout-brokerA-49123893.json"), true);
    }

    public function testASymbolSwitchedOffIsWrittenForTheLaptopAndShowsSavedUntilScoutAcknowledges(): void
    {
        $r = $this->set(['scope' => 'symbol', 'target' => 'XNGUSD', 'key' => 'enabled', 'value' => false]);
        self::assertSame(200, $r->status);
        $f = $this->controlFile();
        self::assertSame([1, 'brokerA-49123893', false, 1], [$f['version'], $f['account'], $f['symbols']['XNGUSD']['enabled'], $f['catalogue']]);
        $xng = $r->data()['symbols'][1];
        self::assertSame(['XNGUSD', 'saved'], [$xng['symbol'], $xng['state']['enabled']['state']]);

        // The laptop brings Scout's acknowledgement up: applied, with the order it cancelled.
        file_put_contents("{$this->kit->root}/controls/scout-brokerA-49123893.ack.json", json_encode(['version' => 1, 'applied_utc' => '2026-11-06 18:01:00',
            'knows' => ['symbol.enabled', 'symbol.timeframe', 'setup.decision'],
            'entries' => [['scope' => 'symbol', 'target' => 'XNGUSD', 'key' => 'enabled', 'value' => 'False', 'state' => 'applied', 'reason' => null, 'cancelled' => [1036848429]]]]));
        $v = $this->kit->request('GET', '/api/controls', ['headers' => $this->viewer, 'query' => ['account' => 'brokerA-49123893']])->data();
        self::assertSame(['applied', [1036848429]], [$v['symbols'][1]['state']['enabled']['state'], $v['symbols'][1]['state']['enabled']['cancelled']]);
        self::assertFalse($v['can_change']);

        // Back on = as configured: the entry goes, and the history says so.
        $this->set(['scope' => 'symbol', 'target' => 'XNGUSD', 'key' => 'enabled', 'value' => true]);
        self::assertSame([], (array) $this->controlFile()['symbols']);
        $status = $this->kit->request('GET', '/api/status', ['headers' => $this->admin]);
        self::assertSame(200, $status->status);
    }

    public function testOnlyTheCataloguesOptionsValuesAndSymbolsAndNeverForViewers(): void
    {
        self::assertSame(400, $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'timeframe', 'value' => 'M5'])->status);
        self::assertSame(400, $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'lots', 'value' => 5])->status);
        self::assertSame(400, $this->set(['scope' => 'symbol', 'target' => 'AUDCAD', 'key' => 'enabled', 'value' => false])->status);
        self::assertSame(400, $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'enabled', 'value' => 'no'])->status);
        self::assertSame(400, $this->set(['scope' => 'setup', 'target' => '../../x', 'key' => 'decision', 'value' => 'skip'])->status);
        self::assertSame(403, $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'enabled', 'value' => false], $this->viewer)->status);
        self::assertFalse(is_file("{$this->kit->root}/controls/scout-brokerA-49123893.json"));
    }

    public function testTimeframesAndSetupDecisionsKeepOnlyWhatDiffersFromTheConfiguration(): void
    {
        $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'timeframe', 'value' => 'D1']);
        $this->set(['scope' => 'setup', 'target' => 'EURUSD-H1-202611061400', 'key' => 'decision', 'value' => 'skip']);
        $f = $this->controlFile();
        self::assertSame(['D1', 'skip', 'claude', 2], [$f['symbols']['EURUSD']['timeframe'], $f['setups']['EURUSD-H1-202611061400']['decision'], $f['setups']['EURUSD-H1-202611061400']['by'], $f['version']]);
        $this->set(['scope' => 'symbol', 'target' => 'EURUSD', 'key' => 'timeframe', 'value' => 'H1']);   // its configured timeframe
        $this->set(['scope' => 'setup', 'target' => 'EURUSD-H1-202611061400', 'key' => 'decision', 'value' => 'keep']);
        $f = $this->controlFile();
        self::assertSame([[], [], 4], [(array) $f['symbols'], (array) $f['setups'], $f['version']]);
        $history = array_map(fn ($l) => str_getcsv($l, ',', '"', ''), file("{$this->kit->root}/controls/control-history.csv", FILE_IGNORE_NEW_LINES));
        self::assertSame(['old', 'new'], array_slice($history[0], 8));
        self::assertSame(['EURUSD-H1-202611061400', 'decision', 'skip', 'as configured'], array_slice($history[4], 6));
    }
}
