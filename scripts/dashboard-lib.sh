# Shared by the dashboard-*.sh scripts (source it; dashboard.md §4, §7). SSH to Hostinger with the upload key
# and the pinned server identity from ~/.config/trader/dashboard.env; never a password, never an unknown server.

dash_env="$HOME/.config/trader/dashboard.env"
[ -r "$dash_env" ] || { echo "Missing $dash_env (dashboard.md §4)" >&2; exit 1; }
# shellcheck source=/dev/null
. "$dash_env"

dash_web='domains/uzlabz.com/public_html/trader'   # web root of trader.uzlabz.com, relative to the server home
dash_home='trader-dashboard'                       # code and data, outside the web root
dash_site='https://trader.uzlabz.com'

dash_ssh_opts=(-p "$DASH_SSH_PORT" -i "$DASH_SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes
  -o UserKnownHostsFile="$DASH_SSH_KNOWN_HOSTS" -o StrictHostKeyChecking=yes)

# dash_ssh '<remote command>'  (runs in the server home)
dash_ssh() {
  ssh "${dash_ssh_opts[@]}" "$DASH_SSH_USER@$DASH_SSH_HOST" "$@"
}

# dash_rsync <rsync args…>  (remote paths as "$dash_remote:path")
dash_remote="$DASH_SSH_USER@$DASH_SSH_HOST"
dash_rsync() {
  rsync -e "ssh ${dash_ssh_opts[*]}" "$@"
}
