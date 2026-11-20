#!/bin/bash
# Sway with Waybar, on wlroots' headless backend drawn by pixman: the README's
# Waybar module, and the panel as a floating window. The app has no Wayland
# backend of its own: it runs under XWayland. Runs inside the container
# desktops-check.sh starts.
source /desktops/common.sh
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1
export XDG_CURRENT_DESKTOP=sway XDG_SESSION_TYPE=wayland
W=1600 H=1000

echo "== Sway with Waybar (headless Wayland, the app under XWayland)"
mkdir -p "$HOME/.config/waybar" "$HOME/bin"
cp /desktops/sway/waybar.json "$HOME/.config/waybar/config"
cp /desktops/sway/waybar.css "$HOME/.config/waybar/style.css"
# On the PATH, as the README's module expects.
ln -s "$U" "$HOME/bin/usagetop"; export PATH="$HOME/bin:$PATH"
# The providers' marks, as install.sh puts them in.
install_marks
start_bus
sway -c /desktops/sway/config > /tmp/sway.log 2>&1 &
wait_for 20 sh -c 'ls "$XDG_RUNTIME_DIR"/sway-ipc.*.sock'
SWAYSOCK=$(ls "$XDG_RUNTIME_DIR"/sway-ipc.*.sock); export SWAYSOCK WAYLAND_DISPLAY=wayland-1
wait_for 10 swaymsg -t get_outputs
# Out of the way of the panel, whose tooltips would otherwise show.
swaymsg seat seat0 cursor set $((W - 10)) $((H - 10)) > /dev/null

# Started by Sway, as a key binding or the session would: so it has Sway's
# DISPLAY for XWayland.
swaymsg exec 'usagetop --sample > /tmp/app.log 2>&1' > /dev/null
wait_for 20 usagetop status
# XWayland's display, for xdotool to pick the Top bar's limits with.
export DISPLAY=$(ls /tmp/.X11-unix | sed -n 's/^X/:/p' | head -1)
choose_bar_limits
check "the app starts" "$(usagetop status | head -1)" '% left$'
check "doctor sees Sway, Wayland, tiling" "$(usagetop doctor | tr -s ' ' | tr '\n' ';')" 'desktop sway;session Wayland;tiling yes;compositing yes'
waybar_text=$(usagetop status --waybar | jq -r .text)
if [ "$BAR_LIMITS" -gt 0 ]; then
  echo "      Waybar text: $waybar_text"
  n=$(sed 's/<[^>]*>//g' <<< "$waybar_text" | grep -oE '[0-9]+%' | wc -l)
  check "the Waybar form: the limits as text, $BAR_LIMITS or as many as there are" "$(( n >= (BAR_LIMITS < 2 ? BAR_LIMITS : 2) && n <= BAR_LIMITS )): $n" '^1:'
  check_marks "the Waybar text" sway "$waybar_text"
  # The class is the tightest limit's: the first figure.
  first=$(sed 's/<[^>]*>//g' <<< "$waybar_text" | grep -oE '[0-9]+%' | head -1 | tr -d %)
  check "a class to style it by, the tightest limit's, and limits" "$(usagetop status --waybar | jq -r '.class | join(" ")')" \
    "^$( [ "$first" -le 15 ] && echo critical || { [ "$first" -le 40 ] && echo warning || echo normal; }) limits\$"
else
  check "the Waybar form: the figure as text" "$waybar_text" '^[0-9]+%$'
  check "a class to style it by" "$(usagetop status --waybar | jq -r .class)" '^(normal|warning|critical)$'
fi
check "the figures in its tooltip" "$(usagetop status --waybar | jq -r .tooltip | head -1)" '^[0-9]+% left$'

