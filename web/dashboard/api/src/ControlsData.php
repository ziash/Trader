<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Http\HttpError;

/**
 * Scout's controls (dashboard.md §3.4, scout.md §2.6). The site may set only the options in the laptop's catalogue
 * (data/scout-options.json, uploaded with the data), only to the values it allows, only for the symbols in scout.json.
 * It writes the control file controls/scout-<broker>-<ctid>.json (whole, to a temporary file, then renamed) and a row in
 * controls/control-history.csv; the laptop fetches the file every minute and sends Scout's acknowledgement back
 * (controls/scout-<broker>-<ctid>.ack.json), which turns each control's state from "saved" into "applied" or "refused".
 */
final class ControlsData
{
    private const SETUP = '/^([A-Za-z0-9._]{1,32})-([A-Z][0-9])-(\d{12})$/';
    private const SETUPS_KEPT_DAYS = 7;
    public const HISTORY = ['time_utc', 'by', 'role', 'account', 'version', 'scope', 'target', 'key', 'old', 'new'];

    public function __construct(private readonly string $dataDir, private readonly string $controlsDir, private readonly Clock $clock)
    {
    }

    /** The account's controls: the catalogue's options, each symbol as configured and as set, each setup decision, and their states. */
    public function view(string $accountId, bool $canChange): array
    {
        [$broker, $ctid] = ScoutData::account($accountId);
        $options = $this->options();
        $file = $this->file($broker, $ctid);
        $ack = $this->ack($broker, $ctid);
        $configured = $options['accounts'][$accountId] ?? [];

        $symbols = [];
        foreach ($configured as $c) {
            $s = $c['symbol'] ?? '';
            $set = $file['symbols'][$s] ?? [];
            $state = [];
            foreach ($set as $key => $value) {
                $state[$key] = $this->state($file, $ack, 'symbol', $s, $key);
            }
            $symbols[] = [
                'symbol' => $s, 'configured' => ['enabled' => true, 'timeframe' => $c['timeframe'] ?? null, 'mode' => $c['mode'] ?? 'watch'],
                'set' => (object) $set, 'state' => (object) $state,
                'charts' => count(array_filter($configured, fn ($x) => ($x['symbol'] ?? '') === $s)),
            ];
        }
        $setups = [];
        foreach ($file['setups'] ?? [] as $key => $s) {
            $setups[$key] = $s + ['state' => $this->state($file, $ack, 'setup', $key, 'decision')];
        }

        return [
            'account' => $accountId,
            'available' => $options !== null && $configured !== [],
            'can_change' => $canChange,
            'catalogue_version' => $options['catalogue']['version'] ?? null,
            'options' => $options['catalogue']['options'] ?? [],
            'knows' => $ack['knows'] ?? null,
            'version' => $file['version'] ?? 0,
            'updated_utc' => $file['updated_utc'] ?? null,
            'updated_by' => $file['updated_by'] ?? null,
            'ack' => $ack === null ? null : ['version' => $ack['version'] ?? 0, 'applied_utc' => $ack['applied_utc'] ?? null, 'error' => $ack['error'] ?? null],
            'symbols' => $symbols,
            'setups' => (object) $setups,
        ];
    }

