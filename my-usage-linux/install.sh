#!/usr/bin/env bash
# Installs usagetop for the current user into ~/.local/share/usagetop/app,
# links ~/.local/bin/usagetop, and adds a desktop launcher so the app can
# update itself in place from GitHub releases. On GNOME it also puts in the
# top bar extension, on KDE Plasma the panel widget.
#
#   ./install.sh              from an unpacked release folder, installs that copy;
#                             otherwise downloads the latest release from GitHub
#   curl -fsSL https://github.com/rzukic/usagetop/releases/latest/download/install-linux.sh | bash
#                             the same with nothing downloaded first: each release
#                             carries this script as install-linux.sh
#   ./install.sh --uninstall  removes the installed copy, link, launcher, extension
#                             and widget (keeps ~/.config/usagetop)
set -euo pipefail

# All of it in one block, which bash reads to the end before running any of it:
# piped from curl, a command that read its input would otherwise eat the rest of
# the script. Nothing in it asks for input.
{

REPO="rzukic/usagetop"
DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_ROOT="$DATA_HOME/usagetop"
APP_DIR="$INSTALL_ROOT/app"
BIN_DIR="$HOME/.local/bin"
BIN_LINK="$BIN_DIR/usagetop"
DESKTOP_DIR="$DATA_HOME/applications"
DESKTOP_FILE="$DESKTOP_DIR/usagetop.desktop"
FONT_FILE="$DATA_HOME/fonts/usagetop-marks.otf"
EXT_UUID="usagetop@rzukic.github.io"
EXT_DIR="$DATA_HOME/gnome-shell/extensions/$EXT_UUID"
PLASMOID_ID="io.github.rzukic.usagetop"
PLASMOID_DIR="$DATA_HOME/plasma/plasmoids/$PLASMOID_ID"

fail() {
	echo "usagetop: $1" >&2
	exit 1
}

is_kde() { [[ "${XDG_CURRENT_DESKTOP:-}" == *KDE* ]] || command -v kpackagetool6 >/dev/null 2>&1; }

# A line of Plasma's desktop scripting, run in the running plasmashell.
plasma_script() {
	local qdbus
	qdbus="$(command -v qdbus6 || command -v qdbus-qt6 || command -v qdbus || true)"
	[[ -n "$qdbus" ]] || return 1
	"$qdbus" org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell.evaluateScript "$1" 2>/dev/null
}

# The widget in the panel that holds the system tray, just before the tray,
# unless it is in a panel already. Prints "added" when it put it there,
# "placed" when it was in one, "none" with no tray to put it by.
PLASMA_ADD_SCRIPT='
const id = "'"$PLASMOID_ID"'";
let placed = false, added = false;
for (const p of panels()) if (p.widgets(id).length > 0) placed = true;
for (const p of panels()) {
	if (placed) break;
	const tray = p.widgets("org.kde.plasma.systemtray")[0];
	if (!tray) continue;
	// Added where the tray is, the panel puts it just before the tray.
	const g = tray.geometry;
	p.addWidget(id, g.x + 1, g.y + 1, g.width, g.height);
	placed = added = true;
}
print(added ? "added" : placed ? "placed" : "none");
'

# Every copy of the widget, out of every panel.
PLASMA_REMOVE_SCRIPT='
for (const p of panels()) for (const w of p.widgets("'"$PLASMOID_ID"'")) w.remove();
'

if [[ "${1:-}" == "--uninstall" ]]; then
	if command -v "$BIN_LINK" >/dev/null 2>&1; then
		"$BIN_LINK" quit >/dev/null 2>&1 || true
	fi
	rm -rf "$APP_DIR" "$INSTALL_ROOT/updates" "$INSTALL_ROOT/.usagetop-old"
	rmdir "$INSTALL_ROOT" 2>/dev/null || true
	rm -f "$BIN_LINK" "$DESKTOP_FILE"
	# The Plasma widget, out of the panel and away; a linked one is a working copy.
	if [[ -d "$PLASMOID_DIR" && ! -L "$PLASMOID_DIR" ]]; then
		plasma_script "$PLASMA_REMOVE_SCRIPT" >/dev/null || true
		if command -v kpackagetool6 >/dev/null 2>&1; then
			kpackagetool6 -t Plasma/Applet -r "$PLASMOID_ID" >/dev/null 2>&1 || true
		fi
		rm -rf "$PLASMOID_DIR"
	fi
	# A linked extension is somebody's working copy: leave it.
	if [[ -d "$EXT_DIR" && ! -L "$EXT_DIR" ]]; then
		command -v gnome-extensions >/dev/null 2>&1 && gnome-extensions disable "$EXT_UUID" >/dev/null 2>&1 || true
		rm -rf "$EXT_DIR"
	fi
	rm -f "$FONT_FILE"
	command -v fc-cache >/dev/null 2>&1 && fc-cache -f "$DATA_HOME/fonts" >/dev/null 2>&1 || true
	echo "Removed usagetop. Your settings in ${XDG_CONFIG_HOME:-$HOME/.config}/usagetop were left alone."
	exit 0
fi

# Run from a file, the folder it is in; piped into bash there is none, and
# whatever happens to be in the current folder is not a release to install.
SCRIPT_DIR=""
if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
	SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fi
# Where the releases' archives are; a test points it at a server of its own.
DOWNLOADS="${USAGETOP_DOWNLOADS:-https://github.com/$REPO/releases/latest/download}"
TMP=""
cleanup() {
	if [[ -n "$TMP" ]]; then rm -rf "$TMP"; fi
}
trap cleanup EXIT

if [[ -n "$SCRIPT_DIR" && -x "$SCRIPT_DIR/usagetop" ]]; then
	SOURCE_DIR="$SCRIPT_DIR"
else
	case "$(uname -m)" in
	x86_64 | amd64) ARCH="x64" ;;
	aarch64 | arm64) ARCH="arm64" ;;
	*) fail "unsupported architecture: $(uname -m)" ;;
	esac
	ASSET="usagetop-linux-$ARCH.tar.gz"
	URL="$DOWNLOADS/$ASSET"
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