swaymsg exec waybar > /dev/null
wait_for 15 sh -c 'swaymsg -t get_tree | grep -q "\"name\": \"waybar\"" || pgrep -x waybar'
items() { dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems 2> /dev/null | grep -o '"[^"]*"' | tr '\n' ' '; }
wait_for 10 sh -c "dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems | grep -q StatusNotifierItem"
# The module asks with --bar, as the README has it: the bar shows the figures,
# so the app's tray icon stands aside.
wait_for 20 sh -c "! dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems | grep -q StatusNotifierItem"
check "the module asks, and the app's tray icon stands aside (--bar)" "$(items)" '^$'
check "Waybar keeps running" "$(pgrep -c -x waybar)" '^1$'
sleep 1; grim /tmp/waybar.png
# The module's text, in the middle of the bar: as many things apart as it
# shows limits, each a name and a figure, and none as long as its markup
# would be if it were shown as it is ("Kimi <span alpha=...").
read -r n spans <<< "$(python3 /desktops/count-items.py /tmp/waybar.png 300 0 1000 30 9)"
# The module's text across the bar's height, three times the size.
convert /tmp/waybar.png -crop "$(( ${spans##*-} - ${spans%%-*} + 17 ))x30+$(( ${spans%%-*} - 8 ))+0" +repage -filter point -resize 300% /out/sway-bar-closeup.png
widest=0; for span in $spans; do a=${span%-*} b=${span#*-}; [ $((b - a)) -gt $widest ] && widest=$((b - a)); done
if [ "$BAR_LIMITS" -gt 0 ]; then
  check "Waybar draws the $BAR_LIMITS limits, the markup taken as such" "$n $((widest < 170)): $spans" "^$BAR_LIMITS 1:"
else
  check "Waybar draws the figure" "$n: $spans" '^1:'
fi

panel() { swaymsg -t get_tree | jq -c '.. | objects | select(.name? == "usagetop" and .shell? != null)'; }
usagetop hide; sleep 1
check "hide takes the panel off the screen" "$(panel | jq -r .visible)" '^(false)?$'
# What the module's on-click runs: Waybar can't be clicked here, for a headless
# Sway has no pointer to click with.
usagetop toggle
wait_for 5 sh -c 'swaymsg -t get_tree | grep -q "\"name\": \"usagetop\""'; sleep 1
p=$(panel)
check "toggle opens the panel" "$(jq -r .visible <<< "$p")" '^true$'
check "as a floating window" "$(jq -r .type <<< "$p")" '^floating_con$'
check "under XWayland" "$(jq -r .shell <<< "$p")" '^xwayland$'
check "with the class usagetop" "$(jq -r .window_properties.class <<< "$p")" '^usagetop$'
read -r x y w h <<< "$(jq -r '.rect | "\(.x) \(.y) \(.width) \(.height)"' <<< "$p")"
check "centred" "$(( x + w / 2 ))" "^$(( W / 2 ))$"
check "clear of the bar" "$(( y + 22 >= 30 ))" '^1$'
# Moved under the bar's right end, as the README's rule for it does. Out of
# the middle, too, where XWayland keeps a pointer that no one moves here.
swaymsg '[class="usagetop"] move position 1108 16' > /dev/null; sleep 1.5
# Its position counts from the workspace's corner, under Waybar's 30.
check "a rule of the user's can move it" "$(panel | jq -r '.rect | "\(.x),\(.y)"')" '^1108,46$'
grim /out/sway-waybar.png
# For the README: the bar's middle, with the limits, and the panel under
# them, 6 below the bar, as a rule could put it.
swaymsg "[class=\"usagetop\"] move position $(( W / 2 - w / 2 )) 6" > /dev/null; sleep 1.5
grim /out/sway-bar-full.png
read -r x y w h <<< "$(jq -r '.rect | "\(.x) \(.y) \(.width) \(.height)"' <<< "$(panel)")"
first=${spans%%-*} last=${spans##*-}
left=$(( (first < x ? first : x) - 24 )) right=$(( (last > x + w ? last : x + w) + 24 ))
convert /out/sway-bar-full.png -crop "$((right - left))x$((y + h + 24))+$left+0" +repage /out/sway-bar.png

usagetop settings; sleep 1.5
grim /out/sway-settings.png
usagetop toggle; sleep 1
check "toggle puts it away again" "$(panel | jq -r .visible)" '^(false)?$'

usagetop quit
wait_for 5 sh -c '! pgrep -x usagetop'
check "quit ends it" "$(pgrep -c -x usagetop)" '^0$'
check "nothing on its error output" "$(wc -c < /tmp/app.log)" '^0$'
finish
