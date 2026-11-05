<?php

declare(strict_types=1);

namespace TraderDash\Auth;

use TraderDash\AccessLog;
use TraderDash\Clock;
use TraderDash\Http\HttpError;
use TraderDash\Http\Request;
use TraderDash\Random;
use TraderDash\Store\JsonStore;
use TraderDash\Store\SecretFiles;
use TraderDash\Users;

/**
 * M2M sign-in (dashboard.md §5.3): client ID + secret in an HTTP Basic header → an access token for 1 hour.
 * After 5 wrong secrets in 15 minutes a client ID is locked for 15 minutes (20 for one address, whatever the ID).
 */
final class MachineAuth
{
    public const TOKEN_SECONDS = 3600;
    public const WINDOW = 900;
    public const MAX_FAILS_ID = 5;
    public const MAX_FAILS_IP = 20;

    public function __construct(
        private readonly Users $users,
        private readonly SecretFiles $tokens,
        private readonly JsonStore $limits,
        private readonly AccessLog $log,
        private readonly Clock $clock,
    ) {
    }

    /** @return array{access_token: string, token_type: string, expires_in: int} */
    public function issue(Request $request): array
    {
        $now = $this->clock->now();
        [$clientId, $secret] = $this->credentials($request->header('authorization') ?? '');
        $keys = ['id:' . substr($clientId, 0, 80), 'ip:' . $request->ip];

        if ($this->lockedUntil($keys) > $now) {
            $this->log->write('m2m_locked', $clientId, '', $request, 'sign-in locked');
            throw new HttpError(429, 'locked', 'Too many wrong secrets; try again later');
        }
        $machine = $clientId === '' ? null : $this->users->machineByClientId($clientId);
        if ($machine === null || !password_verify($secret, $machine['secret_hash'])) {
            $locked = $this->fail($keys);
            $this->log->write('m2m_refused', $clientId, '', $request, $locked ? 'wrong client ID or secret; now locked for 15 min' : 'wrong client ID or secret');
            throw new HttpError(401, 'invalid_client', 'Client ID or secret not valid');
        }
        $this->clear($keys[0]);
        $this->tokens->deleteWhere(fn (array $t) => ($t['expires'] ?? 0) < $now);
        $token = Random::token();
        $this->tokens->put($token, ['machine' => $machine['name'], 'expires' => $now + self::TOKEN_SECONDS]);
        $this->users->touchMachine($machine['name']);
        $this->log->write('m2m_token', $machine['name'], $machine['role'], $request);
        return ['access_token' => $token, 'token_type' => 'Bearer', 'expires_in' => self::TOKEN_SECONDS];
    }

    public function resolve(string $token): ?Principal
    {
        $t = $this->tokens->get($token);
        if ($t === null || $t['expires'] <= $this->clock->now()) {
            return null;
        }
        $role = $this->users->machineRole($t['machine']);
        return $role === null ? null : new Principal('machine', $t['machine'], $role);
    }

    /** Ends a machine's tokens at once (revoke or new secret). */
    public function endTokens(string $machine): int
    {
        return $this->tokens->deleteWhere(fn (array $t) => ($t['machine'] ?? null) === $machine);
    }

    /** @return array{0: string, 1: string} */
    private function credentials(string $header): array
    {
        if (!preg_match('/^Basic\s+([A-Za-z0-9+\/=]+)$/', $header, $m)) {
            return ['', ''];
        }
        $pair = base64_decode($m[1], true);
        if ($pair === false || !str_contains($pair, ':')) {
            return ['', ''];
        }
        [$id, $secret] = explode(':', $pair, 2);
        return [rawurldecode($id), rawurldecode($secret)];
    }

    private function lockedUntil(array $keys): int
    {
        $limits = $this->limits->read();
        return max(array_map(fn ($k) => (int) ($limits[$k]['locked_until'] ?? 0), $keys));
    }

    /** Counts a wrong attempt; true when this attempt locked the client ID. */
    private function fail(array $keys): bool
    {
        $now = $this->clock->now();
        return $this->limits->update(function (array &$limits) use ($keys, $now): bool {
            foreach ($limits as $k => $entry) {
                if (($entry['locked_until'] ?? 0) < $now && max($entry['fails'] ?: [0]) < $now - self::WINDOW) {
                    unset($limits[$k]);
                }
            }
            $locked = false;
            foreach ($keys as $k) {
                $fails = array_values(array_filter($limits[$k]['fails'] ?? [], fn ($t) => $t >= $now - self::WINDOW));
                $fails[] = $now;
                $max = str_starts_with($k, 'id:') ? self::MAX_FAILS_ID : self::MAX_FAILS_IP;
                $limits[$k] = ['fails' => $fails, 'locked_until' => count($fails) >= $max ? $now + self::WINDOW : 0];
                $locked = $locked || ($limits[$k]['locked_until'] > 0 && str_starts_with($k, 'id:'));
            }
            return $locked;
        });
    }

    private function clear(string $key): void
    {
        $this->limits->update(function (array &$limits) use ($key): void {
            unset($limits[$key]);
        });
    }
}
