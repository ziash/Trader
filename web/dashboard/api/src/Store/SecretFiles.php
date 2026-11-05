<?php

declare(strict_types=1);

namespace TraderDash\Store;

/**
 * A folder of small JSON files, one per secret (session, sign-in in progress, machine token).
 * A file is named after the SHA-256 of its secret, so the folder never holds a secret itself.
 */
final class SecretFiles
{
    public function __construct(private readonly string $dir)
    {
    }

    public function put(string $secret, array $data): void
    {
        Files::writeAtomic($this->file($secret), json_encode($data, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES));
    }

    public function get(string $secret): ?array
    {
        if ($secret === '' || !is_file($file = $this->file($secret))) {
            return null;
        }
        $data = json_decode((string) file_get_contents($file), true);
        return is_array($data) ? $data : null;
    }

    /** Reads and removes in one go; of two requests with the same secret only one gets it. */
    public function take(string $secret): ?array
    {
        if ($secret === '') {
            return null;
        }
        $file = $this->file($secret);
        $claimed = $file . '.taken-' . bin2hex(random_bytes(4));
        if (!@rename($file, $claimed)) {
            return null;
        }
        $data = json_decode((string) file_get_contents($claimed), true);
        unlink($claimed);
        return is_array($data) ? $data : null;
    }

    public function delete(string $secret): void
    {
        @unlink($this->file($secret));
    }

    /** Removes every file whose data matches; returns how many. */
    public function deleteWhere(callable $matches): int
    {
        $count = 0;
        foreach (glob($this->dir . '/*.json') ?: [] as $file) {
            $data = json_decode((string) @file_get_contents($file), true);
            if (!is_array($data) || $matches($data)) {
                @unlink($file);
                $count++;
            }
        }
        return $count;
    }

    private function file(string $secret): string
    {
        return $this->dir . '/' . hash('sha256', $secret) . '.json';
    }
}
