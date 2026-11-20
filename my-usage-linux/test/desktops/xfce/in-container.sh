#!/bin/bash
# Xfce on X11: xfwm4 compositing, and xfce4-panel's status tray. Runs inside
# the container desktops-check.sh starts.
source /desktops/common.sh
export XDG_CURRENT_DESKTOP=XFCE XDG_SESSION_TYPE=x11
W=1600 H=1000

echo "== Xfce (X11, xfwm4 compositing)"
start_x ${W}x$H
# The panel as Xfce sets it up on a first start, without asking, less its
# action buttons, which show the user's name (here root) at its right end.
conf=$HOME/.config/xfce4/xfconf/xfce-perchannel-xml
mkdir -p "$conf"
sed '/<value type="int" value="\(9\|10\)"\/>/d' /etc/xdg/xfce4/panel/default.xml > "$conf/xfce4-panel.xml"
# On the PATH, as the README's Generic Monitor command expects.
mkdir -p "$HOME/bin"; ln -s "$U" "$HOME/bin/usagetop"; export PATH="$HOME/bin:$PATH"
xfsettingsd > /tmp/xfsettingsd.log 2>&1 &
xfwm4 > /tmp/xfwm4.log 2>&1 &
wait_for 10 sh -c 'xprop -root _NET_SUPPORTING_WM_CHECK | grep -q window'
xfdesktop > /tmp/xfdesktop.log 2>&1 &
xfce4-panel > /tmp/panel.log 2>&1 &
# The tray's window: the panel runs it in a process of its own.
tray() {
  for w in $(xwininfo -root -tree | awk '/"wrapper-2.0"/{print $1}'); do
    pid=$(xprop -id "$w" _NET_WM_PID 2> /dev/null | awk '{print $3}')
    [ -n "$pid" ] && grep -q libsystray "/proc/$pid/cmdline" 2> /dev/null && mapped "$w" || continue
    read -r gx gy gw gh <<< "$(geometry "$w")"
    [ "$gy" = 0 ] && echo "$w $gx $gy $gw $gh" && return
  done
}
wait_for 30 test -n "$(tray)"
for p in $(xfconf-query -c xfce4-desktop -l | grep last-image); do
  xfconf-query -c xfce4-desktop -p "$p" -s /usr/share/backgrounds/xfce/xfce-shapes.svg
done
sleep 4
check "doctor sees Xfce, compositing" "$("$U" doctor | tr -s ' ' | tr '\n' ';')" 'desktop XFCE;session X11;.*compositing yes'
read -r _ tray_x _ tray_w _ <<< "$(tray)"

# The providers' marks, as install.sh puts them in.
install_marks
start_app
check "the app starts" "$("$U" status | head -1)" '% left$'
items() { dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems | grep -o '"[^"]*"' | tr '\n' ' '; }
wait_for 10 sh -c "dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems | grep -q StatusNotifierItem"
check "its tray icon is registered with the panel's tray" "$(items)" '^"org\.kde\.StatusNotifierItem-[0-9]+-[0-9]+/StatusNotifierItem" $'
"$U" hide; sleep 2
read -r _ ix iy iw ih <<< "$(tray)"
check "the tray grew by an icon" "$(( iw - tray_w ))" '^(2[0-9]|3[0-2])$'
ix=$(( ix + iw / 2 )) iy=$(( iy + ih / 2 ))
# The tray's newest icon is at its right end.
shot /tmp/after.png; closeup /tmp/after.png "$(( ${ix} + iw / 2 - 14 ))" "$iy" /out/xfce-tray-closeup.png

id=$(window)
xdotool mousemove "$ix" "$iy" click 1
wait_for 5 mapped "$id"; sleep 1
check "a click on the icon opens the panel" "$(xwininfo -id "$id" | grep 'Map State')" 'IsViewable'
read -r x y w h <<< "$(geometry "$id")"
read -r _ area_y _ _ <<< "$(xprop -root _NET_WORKAREA | tr -d ',' | awk '{print $3, $4, $5, $6}')"
# The window has room for a shadow: 30 to each side, 22 above, 38 below.
check "it opens in the corner by the tray, 8 from the right" "$(( W - (x + w - 30) ))" '^8$'
check "and 8 under the panel" "$(( y + 22 - area_y ))" '^8$'
check "with an alpha channel" "$(xwininfo -id "$id" | grep Depth)" '32'
xdotool mousemove 800 500
sleep 0.5; shot /out/xfce-overview.png

# Mapped windows on the screen, leaving out the panel's helpers kept off it.
toplevels() { xwininfo -root -children | awk '/^ +0x/ && $0 !~ /\+-[0-9]+\+-[0-9]+ *$/ {print $1}' | while read -r w; do mapped "$w" && echo "$w"; done | sort; }
toplevels > /tmp/before
xdotool mousemove "$ix" "$iy" click 3; sleep 1.5
menu=$(toplevels | comm -13 /tmp/before - | grep -v "$id" | head -1)
check "a right click opens its menu" "${menu:-none}" '^0x'
read -r mx my mw mh <<< "$(geometry "$menu")"
check "the menu is under the icon" "$(( mx <= ix && ix <= mx + mw && my >= area_y - 8 )): ${mw}x$mh+$mx+$my, the icon at $ix,$iy" '^1:'
shot /out/xfce-menu.png
xdotool key Escape; sleep 1

