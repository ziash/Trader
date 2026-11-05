<?php

declare(strict_types=1);

namespace TraderDash\Auth;

/** Who is calling: a person signed in with Google (session cookie) or a machine (M2M access token). */
final class Principal
{
    public const RANK = ['viewer' => 1, 'admin' => 2, 'owner' => 3];

    public function __construct(
        public readonly string $kind,      // "person" or "machine"
        public readonly string $id,        // e-mail address or machine name
        public readonly string $role,      // owner, admin or viewer
        public readonly ?string $csrf = null,
        public readonly ?string $sessionToken = null,
    ) {
    }

    public function atLeast(string $role): bool
    {
        return self::RANK[$this->role] >= self::RANK[$role];
    }
}