# On KDE Plasma the panel widget is how usagetop shows the limits: put it in
# (or bring it up to date) and add it to the panel by the system tray. A linked
# widget (a developer's checkout) is left as it is.
KDE_NOTE=""
WIDGET_SOURCE="$APP_DIR/plasma-widget/$PLASMOID_ID"
if [[ -d "$WIDGET_SOURCE" && ! -L "$PLASMOID_DIR" ]] && is_kde; then
	if command -v kpackagetool6 >/dev/null 2>&1; then
		if [[ -d "$PLASMOID_DIR" ]]; then
			kpackagetool6 -t Plasma/Applet -u "$WIDGET_SOURCE" >/dev/null 2>&1 || true
		else
			kpackagetool6 -t Plasma/Applet -i "$WIDGET_SOURCE" >/dev/null 2>&1 || true
		fi
	fi
	# Without kpackagetool6, or when it would not: the files are all it takes.
	if [[ ! -f "$PLASMOID_DIR/metadata.json" ]] || ! cmp -s "$WIDGET_SOURCE/contents/ui/main.qml" "$PLASMOID_DIR/contents/ui/main.qml"; then
		rm -rf "$PLASMOID_DIR"
		mkdir -p "$(dirname "$PLASMOID_DIR")"
		cp -R "$WIDGET_SOURCE" "$PLASMOID_DIR"
	fi
	case "$(plasma_script "$PLASMA_ADD_SCRIPT" || true)" in
	*added*) KDE_NOTE="KDE Plasma: the usagetop widget is in your panel, by the system tray." ;;
	*placed*) ;;
	*)
		if [[ "${XDG_CURRENT_DESKTOP:-}" == *KDE* ]]; then
			KDE_NOTE="KDE Plasma: add the usagetop widget to your panel (right-click the panel › Add Widgets…)."
		fi
		;;
	esac
fi

# The providers' marks as a font, for bars that show text (Waybar, swaybar,
# polybar, Xfce's Generic Monitor): the app puts them in front of each limit
# once fontconfig knows the font.
if [[ -f "$APP_DIR/fonts/usagetop-marks.otf" ]]; then
	mkdir -p "$(dirname "$FONT_FILE")"
	if ! cmp -s "$APP_DIR/fonts/usagetop-marks.otf" "$FONT_FILE"; then
		cp "$APP_DIR/fonts/usagetop-marks.otf" "$FONT_FILE"
		command -v fc-cache >/dev/null 2>&1 && fc-cache -f "$(dirname "$FONT_FILE")" >/dev/null 2>&1 || true
	fi
fi

VERSION="$("$BIN_LINK" --version 2>/dev/null || echo "")"
echo "Installed usagetop ${VERSION} to $APP_DIR"
echo "Command: $BIN_LINK"
case ":${PATH:-}:" in
*":$BIN_DIR:"*) ;;
*) echo "Note: add $BIN_DIR to your PATH to run 'usagetop' from any shell." ;;
esac
if [[ -n "$GNOME_NOTE" ]]; then echo "$GNOME_NOTE"; fi
if [[ -n "$KDE_NOTE" ]]; then echo "$KDE_NOTE"; fi
} </dev/null
exit
