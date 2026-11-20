# Sourced by each desktop's in-container.sh: /app is the published app, /out
# takes the pictures, /desktops is this folder.
cd /tmp
export HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/run LANG=C.UTF-8
mkdir -p "$HOME" "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
U=/app/usagetop
failed=0
# Settings › Top bar: the ring (0), or the 1-4 limits closest to running out.
BAR_LIMITS=${BAR_LIMITS:-0}

check() { # check <what> <got> <wanted, as a grep -E pattern>
  if printf '%s\n' "$2" | grep -Eq -- "$3"; then echo "ok    $1"
  else echo "FAIL  $1: got '$2', wanted '$3'"; failed=$((failed + 1)); fi
}
# Waits up to <seconds> for a command to succeed.
wait_for() { local n=$(( $1 * 4 )); shift; until "$@" > /dev/null 2>&1; do n=$((n - 1)); [ $n -le 0 ] && return 1; sleep 0.25; done; }
start_app() { ("$U" --sample > /tmp/app.log 2>&1 &); wait_for 20 "$U" status && choose_bar_limits; }
# With BAR_LIMITS 1-4, picks that many in Settings › Top bar, as the user would:
# the sample data leaves the saved settings unread. The page scrolled to its
# end has the choice 320-410 from the window's left (which includes 30 of room
# for a shadow) and 114-140 from its top, as long as the notes above it wrap.
choose_bar_limits() {
  [ "$BAR_LIMITS" -gt 0 ] || return 0
  local id x y w h dy offsets=(0 320 349 379 410)
  "$U" settings; sleep 1.5
  id=$(window); read -r x y w h <<< "$(geometry "$id")"
  xdotool mousemove $((x + w / 2)) $((y + h / 2)); sleep 0.3
  for _ in $(seq 15); do xdotool click 5; sleep 0.1; done; sleep 0.5
  for dy in 114 122 130 138; do
    xdotool mousemove $((x + offsets[BAR_LIMITS])) $((y + dy)) click 1; sleep 0.7
    bar_kinds | grep -q limit && break
  done
  "$U" overview; sleep 0.5
  check "Settings › Top bar on $BAR_LIMITS limits" "$(bar_kinds)" "^ ?$BAR_LIMITS \"kind\":\"limit\"$"
}
bar_kinds() { "$U" status --json | grep -o '"kind":"[a-z]*"' | sort | uniq -c | tr -s ' '; }
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
# closeup <picture> <x> <y> <out>: the 40x40 around x,y, four times the size.
closeup() { convert "$1" -crop "40x40+$(( $2 - 20 ))+$(( $3 - 20 ))" +repage -filter point -resize 400% "$4"; }
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

# ---- The providers' marks as a font ----

# As install.sh puts it in: the user's own fonts, and fontconfig told.
install_marks() {
  mkdir -p "$HOME/.local/share/fonts"
  cp /src/fonts/usagetop-marks.otf "$HOME/.local/share/fonts/"
  fc-cache -f "$HOME/.local/share/fonts"
}
# check_marks <what> <desktop> <markup>: with the font in, the bar's text has each
# provider's mark in the font, and no provider's name; fontconfig knows the
# font; and Pango draws its marks from it, not as boxes for characters it has
# no font for: each mark comes out otherwise than with the font hidden from
# fontconfig, when Pango draws its box with the code in it.
check_marks() {
  check "$1: each limit's mark in the marks' font" "$(grep -o 'font_family="usagetop marks"' <<< "$3" | wc -l)" "^$BAR_LIMITS$"
  check "$1: and no provider's name" "$(grep -oE 'Claude|ChatGPT|Kimi|Mistral|Muse|Antigravity' <<< "$3" | tr '\n' ' ')" '^$'
  check "fontconfig has the marks' font" "$(fc-list | grep -i 'usagetop marks' | sed 's/:.*//')" '/\.local/share/fonts/usagetop-marks\.otf$'
  local code with without differ
  # Fontconfig as it is, less the user's fonts: where the marks' font is.
  printf '<fontconfig><dir>/usr/share/fonts</dir><cachedir>/tmp/fc-hidden</cachedir></fontconfig>' > /tmp/fonts-hidden.conf
  for code in E000 E001 E002 E003 E004 E005; do
    printf '<span font_family="usagetop marks">&#x%s;</span>' "$code" > /tmp/mark.txt
    pango-view -q --markup --dpi 96 --font "Noto Sans 30" --margin 4 -o "/tmp/mark-$code.png" /tmp/mark.txt
    FONTCONFIG_FILE=/tmp/fonts-hidden.conf pango-view -q --markup --dpi 96 --font "Noto Sans 30" --margin 4 -o "/tmp/mark-$code-hidden.png" /tmp/mark.txt
    with=$(convert "/tmp/mark-$code.png" -trim -format '%wx%h' info:)
    without=$(convert "/tmp/mark-$code-hidden.png" -trim -format '%wx%h' info:)
    convert "/tmp/mark-$code.png" -background white -extent 120x80 /tmp/differ-a.png
    convert "/tmp/mark-$code-hidden.png" -background white -extent 120x80 /tmp/differ-b.png
    differ=$(compare -metric AE -fuzz 20% /tmp/differ-a.png /tmp/differ-b.png null: 2>&1 | cut -d' ' -f1)
    check "Pango draws U+$code from the font, not as a box ($with with it, $without without)" "$differ" '^[1-9][0-9]{2,}'
  done
  montage /tmp/mark-E00?.png /tmp/mark-E00?-hidden.png -tile 6x2 -geometry +4+4 "/out/$2-marks.png" 2> /dev/null || true
}
