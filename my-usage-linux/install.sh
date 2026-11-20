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
EXT_UUID="usagetop@rzukic.github.io"
EXT_DIR="$DATA_HOME/gnome-shell/extensions/$EXT_UUID"

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
	# A linked extension is somebody's working copy: leave it.
	if [[ -d "$EXT_DIR" && ! -L "$EXT_DIR" ]]; then
		command -v gnome-extensions >/dev/null 2>&1 && gnome-extensions disable "$EXT_UUID" >/dev/null 2>&1 || true
		rm -rf "$EXT_DIR"
	fi
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

# On GNOME the top bar extension is how usagetop shows: put it in and switch
# it on. The app refreshes this copy each time it updates itself. A linked
# extension (a developer's checkout) is left as it is.
GNOME_NOTE=""
if [[ -d "$APP_DIR/gnome-extension/$EXT_UUID" && ! -L "$EXT_DIR" ]] &&
	{ [[ "${XDG_CURRENT_DESKTOP:-}" == *GNOME* ]] || command -v gnome-shell >/dev/null 2>&1; }; then
	NEW_EXT=1
	[[ -d "$EXT_DIR" ]] && NEW_EXT=""
	mkdir -p "$EXT_DIR"
	cp -R "$APP_DIR/gnome-extension/$EXT_UUID/." "$EXT_DIR/"
	if command -v gnome-extensions >/dev/null 2>&1; then
		gnome-extensions enable "$EXT_UUID" >/dev/null 2>&1 || true
	fi
	# Also in the settings, which gnome-extensions cannot reach for an
	# extension the running shell has not loaded yet.
	if command -v gsettings >/dev/null 2>&1; then
		ENABLED="$(gsettings get org.gnome.shell enabled-extensions 2>/dev/null || true)"
		if [[ -n "$ENABLED" && "$ENABLED" != *"'$EXT_UUID'"* ]]; then
			if [[ "$ENABLED" == "@as []" || "$ENABLED" == "[]" ]]; then
				ENABLED="['$EXT_UUID']"
			else
				ENABLED="${ENABLED%]}, '$EXT_UUID']"
			fi
			gsettings set org.gnome.shell enabled-extensions "$ENABLED" >/dev/null 2>&1 || true
		fi
	fi
	[[ -n "$NEW_EXT" ]] && GNOME_NOTE="GNOME: the top bar extension is installed; log out and back in once to see it."
fi

VERSION="$("$BIN_LINK" --version 2>/dev/null || echo "")"
echo "Installed usagetop ${VERSION} to $APP_DIR"
echo "Command: $BIN_LINK"
case ":${PATH:-}:" in
*":$BIN_DIR:"*) ;;
*) echo "Note: add $BIN_DIR to your PATH to run 'usagetop' from any shell." ;;
esac
if [[ -n "$GNOME_NOTE" ]]; then echo "$GNOME_NOTE"; fi
