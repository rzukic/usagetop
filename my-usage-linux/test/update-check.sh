#!/usr/bin/env bash
# End-to-end test of the Linux self-updater inside a real Linux container:
# builds two self-contained versions (1.6.0 and 9.9.9) for this host's arch,
# installs 1.6.0 via install.sh, serves a mock GitHub Releases API in the
# container, verifies:
#   1. doctor reports updates on for the installed copy (and off for raw build)
#   2. tampered SHA-256 is refused
#   3. valid 9.9.9 archive is downloaded, SHA-256 + --version verified, staged,
#      and atomically swapped in (both via `usagetop update` and via startup
#      pending-update handover)
#   4. ~/.local/bin/usagetop keeps working and now reports 9.9.9
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
  arm64 | aarch64) rid=linux-arm64; arch=arm64 ;;
  *) rid=linux-x64; arch=x64 ;;
esac

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "== publish 1.6.0 and 9.9.9 for $rid"
dotnet publish src/AiUsage.Linux -c Release -r "$rid" --self-contained true --nologo -v q \
  -p:Version=1.6.0 -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none \
  -o "$tmp/v1/usagetop"
cp install.sh "$tmp/v1/usagetop/install.sh"
chmod 755 "$tmp/v1/usagetop/usagetop" "$tmp/v1/usagetop/install.sh"
mkdir -p "$tmp/v1/usagetop/gnome-extension"
cp -R gnome-extension/usagetop@rzukic.github.io "$tmp/v1/usagetop/gnome-extension/"

dotnet publish src/AiUsage.Linux -c Release -r "$rid" --self-contained true --nologo -v q \
  -p:Version=9.9.9 -p:IncludeSourceRevisionInInformationalVersion=false -p:DebugType=none \
  -o "$tmp/v2/usagetop"
cp install.sh "$tmp/v2/usagetop/install.sh"
chmod 755 "$tmp/v2/usagetop/usagetop" "$tmp/v2/usagetop/install.sh"
mkdir -p "$tmp/v2/usagetop/gnome-extension"
cp -R gnome-extension/usagetop@rzukic.github.io "$tmp/v2/usagetop/gnome-extension/"

asset="usagetop-linux-$arch.tar.gz"
COPYFILE_DISABLE=1 tar -C "$tmp/v2" -czf "$tmp/$asset" usagetop

echo "== run in Linux container"
docker run --rm \
  -v "$tmp/v1/usagetop:/src-v1:ro" \
  -v "$tmp/$asset:/release/$asset:ro" \
  -e "ASSET=$asset" \
  python:3.12-slim bash -euo pipefail -c '
export HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/run
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
failed=0
check() {
  if printf "%s" "$2" | grep -Eq -- "$3"; then echo "ok    $1"
  else echo "FAIL  $1: got '\''$2'\'', wanted '\''$3'\''"; failed=$((failed + 1)); fi
}

check "raw build has updates off" "$(/src-v1/usagetop doctor | grep updates)" "^updates[[:space:]]+off"

install_out="$(XDG_CURRENT_DESKTOP=GNOME /src-v1/install.sh)"
BIN="$HOME/.local/bin/usagetop"
EXT="$HOME/.local/share/gnome-shell/extensions/usagetop@rzukic.github.io"
check "install puts the GNOME extension in" "$(test -f "$EXT/metadata.json" && echo yes || echo no)" "^yes$"
check "install says to log in again" "$install_out" "log out and back in"
check "installed version is 1.6.0" "$("$BIN" --version)" "^1\.6\.0$"
check "installed copy has updates on" "$("$BIN" doctor | grep updates)" "^updates[[:space:]]+on"

# Make the installed extension look old, to see the update refresh it.
echo "old" > "$EXT/extension.js"

# Start mock GitHub Releases API server.
python3 - <<PY >/tmp/mock.log 2>&1 &
import hashlib, http.server, json, os, socketserver

