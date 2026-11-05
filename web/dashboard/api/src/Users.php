<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\HttpError;
use TraderDash\Store\JsonStore;

/**
 * users.json (dashboard.md §5): the owner's pinned Google account, approved people (viewers), access requests,
 * refused addresses, machine accounts (secrets as hashes only) and the access-request switch.
 * The owner's address itself comes from config.php, so it can never be changed from the web.
 */
final class Users
{
    public const MAX_REQUESTS = 50;
    public const MACHINE_ROLES = ['admin', 'viewer'];

    public function __construct(private readonly JsonStore $store, private readonly string $ownerEmail, private readonly Clock $clock)
    {
    }

    public static function normalise(string $email): string
    {
        return strtolower(trim($email));
    }

    /**
     * Decides a Google sign-in: "owner", "viewer", "requested" (now waiting for the owner), "refused" or "account_mismatch"
     * (the address is known but belongs to another Google account than the one first seen).
     */
    public function admit(string $email, string $sub, string $ip): string
    {
        $email = self::normalise($email);
        $now = $this->clock->utc();
        return $this->store->update(function (array &$d) use ($email, $sub, $ip, $now): string {
            $d = self::complete($d);
            if ($email === $this->ownerEmail) {
                if ($d['owner'] === null) {
                    $d['owner'] = ['sub' => $sub, 'first_utc' => $now, 'last_seen_utc' => $now, 'last_ip' => $ip];
                    return 'owner';
                }
                if (!hash_equals($d['owner']['sub'], $sub)) {
                    return 'account_mismatch';
                }
                $d['owner']['last_seen_utc'] = $now;
                $d['owner']['last_ip'] = $ip;
                return 'owner';
            }
            if (isset($d['people'][$email])) {
                $person = &$d['people'][$email];
                if ($person['sub'] !== null && !hash_equals($person['sub'], $sub)) {
                    return 'account_mismatch';
                }
                $person['sub'] ??= $sub;
                $person['last_seen_utc'] = $now;
                $person['last_ip'] = $ip;
                return $person['role'];
            }
            if (isset($d['refused'][$email]) || !$d['settings']['access_requests']) {
                return 'refused';
            }
            $d['requests'][$email] = [
                'sub' => $sub,
                'requested_utc' => $d['requests'][$email]['requested_utc'] ?? $now,
                'last_try_utc' => $now,
                'ip' => $ip,
            ];
            if (count($d['requests']) > self::MAX_REQUESTS) {
                uasort($d['requests'], fn ($a, $b) => strcmp($a['last_try_utc'], $b['last_try_utc']));
                $d['requests'] = array_slice($d['requests'], -self::MAX_REQUESTS, null, true);
            }
            return 'requested';
        });
    }

    /** The role a signed-in person has now (null = no longer allowed), checked on every request. */
    public function personRole(string $email, string $sub): ?string
    {
        $d = self::complete($this->store->read());
        if ($email === $this->ownerEmail) {
            return $d['owner'] !== null && hash_equals($d['owner']['sub'], $sub) ? 'owner' : null;
        }
        $person = $d['people'][$email] ?? null;
        return $person !== null && $person['sub'] !== null && hash_equals($person['sub'], $sub) ? $person['role'] : null;
    }

