<?php

declare(strict_types=1);

namespace TraderDash\Auth;

use Firebase\JWT\JWK;
use Firebase\JWT\JWT;
use TraderDash\Clock;
use TraderDash\Config;
use TraderDash\Http\HttpClient;
use TraderDash\Random;
use TraderDash\Store\Files;
use TraderDash\Store\SecretFiles;
use TraderDash\Users;

/**
 * "Sign in with Google" (dashboard.md §5.2): OpenID Connect, authorisation code with PKCE.
 * A one-time state (bound to this browser by a cookie) and nonce; the ID token's signature is checked against
 * Google's published keys, then its issuer, audience, expiry, nonce and email_verified.
 */
final class GoogleSignIn
{
    public const AUTH_URL = 'https://accounts.google.com/o/oauth2/v2/auth';
    public const TOKEN_URL = 'https://oauth2.googleapis.com/token';
    public const KEYS_URL = 'https://www.googleapis.com/oauth2/v3/certs';
    public const ISSUERS = ['https://accounts.google.com', 'accounts.google.com'];
    public const PENDING_SECONDS = 600;

    public function __construct(
        private readonly Config $config,
        private readonly HttpClient $http,
        private readonly SecretFiles $pending,
        private readonly string $keysCacheFile,
        private readonly Clock $clock,
    ) {
    }

    /** @return array{0: string, 1: string} the Google address to send the browser to, and the browser-binding cookie value */
    public function start(): array
    {
        $now = $this->clock->now();
        $this->pending->deleteWhere(fn (array $p) => ($p['created'] ?? 0) < $now - self::PENDING_SECONDS);
        $state = Random::token();
        $nonce = Random::token();
        $verifier = Random::token(48);
        $binding = Random::token();
        $this->pending->put($state, [
            'nonce' => $nonce,
            'verifier' => $verifier,
            'binding' => hash('sha256', $binding),
            'created' => $now,
        ]);
        $url = self::AUTH_URL . '?' . http_build_query([
            'response_type' => 'code',
            'client_id' => $this->config->googleClientId,
            'redirect_uri' => $this->config->googleRedirectUri,
            'scope' => 'openid email',
            'state' => $state,
            'nonce' => $nonce,
            'code_challenge' => Random::base64Url(hash('sha256', $verifier, true)),
            'code_challenge_method' => 'S256',
            'prompt' => 'select_account',
        ], '', '&', PHP_QUERY_RFC3986);
        return [$url, $binding];
    }

    /**
     * Google's reply at /auth/callback.
     *
     * @param array<string, string> $query
     * @return array{email: string, sub: string}
     */
    public function finish(array $query, string $binding): array
    {
        if (isset($query['error'])) {
            throw new SignInRefused('google_error: ' . substr(preg_replace('/[^a-z_]/', '', $query['error']) ?? '', 0, 40));
        }
        $state = $query['state'] ?? '';
        $code = $query['code'] ?? '';
        if ($state === '' || $code === '') {
            throw new SignInRefused('missing_code_or_state');
        }
        $pending = $this->pending->take($state);
        if ($pending === null) {
            throw new SignInRefused('unknown_or_used_state');
        }
        if ($pending['created'] < $this->clock->now() - self::PENDING_SECONDS) {
            throw new SignInRefused('state_expired');
        }
        if ($binding === '' || !hash_equals($pending['binding'], hash('sha256', $binding))) {
            throw new SignInRefused('other_browser');
        }

        [$status, , $body] = $this->http->postForm(self::TOKEN_URL, [
            'grant_type' => 'authorization_code',
            'code' => $code,
            'client_id' => $this->config->googleClientId,
            'client_secret' => $this->config->googleClientSecret,
            'redirect_uri' => $this->config->googleRedirectUri,
            'code_verifier' => $pending['verifier'],
        ]);
        $tokens = json_decode($body, true);
        if ($status !== 200 || !is_string($tokens['id_token'] ?? null)) {
            throw new SignInRefused("token_exchange_failed: http $status");
        }
        $claims = $this->verify($tokens['id_token'], $pending['nonce']);
        return ['email' => Users::normalise($claims['email']), 'sub' => $claims['sub']];
    }

    /** @return array{email: string, sub: string} */
    public function verify(string $idToken, string $nonce): array
    {
        $kid = $this->keyId($idToken);
        $keys = $this->keys(false);
        if (!$this->hasKey($keys, $kid)) {
            $keys = $this->keys(true); // Google has turned its keys over since we fetched them
        }
        JWT::$timestamp = $this->clock->now();
        JWT::$leeway = 60;
        try {
            $claims = (array) JWT::decode($idToken, JWK::parseKeySet($keys, 'RS256'));
        } catch (\Throwable $e) {
            throw new SignInRefused('id_token_invalid: ' . (new \ReflectionClass($e))->getShortName());
        }
        $aud = $claims['aud'] ?? null;
        $checks = [
            'issuer' => in_array($claims['iss'] ?? null, self::ISSUERS, true),
            'audience' => $aud === $this->config->googleClientId || is_array($aud) && in_array($this->config->googleClientId, $aud, true),
            'azp' => !isset($claims['azp']) || $claims['azp'] === $this->config->googleClientId,
            'nonce' => is_string($claims['nonce'] ?? null) && hash_equals($nonce, $claims['nonce']),
            'expiry' => is_int($claims['exp'] ?? null),
            'email_verified' => ($claims['email_verified'] ?? false) === true,
            'email' => is_string($claims['email'] ?? null) && filter_var($claims['email'], FILTER_VALIDATE_EMAIL) !== false,
            'sub' => is_string($claims['sub'] ?? null) && $claims['sub'] !== '',
        ];
        foreach ($checks as $name => $ok) {
            if (!$ok) {
                throw new SignInRefused("id_token_$name");
            }
        }
        return ['email' => $claims['email'], 'sub' => $claims['sub']];
    }

    private function keyId(string $jwt): string
    {
        $header = json_decode((string) base64_decode(strtr(explode('.', $jwt)[0], '-_', '+/'), true), true);
        return is_array($header) && is_string($header['kid'] ?? null) ? $header['kid'] : '';
    }

    private function hasKey(array $keys, string $kid): bool
    {
        foreach ($keys['keys'] ?? [] as $key) {
            if (($key['kid'] ?? null) === $kid) {
                return true;
            }
        }
        return false;
    }

    /** Google's public keys, cached as long as Google's Cache-Control allows. */
    private function keys(bool $refresh): array
    {
        $now = $this->clock->now();
        $cached = is_file($this->keysCacheFile) ? json_decode((string) file_get_contents($this->keysCacheFile), true) : null;
        if (!$refresh && is_array($cached) && $cached['until'] > $now) {
            return $cached['jwks'];
        }
        try {
            [$status, $headers, $body] = $this->http->get(self::KEYS_URL);
            $jwks = json_decode($body, true);
            if ($status !== 200 || !is_array($jwks['keys'] ?? null)) {
                throw new \RuntimeException("Google keys: http $status");
            }
        } catch (\Throwable $e) {
            if (is_array($cached)) {
                return $cached['jwks'];
            }
            throw new SignInRefused('google_keys_unavailable');
        }
        $maxAge = preg_match('/max-age=(\d+)/', $headers['cache-control'] ?? '', $m) ? (int) $m[1] : 3600;
        Files::writeAtomic($this->keysCacheFile, json_encode(['until' => $now + min($maxAge, 86400), 'jwks' => $jwks], JSON_THROW_ON_ERROR));
        return $jwks;
    }
}
