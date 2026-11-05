<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;
use TraderDash\Auth\MachineAuth;

final class MachineAuthTest extends TestCase
{
    private TestKit $kit;

    protected function setUp(): void
    {
        $this->kit = new TestKit();
    }

    protected function tearDown(): void
    {
        $this->kit->cleanUp();
    }

    public function testRightSecretGivesAOneHourToken(): void
    {
        [$id, $secret] = $this->kit->machine();
        $r = $this->kit->machineToken($id, $secret);
        self::assertSame(200, $r->status);
        self::assertSame('Bearer', $r->data()['token_type']);
        self::assertSame(3600, $r->data()['expires_in']);
        $me = $this->kit->request('GET', '/api/me', ['headers' => ['authorization' => 'Bearer ' . $r->data()['access_token']]]);
        self::assertSame(['kind' => 'machine', 'id' => 'claude', 'role' => 'admin'], $me->data());
    }

    public function testWrongSecretOrUnknownClientIsRefused(): void
    {
        [$id] = $this->kit->machine();
        self::assertSame(401, $this->kit->machineToken($id, 'wrong')->status);
        self::assertSame(401, $this->kit->machineToken('m2m_nobody_00000000', 'x')->status);
        self::assertSame(401, $this->kit->request('POST', '/api/auth/token')->status);
        self::assertSame(401, $this->kit->request('POST', '/api/auth/token', ['headers' => ['authorization' => 'Basic !!!']])->status);
    }

    public function testFiveWrongSecretsLockTheClientForFifteenMinutes(): void
    {
        [$id, $secret] = $this->kit->machine();
        for ($i = 0; $i < MachineAuth::MAX_FAILS_ID; $i++) {
            self::assertSame(401, $this->kit->machineToken($id, 'wrong')->status);
        }
        self::assertSame(429, $this->kit->machineToken($id, $secret)->status, 'even the right secret waits');
        $this->kit->clock->time += MachineAuth::WINDOW + 1;
        self::assertSame(200, $this->kit->machineToken($id, $secret)->status);
    }

    public function testTokenExpiresAfterAnHour(): void
    {
        [$id, $secret] = $this->kit->machine();
        $bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        $this->kit->clock->time += 3599;
        self::assertSame(200, $this->kit->request('GET', '/api/me', ['headers' => $bearer])->status);
        $this->kit->clock->time += 1;
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['headers' => $bearer])->status);
    }

    public function testRevokeEndsTokensAndSignIn(): void
    {
        [$id, $secret] = $this->kit->machine();
        $bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        $owner = $this->kit->session(TestKit::OWNER, '111');
        self::assertSame(200, $this->kit->personPost($owner, '/api/users/machines/revoke', ['name' => 'claude'])->status);
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['headers' => $bearer])->status);
        self::assertSame(401, $this->kit->machineToken($id, $secret)->status);
    }

    public function testMachineCannotUseTheUsersPageOrSignOut(): void
    {
        [$id, $secret] = $this->kit->machine();
        $bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        self::assertSame(403, $this->kit->request('GET', '/api/users', ['headers' => $bearer])->status);
        self::assertSame(403, $this->kit->request('POST', '/api/users/approve', ['headers' => $bearer, 'json' => ['email' => 'x@example.com']])->status);
        self::assertSame(400, $this->kit->request('POST', '/api/auth/logout', ['headers' => $bearer])->status);
    }

    public function testEveryMachineCallIsLoggedWithItsName(): void
    {
        [$id, $secret] = $this->kit->machine();
        $bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];
        $this->kit->request('GET', '/api/status', ['headers' => $bearer]);
        $log = file_get_contents("{$this->kit->root}/logs/access.csv");
        self::assertStringContainsString('m2m_token,claude,admin', $log);
        self::assertStringContainsString('api,claude,admin,203.0.113.7,PHPUnit,"GET /api/status 200"', $log);
        self::assertStringNotContainsString($secret, $log);
    }

    public function testMachineRoleCanNeverBeOwner(): void
    {
        $this->expectException(\InvalidArgumentException::class);
        $this->kit->machine('rogue', 'owner');
    }
}
