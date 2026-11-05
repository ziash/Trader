<?php

declare(strict_types=1);

// Front controller for /api/* and /auth/* on trader.uzlabz.com (dashboard.md §4). Deployed as <web root>/api/index.php;
// the code and all data live outside the web root, in ~/trader-dashboard.

$root = getenv('TRADER_DASH_ROOT') ?: dirname(__DIR__, 5) . '/trader-dashboard';
$app = getenv('TRADER_DASH_APP') ?: "$root/app";
require "$app/vendor/autoload.php";

\TraderDash\App::fromRoot($root)->handle(\TraderDash\Http\Request::fromGlobals())->emit();
