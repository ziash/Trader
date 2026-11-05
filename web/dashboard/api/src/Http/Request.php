<?php

declare(strict_types=1);

namespace TraderDash\Http;

/** One HTTP request, built from PHP's globals on the server or directly in tests. */
final class Request
{
    /**
     * @param array<string, string> $query
     * @param array<string, string> $headers names in lower case
     * @param array<string, string> $cookies
     */
    public function __construct(
        public readonly string $method,
        public readonly string $path,
        public readonly array $query = [],
        public readonly array $headers = [],
        public readonly array $cookies = [],
        public readonly string $body = '',
        public readonly string $ip = '',
        public readonly string $userAgent = '',
    ) {
    }

    public static function fromGlobals(): self
    {
        $headers = [];
        foreach ($_SERVER as $name => $value) {
            if (is_string($value) && str_starts_with($name, 'HTTP_')) {
                $headers[strtolower(str_replace('_', '-', substr($name, 5)))] = $value;
            }
        }
        if (isset($_SERVER['CONTENT_TYPE'])) {
            $headers['content-type'] = (string) $_SERVER['CONTENT_TYPE'];
        }
        // Apache drops Authorization before PHP unless .htaccess hands it on (it then arrives with a REDIRECT_ prefix).
        $auth = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? null;
        if (is_string($auth) && $auth !== '') {
            $headers['authorization'] = $auth;
        }
        $path = parse_url((string) ($_SERVER['REQUEST_URI'] ?? '/'), PHP_URL_PATH);

        return new self(
            strtoupper((string) ($_SERVER['REQUEST_METHOD'] ?? 'GET')),
            is_string($path) && $path !== '' ? $path : '/',
            array_filter($_GET, 'is_string'),
            $headers,
            array_filter($_COOKIE, 'is_string'),
            (string) file_get_contents('php://input'),
            (string) ($_SERVER['REMOTE_ADDR'] ?? ''),
            substr((string) ($_SERVER['HTTP_USER_AGENT'] ?? ''), 0, 200),
        );
    }

    public function header(string $name): ?string
    {
        return $this->headers[strtolower($name)] ?? null;
    }

    /** The body as a JSON object; anything else is a 400. */
    public function json(): array
    {
        $data = json_decode($this->body, true);
        if (!is_array($data) || array_is_list($data) && $data !== []) {
            throw new HttpError(400, 'bad_request', 'The body must be a JSON object');
        }
        return $data;
    }

    public function jsonString(string $field, int $maxLength = 254): string
    {
        $value = $this->json()[$field] ?? null;
        if (!is_string($value) || $value === '' || strlen($value) > $maxLength) {
            throw new HttpError(400, 'bad_request', "Field \"$field\" is missing or not valid");
        }
        return $value;
    }
}
