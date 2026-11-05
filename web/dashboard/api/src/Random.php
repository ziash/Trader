<?php

declare(strict_types=1);

namespace TraderDash;

final class Random
{
    /** A random URL-safe token (32 bytes → 43 characters). */
    public static function token(int $bytes = 32): string
    {
        return self::base64Url(random_bytes($bytes));
    }

    public static function base64Url(string $raw): string
    {
        return rtrim(strtr(base64_encode($raw), '+/', '-_'), '=');
    }
}