    public function touchPerson(string $email, string $ip): void
    {
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($email, $ip, $now): void {
            $d = self::complete($d);
            if ($email === $this->ownerEmail && $d['owner'] !== null) {
                $d['owner']['last_seen_utc'] = $now;
                $d['owner']['last_ip'] = $ip;
            } elseif (isset($d['people'][$email])) {
                $d['people'][$email]['last_seen_utc'] = $now;
                $d['people'][$email]['last_ip'] = $ip;
            }
        });
    }

    /** Approves a waiting (or earlier refused) address as viewer. */
    public function approve(string $email): void
    {
        $email = self::normalise($email);
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($email, $now): void {
            $d = self::complete($d);
            $from = $d['requests'][$email] ?? $d['refused'][$email] ?? null;
            if ($from === null) {
                throw new HttpError(404, 'not_found', 'No access request from this address');
            }
            $d['people'][$email] = [
                'role' => 'viewer',
                'sub' => $from['sub'] ?? null,
                'approved_utc' => $now,
                'last_seen_utc' => null,
                'last_ip' => null,
            ];
            unset($d['requests'][$email], $d['refused'][$email]);
        });
    }

    public function refuse(string $email): void
    {
        $email = self::normalise($email);
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($email, $now): void {
            $d = self::complete($d);
            if (!isset($d['requests'][$email])) {
                throw new HttpError(404, 'not_found', 'No access request from this address');
            }
            $d['refused'][$email] = ['sub' => $d['requests'][$email]['sub'], 'refused_utc' => $now];
            unset($d['requests'][$email]);
        });
    }

    /** Removes an approved viewer; they can ask again, and the owner decides again. */
    public function remove(string $email): void
    {
        $email = self::normalise($email);
        $this->store->update(function (array &$d) use ($email): void {
            $d = self::complete($d);
            if (!isset($d['people'][$email])) {
                throw new HttpError(404, 'not_found', 'No such person');
            }
            unset($d['people'][$email]);
        });
    }

    public function setAccessRequests(bool $on): void
    {
        $this->store->update(function (array &$d) use ($on): void {
            $d = self::complete($d);
            $d['settings']['access_requests'] = $on;
        });
    }

    /** Creates a machine account or replaces its client ID and secret hash (rotation); only from the laptop over SSH. */
    public function setMachine(string $name, string $role, string $clientId, string $secretHash): void
    {
        if (!preg_match('/^[a-z][a-z0-9-]{1,30}$/', $name) || !in_array($role, self::MACHINE_ROLES, true)
            || !preg_match('/^m2m_[a-z0-9-]+_[0-9a-f]{8,}$/', $clientId) || password_get_info($secretHash)['algo'] === null) {
            throw new \InvalidArgumentException('Machine name, role, client ID or secret hash not valid');
        }
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($name, $role, $clientId, $secretHash, $now): void {
            $d = self::complete($d);
            foreach ($d['machines'] as $other => $m) {
                if ($other !== $name && $m['client_id'] === $clientId) {
                    throw new \InvalidArgumentException('Client ID already used');
                }
            }
            $d['machines'][$name] = [
                'role' => $role,
                'client_id' => $clientId,
                'secret_hash' => $secretHash,
                'created_utc' => $d['machines'][$name]['created_utc'] ?? $now,
                'secret_set_utc' => $now,
                'revoked_utc' => null,
                'last_seen_utc' => $d['machines'][$name]['last_seen_utc'] ?? null,
            ];
        });
    }

    public function revokeMachine(string $name): void
    {
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($name, $now): void {
            $d = self::complete($d);
            if (!isset($d['machines'][$name])) {
                throw new HttpError(404, 'not_found', 'No such machine');
            }
            $d['machines'][$name]['revoked_utc'] = $now;
        });
    }

    /** @return array{name: string, role: string, secret_hash: string}|null an active machine */
    public function machineByClientId(string $clientId): ?array
    {
        foreach (self::complete($this->store->read())['machines'] as $name => $m) {
            if (hash_equals($m['client_id'], $clientId) && $m['revoked_utc'] === null) {
                return ['name' => $name, 'role' => $m['role'], 'secret_hash' => $m['secret_hash']];
            }
        }
        return null;
    }

    public function machineRole(string $name): ?string
    {
        $m = self::complete($this->store->read())['machines'][$name] ?? null;
        return $m !== null && $m['revoked_utc'] === null ? $m['role'] : null;
    }

    public function touchMachine(string $name): void
    {
        $now = $this->clock->utc();
        $this->store->update(function (array &$d) use ($name, $now): void {
            $d = self::complete($d);
            if (isset($d['machines'][$name])) {
                $d['machines'][$name]['last_seen_utc'] = $now;
            }
        });
    }

    /** Everything the Users page shows; never the secret hashes or Google account numbers. */
    public function overview(): array
    {
        $d = self::complete($this->store->read());
        $list = static function (array $map, array $fields): array {
            $out = [];
            foreach ($map as $key => $row) {
                $out[] = ['email' => $key] + array_intersect_key($row, array_flip($fields));
            }
            return $out;
        };
        $machines = [];
        foreach ($d['machines'] as $name => $m) {
            $machines[] = ['name' => $name] + array_intersect_key($m, array_flip(['role', 'client_id', 'created_utc', 'secret_set_utc', 'revoked_utc', 'last_seen_utc']));
        }
        return [
            'owner' => ['email' => $this->ownerEmail]
                + array_intersect_key($d['owner'] ?? [], array_flip(['first_utc', 'last_seen_utc', 'last_ip'])),
            'people' => $list($d['people'], ['role', 'approved_utc', 'last_seen_utc', 'last_ip']),
            'requests' => $list($d['requests'], ['requested_utc', 'last_try_utc', 'ip']),
            'refused' => $list($d['refused'], ['refused_utc']),
            'machines' => $machines,
            'settings' => $d['settings'],
        ];
    }

    private static function complete(array $d): array
    {
        $d += ['version' => 1, 'owner' => null, 'people' => [], 'requests' => [], 'refused' => [], 'machines' => [], 'settings' => []];
        $d['settings'] += ['access_requests' => true];
        return $d;
    }
}
