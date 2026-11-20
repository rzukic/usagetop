# Sourced by each desktop's in-container.sh: /app is the published app, /out
# takes the pictures, /desktops is this folder.
cd /tmp
export HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/run LANG=C.UTF-8
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
U=/app/usagetop
failed=0

check() { # check <what> <got> <wanted, as a grep -E pattern>
  if printf '%s\n' "$2" | grep -Eq -- "$3"; then echo "ok    $1"
  else echo "FAIL  $1: got '$2', wanted '$3'"; failed=$((failed + 1)); fi
}
# Waits up to <seconds> for a command to succeed.
wait_for() { local n=$(( $1 * 4 )); shift; until "$@" > /dev/null 2>&1; do n=$((n - 1)); [ $n -le 0 ] && return 1; sleep 0.25; done; }
start_app() { ("$U" --sample > /tmp/app.log 2>&1 &); wait_for 20 "$U" status; }
start_bus() { DBUS_SESSION_BUS_ADDRESS=$(dbus-daemon --session --fork --print-address); export DBUS_SESSION_BUS_ADDRESS; }
finish() { chown -R "${OWNER:-0:0}" /out; echo "== $failed failed"; exit $failed; }

# ---- X11 on Xvfb ----

# The screen as it is shown, after the compositor: Xvfb keeps its frame buffer
# in a file. Asking the X server for the root window's picture would give the
# windows as drawn, without the compositor's blending, blur or shadows.
start_x() { # start_x <width>x<height>
  mkdir -p /tmp/fb
  Xvfb :99 -screen 0 "$1x24" -fbdir /tmp/fb +extension GLX > /tmp/xvfb.log 2>&1 &
  export DISPLAY=:99
  wait_for 10 xdpyinfo
  start_bus
}
shot() { convert xwd:/tmp/fb/Xvfb_screen0 -alpha off "$1"; }
window() { xwininfo -root -tree | grep '"usagetop"' | head -1 | awk '{print $1}'; }
geometry() { [ -n "$1" ] || { echo 0 0 0 0; return; }; xwininfo -id "$1" | awk '/Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width/{w=$2} /Height/{h=$2} END{print x, y, w, h}'; }
mapped() { [ -n "$1" ] && xwininfo -id "$1" | grep -q IsViewable; }
# Where the tray icon is: what changed in the band <y0>-<y1> between a picture
# from before the app started and one from after, leftmost first (a clock to
# its right may have moved on too). Prints its centre, "x y".
find_icon() { python3 /desktops/find-icon.py "$1" "$2" "$3" "$4"; }
# How different the same strip of two pictures is, as the mean difference per
# channel in 0-1: under 0.01 is the same picture.
differ() {
  convert "$1" -crop "$3" +repage /tmp/differ-a.png; convert "$2" -crop "$3" +repage /tmp/differ-b.png
  compare -metric MAE /tmp/differ-a.png /tmp/differ-b.png null: 2>&1 | sed 's/.*(\(.*\))/\1/'
}
