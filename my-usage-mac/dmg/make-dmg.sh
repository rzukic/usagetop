#!/usr/bin/env bash
# Packs usagetop.app into a disk image for people to download: a window with
# the app, an arrow and the Applications folder to drag it onto.
#
#   ./make-dmg.sh <usagetop.app> <out.dmg>
#
# Signs the image with the app's Developer ID when the app has one (release-mac.sh
# then notarizes it). Needs create-dmg (brew install create-dmg), and Finder,
# which create-dmg asks to lay out the window: the first run may ask to allow
# the terminal to control Finder.
set -euo pipefail

APP="$1"
DMG="$2"
HERE="$(cd "$(dirname "$0")" && pwd)"
command -v create-dmg >/dev/null || { echo "create-dmg is missing: brew install create-dmg" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The background, sharp at 1x and 2x (layout in background.swift).
swift "$HERE/background.swift" "$WORK/bg.png" 1
swift "$HERE/background.swift" "$WORK/bg@2x.png" 2
tiffutil -cathidpicheck "$WORK/bg.png" "$WORK/bg@2x.png" -out "$WORK/background.tiff" >/dev/null 2>&1

mkdir "$WORK/src"
ditto "$APP" "$WORK/src/usagetop.app"

SIGN=()
ID="$(codesign -dvv "$APP" 2>&1 | sed -n 's/^Authority=\(Developer ID Application: .*\)$/\1/p' | head -n 1)"
[[ -n "$ID" ]] && SIGN=(--codesign "$ID")

rm -f "$DMG"
create-dmg \
	--volname "usagetop" \
	--volicon "$APP/Contents/Resources/AppIcon.icns" \
	--background "$WORK/background.tiff" \
	--window-pos 200 160 \
	--window-size 660 400 \
	--icon-size 128 \
	--text-size 12 \
	--icon "usagetop.app" 160 190 \
	--hide-extension "usagetop.app" \
	--app-drop-link 500 190 \
	--no-internet-enable \
	--filesystem APFS \
	--format ULFO \
	${SIGN[@]+"${SIGN[@]}"} \
	"$DMG" "$WORK/src" >/dev/null
