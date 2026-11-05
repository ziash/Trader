<?php

declare(strict_types=1);

namespace TraderDash\Http;

/** An error answered to the caller as JSON: {"error": code, "message": text}. */
final class HttpError extends \RuntimeException
{
    public function __construct(public readonly int $status, public readonly string $errorCode, string $message)
    {
        parent::__construct($message);
    }
}
