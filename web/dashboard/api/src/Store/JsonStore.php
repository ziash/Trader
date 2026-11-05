<?php

declare(strict_types=1);

namespace TraderDash\Store;

/** One JSON file (users.json, ratelimit.json …): read under a shared lock, changed under an exclusive one, written whole then renamed. */
final class JsonStore
{
    public function __construct(private readonly string $path)
    {
    }

    public function read(): array
    {
        return $this->locked(LOCK_SH, fn () => $this->load());
    }

    /**
     * Changes the file: $change gets the data by reference and its return value is passed back.
     *
     * @template T
     * @param callable(array &$data): T $change
     * @return T
     */
    public function update(callable $change): mixed
    {
        return $this->locked(LOCK_EX, function () use ($change) {
            $data = $this->load();
            $result = $change($data);
            Files::writeAtomic($this->path, json_encode($data, JSON_THROW_ON_ERROR | JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE) . "\n");
            return $result;
        });
    }

    private function load(): array
    {
        if (!is_file($this->path)) {
            return [];
        }
        $data = json_decode((string) file_get_contents($this->path), true);
        if (!is_array($data)) {
            throw new \RuntimeException("Not a JSON object: {$this->path}");
        }
        return $data;
    }

    private function locked(int $mode, callable $work): mixed
    {
        Files::ensureDir(dirname($this->path));
        $lock = fopen($this->path . '.lock', 'c');
        @chmod($this->path . '.lock', 0600);
        if ($lock === false || !flock($lock, $mode)) {
            throw new \RuntimeException("Cannot lock {$this->path}");
        }
        try {
            return $work();
        } finally {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}
