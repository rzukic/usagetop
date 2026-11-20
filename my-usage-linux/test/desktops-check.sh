#!/usr/bin/env bash
# Tries the Linux app on desktops that are hard to have at hand, each in a
# container of its own that is thrown away after: KDE Plasma (X11, KWin
# compositing with software OpenGL), Xfce, and Sway with Waybar (headless
# Wayland, the app under XWayland). On each it checks what the app does there
# (its tray icon, a click on it, where the panel opens, the Plasma widget as
# install.sh puts it in, Xfce's Generic Monitor, the Waybar module) and
# takes pictures of it, with the made-up sample data.
#
#   ./test/desktops-check.sh                      all three; pictures in a temp folder
#   ./test/desktops-check.sh <folder> [kde|xfce|sway ...]
#   ./test/desktops-check.sh --clean              remove the images it built
#   ./test/desktops-check.sh --bar-limits N ...   with Settings › Top bar on N (1-4)
#                                                 limits in place of the ring
#
# The pictures are <desktop>-<what>.png. Docker is all it needs; the first run
# builds an image per desktop (KDE's is about 1.2 GB), later runs reuse them.
set -euo pipefail
cd "$(dirname "$0")/.."
all=(kde xfce sway)

if [ "${1:-}" = --clean ]; then
  for d in "${all[@]}"; do docker image rm "usagetop-desktops-$d" 2> /dev/null || true; done
  exit 0
fi

out=""
desktops=()
bar_limits=0
while [ $# -gt 0 ]; do
  case "$1" in
    kde | xfce | sway) desktops+=("$1") ;;
    --bar-limits) bar_limits="${2:?--bar-limits takes 1 to 4}"; shift ;;
    *) out="$1" ;;
  esac
  shift
done
case "$bar_limits" in [0-4]) ;; *) echo "--bar-limits takes 1 to 4" >&2; exit 2 ;; esac
[ ${#desktops[@]} -eq 0 ] && desktops=("${all[@]}")
out="${out:-$(mktemp -d)}"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
app="$(mktemp -d)"
trap 'rm -rf "$app"' EXIT

echo "== publish for linux-x64"
DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-LatestMajor}" \
  dotnet publish src/AiUsage.Linux -c Release -r linux-x64 --self-contained -o "$app" -v q -nologo

failed=0
for d in "${desktops[@]}"; do
  echo "== build the $d image"
  docker build -q -t "usagetop-desktops-$d" "test/desktops/$d" > /dev/null
  # The pictures are handed to this user at the end; the app only reads its
  # own sample data, never this machine's settings.
  docker run --rm --init -e OWNER="$(id -u):$(id -g)" -e BAR_LIMITS="$bar_limits" --shm-size 256m \
    -v "$app:/app:ro" -v "$PWD/test/desktops:/desktops:ro" -v "$PWD:/src:ro" -v "$out:/out" \
    "usagetop-desktops-$d" timeout 420 bash "/desktops/$d/in-container.sh" || failed=$((failed + $?))
done

echo "== pictures in $out:"
ls "$out"
echo "== $failed failed in all"
exit $(( failed > 0 ))
