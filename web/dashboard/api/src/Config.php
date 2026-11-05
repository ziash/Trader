<?php

declare(strict_types=1);

namespace TraderDash;

/** The server's settings from ~/trader-dashboard/config.php (mode 600, never in Git; dashboard.md §4). */
final class Config
{
    public function __construct(
        public readonly string $ownerEmail,
        public readonly string $googleClientId,
        #[\SensitiveParameter] public readonly string $googleClientSecret,
        public readonly string $googleRedirectUri,
        public readonly string $siteOrigin = 'https://trader.uzlabz.com',
    ) {
    }

    public static function load(string $file): self
    {
        $c = require $file;
        if (!is_array($c) || !isset($c['owner_email'], $c['google']['client_id'], $c['google']['client_secret'], $c['google']['redirect_uri'])) {
            throw new \RuntimeException("config.php is incomplete: $file");
        }
        return new self(
            Users::normalise((string) $c['owner_email']),
            (string) $c['google']['client_id'],
            (string) $c['google']['client_secret'],
            (string) $c['google']['redirect_uri'],
            (string) ($c['site_origin'] ?? 'https://trader.uzlabz.com'),
        );
    }
}
