#!/usr/bin/env bash
# Builds the dashboard and puts it on Hostinger (dashboard.md §4, §7):
#   screens (web/dashboard/ui, Next.js static export) + the API's entry file → the web root of trader.uzlabz.com;
#   the PHP API (web/dashboard/api, without its test tools) → ~/trader-dashboard/app-<git hash>, then the link
#   ~/trader-dashboard/app is switched to it in one step, so a request never sees half a deploy.
# Never touches config.php, users.json, data/, controls/, state/ or logs/ on the server. Runs both test suites first.
#   scripts/deploy-dashboard.sh             test, build, upload, check the live site
#   scripts/deploy-dashboard.sh --dry-run   test and build, then list what would change, sending nothing
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
. "$repo/scripts/dashboard-lib.sh"
dry=no
[ "${1:-}" = "--dry-run" ] && dry=yes

version=$(git -C "$repo" rev-parse --short HEAD)
git -C "$repo" diff --quiet HEAD -- web/dashboard || version="$version-dirty"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

echo "Testing the API (PHPUnit) and the screens (Vitest)"
(cd "$repo/web/dashboard/api" && composer install --quiet --no-interaction && vendor/bin/phpunit --no-progress >/dev/null) \
  || { echo "API tests failed: run vendor/bin/phpunit in web/dashboard/api" >&2; exit 1; }
(cd "$repo/web/dashboard/ui" && npm ci --silent --no-fund && npx vitest run >/dev/null) \
  || { echo "Screen tests failed: run npm test in web/dashboard/ui" >&2; exit 1; }

echo "Building the screens (Next.js static export)"
(cd "$repo/web/dashboard/ui" && NEXT_TELEMETRY_DISABLED=1 npm run -s build >/dev/null)
mkdir -p "$stage/web/api" "$stage/app"
cp -a "$repo/web/dashboard/ui/out/." "$stage/web/"
cp "$repo/web/dashboard/api/public/index.php" "$stage/web/api/index.php"
echo "$version" > "$stage/web/VERSION.txt"

echo "Packing the API $version (no test tools)"
cp -a "$repo/web/dashboard/api/src" "$repo/web/dashboard/api/cli" "$repo/web/dashboard/api/composer.json" \
  "$repo/web/dashboard/api/composer.lock" "$stage/app/"
(cd "$stage/app" && composer install --quiet --no-interaction --no-dev --optimize-autoloader)
find "$stage/app/vendor" -type f \( -iname "*.md" -o -iname "*.txt" -o -iname "*.dist" \) -delete   # libraries' docs (one holds an example key)
echo "$version" > "$stage/app/VERSION"

# Nothing private may be in the upload (the screens hold no data; the API holds no settings).
if grep -rlE 'GOCSPX-[A-Za-z0-9_-]{10}|BEGIN [A-Z ]*PRIVATE KEY' "$stage" >/dev/null; then
  echo "Refusing to upload: something that looks like a secret is in the build:" >&2
  grep -rlE 'GOCSPX-[A-Za-z0-9_-]{10}|BEGIN [A-Z ]*PRIVATE KEY' "$stage" >&2
  exit 1
fi

if [ "$dry" = yes ]; then
  echo "Dry run — would send:"
  dash_rsync -rlti --delete --dry-run "$stage/web/" "$dash_remote:$dash_web/"
  echo "and the API to ~/$dash_home/app-$version"
  exit 0
fi

echo "Uploading the API → ~/$dash_home/app-$version"
dash_ssh "mkdir -p $dash_home && chmod 700 $dash_home"
dash_rsync -rlt --chmod=D700,F600 --delete "$stage/app/" "$dash_remote:$dash_home/app-$version/"
echo "Checking it loads with the server's PHP"
dash_ssh "cd $dash_home && php -r 'require \"app-$version/vendor/autoload.php\"; foreach ([\"TraderDash\\\\App\", \"Firebase\\\\JWT\\\\JWT\"] as \$c) { class_exists(\$c) or exit(1); } echo \"  PHP \", PHP_VERSION, \" ok\n\";'"

echo "Switching ~/$dash_home/app → app-$version"
dash_ssh "cd $dash_home && ln -sfn app-$version app.link-new && mv -T app.link-new app"

echo "Uploading the screens → $dash_site"
dash_rsync -rlt --chmod=D755,F644 --delete-after "$stage/web/" "$dash_remote:$dash_web/"

echo "Removing old API folders (keeping the newest 3)"
dash_ssh "cd $dash_home && ls -1dt app-*/ 2>/dev/null | tail -n +4 | grep -v \"^\$(readlink app)/\$\" | xargs -r rm -rf --"

echo "Checking the live site"
fail=0
check() { # check <what> <expected> <actual>
  if [ "$2" = "$3" ]; then echo "  ✓ $1"; else echo "  ✗ $1: expected $2, got $3"; fail=1; fi
}
check "sign-in page" 200 "$(curl -s -o /dev/null -w '%{http_code}' "$dash_site/")"
check "version" "$version" "$(curl -s "$dash_site/VERSION.txt")"
check "API refuses without sign-in" 401 "$(curl -s -o /dev/null -w '%{http_code}' "$dash_site/api/me")"
check "Google sign-in starts" accounts.google.com "$(curl -s -o /dev/null -w '%{redirect_url}' "$dash_site/auth/google" | cut -d/ -f3)"
check "code folder not reachable" 404 "$(curl -s -o /dev/null -w '%{http_code}' "$dash_site/../$dash_home/config.php")"
[ "$fail" = 0 ] && echo "Deployed $version." || { echo "Deployed $version, but a check failed (above)." >&2; exit 1; }
