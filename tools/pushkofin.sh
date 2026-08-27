#!/usr/bin/env bash
# Push a kofin working tree to the Android members and reload it.
#
# The local dev-install (tools/dev-install.sh in the kofin repo) only covers
# the two desktop Kodis; the Tab and the Bravia need adb. Files are pushed
# over the top rather than the directory replaced, because deleting under
# Android/data fails while pushing succeeds.
#
#   tools/pushkofin.sh <src> <serial> [<serial> ...]
set -euo pipefail
src="$1"; shift
dest="/storage/emulated/0/Android/data/org.xbmc.kodi/files/.kodi/addons/plugin.video.kofin"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
rsync -a --exclude '.git' --exclude '.venv' --exclude '__pycache__' \
      --exclude '.mypy_cache' --exclude '.pytest_cache' --exclude 'docs' \
      --exclude 'tests' --exclude 'tools' --exclude '*.pyc' \
      "$src/" "$stage/"

for serial in "$@"; do
    echo "== $serial"
    adb -s "$serial" push "$stage/lib" "$dest/" >/dev/null
    adb -s "$serial" push "$stage/addon.xml" "$dest/" >/dev/null
    adb -s "$serial" shell "am force-stop org.xbmc.kodi"
    sleep 2
    adb -s "$serial" shell "monkey -p org.xbmc.kodi -c android.intent.category.LAUNCHER 1" >/dev/null 2>&1
done
echo "pushed to $*"