asset = os.environ["ASSET"]
data = open(f"/release/{asset}", "rb").read()
good_sha = hashlib.sha256(data).hexdigest()

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        if self.path == "/bad/releases/latest":
            body = json.dumps({
                "tag_name": "v9.9.9", "draft": False, "prerelease": False,
                "assets": [{"name": asset, "browser_download_url": f"http://127.0.0.1:18377/dl/{asset}", "digest": "sha256:" + "0"*64}]
            }).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        elif self.path == "/good/releases/latest":
            body = json.dumps({
                "tag_name": "v9.9.9", "draft": False, "prerelease": False,
                "assets": [{"name": asset, "browser_download_url": f"http://127.0.0.1:18377/dl/{asset}", "digest": f"sha256:{good_sha}"}]
            }).encode()
            self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        elif self.path == f"/dl/{asset}":
            self.send_response(200); self.send_header("Content-Type", "application/gzip"); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
        else:
            self.send_response(404); self.end_headers()

socketserver.TCPServer.allow_reuse_address = True
with socketserver.TCPServer(("127.0.0.1", 18377), Handler) as srv:
    srv.serve_forever()
PY
sleep 1

# 1. Tampered SHA-256 must fail and leave 1.6.0 untouched.
set +e
bad_out="$(USAGETOP_UPDATE_SOURCE=http://127.0.0.1:18377/bad "$BIN" update 2>&1)"
bad_rc=$?
set -e
check "bad SHA-256 fails" "$bad_rc" "^1$"
check "bad SHA-256 mentions SHA-256" "$bad_out" "SHA-256"
check "still on 1.6.0 after bad SHA-256" "$("$BIN" --version)" "^1\.6\.0$"

# 2. Valid release downloads, verifies, swaps in, and refreshes GNOME extension.
set +e
good_out="$(USAGETOP_UPDATE_SOURCE=http://127.0.0.1:18377/good "$BIN" update 2>&1)"
good_rc=$?
set -e
check "valid update exits 0" "$good_rc" "^0$"
check "valid update succeeds" "$good_out" "Updated usagetop to 9\.9\.9"
check "symlink now runs 9.9.9" "$("$BIN" --version)" "^9\.9\.9$"
check "GNOME extension refreshed" "$(grep -c "usagetop" "$EXT/extension.js")" "^[1-9]"
check "staging folder cleaned up" "$(test -d "$HOME/.local/share/usagetop/updates" && echo dirty || echo clean)" "^clean$"
check "backup folder cleaned up" "$(test -d "$HOME/.local/share/usagetop/.usagetop-old" && echo dirty || echo clean)" "^clean$"

# 3. Re-checking on 9.9.9 reports up to date.
again_out="$(USAGETOP_UPDATE_SOURCE=http://127.0.0.1:18377/good "$BIN" update 2>&1)"
check "9.9.9 is up to date" "$again_out" "9\.9\.9 is up to date"

# 4. Uninstall leaves settings intact and removes app + link.
/src-v1/install.sh --uninstall >/dev/null
check "uninstall removes link" "$(test -e "$BIN" && echo exists || echo gone)" "^gone$"
check "uninstall removes the GNOME extension" "$(test -e "$EXT" && echo exists || echo gone)" "^gone$"

# 5. Piped into bash from an empty folder, as the one-line install does: it
# fetches the newest release itself (here from the mock server) and installs it.
command -v curl >/dev/null || { apt-get update -qq >/dev/null && apt-get install -y -qq curl >/dev/null 2>&1; }
mkdir -p /tmp/empty && cd /tmp/empty
piped_out="$(cat /src-v1/install.sh | USAGETOP_DOWNLOADS=http://127.0.0.1:18377/dl bash 2>&1)"
check "piped install fetches the release" "$piped_out" "Installed usagetop 9\.9\.9"
check "piped install runs" "$("$BIN" --version 2>&1)" "^9\.9\.9$"
check "piped install has updates on" "$("$BIN" doctor | grep updates)" "^updates[[:space:]]+on"
/src-v1/install.sh --uninstall >/dev/null
cd /

echo "== $failed failed"
exit $failed
'
