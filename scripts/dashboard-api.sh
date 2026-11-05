#!/usr/bin/env bash
# Calls the dashboard's API as a machine account (dashboard.md §5.3) and prints the JSON answer.
# Signs in with the client ID and secret from ~/.config/trader/dashboard-m2m.env and keeps the 1-hour token in
# ~/.cache/trader (600). Secrets and tokens never appear on a command line or in the output.
#   scripts/dashboard-api.sh GET /api/me
#   scripts/dashboard-api.sh GET /api/status
#   scripts/dashboard-api.sh [--as <name>] POST /api/<path> '<json>'
set -euo pipefail

name=claude
[ "${1:-}" = --as ] && { name=$2; shift 2; }
method=${1:-}; path=${2:-}; body=${3:-}
[[ "$method" =~ ^(GET|POST)$ && "$path" == /api/* ]] || { sed -n '4,7p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

file="$HOME/.config/trader/dashboard-m2m$([ "$name" = claude ] || echo "-$name").env"
[ -r "$file" ] || { echo "No $file: scripts/dashboard-m2m.sh create $name" >&2; exit 1; }
# shellcheck source=/dev/null
. "$file"
cache="$HOME/.cache/trader/dashboard-token-$name"

token=""
if [ -r "$cache" ] && [ "$(sed -n 2p "$cache")" -gt "$(( $(date +%s) + 60 ))" ]; then
  token=$(sed -n 1p "$cache")
fi
if [ -z "$token" ]; then
  answer=$(printf 'user = "%s:%s"\n' "$DASH_M2M_CLIENT_ID" "$DASH_M2M_SECRET" \
    | curl -sS -K - -X POST -w '\n%{http_code}' "$DASH_M2M_URL/api/auth/token")
  status=${answer##*$'\n'}
  [ "$status" = 200 ] || { echo "Sign-in refused (HTTP $status): ${answer%$'\n'*}" >&2; exit 1; }
  token=$(printf '%s' "${answer%$'\n'*}" | php -r '$a = json_decode(stream_get_contents(STDIN), true); echo $a["access_token"] ?? "";')
  expires=$(( $(date +%s) + 3600 ))
  (umask 077 && mkdir -p "$(dirname "$cache")" && printf '%s\n%s\n' "$token" "$expires" > "$cache")
fi

args=(-sS -K - -X "$method" -w '\n%{http_code}' -H 'Accept: application/json')
[ "$method" = POST ] && args+=(-H 'Content-Type: application/json' --data-binary "${body:-{\}}")
answer=$(printf 'header = "Authorization: Bearer %s"\n' "$token" | curl "${args[@]}" "$DASH_M2M_URL$path")
status=${answer##*$'\n'}
printf '%s\n' "${answer%$'\n'*}"
[ "$status" -lt 400 ] || { echo "HTTP $status" >&2; exit 1; }
