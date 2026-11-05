<?php

declare(strict_types=1);

namespace TraderDash;

/** The current time; tests pass a fixed one. */
class Clock
{
    public function now(): int
    {
        return time();
    }

    /** "2026-11-05 17:09:00", the format of every time in the bots' files. */
    public function utc(?int $at = null): string
    {
        return gmdate('Y-m-d H:i:s', $at ?? $this->now());
    }
}