    /**
     * Sets one control, or puts it back as configured ($value null; also a symbol switched back on, its configured timeframe,
     * a setup kept). Checked against the catalogue and the account's symbols here, and again by Scout.
     */
    public function set(string $accountId, string $scope, string $target, string $key, mixed $value, string $by, string $role): array
    {
        [$broker, $ctid] = ScoutData::account($accountId);
        $options = $this->options() ?? throw new HttpError(409, 'no_catalogue', 'The laptop has not uploaded the options catalogue yet');
        $configured = $options['accounts'][$accountId] ?? throw new HttpError(404, 'not_found', 'No Scout charts for this account');
        $option = null;
        foreach ($options['catalogue']['options'] ?? [] as $o) {
            if (($o['scope'] ?? '') === $scope && ($o['key'] ?? '') === $key) {
                $option = $o;
            }
        }
        if ($option === null) {
            throw new HttpError(400, 'bad_request', "No $scope option \"$key\" in the catalogue");
        }
        $symbols = array_column($configured, null, 'symbol');
        if ($scope === 'symbol' && !isset($symbols[$target])) {
            throw new HttpError(400, 'bad_request', "$target is not one of Scout's symbols on this account");
        }
        if ($scope === 'setup' && (!preg_match(self::SETUP, $target, $m) || !isset($symbols[$m[1]]))) {
            throw new HttpError(400, 'bad_request', 'Not a setup of one of Scout\'s symbols (symbol-timeframe-yyyyMMddHHmm)');
        }
        if ($value !== null) {
            $ok = match ($option['type'] ?? '') {
                'switch' => is_bool($value),
                'choice' => is_string($value) && in_array($value, $option['values'] ?? [], true),
                default => false,
            };
            if (!$ok) {
                throw new HttpError(400, 'bad_request', "Value not allowed for \"$key\"");
            }
        }
        // As configured = no entry: the file holds only what the owner changed.
        $asConfigured = $value === null
            || ($key === 'enabled' && $value === true)
            || ($key === 'timeframe' && $value === ($symbols[$target]['timeframe'] ?? null))
            || ($key === 'decision' && $value === 'keep');

        @mkdir($this->controlsDir, 0700, true);
        $lock = fopen("{$this->controlsDir}/.lock", 'c');
        flock($lock, LOCK_EX);
        try {
            $file = $this->file($broker, $ctid) ?? ['version' => 0, 'account' => $accountId, 'scout' => new \stdClass(), 'symbols' => [], 'setups' => []];
            $now = $this->clock->now();
            $old = $scope === 'symbol' ? ($file['symbols'][$target][$key] ?? null) : ($file['setups'][$target]['decision'] ?? null);
            if ($scope === 'symbol') {
                if ($asConfigured) {
                    unset($file['symbols'][$target][$key]);
                    if (($file['symbols'][$target] ?? []) === []) {
                        unset($file['symbols'][$target]);
                    }
                } else {
                    $file['symbols'][$target][$key] = $value;
                }
            } elseif ($asConfigured) {
                unset($file['setups'][$target]);
            } else {
                $file['setups'][$target] = ['decision' => $value, 'utc' => gmdate('Y-m-d H:i:s', $now), 'by' => $by];
            }
            foreach ($file['setups'] ?? [] as $k => $s) {
                if ($now - (int) strtotime(($s['utc'] ?? '') . ' UTC') > self::SETUPS_KEPT_DAYS * 86400) {
                    unset($file['setups'][$k]);
                }
            }
            $file['version'] = (int) ($file['version'] ?? 0) + 1;
            $file['updated_utc'] = gmdate('Y-m-d H:i:s', $now);
            $file['updated_by'] = $by;
            $file['catalogue'] = $options['catalogue']['version'] ?? null;
            $file['account'] = $accountId;
            $file['symbols'] = (object) ($file['symbols'] ?? []);
            $file['setups'] = (object) ($file['setups'] ?? []);
            $path = $this->path($broker, $ctid);
            $tmp = "$path.tmp";
            file_put_contents($tmp, json_encode($file, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES) . "\n");
            chmod($tmp, 0600);
            rename($tmp, $path);

            $history = "{$this->controlsDir}/control-history.csv";
            $isNew = !is_file($history) || filesize($history) === 0;
            $h = fopen($history, 'a');
            if ($isNew) {
                fputcsv($h, self::HISTORY, ',', '"', '');
            }
            $show = fn ($v) => $v === null ? 'as configured' : (is_bool($v) ? ($v ? 'on' : 'off') : (string) $v);
            fputcsv($h, [gmdate('Y-m-d H:i:s', $now), $by, $role, $accountId, $file['version'], $scope, $target, $key,
                $show($old), $show($asConfigured ? null : $value)], ',', '"', '');
            fclose($h);
            @chmod($history, 0600);
        } finally {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
        return $this->view($accountId, true);
    }

    /** The last control changes, newest first, with whether Scout applied them yet (Status page, dashboard.md §2.6). */
    public function history(int $max = 50): array
    {
        $rows = array_slice(array_reverse(ScoutData::readCsv("{$this->controlsDir}/control-history.csv")), 0, $max);
        $out = [];
        foreach ($rows as $r) {
            [$broker, $ctid] = ScoutData::account($r['account']);
            $ack = $this->ack($broker, $ctid);
            $done = $ack !== null && (int) ($ack['version'] ?? 0) >= (int) $r['version'];
            $entry = null;
            foreach ($ack['entries'] ?? [] as $e) {
                if ($e['scope'] === $r['scope'] && $e['target'] === $r['target'] && $e['key'] === $r['key']) {
                    $entry = $e;
                }
            }
            $out[] = [
                'time_utc' => $r['time_utc'],
                'change' => "{$r['account']} · {$r['target']} {$r['key']}: {$r['old']} → {$r['new']}",
                'by' => $r['by'],
                'applied' => !$done ? 'waiting' : (($entry['state'] ?? 'applied') === 'refused' ? 'refused' : 'applied'),
                'applied_utc' => $done ? ($ack['applied_utc'] ?? null) : null,
                'reason' => $entry['reason'] ?? null,
            ];
        }
        return $out;
    }

    // --- Files -----------------------------------------------------------------------------

    private function options(): ?array
    {
        $file = "{$this->dataDir}/scout-options.json";
        $o = is_file($file) ? json_decode((string) file_get_contents($file), true) : null;
        return is_array($o) ? $o : null;
    }

    private function path(string $broker, string $ctid): string
    {
        return "{$this->controlsDir}/scout-$broker-$ctid.json";
    }

    private function file(string $broker, string $ctid): ?array
    {
        $f = $this->path($broker, $ctid);
        $d = is_file($f) ? json_decode((string) file_get_contents($f), true) : null;
        return is_array($d) ? $d : null;
    }

    private function ack(string $broker, string $ctid): ?array
    {
        $f = "{$this->controlsDir}/scout-$broker-$ctid.ack.json";
        $d = is_file($f) ? json_decode((string) file_get_contents($f), true) : null;
        return is_array($d) ? $d : null;
    }

    /** saved (Scout has not seen this version yet), applied (with when) or refused (with why). */
    private function state(?array $file, ?array $ack, string $scope, string $target, string $key): array
    {
        if ($ack === null || (int) ($ack['version'] ?? 0) < (int) ($file['version'] ?? 0)) {
            return ['state' => 'saved', 'utc' => $file['updated_utc'] ?? null, 'reason' => null];
        }
        foreach ($ack['entries'] ?? [] as $e) {
            if ($e['scope'] === $scope && $e['target'] === $target && $e['key'] === $key) {
                return ['state' => $e['state'] === 'refused' ? 'refused' : 'applied', 'utc' => $ack['applied_utc'] ?? null, 'reason' => $e['reason'] ?? null,
                    'cancelled' => $e['cancelled'] ?? []];
            }
        }
        return ['state' => 'applied', 'utc' => $ack['applied_utc'] ?? null, 'reason' => null];
    }
}
