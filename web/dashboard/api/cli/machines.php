<?php

declare(strict_types=1);

// Machine accounts on the server (dashboard.md §5.3), run over SSH by scripts/dashboard-m2m.sh on the laptop:
//   php machines.php set <name> <admin|viewer> <client_id>    (the secret's hash on standard input)
//   php machines.php revoke <name>
//   php machines.php list
// The secret itself never reaches the server; only its hash does.

use TraderDash\Clock;
use TraderDash\Store\JsonStore;
use TraderDash\Store\SecretFiles;
use TraderDash\Users;

$root = getenv('TRADER_DASH_ROOT') ?: dirname(__DIR__, 2);
require __DIR__ . '/../vendor/autoload.php';

$config = \TraderDash\Config::load("$root/config.php");
$clock = new Clock();
$users = new Users(new JsonStore("$root/users.json"), $config->ownerEmail, $clock);
$endTokens = fn (string $name) => (new SecretFiles("$root/state/tokens"))->deleteWhere(fn (array $t) => ($t['machine'] ?? null) === $name);

[$command, $name, $role, $clientId] = array_pad(array_slice($argv, 1), 4, '');
try {
    switch ($command) {
        case 'set':
            $hash = trim((string) stream_get_contents(STDIN));
            $users->setMachine($name, $role, $clientId, $hash);
            $ended = $endTokens($name);
            echo "Machine $name set ($role, $clientId); $ended old tokens ended\n";
            break;
        case 'revoke':
            $users->revokeMachine($name);
            echo "Machine $name revoked; {$endTokens($name)} tokens ended\n";
            break;
        case 'list':
            foreach ($users->overview()['machines'] as $m) {
                printf("%-12s %-7s %-28s revoked=%s last_seen=%s\n", $m['name'], $m['role'], $m['client_id'], $m['revoked_utc'] ?? '-', $m['last_seen_utc'] ?? '-');
            }
            break;
        default:
            fwrite(STDERR, "Usage: machines.php set <name> <admin|viewer> <client_id> | revoke <name> | list\n");
            exit(2);
    }
} catch (\Throwable $e) {
    fwrite(STDERR, $e->getMessage() . "\n");
    exit(1);
}
