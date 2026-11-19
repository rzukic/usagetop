#!/bin/bash
# Runs inside the test container: /app is the published app, /out takes the
# pictures. Prints one line per check and leaves with the number that failed.
cd /tmp
export HOME=/tmp/home XDG_RUNTIME_DIR=/tmp/run
mkdir -p "$HOME/.config/i3" "$XDG_RUNTIME_DIR"; chmod 700 "$XDG_RUNTIME_DIR"
printf 'font pango:monospace 8\n' > "$HOME/.config/i3/config"
U=/app/usagetop
failed=0

check() { # check <what> <got> <wanted, as a grep -E pattern>
  if printf '%s' "$2" | grep -Eq -- "$3"; then echo "ok    $1"
  else echo "FAIL  $1: got '$2', wanted '$3'"; failed=$((failed + 1)); fi
}
window() { xwininfo -root -tree | grep '"usagetop"' | head -1 | awk '{print $1}'; }
geometry() { xwininfo -id "$1" | awk '/Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width/{w=$2} /Height/{h=$2} END{print x, y, w, h}'; }
start() { ("$U" --sample > /tmp/app.log 2>&1 &); sleep 5; }
stop() { "$U" quit; sleep 1.5; }

echo "== without a display"
check "version" "$("$U" --version)" '^[0-9]+\.[0-9]+\.[0-9]+'
"$U" status > /tmp/status.txt 2>&1; check "status with no copy running leaves with 1" "$?" '^1$'

echo "== i3: tiling, nothing compositing"
Xvfb :99 -screen 0 1600x1000x24 > /dev/null 2>&1 & xvfb=$!
export DISPLAY=:99 XDG_CURRENT_DESKTOP=i3; sleep 1
"$U" --sample --snapshot /out/pages > /dev/null 2>&1
check "pages drawn to files" "$(ls /out/pages 2>/dev/null | wc -l)" '^1[0-9]$'
i3 > /dev/null 2>&1 & wm=$!; sleep 1.5
check "doctor sees no compositing" "$("$U" doctor | grep compositing)" 'no:'
start
id=$(window); read -r x y w h <<< "$(geometry "$id")"
check "a window opened" "$id" '^0x'
check "it is a utility window" "$(xprop -id "$id" _NET_WM_WINDOW_TYPE)" '_NET_WM_WINDOW_TYPE_UTILITY'
check "its class is usagetop" "$(xprop -id "$id" WM_CLASS)" '"usagetop", "usagetop"'
check "i3 floats it" "$(i3-msg -t get_tree | grep -o '"floating":"auto_on"' | head -1)" 'auto_on'
check "square, without room for a shadow" "$w" '^424$'
check "centred" "$x" '^5[0-9][0-9]$'
check "its foot is on the screen" "$(( y + h <= 1000 ))" '^1$'
check "status answers" "$("$U" status | head -1)" '% left$'
check "the Waybar form is JSON with a class" "$("$U" status --waybar)" '^\{"text":"[0-9]+%".*"class":"(normal|warning|critical)"'
"$U" toggle; sleep 1; check "toggle puts it away" "$(xwininfo -id "$id" | grep 'Map State')" 'IsUnMapped'
"$U" toggle; sleep 1; check "toggle brings it back" "$(xwininfo -id "$id" | grep 'Map State')" 'IsViewable'
"$U" --sample; check "a second copy hands over and leaves" "$?" '^0$'
check "one copy is running" "$(pgrep -c -x usagetop)" '^1$'
stop
check "quit ends it" "$(pgrep -c -x usagetop)" '^0$'
check "it leaves its socket tidy" "$(ls "$XDG_RUNTIME_DIR" | grep -c usagetop.sock)" '^0$'
check "nothing on its error output" "$(wc -c < /tmp/app.log)" '^0$'
kill $wm $xvfb; sleep 1

echo "== openbox and picom: floating, compositing"
Xvfb :98 -screen 0 1600x1000x24 > /dev/null 2>&1 &
export DISPLAY=:98; unset XDG_CURRENT_DESKTOP; sleep 1
openbox > /dev/null 2>&1 &
picom --backend xrender > /dev/null 2>&1 &
sleep 2
check "doctor sees compositing" "$("$U" doctor | grep compositing)" 'yes'
start
id=$(window); read -r x y w h <<< "$(geometry "$id")"
check "a window with an alpha channel" "$(xwininfo -id "$id" | grep Depth)" '32'
# The window's last pixel is in the shadow's room, outside the panel: fully see-through.
check "see-through outside the panel" "$(xwd -id "$id" -silent | tail -c 4 | od -An -tx1 | tr -d ' ')" '^00000000$'
check "wide enough for the panel and its shadow" "$w" '^484$'
check "8 from the right edge" "$(( 1600 - (x + w - 30) ))" '^8$'
check "8 from the top" "$(( y + 22 ))" '^8$'
stop

export XDG_CURRENT_DESKTOP=KDE
start
id=$(window); read -r x y w h <<< "$(geometry "$id")"
check "on a desktop with its panel at the bottom, 8 from the bottom" "$(( 1000 - (y + h - 38) ))" '^8$'
stop

echo "== $failed failed"
exit $failed
