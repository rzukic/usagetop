#!/bin/bash
# KDE Plasma on X11: plasmashell's tray and KWin compositing with software
# OpenGL. Runs inside the container desktops-check.sh starts.
source /desktops/common.sh
export XDG_CURRENT_DESKTOP=KDE XDG_SESSION_TYPE=x11 KDE_FULL_SESSION=true KDE_SESSION_VERSION=6
W=1600 H=1000

echo "== KDE Plasma (X11, KWin with llvmpipe)"
start_x ${W}x$H
kwin_x11 > /tmp/kwin.log 2>&1 &
wait_for 20 qdbus6 org.kde.KWin /Compositor
kded6 > /tmp/kded.log 2>&1 &
wait_for 20 qdbus6 org.kde.kded6
# Plasma's session would load the tray's watcher; here nothing asks for it.
qdbus6 org.kde.kded6 /kded org.kde.kded6.loadModule statusnotifierwatcher > /dev/null
plasmashell > /tmp/plasma.log 2>&1 &
panel() { xwininfo -root -tree | awk '/"plasmashell"/ && $0 ~ /1600x[0-9]+\+0\+0 +\+0\+9[0-9][0-9]/ {print $1; exit}'; }
wait_for 40 test -n "$(panel)"
# KWin leaves its blur off where OpenGL is drawn in software: asked for here,
# as a desktop with a graphics card has it.
qdbus6 org.kde.KWin /Effects org.kde.kwin.Effects.loadEffect blur > /dev/null
sleep 8 # the panel fills in, the desktop draws its wallpaper
check "KWin composites" "$(qdbus6 org.kde.KWin /Compositor org.kde.kwin.Compositing.active 2>/dev/null)" '^true$'
read -r _ bar_y _ _ <<< "$(geometry "$(panel)")"
shot /tmp/before.png

start_app
check "the app starts" "$("$U" status | head -1)" '% left$'
check "doctor sees KDE, compositing, blur asked of KWin" "$("$U" doctor | tr -s ' ' | tr '\n' ';')" 'desktop KDE;session X11;.*compositing yes;blur asked of KWin'
items() { busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems; }
wait_for 10 sh -c "busctl --user get-property org.kde.StatusNotifierWatcher /StatusNotifierWatcher org.kde.StatusNotifierWatcher RegisteredStatusNotifierItems | grep -q StatusNotifierItem"
check "its tray icon is registered" "$(items)" '^as 1 "org\.kde\.StatusNotifierItem-[0-9]+-[0-9]+/StatusNotifierItem"$'
item=$(items | sed 's/.*"\(.*\)\/StatusNotifierItem"/\1/')
# Avalonia gives the tooltip's text as the item's title, which Plasma shows.
check "the icon's tooltip has the figures" "$(busctl --user get-property "$item" /StatusNotifierItem org.kde.StatusNotifierItem Title)" '^s "usagetop .+ [0-9]+% left'

"$U" hide; sleep 3
shot /tmp/after.png
read -r ix iy <<< "$(find_icon /tmp/before.png /tmp/after.png "$bar_y" "$H")"
check "the icon shows in the panel's tray, at the right" "${ix:-none}" '^1[2-5][0-9][0-9]$'
[ -n "$ix" ] && closeup /tmp/after.png "$ix" "$iy" /out/kde-tray-closeup.png

id=$(window)
xdotool mousemove "$ix" "$iy" click 1
wait_for 5 mapped "$id"; sleep 1.5
check "a click on the icon opens the panel" "$(xwininfo -id "$id" | grep 'Map State')" 'IsViewable'
read -r x y w h <<< "$(geometry "$id")"
# The window has room for a shadow: 30 to each side, 22 above, 38 below.
check "it opens in the corner by the tray, 8 from the right" "$(( W - (x + w - 30) ))" '^8$'
# Plasma's panel floats: what it keeps for itself starts below its window's top.
read -r _ _ _ area_h <<< "$(xprop -root _NET_WORKAREA | tr -d ',' | awk '{print $3, $4, $5, $6}')"
check "and 8 above the panel" "$(( area_h - (y + h - 38) ))" '^8$'
check "KWin is asked to blur behind it" "$(xprop -id "$id" _KDE_NET_WM_BLUR_BEHIND_REGION)" '= '
shot /out/kde-overview.png
# Left of the panel, in the room for its shadow, where the shadow has faded
# out: the desktop as it was, not blurred.
check "the blur stays inside the panel" "$(differ /tmp/after.png /out/kde-overview.png "4x600+$((x + 2))+$((y + 60))")" '^0(\.00|$)'

menus() { for w in $(xwininfo -root -tree | awk '/"plasmashell"/{print $1}'); do mapped "$w" && echo "$w"; done | sort; }
menus > /tmp/menus-before
xdotool mousemove "$ix" "$iy" click 3
sleep 1.5
menu=$(menus | comm -13 /tmp/menus-before - | head -1)
check "a right click opens its menu" "${menu:-none}" '^0x'
[ -n "$menu" ] && read -r mx my mw mh <<< "$(geometry "$menu")"
check "the menu is by the icon" "$(( ${mx:-0} <= ix && ix <= ${mx:-0} + ${mw:-0} + 40 && ${my:-0} + ${mh:-0} <= area_h )): ${mw}x$mh+$mx+$my, the icon at $ix,$iy" '^1:'
menu_path=$(busctl --user get-property "$item" /StatusNotifierItem org.kde.StatusNotifierItem Menu | awk '{print $2}' | tr -d '"')
check "it offers Open, Refresh and Quit" "$(busctl --user call "$item" "$menu_path" com.canonical.dbusmenu GetLayout iias -- 0 -1 1 label | grep -o '"[A-Z][a-z]* [a-z]*"\|"Quit"' | tr '\n' ' ')" '"Open usagetop" "Refresh now" "Quit"'
shot /out/kde-menu.png
xdotool key Escape; sleep 1

# An account's page, from the panel's rail: the first account under Overview.
mapped "$id" || "$U" show
sleep 1; read -r x y w h <<< "$(geometry "$id")"
xdotool mousemove $((x + 60)) $((y + 117)) click 1; sleep 1.5
shot /out/kde-account.png

"$U" quit
wait_for 5 sh -c '! pgrep -x usagetop'
check "quit ends it" "$(pgrep -c -x usagetop)" '^0$'
check "and takes its icon out of the tray" "$(items)" '^as 0$'
finish
