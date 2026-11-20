#!/usr/bin/env bash
# Builds self-contained Linux release archives:
#   usagetop-linux-x64.tar.gz
#   usagetop-linux-arm64.tar.gz
#
# Each archive unpacks to a single `usagetop/` folder containing the `usagetop`
# executable, its runtime, `install.sh`, and the GNOME Shell extension.
#
#   ./my-usage-linux/package.sh [version] [out-dir]
set -euo pipefail

cd "$(dirname "$0")"
REPO_ROOT="$(cd .. && pwd)"

VERSION="${1:-}"
if [[ -z "$VERSION" ]]; then
	VERSION="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$REPO_ROOT/my-usage-win/src/AiUsage/AiUsage.csproj" | head -n 1)"
fi
VERSION="${VERSION#v}"
[[ -n "$VERSION" ]] || { echo "Could not determine version." >&2; exit 1; }

OUT_DIR="${2:-$REPO_ROOT/build/release}"
mkdir -p "$OUT_DIR"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

for rid in linux-x64 linux-arm64; do
	echo "== Linux: $rid ($VERSION)"
	stage="$TMP/$rid/usagetop"
	dotnet publish src/AiUsage.Linux -c Release -r "$rid" --self-contained true --nologo -v q \
		-p:Version="$VERSION" -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none \
		-o "$stage"
	chmod 755 "$stage/usagetop"
	cp install.sh "$stage/install.sh"
	chmod 755 "$stage/install.sh"
	mkdir -p "$stage/gnome-extension"
	cp -R gnome-extension/usagetop@rzukic.github.io "$stage/gnome-extension/"

	archive="$OUT_DIR/usagetop-$rid.tar.gz"
	rm -f "$archive"
	COPYFILE_DISABLE=1 tar -C "$TMP/$rid" -czf "$archive" usagetop
	echo "Wrote $archive"
done
