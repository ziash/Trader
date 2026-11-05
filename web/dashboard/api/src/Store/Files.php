<?php

declare(strict_types=1);

namespace TraderDash\Store;

final class Files
{
    /** Folders only the server account can open (700). */
    public static function ensureDir(string $dir): void
    {
        if (!is_dir($dir) && !mkdir($dir, 0700, true) && !is_dir($dir)) {
            throw new \RuntimeException("Cannot create $dir");
        }
    }

    /** Written to a temporary file in the same folder, then renamed, so a reader never sees half a file. Mode 600. */
    public static function writeAtomic(string $path, string $content): void
    {
        self::ensureDir(dirname($path));
        $tmp = $path . '.tmp-' . bin2hex(random_bytes(4));
        if (file_put_contents($tmp, $content, LOCK_EX) === false) {
            throw new \RuntimeException("Cannot write $tmp");
        }
        chmod($tmp, 0600);
        if (!rename($tmp, $path)) {
            @unlink($tmp);
            throw new \RuntimeException("Cannot rename $tmp");
        }
    }
}
