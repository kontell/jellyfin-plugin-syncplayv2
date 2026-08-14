#!/usr/bin/env bash
# Build the Jellyfin-installable zip: the artifacts named in build.yaml plus
# the meta.json the server reads at load time. Output: dist/.
# (Adapted from jellyfin-plugin-kofinsyncqueue/tools/package.sh.)
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$repo/dist}"
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

changelog="$(awk '
    /^changelog:[[:space:]]*\|-?[[:space:]]*$/ { flag = 1; next }
    flag && /^[^[:space:]]/ { exit }
    flag { sub(/^  /, ""); print }
' "$repo/build.yaml" \
    | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' \
    | awk 'NR > 1 { printf "\\n" } { printf "%s", $0 }')"

"$dotnet" publish "$proj" -c Release -o "$stage"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp "$stage/Jellyfin.Plugin.SyncPlayV2.dll" "$work/"

cat > "$work/meta.json" <<EOF
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
    "changelog": "$changelog",
    "status": "Active",
    "autoUpdate": false
}
EOF

mkdir -p "$out"
out="$(cd "$out" && pwd)"
zip_path="$out/syncplay-v2_$version.zip"
rm -f "$zip_path"
(cd "$work" && zip -q -r "$zip_path" .)

echo "$zip_path"
unzip -l "$zip_path"
