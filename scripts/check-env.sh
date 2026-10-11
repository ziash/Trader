#!/usr/bin/env bash
# Phase 0 environment check for the Trader project (see docs/setup.md).
# Read-only: it changes nothing on the machine. Prints OK / WARN / FAIL per item.

set -u

fails=0
warns=0

ok()   { printf '  \033[32mOK  \033[0m %s\n' "$1"; }
warn() { printf '  \033[33mWARN\033[0m %s\n' "$1"; warns=$((warns + 1)); }
fail() { printf '  \033[31mFAIL\033[0m %s\n' "$1"; fails=$((fails + 1)); }
have() { command -v "$1" >/dev/null 2>&1; }

echo "== Operating system"
if [ -r /etc/os-release ]; then
  . /etc/os-release
  if [ "${ID:-}" = "ubuntu" ]; then
    ok "Ubuntu ${VERSION_ID:-?} (${PRETTY_NAME:-})"
  else
    warn "Not Ubuntu (${PRETTY_NAME:-unknown}); setup steps assume Ubuntu"
  fi
else
  warn "Cannot read /etc/os-release"
fi

echo "== .NET SDK"
if have dotnet; then
  sdks=$(dotnet --list-sdks 2>/dev/null)
  if printf '%s\n' "$sdks" | grep -qE '^10\.'; then
    ok ".NET 10 SDK: $(printf '%s\n' "$sdks" | grep -E '^10\.' | tail -1 | cut -d' ' -f1)"
  else
    fail ".NET 10 SDK not found (installed: ${sdks:-none}). See setup.md 4.1"
  fi
else
  fail "dotnet not installed. See setup.md 4.1"
fi

echo "== Git and GitHub"
if have git; then
  ok "git $(git --version | awk '{print $3}')"
  [ -n "$(git config --global user.name 2>/dev/null)" ] \
    && ok "git user.name set" || warn "git user.name not set"
  [ -n "$(git config --global user.email 2>/dev/null)" ] \
    && ok "git user.email set" || warn "git user.email not set"
else
  fail "git not installed"
fi
if have gh && gh auth status >/dev/null 2>&1; then
  ok "GitHub CLI logged in"
elif [ -f "$HOME/.ssh/id_ed25519" ] || [ -f "$HOME/.ssh/id_rsa" ]; then
  ok "SSH key present (assumed added to GitHub)"
else
  warn "No GitHub login found (gh auth or SSH key). See setup.md 4.2"
fi
repo_root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel 2>/dev/null)
if [ -n "$repo_root" ] && [ -f "$repo_root/docs/plan.md" ]; then
  ok "Trader repo at $repo_root"
else
  warn "Script not run from inside the Trader repo clone"
fi

echo "== VS Code"
if have code; then
  ok "VS Code installed"
  if code --list-extensions 2>/dev/null | grep -qi '^ms-dotnettools.csdevkit$'; then
    ok "C# Dev Kit extension installed"
  else
    warn "C# Dev Kit not installed: code --install-extension ms-dotnettools.csdevkit"
  fi
else
  warn "VS Code not found (sudo snap install code --classic)"
fi

echo "== Clock"
if have timedatectl; then
  synced=$(timedatectl show -p NTPSynchronized --value 2>/dev/null)
  if [ "$synced" = "yes" ]; then
    ok "Clock synchronised by NTP"
  else
    fail "Clock not NTP-synchronised: sudo timedatectl set-ntp true"
  fi
else
  warn "timedatectl not available; check NTP manually"
fi

echo "== Network: cTrader Open API demo endpoint"
host=demo.ctraderapi.com
port=5035
if have openssl; then
  out=$(timeout 10 openssl s_client -connect "$host:$port" -servername "$host" -brief </dev/null 2>&1)
  if printf '%s\n' "$out" | grep -qE 'CONNECTION ESTABLISHED|Protocol version'; then
    proto=$(printf '%s\n' "$out" | grep -m1 -E 'Protocol version' | sed 's/.*: *//')
    ok "TLS connection to $host:$port works${proto:+ ($proto)}"
  else
    fail "Cannot open TLS to $host:$port (blocked port or no internet). See setup.md Step 5"
  fi
elif timeout 10 bash -c "</dev/tcp/$host/$port" 2>/dev/null; then
  ok "TCP connection to $host:$port works (install openssl for a TLS check)"
else
  fail "Cannot reach $host:$port. See setup.md Step 5"
fi

echo "== Runtime folders"
for d in config run/instances logs reports; do
  if [ -d "$HOME/tradebot/$d" ]; then ok "~/tradebot/$d"; else warn "~/tradebot/$d missing (setup.md Step 6)"; fi
done

echo "== Secrets file"
secrets="$HOME/.config/trader/secrets.env"
if [ -f "$secrets" ]; then
  perms=$(stat -c '%a' "$secrets")
  if [ "$perms" = "600" ] || [ "$perms" = "400" ]; then
    ok "secrets.env permissions $perms"
  else
    fail "secrets.env permissions are $perms; run: chmod 600 $secrets"
  fi
  # Report which keys are filled, never their values.
  for key in CTRADER_CLIENT_ID CTRADER_CLIENT_SECRET CTRADER_ACCESS_TOKEN CTRADER_REFRESH_TOKEN; do
    if grep -qE "^${key}=.+" "$secrets"; then
      ok "$key is set"
    else
      warn "$key empty (fill after Open API approval, setup.md Step 3)"
    fi
  done
  if [ -n "$repo_root" ] && case "$secrets" in "$repo_root"/*) true;; *) false;; esac; then
    fail "secrets.env is inside the repo; move it to ~/.config/trader/"
  fi
else
  warn "No secrets file yet at $secrets (setup.md Step 6)"
fi

echo
if [ "$fails" -eq 0 ]; then
  printf 'Result: no failures, %d warning(s).\n' "$warns"
else
  printf 'Result: %d failure(s), %d warning(s).\n' "$fails" "$warns"
fi
[ "$fails" -eq 0 ]
