# Shared by the deploy-*.sh scripts (source it; shared.md §3). Publishes a build into a new folder
# ~/tradebot/bin/<Bot>-<git hash> and then points the link ~/tradebot/bin/<Bot> at it in one step
# (rename of a new link over the old one), so no file a running bot uses is ever overwritten, moved or
# deleted. The .NET host follows the link once, at start: a running bot keeps loading from the folder
# it started in until it is restarted, and a restart through the systemd unit picks up the new folder.
# Keeps the newest $keep folders, the linked one, and every folder a running process uses.

bin="$HOME/tradebot/bin"
keep=3

# True if a running process has a file from folder $1 loaded (the .NET runtime maps every assembly it
# loads; /proc/<pid>/maps lists those files by their real path).
in_use() {
  grep -qsF -- "$(realpath "$1")/" /proc/[0-9]*/maps
}

# First of <base>, <base>-2, <base>-3, … that does not exist yet in $bin.
free_name() {
  local name=$1 n=1
  while [ -e "$bin/$name" ] || [ -L "$bin/$name" ]; do n=$((n + 1)); name="$1-$n"; done
  echo "$name"
}

# deploy_build <project under src/> <Bot> <service>
# Publishes, writes VERSION, switches the link and removes old folders. Sets $switched to yes when the
# link now points at the new build, or no when the old plain folder (before versioned folders) is
# still in use: then it prints the one-time command to switch and leaves everything else as it is.
deploy_build() {
  local project=$1 bot=$2 service=$3
  local link="$bin/$bot" version name dest
  version=$(git -C "$repo" rev-parse --short HEAD)
  git -C "$repo" diff --quiet HEAD || version="$version-dirty"   # uncommitted changes in the build
  name=$(free_name "$bot-$version")
  dest="$bin/$name"

  echo "Publishing $project (Release) → $dest"
  mkdir -p "$bin"
  rm -rf "${dest:?}.new"
  dotnet publish "$repo/src/$project" -c Release -o "$dest.new" --nologo -v quiet
  echo "$version" > "$dest.new/VERSION"
  mv "$dest.new" "$dest"
  echo "  version $version"

  switched=yes
  if [ -d "$link" ] && [ ! -L "$link" ]; then
    # Plain folder from before versioned folders: rename it only when nothing runs from it.
    local old
    old=$(free_name "$bot-$(cat "$link/VERSION" 2>/dev/null || echo old)")
    if in_use "$link"; then
      switched=no
      echo "  $link is a plain folder (before versioned folders) and a running process uses it: left as it is."
      echo "  One-time switch (stops the bot for a few seconds):"
      echo "    systemctl --user stop $service && mv -T $link $bin/$old && ln -s $name $link && systemctl --user start $service"
      return
    fi
    mv -T "$link" "$bin/$old"
    echo "  moved the plain folder $bot → $old"
  fi
  ln -sfn "$name" "$link.link-new"
  mv -T "$link.link-new" "$link"
  echo "  $link → $name"

  prune_builds "$bot" "$name"
}

# prune_builds <Bot> <linked folder>: removes all but the newest $keep <Bot>-* folders, never the linked
# one or one a running process uses; also the <Bot>.old folder the scripts kept before versioned folders.
prune_builds() {
  local bot=$1 current=$2 n=0 dir base
  for dir in $(ls -1dt "$bin/$bot"-*/ 2>/dev/null) "$bin/$bot.old/"; do
    dir=${dir%/}
    base=${dir##*/}
    [ -d "$dir" ] && [ ! -L "$dir" ] || continue
    case "$base" in *.new) continue ;; esac
    if [ "$base" != "$bot.old" ]; then
      n=$((n + 1))
      if [ "$base" = "$current" ] || [ "$n" -le "$keep" ]; then continue; fi
    fi
    if in_use "$dir"; then
      echo "  kept $base (a running process uses it)"
    else
      rm -rf "${dir:?}"
      echo "  removed old build $base"
    fi
  done
}
