<?php

declare(strict_types=1);

namespace TraderDash;

use TraderDash\Auth\GoogleSignIn;
use TraderDash\Auth\MachineAuth;
use TraderDash\Auth\Principal;
use TraderDash\Auth\Sessions;
use TraderDash\Auth\SignInRefused;
use TraderDash\Http\CurlHttpClient;
use TraderDash\Http\HttpClient;
use TraderDash\Http\HttpError;
use TraderDash\Http\Request;
use TraderDash\Http\Response;
use TraderDash\Store\JsonStore;
use TraderDash\Store\SecretFiles;

/**
 * The dashboard's API and sign-in addresses (dashboard.md §4.1, §5; contract: web/dashboard/openapi.yaml).
 * Every route names the least role it needs; the role is checked here, on the server, for every call.
 */
final class App
{
    public const SESSION_COOKIE = '__Host-tdash';
    public const SIGNIN_COOKIE = '__Host-tdash_signin';

    /** "METHOD /path" => [least role: public, viewer, admin or owner; handler] */
    public const ROUTES = [
        'GET /auth/google' => ['public', 'googleStart'],
        'GET /auth/callback' => ['public', 'googleCallback'],
        'POST /api/auth/token' => ['public', 'machineToken'],
        'POST /api/auth/logout' => ['viewer', 'logout'],
        'GET /api/me' => ['viewer', 'me'],
        'GET /api/status' => ['viewer', 'status'],
        'GET /api/scout' => ['viewer', 'scout'],
        'GET /api/scout/chart' => ['viewer', 'scoutChart'],
        'GET /api/accounts' => ['viewer', 'accounts'],
        'GET /api/trades' => ['viewer', 'trades'],
        'GET /api/trades/chart' => ['viewer', 'tradeChart'],
        'GET /api/conditions' => ['viewer', 'conditions'],
        'GET /api/audit' => ['viewer', 'audit'],
        'GET /api/controls' => ['viewer', 'controls'],
        'POST /api/controls' => ['admin', 'controlsSet'],
        'GET /api/users' => ['owner', 'usersOverview'],
        'POST /api/users/approve' => ['owner', 'usersApprove'],
        'POST /api/users/refuse' => ['owner', 'usersRefuse'],
        'POST /api/users/remove' => ['owner', 'usersRemove'],
        'POST /api/users/machines/revoke' => ['owner', 'machinesRevoke'],
        'POST /api/users/settings' => ['owner', 'usersSettings'],
    ];

    public function __construct(
        private readonly Config $config,
        private readonly Users $users,
        private readonly GoogleSignIn $google,
        private readonly Sessions $sessions,
        private readonly MachineAuth $machines,
        private readonly AccessLog $log,
        private readonly string $dataDir,
        private readonly Clock $clock = new Clock(),
    ) {
    }

    /** Wires the app to the server folder ~/trader-dashboard (dashboard.md §4). */
    public static function fromRoot(string $root, ?HttpClient $http = null, ?Clock $clock = null, ?Config $config = null): self
    {
        $clock ??= new Clock();
        $config ??= Config::load("$root/config.php");
        $log = new AccessLog("$root/logs/access.csv", $clock);
        $users = new Users(new JsonStore("$root/users.json"), $config->ownerEmail, $clock);
        return new self(
            $config,
            $users,
            new GoogleSignIn($config, $http ?? new CurlHttpClient(), new SecretFiles("$root/state/signin"), "$root/state/google-keys.json", $clock),
            new Sessions(new SecretFiles("$root/state/sessions"), $clock),
            new MachineAuth($users, new SecretFiles("$root/state/tokens"), new JsonStore("$root/state/ratelimit.json"), $log, $clock),
            $log,
            "$root/data",
            $clock,
        );
    }

    public function handle(Request $request): Response
    {
        $who = null;
        $path = rtrim($request->path, '/') ?: '/';
        try {
            $route = self::ROUTES["{$request->method} $path"] ?? null;
            if ($route === null) {
                throw new HttpError(404, 'not_found', 'No such address');
            }
            [$least, $handler] = $route;
            if ($least !== 'public') {
                $who = $this->authenticate($request);
                if (!$who->atLeast($least)) {
                    throw new HttpError(403, 'forbidden', 'Your role does not allow this');
                }
                $this->checkForgery($request, $who);
            }
            $response = $this->$handler($request, $who);
        } catch (HttpError $e) {
            $response = Response::json($e->status, ['error' => $e->errorCode, 'message' => $e->getMessage()]);
        } catch (\Throwable $e) {
            error_log('trader-dashboard: ' . $e::class . ': ' . $e->getMessage());
            $response = Response::json(500, ['error' => 'server_error', 'message' => 'Something went wrong on the server']);
        }
        if ($who?->kind === 'machine') { // every machine call, refused ones too
            $this->log->write('api', $who->id, $who->role, $request, "{$request->method} $path {$response->status}");
        }
        return isset($response->headers['Cache-Control']) ? $response : $response->withHeader('Cache-Control', 'no-store');
    }

