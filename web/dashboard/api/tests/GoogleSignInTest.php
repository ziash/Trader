<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;
use TraderDash\App;
use TraderDash\Auth\GoogleSignIn;
use TraderDash\Random;

final class GoogleSignInTest extends TestCase
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

    public function testStartSendsOnlySignInScopesWithStateNonceAndPkce(): void
    {
        $r = $this->kit->request('GET', '/auth/google');
        self::assertSame(302, $r->status);
        self::assertStringStartsWith(GoogleSignIn::AUTH_URL . '?', $r->headers['Location']);
        parse_str((string) parse_url($r->headers['Location'], PHP_URL_QUERY), $q);
        self::assertSame('openid email', $q['scope']);
        self::assertSame(TestKit::CLIENT_ID, $q['client_id']);
        self::assertSame(TestKit::SITE . '/auth/callback', $q['redirect_uri']);
        self::assertSame('S256', $q['code_challenge_method']);
        self::assertGreaterThanOrEqual(43, strlen($q['state']));
        self::assertGreaterThanOrEqual(43, strlen($q['nonce']));
        self::assertNotEmpty($r->cookies[App::SIGNIN_COOKIE]['value']);
    }

    public function testCodeVerifierSentToGoogleMatchesTheChallenge(): void
    {
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $this->kit->nextClaims = ['email' => TestKit::OWNER, 'sub' => '111', 'nonce' => $q['nonce']];
        $this->kit->request('GET', '/auth/callback', [
            'query' => ['code' => 'c', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ]);
        $tokenCall = array_values(array_filter($this->kit->http->calls, fn ($c) => $c[0] === GoogleSignIn::TOKEN_URL))[0][1];
        self::assertSame($q['code_challenge'], Random::base64Url(hash('sha256', $tokenCall['code_verifier'], true)));
    }

    public function testOwnerSignsInAndGetsASession(): void
    {
        $r = $this->kit->googleSignIn(TestKit::OWNER, '111');
        self::assertSame(TestKit::SITE . '/scout', $r->headers['Location']);
        self::assertArrayHasKey(App::SESSION_COOKIE, $r->cookies);
        self::assertSame(-1, $r->cookies[App::SIGNIN_COOKIE]['maxAge']);
        $me = $this->kit->request('GET', '/api/me', ['cookies' => [App::SESSION_COOKIE => $r->cookies[App::SESSION_COOKIE]['value']]]);
        self::assertSame(['kind' => 'person', 'id' => TestKit::OWNER, 'role' => 'owner'], array_diff_key($me->data(), ['csrf' => 1]));
    }

    public function testOwnerAddressIsMatchedWithoutCase(): void
    {
        $r = $this->kit->googleSignIn('Owner@Example.COM', '111');
        self::assertSame(TestKit::SITE . '/scout', $r->headers['Location']);
    }

    public function testOwnerAddressOnAnotherGoogleAccountIsRefused(): void
    {
        $this->kit->googleSignIn(TestKit::OWNER, '111');
        $r = $this->kit->googleSignIn(TestKit::OWNER, '999');
        self::assertSame(TestKit::SITE . '/?access=refused', $r->headers['Location']);
        self::assertArrayNotHasKey(App::SESSION_COOKIE, $r->cookies);
    }

    public function testUnknownAddressBecomesAnAccessRequest(): void
    {
        $r = $this->kit->googleSignIn('someone@example.com', '222');
        self::assertSame(TestKit::SITE . '/?access=requested', $r->headers['Location']);
        self::assertArrayNotHasKey(App::SESSION_COOKIE, $r->cookies);
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $users = $this->kit->request('GET', '/api/users', ['cookies' => $owner[0]])->data();
        self::assertSame('someone@example.com', $users['requests'][0]['email']);
    }

    public function testUnknownAddressIsRefusedWhenRequestsAreOff(): void
    {
        $owner = $this->kit->session(TestKit::OWNER, '111');
        $this->kit->personPost($owner, '/api/users/settings', ['access_requests' => false]);
        $r = $this->kit->googleSignIn('someone@example.com', '222');
        self::assertSame(TestKit::SITE . '/?access=refused', $r->headers['Location']);
    }

    /** @return iterable<string, array{0: array}> */
    public static function untrustedTokens(): iterable
    {
        yield 'other audience' => [['aud' => 'someone-else.apps.googleusercontent.com']];
        yield 'other issuer' => [['iss' => 'https://evil.example.com']];
        yield 'expired' => [['exp' => 1_793_900_000 - 3600, 'iat' => 1_793_900_000 - 7200]];
        yield 'other nonce' => [['nonce' => 'not-the-one-we-sent']];
        yield 'email not verified' => [['email_verified' => false]];
        yield 'other authorised party' => [['azp' => 'someone-else.apps.googleusercontent.com']];
        yield 'not an email' => [['email' => 'not-an-email']];
    }

    #[DataProvider('untrustedTokens')]
    public function testUntrustedIdTokenIsRefused(array $claims): void
    {
        $r = $this->kit->googleSignIn(TestKit::OWNER, '111', $claims);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
        self::assertArrayNotHasKey(App::SESSION_COOKIE, $r->cookies);
    }

    public function testTokenSignedWithAnotherKeyIsRefused(): void
    {
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $forged = $this->kit->idToken(['email' => TestKit::OWNER, 'sub' => '111', 'nonce' => $q['nonce']],
            openssl_pkey_new(['private_key_bits' => 2048, 'private_key_type' => OPENSSL_KEYTYPE_RSA]));
        $this->kit->http->routes[GoogleSignIn::TOKEN_URL] = fn () => [200, [], json_encode(['id_token' => $forged])];
        $r = $this->kit->request('GET', '/auth/callback', [
            'query' => ['code' => 'c', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ]);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
    }

    public function testReplyInAnotherBrowserIsRefused(): void
    {
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $this->kit->nextClaims = ['email' => TestKit::OWNER, 'sub' => '111', 'nonce' => $q['nonce']];
        $r = $this->kit->request('GET', '/auth/callback', ['query' => ['code' => 'c', 'state' => $q['state']]]);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
        self::assertStringContainsString('other_browser', $this->lastLogDetail());
    }

    public function testStateCanBeUsedOnlyOnce(): void
    {
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $this->kit->nextClaims = ['email' => TestKit::OWNER, 'sub' => '111', 'nonce' => $q['nonce']];
        $reply = [
            'query' => ['code' => 'c', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ];
        self::assertSame(TestKit::SITE . '/scout', $this->kit->request('GET', '/auth/callback', $reply)->headers['Location']);
        self::assertSame(TestKit::SITE . '/?access=error', $this->kit->request('GET', '/auth/callback', $reply)->headers['Location']);
    }

    public function testStateExpiresAfterTenMinutes(): void
    {
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $this->kit->clock->time += 601;
        $this->kit->nextClaims = ['email' => TestKit::OWNER, 'sub' => '111', 'nonce' => $q['nonce']];
        $r = $this->kit->request('GET', '/auth/callback', [
            'query' => ['code' => 'c', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ]);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
    }

    public function testGoogleErrorAndFailedExchangeAreRefused(): void
    {
        $r = $this->kit->request('GET', '/auth/callback', ['query' => ['error' => 'access_denied', 'state' => 'x']]);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
        $this->kit->nextClaims = null;
        $start = $this->kit->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $r = $this->kit->request('GET', '/auth/callback', [
            'query' => ['code' => 'c', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ]);
        self::assertSame(TestKit::SITE . '/?access=error', $r->headers['Location']);
    }

    public function testGoogleKeysAreCachedAndRefetchedForANewKeyId(): void
    {
        $this->kit->googleSignIn(TestKit::OWNER, '111');
        $this->kit->googleSignIn(TestKit::OWNER, '111');
        self::assertCount(1, array_filter($this->kit->http->calls, fn ($c) => $c[0] === GoogleSignIn::KEYS_URL));
        $this->kit->keyId = 'test-key-2'; // Google turned its keys over
        $r = $this->kit->googleSignIn(TestKit::OWNER, '111');
        self::assertSame(TestKit::SITE . '/scout', $r->headers['Location']);
        self::assertCount(2, array_filter($this->kit->http->calls, fn ($c) => $c[0] === GoogleSignIn::KEYS_URL));
    }

    private function lastLogDetail(): string
    {
        $lines = file("{$this->kit->root}/logs/access.csv", FILE_IGNORE_NEW_LINES);
        return (string) end($lines);
    }
}
