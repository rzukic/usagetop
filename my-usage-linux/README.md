# usagetop for Linux

A tray icon and the Mac app's panel, on the core the Windows app and the server
share (`my-usage-win/src/AiUsage.Core`), drawn with [Avalonia](https://avaloniaui.net).

**Not released yet.** What is here shows sample data: the overview and an
account's page, light and dark, a tray icon, and the command line. Adding
accounts, signing in, a server, Settings, and a package that updates itself are
still to come.

## Building and running

Needs the .NET SDK, 9 or newer. It also runs on macOS and Windows, which is how
the panel is worked on away from a Linux desktop.

```sh
cd my-usage-linux/src/AiUsage.Linux
dotnet run -- --sample                         # the app, with made-up usage
dotnet run -- --sample --snapshot /tmp/pages   # every page as a PNG, no window
dotnet publish -c Release -r linux-x64 --self-contained -o out   # or linux-arm64
```

The binary is `usagetop`, and it is also its command line:

```
usagetop                 start, and show the panel (a second copy shows the first one's)
usagetop --hidden        start as the tray icon alone: for autostart
usagetop toggle          show the panel, or put it away
usagetop show | hide | refresh | quit
usagetop status          the reading as text; --json for a script, --waybar for Waybar
usagetop doctor          what this desktop offers, as the app sees it
```

## On different desktops

Linux has no one way to show a tray icon, float a window or blur behind it, so
the app looks at where it is and takes what is on offer:

- **A tray** (KDE, Xfce, Cinnamon, GNOME with the AppIndicator extension, most
  bars): the level as a ring; the figures are in its tooltip. A click toggles
  the panel where the desktop passes clicks on, and the menu has "Open" where it
  does not.
- **No tray:** bind a key to `usagetop toggle`, and let the bar run
  `usagetop status`. For Waybar:

  ```json
  "custom/usagetop": {
    "exec": "usagetop status --waybar",
    "return-type": "json",
    "interval": 60,
    "on-click": "usagetop toggle"
  }
  ```

  The module's class is `normal`, `warning` or `critical`, to style it by.
- **Tiling window managers** (i3, Sway, Hyprland, bspwm and the like): the panel
  is a utility window, which they float, and its class is `usagetop` for rules
  of your own, such as `for_window [class="usagetop"] move position 1400 40`.
  It stays open when the focus moves, since there the focus follows the
  pointer; `usagetop toggle` or Escape puts it away.
- **Blur:** asked of KWin on KDE. Elsewhere it is up to the compositor.
- **Nothing compositing** (a bare i3 or bspwm without picom): no window can be
  see-through, so the panel is drawn square and opaque rather than with black
  corners.

## Testing

`./test/desktop-check.sh` needs Docker. It publishes the app and runs it in a
container on a virtual display, under i3 with nothing compositing and under
openbox with picom, and checks the window it opens, where it is put, and the
command line. A tray, KWin's blur and Wayland need a real desktop and are not
covered; none of those has been tried yet.