    // --- Who is calling --------------------------------------------------------------------

    private function authenticate(Request $request): Principal
    {
        $auth = $request->header('authorization') ?? '';
        if (preg_match('/^Bearer\s+([A-Za-z0-9_-]{20,100})$/', $auth, $m)) {
            return $this->machines->resolve($m[1]) ?? throw new HttpError(401, 'not_signed_in', 'Token not valid or expired');
        }
        $token = $request->cookies[self::SESSION_COOKIE] ?? '';
        $session = $token === '' ? null : $this->sessions->resolve($token, $this->users->personRole(...));
        if ($session === null) {
            throw new HttpError(401, 'not_signed_in', 'Please sign in');
        }
        if ($session['touched']) {
            $this->users->touchPerson($session['email'], $request->ip);
        }
        return new Principal('person', $session['email'], $session['role'], $session['csrf'], $token);
    }

    /** A change made with the session cookie must carry the session's anti-forgery token and come from our own site. */
    private function checkForgery(Request $request, Principal $who): void
    {
        if ($who->kind !== 'person' || $request->method === 'GET') {
            return;
        }
        $origin = $request->header('origin');
        $token = $request->header('x-csrf-token') ?? '';
        if ($origin !== null && $origin !== $this->config->siteOrigin || !hash_equals((string) $who->csrf, $token)) {
            throw new HttpError(403, 'forgery_check', 'Request not accepted; reload the page and try again');
        }
    }

    // --- Sign-in ---------------------------------------------------------------------------

    private function googleStart(Request $request): Response
    {
        [$url, $binding] = $this->google->start();
        return Response::redirect($url)->withCookie(self::SIGNIN_COOKIE, $binding, GoogleSignIn::PENDING_SECONDS);
    }

    private function googleCallback(Request $request): Response
    {
        $site = $this->config->siteOrigin;
        $done = fn (string $to) => Response::redirect($site . $to)->withCookie(self::SIGNIN_COOKIE, '', -1);
        try {
            $id = $this->google->finish($request->query, $request->cookies[self::SIGNIN_COOKIE] ?? '');
        } catch (SignInRefused $e) {
            $this->log->write('signin_refused', '', '', $request, $e->getMessage());
            return $done('/?access=error');
        }
        $outcome = $this->users->admit($id['email'], $id['sub'], $request->ip);
        switch ($outcome) {
            case 'owner':
            case 'viewer':
                $this->log->write('signin', $id['email'], $outcome, $request, 'google');
                $token = $this->sessions->create($id['email'], $id['sub']);
                return $done('/scout')->withCookie(self::SESSION_COOKIE, $token, Sessions::IDLE['owner']);
            case 'requested':
                $this->log->write('access_requested', $id['email'], '', $request);
                return $done('/?access=requested');
            default:
                $this->log->write('signin_refused', $id['email'], '', $request, $outcome);
                return $done('/?access=refused');
        }
    }

    private function machineToken(Request $request): Response
    {
        return Response::json(200, $this->machines->issue($request));
    }

    private function logout(Request $request, Principal $who): Response
    {
        if ($who->kind !== 'person') {
            throw new HttpError(400, 'bad_request', 'Machines sign out by letting the token expire');
        }
        $this->sessions->destroy((string) $who->sessionToken);
        $this->log->write('signout', $who->id, $who->role, $request);
        return Response::json(200, ['signed_out' => true])->withCookie(self::SESSION_COOKIE, '', -1);
    }

    private function me(Request $request, Principal $who): Response
    {
        return Response::json(200, array_filter([
            'kind' => $who->kind,
            'id' => $who->id,
            'role' => $who->role,
            'csrf' => $who->csrf,
        ], fn ($v) => $v !== null));
    }

    // --- Data ------------------------------------------------------------------------------

    private function status(): Response
    {
        $file = "{$this->dataDir}/status.json";
        $status = is_file($file) ? json_decode((string) file_get_contents($file), true) : null;
        if (is_array($status)) {
            $status['controls'] = $this->controlsData()->history(); // kept on the server, where the site writes them (§3.4)
        }
        return Response::json(200, is_array($status)
            ? ['available' => true] + $status
            : ['available' => false, 'reason' => 'No data uploaded yet']);
    }

    private function scout(Request $request): Response
    {
        $account = $request->query['account'] ?? '';
        return Response::json(200, (new ScoutData("{$this->dataDir}/logs"))->day($account, $request->query['day'] ?? null));
    }

