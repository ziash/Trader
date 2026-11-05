<?php

declare(strict_types=1);

// Sample accounts and data for the local preview (dev/serve.sh). Made-up values, never uploaded anywhere.

use TraderDash\AccessLog;
use TraderDash\Clock;
use TraderDash\Http\Request;
use TraderDash\Store\Files;
use TraderDash\Store\JsonStore;
use TraderDash\Users;

$root = $argv[1];
require __DIR__ . '/../api/vendor/autoload.php';

Files::ensureDir($root);
file_put_contents("$root/config.php", <<<PHP
<?php
// Local preview settings: no real Google client.
return [
    'owner_email' => 'owner@example.com',
    'site_origin' => 'http://localhost:8090',
    'google' => ['client_id' => 'preview.apps.googleusercontent.com', 'client_secret' => 'none', 'redirect_uri' => 'http://localhost:8090/auth/callback'],
];
PHP);

$clock = new Clock();
$users = new Users(new JsonStore("$root/users.json"), 'owner@example.com', $clock);
$users->admit('owner@example.com', 'dev-owner', '192.0.2.15');
$users->admit('viewer@example.com', 'dev-viewer', '198.51.100.40');
$users->approve('viewer@example.com');
$users->admit('someone@example.com', 'dev-someone', '198.51.100.7');
$users->setMachine('claude', 'admin', 'm2m_claude_7f3a9c21', password_hash(bin2hex(random_bytes(16)), PASSWORD_DEFAULT));

$log = new AccessLog("$root/logs/access.csv", $clock);
$req = fn (string $ip, string $ua = 'Mozilla/5.0') => new Request('GET', '/', [], [], [], '', $ip, $ua);
$log->write('signin_refused', 'unknown@example.com', '', $req('203.0.113.90'), 'refused');
$log->write('signin', 'viewer@example.com', 'viewer', $req('198.51.100.40'), 'google');
$log->write('signin', 'owner@example.com', 'owner', $req('192.0.2.15'), 'google');
$log->write('access_requested', 'someone@example.com', '', $req('198.51.100.7'));
$log->write('m2m_token', 'claude', 'admin', $req('203.0.113.24', 'curl/8'));
$log->write('api', 'claude', 'admin', $req('203.0.113.24', 'curl/8'), 'GET /api/status 200');

$t = fn (int $minAgo) => gmdate('Y-m-d H:i:s', time() - $minAgo * 60);
$status = [
    'generated_utc' => $t(6),
    'accounts' => [
        ['id' => 'brokerA-49123893', 'broker' => 'brokerA', 'ctid' => 49123893, 'name' => 'IC Markets demo', 'scout' => true, 'tm' => true],
        ['id' => 'brokerB-49124666', 'broker' => 'brokerB', 'ctid' => 49124666, 'name' => 'Pepperstone demo', 'scout' => false, 'tm' => true],
    ],
    'units' => [
        ['name' => 'Scout', 'kind' => 'bot', 'state' => 'running', 'since_utc' => $t(960), 'build' => 'Scout-ae41f2c', 'last_log_utc' => $t(3), 'errors_today' => 0, 'warnings_today' => 2],
        ['name' => 'TM', 'kind' => 'bot', 'state' => 'running', 'since_utc' => $t(2400), 'build' => 'TM-41ca4d0', 'last_log_utc' => $t(4), 'errors_today' => 0, 'warnings_today' => 0],
        ['name' => 'Audit Bot', 'kind' => 'bot', 'state' => 'stopped', 'since_utc' => $t(1000), 'build' => 'AuditBot-62828b', 'last_log_utc' => $t(1000), 'errors_today' => 0, 'warnings_today' => 0],
        ['name' => 'Audit report', 'kind' => 'timer', 'state' => 'waiting', 'schedule' => 'daily', 'last_run_utc' => $t(700), 'last_result' => 'ok', 'next_run_utc' => $t(-740)],
        ['name' => 'Data upload', 'kind' => 'timer', 'state' => 'waiting', 'schedule' => 'every 30 min', 'last_run_utc' => $t(6), 'last_result' => 'ok', 'next_run_utc' => $t(-24)],
        ['name' => 'Control sync', 'kind' => 'timer', 'state' => 'waiting', 'schedule' => 'every minute', 'last_run_utc' => $t(1), 'last_result' => 'ok', 'next_run_utc' => $t(0)],
    ],
    'events' => [
        ['time_utc' => $t(95), 'bot' => 'Scout', 'level' => 'warning', 'message' => 'XNGUSD spread above limit — order not moved this candle (sample)'],
        ['time_utc' => $t(230), 'bot' => 'Scout', 'level' => 'warning', 'message' => 'cTrader connection dropped; reconnected after 4 s (sample)'],
    ],
    'upload' => ['last_utc' => $t(6), 'seconds' => 3, 'files' => 14, 'next_utc' => $t(-24)],
    'sync' => ['last_utc' => $t(1), 'ack_received' => true, 'next_utc' => $t(0)],
    'controls' => [],
];
Files::writeAtomic("$root/data/status.json", json_encode($status, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES));

// Scout's logs for the Scout page: the laptop's own staged upload when there is one (stays in the git-ignored .data folder).
$outbox = getenv('HOME') . '/tradebot/dashboard/outbox/logs';
if (is_dir($outbox)) {
    exec('cp -r ' . escapeshellarg($outbox) . ' ' . escapeshellarg("$root/data/"));
    echo "Copied the staged logs from $outbox\n";
}
echo "Sample data in $root\n";
