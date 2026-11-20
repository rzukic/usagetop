#!/usr/bin/env bash
# Installs usagetop for the current user into ~/.local/share/usagetop/app,
# links ~/.local/bin/usagetop, and adds a desktop launcher so the app can
# update itself in place from GitHub releases.
#
#   ./install.sh              from an unpacked release folder, installs that copy;
#                             otherwise downloads the latest release from GitHub
#   ./install.sh --uninstall  removes the installed copy, link and launcher
#                             (keeps ~/.config/usagetop)
set -euo pipefail

REPO="rzukic/usagetop"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_ROOT="$DATA_HOME/usagetop"
APP_DIR="$INSTALL_ROOT/app"
BIN_DIR="$HOME/.local/bin"
BIN_LINK="$BIN_DIR/usagetop"
DESKTOP_DIR="$DATA_HOME/applications"
DESKTOP_FILE="$DESKTOP_DIR/usagetop.desktop"

fail() {
	echo "usagetop: $1" >&2
	exit 1
}

if [[ "${1:-}" == "--uninstall" ]]; then
	if command -v "$BIN_LINK" >/dev/null 2>&1; then
		"$BIN_LINK" quit >/dev/null 2>&1 || true
	fi
	rm -rf "$APP_DIR" "$INSTALL_ROOT/updates" "$INSTALL_ROOT/.usagetop-old"
	rmdir "$INSTALL_ROOT" 2>/dev/null || true
	rm -f "$BIN_LINK" "$DESKTOP_FILE"
	echo "Removed usagetop. Your settings in ${XDG_CONFIG_HOME:-$HOME/.config}/usagetop were left alone."
	exit 0
fi

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
TMP=""
cleanup() {
	if [[ -n "$TMP" ]]; then rm -rf "$TMP"; fi
}
trap cleanup EXIT

if [[ -x "$SCRIPT_DIR/usagetop" ]]; then
	SOURCE_DIR="$SCRIPT_DIR"
else
	case "$(uname -m)" in
	x86_64 | amd64) ARCH="x64" ;;
	aarch64 | arm64) ARCH="arm64" ;;
	*) fail "unsupported architecture: $(uname -m)" ;;
	esac
	ASSET="usagetop-linux-$ARCH.tar.gz"
	URL="https://github.com/$REPO/releases/latest/download/$ASSET"
	TMP="$(mktemp -d)"
	echo "Downloading $URL..."
	curl -fsSL "$URL" -o "$TMP/$ASSET" || fail "could not download $ASSET"
	mkdir -p "$TMP/unpacked"
	tar -xzf "$TMP/$ASSET" -C "$TMP/unpacked"
	if [[ -x "$TMP/unpacked/usagetop/usagetop" ]]; then
		SOURCE_DIR="$TMP/unpacked/usagetop"
	elif [[ -x "$TMP/unpacked/usagetop" ]]; then
		SOURCE_DIR="$TMP/unpacked"
	else
		fail "archive did not contain usagetop"
	fi
fi

# Stop a running copy cleanly before swapping the directory.
if [[ -x "$BIN_LINK" ]]; then
	"$BIN_LINK" quit >/dev/null 2>&1 || true
fi

mkdir -p "$INSTALL_ROOT" "$BIN_DIR" "$DESKTOP_DIR"
STAGE="$INSTALL_ROOT/.install-stage-$$"
rm -rf "$STAGE"
mkdir -p "$STAGE"
cp -a "$SOURCE_DIR/." "$STAGE/"
chmod 755 "$STAGE/usagetop"

BACKUP="$INSTALL_ROOT/.usagetop-old"
rm -rf "$BACKUP"
if [[ -d "$APP_DIR" ]]; then
	mv "$APP_DIR" "$BACKUP"
fi
mv "$STAGE" "$APP_DIR"
rm -rf "$BACKUP"

ln -sfn "$APP_DIR/usagetop" "$BIN_LINK"

cat >"$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=usagetop
Comment=How much of each AI subscription is left
Exec="$BIN_LINK"
Icon=usagetop
Terminal=false
Categories=Utility;
EOF

VERSION="$("$BIN_LINK" --version 2>/dev/null || echo "")"
echo "Installed usagetop ${VERSION} to $APP_DIR"
echo "Command: $BIN_LINK"
case ":${PATH:-}:" in
*":$BIN_DIR:"*) ;;
*) echo "Note: add $BIN_DIR to your PATH to run 'usagetop' from any shell." ;;
esac
