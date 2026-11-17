#!/usr/bin/env bash
# Builds usagetop.app: a menu-bar-only bundle around the SwiftPM executable.
#
#   ./build.sh            release build
#   ./build.sh debug      debug build
#   ./build.sh release run   build, then launch
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${1:-release}"
APP="usagetop.app"

swift build -c "$CONFIG"

BIN="$(swift build -c "$CONFIG" --show-bin-path)/AiUsageMac"
if [[ ! -x "$BIN" ]]; then
	echo "No executable at $BIN" >&2
	exit 1
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/AiUsageMac"
cp Info.plist "$APP/Contents/Info.plist"

# The app icon, from the 1024px master (an 824px tile on Apple's icon grid).
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
	sips -z "$size" "$size" AppIcon.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
	sips -z "$((size * 2))" "$((size * 2))" AppIcon.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature: enough for the app to run locally and to keep the Keychain
# item stable across rebuilds.
codesign --force --sign - "$APP" >/dev/null 2>&1 || echo "codesign skipped" >&2

echo "Built $(pwd)/$APP"

if [[ "${2:-}" == "run" ]]; then
	open "$APP"
fi
