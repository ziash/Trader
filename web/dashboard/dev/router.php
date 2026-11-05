<?php

declare(strict_types=1);

// Local preview only — never deployed. Serves the built screens (ui/out) and the real API on http://localhost:8090,
// with a stand-in for Google: /dev/signin?as=owner|viewer signs in a sample account (Google sign-in only works on the live site).

use TraderDash\App;
use TraderDash\Auth\Sessions;
use TraderDash\Clock;
use TraderDash\Http\Request;
use TraderDash\Http\Response;
use TraderDash\Store\JsonStore;
use TraderDash\Store\SecretFiles;
use TraderDash\Users;

$root = (string) getenv('TRADER_DASH_ROOT');
require __DIR__ . '/../api/vendor/autoload.php';

$path = (string) parse_url((string) $_SERVER['REQUEST_URI'], PHP_URL_PATH);

if ($path === '/dev/signin') {
    $who = ['owner' => ['owner@example.com', 'dev-owner'], 'viewer' => ['viewer@example.com', 'dev-viewer']][$_GET['as'] ?? ''] ?? null;
    if ($who === null) {
        http_response_code(400);
        echo 'as=owner or as=viewer';
        return true;
    }
    $clock = new Clock();
    $users = new Users(new JsonStore("$root/users.json"), 'owner@example.com', $clock);
    $users->admit($who[0], $who[1], '127.0.0.1');
    $token = (new Sessions(new SecretFiles("$root/state/sessions"), $clock))->create($who[0], $who[1]);
    Response::redirect('/scout/')->withCookie(App::SESSION_COOKIE, $token, Sessions::IDLE['owner'])->emit();
    return true;
}
if (preg_match('#^/(api|auth)(/|$)#', $path)) {
    App::fromRoot($root)->handle(Request::fromGlobals())->emit();
    return true;
}
if ($path !== '/' && !str_contains(basename($path), '.') && !str_ends_with($path, '/')) {
    header("Location: $path/", true, 301); // as Apache does for folders
    return true;
}
$docRoot = __DIR__ . '/../ui/out';
if (!file_exists($docRoot . $path) && !is_file($docRoot . rtrim($path, '/') . '/index.html')) {
    http_response_code(404);
    readfile("$docRoot/404.html");
    return true;
}
return false; // the built-in server sends the file (or the folder's index.html)
