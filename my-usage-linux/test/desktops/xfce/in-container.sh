#!/bin/bash
# Xfce on X11: xfwm4 compositing, and xfce4-panel's status tray. Runs inside
# the container desktops-check.sh starts.
source /desktops/common.sh
export XDG_CURRENT_DESKTOP=XFCE XDG_SESSION_TYPE=x11
W=1600 H=1000

echo "== Xfce (X11, xfwm4 compositing)"
start_x ${W}x$H
# The panel as Xfce sets it up on a first start, without asking.
conf=$HOME/.config/xfce4/xfconf/xfce-perchannel-xml
mkdir -p "$conf"
cp /etc/xdg/xfce4/panel/default.xml "$conf/xfce4-panel.xml"
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

"$U" quit
wait_for 5 sh -c '! pgrep -x usagetop'
check "quit ends it" "$(pgrep -c -x usagetop)" '^0$'
check "and takes its icon out of the tray" "$(items)" '^$'
finish