# ---- The Generic Monitor plugin, as the README sets it up ----

"$U" overview; "$U" hide; sleep 1
shot /tmp/before-genmon.png
# Just before the tray, with no label, every 30 s, in the panel's own font;
# put in while the panel is not running, which would write its own over it.
xfce4-panel -q; sleep 2
mkdir -p "$HOME/.config/xfce4/panel"
printf '%s\n' 'Command=usagetop status --genmon --bar' 'UseLabel=0' 'Text=' 'UpdatePeriod=30000' \
  > "$HOME/.config/xfce4/panel/genmon-19.rc"
xfconf-query -c xfce4-panel -p /plugins/plugin-19 -n -t string -s genmon
ids=()
for p in $(xfconf-query -c xfce4-panel -p /panels/panel-1/plugin-ids | grep -E '^[0-9]+$'); do
  [ "$(xfconf-query -c xfce4-panel -p "/plugins/plugin-$p")" = systray ] && ids+=(-t int -s 19)
  ids+=(-t int -s "$p")
done
xfconf-query -c xfce4-panel -p /panels/panel-1/plugin-ids "${ids[@]}"
xfce4-panel > /tmp/panel-again.log 2>&1 &
sleep 3
wait_for 30 test -n "$(tray)"
check "the panel has it, just before the tray" "$(xfconf-query -c xfce4-panel -p /panels/panel-1/plugin-ids | grep -E '^[0-9]+$' | tr '\n' ' ')" '19 6 '
text=$(usagetop status --genmon | sed -n 's/.*<txt>\(.*\)<\/txt>.*/\1/p')
echo "      its text: $text"
if [ "$BAR_LIMITS" -gt 0 ]; then
  check "its text: the $BAR_LIMITS limits, each with its name and figure" "$(sed 's/<[^>]*>//g' <<< "$text" | grep -oE '[0-9]+%' | wc -l): $text" "^$BAR_LIMITS: "
  check "the names a little fainter, in Pango markup" "$text" '<span alpha="70%">[A-Za-z0-9]'
  check_marks "its text" xfce "$text"
else
  check "its text: the combined figure" "$text" '^[0-9]+%$'
fi
# What it asks with --bar tells the app that the panel shows the figures now.
wait_for 40 sh -c "! dbus-send --session --print-reply --dest=org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.freedesktop.DBus.Properties.Get string:org.kde.StatusNotifierWatcher string:RegisteredStatusNotifierItems | grep -q StatusNotifierItem"
check "the plugin asks, and the app's tray icon stands aside (--bar)" "$(items)" '^$'
# Where the plugin's text is, from the picture: its plug window keeps its old
# place and size for a while after the panel makes room for the text. In the
# panel's right half nothing else is drawn but the clock, at the end (the
# pager's boxes are flat, the tray is empty now).
# The pointer away from it: it was left on the tray, where the text now is,
# and the plugin would show its hover frame.
xdotool mousemove 700 500; sleep 3; shot /tmp/genmon.png
read -r _ spans <<< "$(python3 /desktops/count-items.py /tmp/genmon.png $((W / 2)) 2 $((W / 2)) 22 9)"
case "$spans" in *" "*) spans=${spans% *} ;; *) spans="" ;; esac
n=$(wc -w <<< "$spans")
gx=${spans%%-*} gw=$(( ${spans##*-} - ${spans%%-*} + 1 ))
echo "      the plugin's text: $spans"
if [ "$BAR_LIMITS" -gt 0 ]; then
  check "the panel shows the $BAR_LIMITS limits" "$n: $spans" "^$BAR_LIMITS:"
else
  check "the panel shows the figure" "$n: $spans" '^1:'
fi
closeup /tmp/genmon.png "$((gx + 24))" 12 /out/xfce-genmon-closeup.png
# The plugin's text across the panel's height, three times the size.
convert /tmp/genmon.png -crop "$((gw + 16))x${area_y}+$((gx - 8))+0" +repage -filter point -resize 300% /out/xfce-bar-closeup.png
# A click on it runs its <txtclick>: usagetop toggle.
xdotool mousemove "$((gx + gw / 2))" 12 click 1
sleep 1; wait_for 5 mapped "$id"; sleep 1
check "a click on it opens the panel" "$(xwininfo -id "$id" | grep 'Map State')" 'IsViewable'
xdotool mousemove 700 500; sleep 1.5
shot /out/xfce-genmon.png
# For the README: the panel's right end with the plugin, and the app's panel under it.
read -r x y w h <<< "$(geometry "$id")"
left=$(( (gx < x + 30 ? gx : x + 30) - 24 ))
convert /out/xfce-genmon.png -crop "$((W - left))x$((y + h - 38 + 24))+$left+0" +repage /out/xfce-bar.png

"$U" quit
wait_for 5 sh -c '! pgrep -x usagetop'
check "quit ends it" "$(pgrep -c -x usagetop)" '^0$'
check "and takes its icon out of the tray" "$(items)" '^$'
sleep 1
check "the plugin then shows a dash" "$(usagetop status --genmon 2>&1; echo " $?")" ' 1$'
finish