    /** An order picture (scout.md §3.1). Scout draws it; it is shown as an image only, so no script in it can run. */
    private function scoutChart(Request $request): Response
    {
        $path = (new ScoutData("{$this->dataDir}/logs"))->chartPath($request->query['file'] ?? '');
        return new Response(200, [
            'Content-Type' => 'image/svg+xml',
            'Content-Security-Policy' => "default-src 'none'; style-src 'unsafe-inline'; sandbox",
            'Cache-Control' => 'private, max-age=86400',
        ], (string) file_get_contents($path));
    }

    private function accounts(Request $request): Response
    {
        return Response::json(200, (new TradesData($this->dataDir))->accounts($request->query['account'] ?? '', gmdate('Y-m-d')));
    }

    private function trades(Request $request): Response
    {
        return Response::json(200, (new TradesData($this->dataDir))->trades($request->query['account'] ?? '', $request->query['day'] ?? null));
    }

    private function conditions(Request $request): Response
    {
        return Response::json(200, (new TradesData($this->dataDir))->conditions($request->query['account'] ?? ''));
    }

    private function controlsData(): ControlsData
    {
        return new ControlsData($this->dataDir, dirname($this->dataDir) . '/controls', $this->clock);
    }

    private function controls(Request $request, Principal $who): Response
    {
        return Response::json(200, $this->controlsData()->view($request->query['account'] ?? '', $who->atLeast('admin')));
    }

    /** One control changed (dashboard.md §3.4): checked against the catalogue, written for the laptop to fetch, logged. */
    private function controlsSet(Request $request, Principal $who): Response
    {
        $body = $request->json();
        $value = $body['value'] ?? null;
        $view = $this->controlsData()->set($request->jsonString('account', 64), $request->jsonString('scope', 16), $request->jsonString('target', 64),
            $request->jsonString('key', 32), $value, $who->id, $who->role);
        $this->log->write('control', $who->id, $who->role, $request, json_encode(array_intersect_key($body, array_flip(['account', 'scope', 'target', 'key', 'value']))));
        return Response::json(200, $view);
    }

    private function audit(Request $request): Response
    {
        return Response::json(200, (new AuditData($this->dataDir))->view($request->query['report'] ?? null, $request->query['period'] ?? null));
    }

    /** A trade picture (dashboard.md §2.3). The laptop draws it; it is shown as an image only, so no script in it can run. */
    private function tradeChart(Request $request): Response
    {
        $path = (new TradesData($this->dataDir))->chartPath($request->query['file'] ?? '');
        return new Response(200, [
            'Content-Type' => 'image/svg+xml',
            'Content-Security-Policy' => "default-src 'none'; style-src 'unsafe-inline'; sandbox",
            'Cache-Control' => 'private, max-age=300',
        ], (string) file_get_contents($path));
    }

    // --- Users page (owner only) -----------------------------------------------------------

    private function usersOverview(): Response
    {
        return Response::json(200, $this->users->overview() + ['log' => $this->log->recent(100)]);
    }

    private function usersApprove(Request $request, Principal $who): Response
    {
        $email = Users::normalise($request->jsonString('email'));
        $this->users->approve($email);
        $this->log->write('approve', $who->id, $who->role, $request, $email);
        return Response::json(200, ['approved' => $email]);
    }

    private function usersRefuse(Request $request, Principal $who): Response
    {
        $email = Users::normalise($request->jsonString('email'));
        $this->users->refuse($email);
        $this->log->write('refuse', $who->id, $who->role, $request, $email);
        return Response::json(200, ['refused' => $email]);
    }

    private function usersRemove(Request $request, Principal $who): Response
    {
        $email = Users::normalise($request->jsonString('email'));
        $this->users->remove($email);
        $ended = $this->sessions->destroyAllFor($email);
        $this->log->write('remove', $who->id, $who->role, $request, "$email ($ended sessions ended)");
        return Response::json(200, ['removed' => $email]);
    }

    private function machinesRevoke(Request $request, Principal $who): Response
    {
        $name = $request->jsonString('name', 31);
        $this->users->revokeMachine($name);
        $ended = $this->machines->endTokens($name);
        $this->log->write('revoke', $who->id, $who->role, $request, "$name ($ended tokens ended)");
        return Response::json(200, ['revoked' => $name]);
    }

    private function usersSettings(Request $request, Principal $who): Response
    {
        $on = $request->json()['access_requests'] ?? null;
        if (!is_bool($on)) {
            throw new HttpError(400, 'bad_request', 'Field "access_requests" must be true or false');
        }
        $this->users->setAccessRequests($on);
        $this->log->write('settings', $who->id, $who->role, $request, 'access_requests=' . ($on ? 'on' : 'off'));
        return Response::json(200, ['access_requests' => $on]);
    }
}
