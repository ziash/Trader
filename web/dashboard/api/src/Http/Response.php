<?php

declare(strict_types=1);

namespace TraderDash\Http;

/** One HTTP response; kept as data so tests can look at it, sent by emit() on the server. */
final class Response
{
    /**
     * @param array<string, string> $headers
     * @param array<string, array{value: string, maxAge: int}> $cookies
     */
    public function __construct(
        public readonly int $status,
        public readonly array $headers = [],
        public readonly string $body = '',
        public readonly array $cookies = [],
    ) {
    }

    public static function json(int $status, array $data): self
    {
        return new self($status, ['Content-Type' => 'application/json; charset=utf-8'],
            json_encode($data, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE));
    }

    public static function redirect(string $to): self
    {
        return new self(302, ['Location' => $to]);
    }

    public function withHeader(string $name, string $value): self
    {
        return new self($this->status, [$name => $value] + $this->headers, $this->body, $this->cookies);
    }

    /** A cookie for the whole site: HTTPS only, not readable by scripts, not sent by other sites' requests. maxAge < 0 deletes it. */
    public function withCookie(string $name, string $value, int $maxAge): self
    {
        return new self($this->status, $this->headers, $this->body,
            [$name => ['value' => $value, 'maxAge' => $maxAge]] + $this->cookies);
    }

    public function data(): array
    {
        return (array) json_decode($this->body, true);
    }

    public function emit(): void
    {
        http_response_code($this->status);
        foreach ($this->headers as $name => $value) {
            header("$name: $value");
        }
        foreach ($this->cookies as $name => $cookie) {
            setcookie($name, $cookie['value'], [
                'expires' => $cookie['maxAge'] < 0 ? 1 : time() + $cookie['maxAge'],
                'path' => '/',
                'secure' => true,
                'httponly' => true,
                'samesite' => 'Lax',
            ]);
        }
        echo $this->body;
    }
}
