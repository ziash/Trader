<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use TraderDash\Clock;

final class FixedClock extends Clock
{
    public function __construct(public int $time = 1_793_900_000)
    {
    }

    public function now(): int
    {
        return $this->time;
    }
}
