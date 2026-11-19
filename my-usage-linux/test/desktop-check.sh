#!/usr/bin/env bash
# Tries the Linux app on Linux from any machine with Docker: publishes it for
# this machine's processor, then runs it in a container on a virtual display,
# once under a tiling window manager with nothing compositing (i3) and once
# under a compositor (openbox with picom), and checks what it does there.
#
#   ./test/desktop-check.sh            build, run the checks
#   ./test/desktop-check.sh <folder>   and leave the pictures it drew in <folder>
#
# It does not cover what only a real desktop has: a tray, KWin's blur, Wayland.
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
  arm64 | aarch64) rid=linux-arm64 ;;
  *) rid=linux-x64 ;;
esac
out="${1:-$(mktemp -d)}"
app="$(mktemp -d)"
trap 'rm -rf "$app"' EXIT

echo "== publish for $rid"
dotnet publish src/AiUsage.Linux -c Release -r "$rid" --self-contained -o "$app" -v q -nologo
docker build -q -t usagetop-desktop-check test > /dev/null
mkdir -p "$out"
docker run --rm -v "$app:/app:ro" -v "$PWD/test/in-container.sh:/check.sh:ro" -v "$out:/out" \
  usagetop-desktop-check bash /check.sh
