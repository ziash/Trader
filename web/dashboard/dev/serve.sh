#!/usr/bin/env bash
# Local preview of the dashboard (never deployed): builds the screens, makes sample data once, serves both on
# http://localhost:8090 with the real PHP API. Sign in with /dev/signin?as=owner or ?as=viewer.
#   web/dashboard/dev/serve.sh            build if needed, then serve
#   web/dashboard/dev/serve.sh --build    always rebuild the screens first
#   web/dashboard/dev/serve.sh --reset    fresh sample data
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
data="$here/.data"
if [[ " $* " == *" --reset "* ]]; then rm -rf "$data"; fi
if [[ " $* " == *" --build "* || ! -f "$here/../ui/out/index.html" ]]; then
  (cd "$here/../ui" && NEXT_TELEMETRY_DISABLED=1 npm run -s build >/dev/null)
fi
[[ -f "$data/config.php" ]] || php "$here/seed.php" "$data"
export TRADER_DASH_ROOT="$data"
echo "Preview: http://localhost:8090/dev/signin?as=owner"
exec php -S localhost:8090 -t "$here/../ui/out" "$here/router.php"
