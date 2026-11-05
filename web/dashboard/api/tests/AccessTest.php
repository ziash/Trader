<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\TestCase;
use TraderDash\App;
use TraderDash\Auth\Sessions;

/** Who may call what: every route's role is checked on the server, and changes need the anti-forgery token. */
final class AccessTest extends TestCase
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

    public function testEveryNonPublicRouteRefusesCallersWhoAreNotSignedIn(): void
    {
        foreach (App::ROUTES as $key => [$least]) {
            if ($least === 'public') {
                continue;
            }
            [$method, $path] = explode(' ', $key);
            foreach ([[], ['cookies' => [App::SESSION_COOKIE => 'made-up']], ['headers' => ['authorization' => 'Bearer ' . str_repeat('a', 43)]]] as $options) {
                $r = $this->kit->request($method, $path, $options);
                self::assertSame(401, $r->status, "$key must refuse " . json_encode($options));
                self::assertSame('no-store', $r->headers['Cache-Control']);
            }
        }
    }

    public function testEveryRouteRefusesRolesBelowItsLeast(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->googleSignIn('viewer@example.com', '222');
        $this->kit->personPost($owner, '/api/users/approve', ['email' => 'viewer@example.com']);
        $viewer = $this->kit->session('viewer@example.com', '222');
        [$id, $secret] = $this->kit->machine('claude', 'admin');
        $bearer = ['authorization' => 'Bearer ' . $this->kit->machineToken($id, $secret)->data()['access_token']];

        $rank = ['viewer' => 1, 'admin' => 2, 'owner' => 3];
        foreach (App::ROUTES as $key => [$least]) {
            if ($least === 'public' || $key === 'POST /api/auth/logout') { // sign-out has its own tests
                continue;
            }
            [$method, $path] = explode(' ', $key);
            $asViewer = $method === 'GET' ? $this->kit->request('GET', $path, ['cookies' => $viewer[0]]) : $this->kit->personPost($viewer, $path);
            $asMachine = $this->kit->request($method, $path, ['headers' => $bearer, 'json' => []]);
            if ($rank[$least] > 1) {
                self::assertSame(403, $asViewer->status, "$key must refuse a viewer");
            } else {
                self::assertNotSame(403, $asViewer->status, "$key must allow a viewer");
            }
            if ($rank[$least] > 2) {
                self::assertSame(403, $asMachine->status, "$key must refuse an administrator machine");
            } else {
                self::assertNotContains($asMachine->status, [401, 403], "$key must allow an administrator machine");
            }
        }
    }

    public function testChangesWithTheCookieNeedTheAntiForgeryTokenAndOurOrigin(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $body = ['json' => ['access_requests' => false], 'cookies' => $owner[0]];
        self::assertSame(403, $this->kit->request('POST', '/api/users/settings', $body)->status);
        self::assertSame(403, $this->kit->request('POST', '/api/users/settings', $body + ['headers' => ['x-csrf-token' => 'wrong']])->status);
        self::assertSame(403, $this->kit->request('POST', '/api/users/settings', $body + ['headers' => ['x-csrf-token' => $owner[1], 'origin' => 'https://evil.example.com']])->status);
        self::assertSame(200, $this->kit->personPost($owner, '/api/users/settings', ['access_requests' => false])->status);
    }

    public function testApprovedViewerCanWatchAndIsRemovedAtOnce(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->googleSignIn('viewer@example.com', '222');
        self::assertSame(200, $this->kit->personPost($owner, '/api/users/approve', ['email' => 'Viewer@Example.com'])->status);
        $viewer = $this->kit->session('viewer@example.com', '222');
        self::assertSame('viewer', $this->kit->request('GET', '/api/me', ['cookies' => $viewer[0]])->data()['role']);
        self::assertSame(200, $this->kit->request('GET', '/api/status', ['cookies' => $viewer[0]])->status);

        self::assertSame(200, $this->kit->personPost($owner, '/api/users/remove', ['email' => 'viewer@example.com'])->status);
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['cookies' => $viewer[0]])->status);
    }

    public function testViewerOnAnotherGoogleAccountIsRefused(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->googleSignIn('viewer@example.com', '222');
        $this->kit->personPost($owner, '/api/users/approve', ['email' => 'viewer@example.com']);
        self::assertSame(TestKit::SITE . '/?access=refused', $this->kit->googleSignIn('viewer@example.com', '333')->headers['Location']);
    }

    public function testRefusedAddressStaysRefusedUntilApproved(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->googleSignIn('someone@example.com', '222');
        self::assertSame(200, $this->kit->personPost($owner, '/api/users/refuse', ['email' => 'someone@example.com'])->status);
        self::assertSame(TestKit::SITE . '/?access=refused', $this->kit->googleSignIn('someone@example.com', '222')->headers['Location']);
        self::assertSame(200, $this->kit->personPost($owner, '/api/users/approve', ['email' => 'someone@example.com'])->status);
        self::assertSame(TestKit::SITE . '/scout', $this->kit->googleSignIn('someone@example.com', '222')->headers['Location']);
    }

    public function testApproveUnknownAddressIs404(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        self::assertSame(404, $this->kit->personPost($owner, '/api/users/approve', ['email' => 'nobody@example.com'])->status);
        self::assertSame(400, $this->kit->personPost($owner, '/api/users/approve', [])->status);
    }

    public function testSessionsEndAfterIdleTimeout(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->googleSignIn('viewer@example.com', '222');
        $this->kit->personPost($owner, '/api/users/approve', ['email' => 'viewer@example.com']);
        $viewer = $this->kit->session('viewer@example.com', '222');

        $this->kit->clock->time += Sessions::IDLE['viewer'] + 1;
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['cookies' => $viewer[0]])->status);
        self::assertSame(200, $this->kit->request('GET', '/api/me', ['cookies' => $owner[0]])->status);
        $this->kit->clock->time += Sessions::IDLE['owner'] + 1;
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['cookies' => $owner[0]])->status);
    }

    public function testUseKeepsTheSessionAlive(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        for ($day = 0; $day < 20; $day++) {
            $this->kit->clock->time += 86400;
            self::assertSame(200, $this->kit->request('GET', '/api/me', ['cookies' => $owner[0]])->status);
        }
    }

    public function testSignOutEndsTheSession(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $r = $this->kit->personPost($owner, '/api/auth/logout');
        self::assertSame(200, $r->status);
        self::assertSame(-1, $r->cookies[App::SESSION_COOKIE]['maxAge']);
        self::assertSame(401, $this->kit->request('GET', '/api/me', ['cookies' => $owner[0]])->status);
    }

    public function testUsersOverviewNeverShowsHashesOrGoogleAccountNumbers(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->machine('claude');
        $this->kit->googleSignIn('someone@example.com', '222');
        $body = $this->kit->request('GET', '/api/users', ['cookies' => $owner[0]])->body;
        self::assertStringNotContainsString('secret_hash', $body);
        self::assertStringNotContainsString('$2y$', $body);
        self::assertStringNotContainsString('"sub"', $body);
        self::assertStringContainsString('"name":"claude"', $body);
    }

    public function testUnknownAddressIs404AndErrorsAreJson(): void
    {
        $r = $this->kit->request('GET', '/api/nothing-here');
        self::assertSame(404, $r->status);
        self::assertSame('not_found', $r->data()['error']);
    }

    public function testStatusSaysNoDataUntilTheFirstUpload(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        self::assertFalse($this->kit->request('GET', '/api/status', ['cookies' => $owner[0]])->data()['available']);
        mkdir("{$this->kit->root}/data", 0700, true);
        file_put_contents("{$this->kit->root}/data/status.json", '{"generated_utc":"2026-11-05 17:02:00"}');
        $data = $this->kit->request('GET', '/api/status', ['cookies' => $owner[0]])->data();
        self::assertTrue($data['available']);
        self::assertSame('2026-11-05 17:02:00', $data['generated_utc']);
    }

    public function testServerFilesAreReadableOnlyByTheServerAccount(): void
    {
        $this->kit->session(TestKit::OWNER, '111');
        foreach (['users.json', 'logs/access.csv'] as $file) {
            self::assertSame('600', decoct(fileperms("{$this->kit->root}/$file") & 0777), $file);
        }
        foreach (glob("{$this->kit->root}/state/sessions/*.json") as $file) {
            self::assertSame('600', decoct(fileperms($file) & 0777));
        }
    }
}
