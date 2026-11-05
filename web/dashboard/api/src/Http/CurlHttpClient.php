<?php

declare(strict_types=1);

namespace TraderDash\Http;

final class CurlHttpClient implements HttpClient
{
    public function get(string $url): array
    {
        return $this->send($url, []);
    }

    public function postForm(string $url, array $fields): array
    {
        return $this->send($url, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => http_build_query($fields, '', '&', PHP_QUERY_RFC3986),
            CURLOPT_HTTPHEADER => ['Content-Type: application/x-www-form-urlencoded'],
        ]);
    }

    private function send(string $url, array $options): array
    {
        $headers = [];
        $curl = curl_init($url);
        curl_setopt_array($curl, $options + [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 10,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_PROTOCOLS => CURLPROTO_HTTPS,
            CURLOPT_HEADERFUNCTION => static function ($curl, string $line) use (&$headers): int {
                $parts = explode(':', $line, 2);
                if (count($parts) === 2) {
                    $headers[strtolower(trim($parts[0]))] = trim($parts[1]);
                }
                return strlen($line);
            },
        ]);
        $body = curl_exec($curl);
        $status = (int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        if ($body === false) {
            throw new \RuntimeException('HTTPS call failed: ' . curl_error($curl));
        }
        return [$status, $headers, (string) $body];
    }
}
