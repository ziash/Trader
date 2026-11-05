#!/usr/bin/env bash
# Machine accounts for the dashboard (dashboard.md §5.3). The secret is made here, on the laptop, and kept in
# ~/.config/trader/dashboard-m2m.env (claude) or dashboard-m2m-<name>.env (others), mode 600; only its hash
# goes to the server. The secret is never printed.
#   scripts/dashboard-m2m.sh create <name> [--role admin|viewer]   (default admin)
#   scripts/dashboard-m2m.sh rotate <name>                         new client ID and secret; old tokens end at once
#   scripts/dashboard-m2m.sh revoke <name>                         sign-in and tokens end; the local file is removed
#   scripts/dashboard-m2m.sh list
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
. "$repo/scripts/dashboard-lib.sh"

usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
cmd=${1:-}; name=${2:-}
[ -n "$cmd" ] || usage
if [ "$cmd" != list ]; then
  [[ "$name" =~ ^[a-z][a-z0-9-]{1,30}$ ]] || { echo "Name: lower-case letters, digits and -, 2–31 characters" >&2; exit 2; }
fi
file="$HOME/.config/trader/dashboard-m2m$([ "$name" = claude ] || echo "-$name").env"
cli="php $dash_home/app/cli/machines.php"

# set_secret <role>: new client ID and secret; the hash to the server, then the file into place.
set_secret() {
  local role=$1 client_id secret hash
  client_id="m2m_${name}_$(openssl rand -hex 4)"
  secret=$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=\n')
  hash=$(printf '%s' "$secret" | php -r 'echo password_hash(stream_get_contents(STDIN), PASSWORD_DEFAULT);')
  (umask 077 && printf 'DASH_M2M_NAME=%s\nDASH_M2M_ROLE=%s\nDASH_M2M_CLIENT_ID=%s\nDASH_M2M_SECRET=%s\nDASH_M2M_URL=%s\n' \
    "$name" "$role" "$client_id" "$secret" "$dash_site" > "$file.new")
  printf '%s' "$hash" | dash_ssh "$cli set $name $role $client_id"
  mv "$file.new" "$file"
  rm -f "$HOME/.cache/trader/dashboard-token-$name"
  echo "Secret saved in $file (600). Client ID: $client_id"
}

case "$cmd" in
  create)
    role=admin
    [ "${3:-}" = --role ] && role=${4:-}
    [[ "$role" =~ ^(admin|viewer)$ ]] || { echo "Role: admin or viewer" >&2; exit 2; }
    [ ! -e "$file" ] || { echo "$file exists: use rotate, or revoke first" >&2; exit 1; }
    set_secret "$role"
    ;;
  rotate)
    [ -r "$file" ] || { echo "No $file: use create" >&2; exit 1; }
    role=$(sed -n 's/^DASH_M2M_ROLE=//p' "$file")
    set_secret "${role:-admin}"
    ;;
  revoke)
    dash_ssh "$cli revoke $name"
    rm -f "$file" "$HOME/.cache/trader/dashboard-token-$name"
    echo "Removed $file"
    ;;
  list)
    dash_ssh "$cli list"
    ;;
  *) usage ;;
esac
