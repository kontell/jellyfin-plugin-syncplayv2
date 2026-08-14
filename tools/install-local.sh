#!/usr/bin/env bash
# Publish the plugin and stage it into a Jellyfin plugins directory as the
# server expects it: <plugins>/<Name>_<version>/{dll,meta.json}.
# Default target is the local throwaway server's mounted config.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="${1:-$repo/run/config/plugins}"
proj="$repo/Jellyfin.Plugin.SyncPlayV2/Jellyfin.Plugin.SyncPlayV2.csproj"
stage="$repo/artifacts/publish"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"

version="$(sed -n 's/.*<AssemblyVersion>\(.*\)<\/AssemblyVersion>.*/\1/p' "$proj")"
guid="$(sed -n 's/^guid: *"\(.*\)"/\1/p' "$repo/build.yaml")"
name="$(sed -n 's/^name: *"\(.*\)"/\1/p' "$repo/build.yaml")"
target_abi="$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' "$repo/build.yaml")"
owner="$(sed -n 's/^owner: *"\(.*\)"/\1/p' "$repo/build.yaml")"
overview="$(sed -n 's/^overview: *"\(.*\)"/\1/p' "$repo/build.yaml")"
description="$(sed -n 's/^description: *"\(.*\)"/\1/p' "$repo/build.yaml")"

"$dotnet" publish "$proj" -c Release -o "$stage"

dir="$target/SyncPlayV2_$version"
rm -rf "$dir"
mkdir -p "$dir"
cp "$stage/Jellyfin.Plugin.SyncPlayV2.dll" "$dir/"

cat > "$dir/meta.json" <<EOF
{
    "category": "General",
    "guid": "$guid",
    "name": "$name",
    "overview": "$overview",
    "description": "$description",
    "owner": "$owner",
    "targetAbi": "$target_abi",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$version",
    "changelog": "M0 spike build",
    "status": "Active",
    "autoUpdate": false
}
EOF

echo "Staged: $dir"
ls -la "$dir"
