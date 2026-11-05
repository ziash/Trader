<?php

declare(strict_types=1);

namespace TraderDash\Auth;

use TraderDash\Clock;
use TraderDash\Random;
use TraderDash\Store\SecretFiles;

/** People's sessions (dashboard.md §5.2): a random cookie token; idle timeout 14 days for the owner, 7 for viewers. */
final class Sessions
{
    public const IDLE = ['owner' => 14 * 86400, 'viewer' => 7 * 86400];
    public const TOUCH_EVERY = 300;

    public function __construct(private readonly SecretFiles $files, private readonly Clock $clock)
    {
    }

    /** @return string the session token for the cookie */
    public function create(string $email, string $sub): string
    {
        $now = $this->clock->now();
        $this->files->deleteWhere(fn (array $s) => ($s['last_seen'] ?? 0) < $now - self::IDLE['owner']);
        $token = Random::token();
        $this->files->put($token, ['email' => $email, 'sub' => $sub, 'created' => $now, 'last_seen' => $now, 'csrf' => Random::token()]);
        return $token;
    }

    /** @return array{email: string, sub: string, csrf: string, touched: bool}|null null when unknown or idle too long for $roleOf's answer */
    public function resolve(string $token, callable $roleOf): ?array
    {
        $s = $this->files->get($token);
        if ($s === null) {
            return null;
        }
        $role = $roleOf($s['email'], $s['sub']);
        $now = $this->clock->now();
        if ($role === null || $s['last_seen'] < $now - self::IDLE[$role === 'owner' ? 'owner' : 'viewer']) {
            $this->files->delete($token);
            return null;
        }
        $touched = $s['last_seen'] < $now - self::TOUCH_EVERY;
        if ($touched) {
            $s['last_seen'] = $now;
            $this->files->put($token, $s);
        }
        return ['email' => $s['email'], 'sub' => $s['sub'], 'csrf' => $s['csrf'], 'role' => $role, 'touched' => $touched];
    }

    public function destroy(string $token): void
    {
        $this->files->delete($token);
    }

    public function destroyAllFor(string $email): int
    {
        return $this->files->deleteWhere(fn (array $s) => ($s['email'] ?? null) === $email);
    }
}
