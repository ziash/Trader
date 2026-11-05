<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use Firebase\JWT\JWT;
use TraderDash\App;
use TraderDash\Auth\GoogleSignIn;
use TraderDash\Config;
use TraderDash\Http\Request;
use TraderDash\Http\Response;
use TraderDash\Random;

/** A whole app on a temporary server folder, with a fake Google that signs ID tokens with a test key. */
final class TestKit
{
    public const OWNER = 'owner@example.com';
    public const CLIENT_ID = 'test-client.apps.googleusercontent.com';
    public const SITE = 'https://trader.uzlabz.com';

    public string $root;
    public FakeHttpClient $http;
    public FixedClock $clock;
    public Config $config;
    public App $app;
    /** Claims the fake Google puts in the next ID token (merged over valid defaults); null = no ID token. */
    public ?array $nextClaims = [];
    public string $keyId = 'test-key-1';

    private static ?\OpenSSLAsymmetricKey $key = null;

    public function __construct()
    {
        $this->root = sys_get_temp_dir() . '/tdash-test-' . bin2hex(random_bytes(6));
        mkdir($this->root, 0700, true);
        $this->http = new FakeHttpClient();
        $this->clock = new FixedClock();
        $this->config = new Config(self::OWNER, self::CLIENT_ID, 'test-secret', self::SITE . '/auth/callback', self::SITE);
        $this->app = App::fromRoot($this->root, $this->http, $this->clock, $this->config);
        $this->http->routes[GoogleSignIn::KEYS_URL] = fn () => [200, ['cache-control' => 'public, max-age=3600'], json_encode($this->jwks())];
        $this->http->routes[GoogleSignIn::TOKEN_URL] = fn (array $fields) => $this->tokenEndpoint($fields);
    }

    public function cleanUp(): void
    {
        exec('rm -rf ' . escapeshellarg($this->root));
    }

    public function request(string $method, string $path, array $options = []): Response
    {
        return $this->app->handle(new Request(
            $method,
            $path,
            $options['query'] ?? [],
            $options['headers'] ?? [],
            $options['cookies'] ?? [],
            isset($options['json']) ? json_encode($options['json']) : '',
            $options['ip'] ?? '203.0.113.7',
            'PHPUnit',
        ));
    }

    /** Runs the whole Google sign-in for $email; returns the callback's response. */
    public function googleSignIn(string $email, string $sub, array $claims = []): Response
    {
        $start = $this->request('GET', '/auth/google');
        parse_str((string) parse_url($start->headers['Location'], PHP_URL_QUERY), $q);
        $this->nextClaims = $claims + ['email' => $email, 'sub' => $sub, 'nonce' => $q['nonce']];
        return $this->request('GET', '/auth/callback', [
            'query' => ['code' => 'auth-code', 'state' => $q['state']],
            'cookies' => [App::SIGNIN_COOKIE => $start->cookies[App::SIGNIN_COOKIE]['value']],
        ]);
    }

    /** Signs in and returns [cookies, csrf] for later calls; fails the test if sign-in does not succeed. */
    public function session(string $email, string $sub): array
    {
        $r = $this->googleSignIn($email, $sub);
        $token = $r->cookies[App::SESSION_COOKIE]['value'] ?? null;
        if ($token === null) {
            throw new \RuntimeException("Sign-in of $email failed: " . ($r->headers['Location'] ?? $r->status));
        }
        $cookies = [App::SESSION_COOKIE => $token];
        $csrf = $this->request('GET', '/api/me', ['cookies' => $cookies])->data()['csrf'];
        return [$cookies, $csrf];
    }

    /** A signed-in person's change: cookie, anti-forgery token and our own Origin. */
    public function personPost(array $session, string $path, array $json = []): Response
    {
        return $this->request('POST', $path, [
            'cookies' => $session[0],
            'headers' => ['x-csrf-token' => $session[1], 'origin' => self::SITE],
            'json' => $json,
        ]);
    }

    /** Creates a machine account the way the laptop script does; returns [client ID, secret]. */
    public function machine(string $name = 'claude', string $role = 'admin'): array
    {
        $clientId = "m2m_{$name}_" . bin2hex(random_bytes(4));
        $secret = Random::token();
        $users = new \TraderDash\Users(new \TraderDash\Store\JsonStore("{$this->root}/users.json"), self::OWNER, $this->clock);
        $users->setMachine($name, $role, $clientId, password_hash($secret, PASSWORD_DEFAULT));
        return [$clientId, $secret];
    }

    public function machineToken(string $clientId, string $secret): Response
    {
        return $this->request('POST', '/api/auth/token', ['headers' => ['authorization' => 'Basic ' . base64_encode("$clientId:$secret")]]);
    }

    public function idToken(array $claims, ?\OpenSSLAsymmetricKey $key = null, ?string $kid = null): string
    {
        $now = $this->clock->now();
        $claims += [
            'iss' => 'https://accounts.google.com',
            'aud' => self::CLIENT_ID,
            'azp' => self::CLIENT_ID,
            'iat' => $now,
            'exp' => $now + 3600,
            'email_verified' => true,
        ];
        openssl_pkey_export($key ?? self::key(), $pem);
        return JWT::encode($claims, $pem, 'RS256', $kid ?? $this->keyId);
    }

    public static function key(): \OpenSSLAsymmetricKey
    {
        return self::$key ??= openssl_pkey_new(['private_key_bits' => 2048, 'private_key_type' => OPENSSL_KEYTYPE_RSA]);
    }

    private function jwks(): array
    {
        $rsa = openssl_pkey_get_details(self::key())['rsa'];
        return ['keys' => [[
            'kty' => 'RSA', 'alg' => 'RS256', 'use' => 'sig', 'kid' => $this->keyId,
            'n' => Random::base64Url($rsa['n']), 'e' => Random::base64Url($rsa['e']),
        ]]];
    }

    private function tokenEndpoint(array $fields): array
    {
        if ($this->nextClaims === null || $fields['client_secret'] !== 'test-secret' || ($fields['code_verifier'] ?? '') === '') {
            return [400, [], '{"error":"invalid_grant"}'];
        }
        return [200, [], json_encode(['id_token' => $this->idToken($this->nextClaims), 'access_token' => 'x', 'token_type' => 'Bearer'])];
    }
}
