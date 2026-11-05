<?php

declare(strict_types=1);

namespace TraderDash\Tests;

use TraderDash\Http\HttpClient;

/** Stands in for Google: answers by URL and records the calls. */
final class FakeHttpClient implements HttpClient
{
    /** @var array<string, callable(array $fields): array> */
    public array $routes = [];
    /** @var list<array{0: string, 1: array}> */
    public array $calls = [];

    public function get(string $url): array
    {
        return $this->answer($url, []);
    }

    public function postForm(string $url, array $fields): array
    {
        return $this->answer($url, $fields);
    }

    private function answer(string $url, array $fields): array
    {
        $this->calls[] = [$url, $fields];
        $route = $this->routes[$url] ?? null;
        return $route === null ? [404, [], ''] : $route($fields);
    }
}
