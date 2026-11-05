<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;
use TraderDash\App;

/** openapi.yaml and the server's routes list the same operations with the same least role. */
final class ContractTest extends TestCase
{
    public function testContractMatchesTheRoutes(): void
    {
        $lines = file(__DIR__ . '/../../openapi.yaml', FILE_IGNORE_NEW_LINES);
        $contract = [];
        $path = $method = null;
        foreach ($lines as $line) {
            if (preg_match('#^  (/[^:]*):$#', $line, $m)) {
                $path = $m[1];
            } elseif ($path !== null && preg_match('/^    (get|post|put|delete|patch):$/', $line, $m)) {
                $method = strtoupper($m[1]);
            } elseif ($method !== null && preg_match('/^      x-least-role: (\w+)$/', $line, $m)) {
                $contract["$method $path"] = $m[1];
            } elseif (preg_match('/^\S/', $line)) {
                $path = $method = null;
            }
        }
        $routes = array_map(fn ($r) => $r[0], App::ROUTES);
        ksort($contract);
        ksort($routes);
        self::assertSame($routes, $contract);
    }
}
