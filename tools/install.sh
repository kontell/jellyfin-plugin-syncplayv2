#!/usr/bin/env bash
# Install a SyncPlay v2 plugin zip into a Jellyfin server's plugins directory,
# removing any previous install of the same plugin, and fixing ownership and
# permissions to match the server. Run with sudo when the server runs as its
# own user (deb/rpm installs do).
#
#   sudo ./install.sh syncplay-v2_10.11.0.90.zip [plugins-dir] [--restart]
#
# plugins-dir is auto-detected from the usual locations when omitted
# (<server datadir>/plugins). --restart restarts the jellyfin systemd unit
# after installing.
set -euo pipefail

zip=""
plugins=""
restart=0
for arg in "$@"; do
    case "$arg" in
        --restart) restart=1 ;;
        *)
            if [ -z "$zip" ]; then zip="$arg"
            elif [ -z "$plugins" ]; then plugins="$arg"
            else echo "unexpected argument: $arg" >&2; exit 2
            fi
            ;;
    esac
done

[ -n "$zip" ] || { echo "usage: $0 <plugin-zip> [plugins-dir] [--restart]" >&2; exit 2; }
[ -f "$zip" ] || { echo "no such zip: $zip" >&2; exit 2; }

if [ -z "$plugins" ]; then
    for candidate in /var/lib/jellyfin/plugins /config/plugins \
                     "$HOME/.local/share/jellyfin/plugins" /srv/jellyfin/plugins; do
        if [ -d "$candidate" ]; then plugins="$candidate"; break; fi
    done
fi
if [ -z "$plugins" ] || [ ! -d "$plugins" ]; then
    echo "plugins directory not found; pass it explicitly (it is <server datadir>/plugins)" >&2
    exit 2
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unzip -q "$zip" -d "$work"
[ -f "$work/meta.json" ] || { echo "zip has no meta.json at its root" >&2; exit 2; }

version="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$work/meta.json")"
name="$(sed -n 's/.*"name": *"\([^"]*\)".*/\1/p' "$work/meta.json" | tr -d ' ')"
guid="$(sed -n 's/.*"guid": *"\([^"]*\)".*/\1/p' "$work/meta.json")"
[ -n "$version" ] && [ -n "$name" ] && [ -n "$guid" ] || { echo "could not read name/version/guid from meta.json" >&2; exit 2; }

# Remove previous installs of the same plugin (matched by guid, any version)
# so exactly one copy loads. If files are held open by a running server (or by
# NFS silly-rename), move the directory OUT of the plugins tree instead — a
# leftover inside it would still be scanned at next start.
remove_old() {
    local target="$1"
    if rm -rf "$target" 2>/dev/null; then
        return 0
    fi
    local trash
    trash="$(dirname "$plugins")/.syncplayv2-old-$$-$RANDOM"
    mv "$target" "$trash"
    echo "note: old plugin files were in use (server running?) — moved to $trash; delete it after restarting" >&2
}

for old in "$plugins"/*/; do
    [ -f "${old}meta.json" ] || continue
    if grep -q "$guid" "${old}meta.json"; then
        echo "removing previous install: $old"
        remove_old "${old%/}"
    fi
done

dir="$plugins/${name}_${version}"
mkdir -p "$dir"
cp "$work"/* "$dir/"

# Ownership and permissions: mirror the plugins directory itself, so the files
# are readable by whatever user the server runs as (jellyfin on deb installs,
# the mapped uid on docker).
if ! chown -R --reference="$plugins" "$dir" 2>/dev/null; then
    echo "note: chown failed — rerun with sudo if Jellyfin runs as a different user" >&2
fi
chmod 755 "$dir"
chmod 644 "$dir"/*

echo "installed: $dir"
ls -la "$dir"

if [ "$restart" = 1 ]; then
    if command -v systemctl >/dev/null 2>&1 && systemctl restart jellyfin 2>/dev/null; then
        echo "jellyfin restarted"
    else
        echo "could not restart via systemd — restart the server yourself" >&2
    fi
else
    echo
    echo "now restart Jellyfin, then verify with:"
    echo "  grep 'SyncPlayV2 spike' <logdir>/log_*.log   # expect: hosted service started ... SpikeSyncPlayManager"
fi
