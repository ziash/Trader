<?php

declare(strict_types=1);

namespace TraderDash\Http;

/** Outgoing HTTPS calls (to Google). Each answer is [status, headers in lower case, body]. */
interface HttpClient
{
    /** @return array{0: int, 1: array<string, string>, 2: string} */
    public function get(string $url): array;

    /**
     * @param array<string, string> $fields
     * @return array{0: int, 1: array<string, string>, 2: string}
     */
    public function postForm(string $url, array $fields): array;
}
