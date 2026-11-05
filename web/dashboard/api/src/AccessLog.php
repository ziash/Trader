<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\Request;
use TraderDash\Store\Files;

/** logs/access.csv: every sign-in, refused sign-in, approval, control change and machine API call (dashboard.md §5.4). */
final class AccessLog
{
    public const HEADER = ['time_utc', 'event', 'who', 'role', 'ip', 'user_agent', 'detail'];

    public function __construct(private readonly string $file, private readonly Clock $clock)
    {
    }

    public function write(string $event, string $who, string $role, Request $request, string $detail = ''): void
    {
        Files::ensureDir(dirname($this->file));
        $new = !is_file($this->file);
        $out = fopen($this->file, 'a');
        if ($out === false) {
            throw new \RuntimeException("Cannot open {$this->file}");
        }
        flock($out, LOCK_EX);
        if ($new) {
            chmod($this->file, 0600);
            fputcsv($out, self::HEADER, escape: '');
        }
        $row = [$this->clock->utc(), $event, $who, $role, $request->ip, $request->userAgent, $detail];
        fputcsv($out, array_map(self::safeCell(...), $row), escape: '');
        flock($out, LOCK_UN);
        fclose($out);
    }

    /** @return list<array<string, string>> the newest $limit rows, newest first */
    public function recent(int $limit = 100): array
    {
        if (!is_file($this->file)) {
            return [];
        }
        $lines = file($this->file, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) ?: [];
        $rows = [];
        foreach (array_reverse(array_slice($lines, 1)) as $line) {
            $cells = str_getcsv($line, escape: '');
            if (count($cells) === count(self::HEADER)) {
                $rows[] = array_combine(self::HEADER, $cells);
            }
            if (count($rows) >= $limit) {
                break;
            }
        }
        return $rows;
    }

    /** Text that a spreadsheet would run as a formula gets a leading quote; line breaks become spaces. */
    private static function safeCell(string $cell): string
    {
        $cell = str_replace(["\r", "\n"], ' ', $cell);
        return $cell !== '' && str_contains('=+-@', $cell[0]) ? "'" . $cell : $cell;
    }
}
