#!/usr/bin/env bash
# Builds usagetop.app: a menu-bar-only bundle around the SwiftPM executable.
#
#   ./build.sh            release build, universal (arm64 and x86_64)
#   ./build.sh debug      debug build, for this Mac's architecture only
#   ./build.sh release run   build, then launch
#
# The app's version is the project's, <Version> in the Windows app's project
# file; VERSION=1.2.3 ./build.sh overrides it.
set -euo pipefail

cd "$(dirname "$0")"

CONFIG="${1:-release}"
APP="usagetop.app"

VERSION="${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' ../my-usage-win/src/AiUsage/AiUsage.csproj | head -n 1)}"
VERSION="${VERSION#v}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
	echo "'$VERSION' is not a version like 1.2.0 or 1.2.1-beta.1." >&2
	exit 1
fi

ARCHS=()
if [[ "$CONFIG" == "release" ]]; then
	ARCHS=(--arch arm64 --arch x86_64)
fi

swift build -c "$CONFIG" ${ARCHS[@]+"${ARCHS[@]}"}

BIN="$(swift build -c "$CONFIG" ${ARCHS[@]+"${ARCHS[@]}"} --show-bin-path)/AiUsageMac"
if [[ ! -x "$BIN" ]]; then
	echo "No executable at $BIN" >&2
	exit 1
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/AiUsageMac"
cp Info.plist "$APP/Contents/Info.plist"
# Settings shows the version as given; the bundle's build number is its three
# numbers, which is all macOS accepts there.
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
plutil -replace CFBundleVersion -string "${VERSION%%-*}" "$APP/Contents/Info.plist"

# The app icon, from the 1024px master (an 824px tile on Apple's icon grid).
ICONS="$(mktemp -d)"
trap 'rm -rf "$ICONS"' EXIT
ICONSET="$ICONS/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
	sips -z "$size" "$size" AppIcon.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
	sips -z "$((size * 2))" "$((size * 2))" AppIcon.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature: there is no Developer ID yet. An Apple silicon Mac runs
# nothing unsigned, so a failure here is a failed build.
codesign --force --sign - "$APP"

echo "Built $(pwd)/$APP ($VERSION)"

if [[ "${2:-}" == "run" ]]; then
	open "$APP"
fi
