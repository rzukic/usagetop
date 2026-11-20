# usagetop for Linux

A tray icon and the Mac app's panel, on the core the Windows app and the server
share (`my-usage-win/src/AiUsage.Core`), drawn with [Avalonia](https://avaloniaui.net).

**Not released yet.** What is here shows your usagetop server's reading (or
sample data): the overview, account pages and charts, Settings, the tray icon,
the GNOME top bar extension, self-updates, and the command line.

## Installing and updates

Run `./install.sh` from an unpacked `usagetop-linux-<arch>.tar.gz` release
archive (or `./package.sh` to build the archives locally). It installs the app
into `~/.local/share/usagetop/app`, links `~/.local/bin/usagetop`, writes a
desktop entry, and on GNOME installs and enables the top bar extension (unless
`~/.local/share/gnome-shell/extensions/usagetop@rzukic.github.io` is a symlink
to a checkout). A release's archives are built by `release.ps1`;
`./test/update-check.sh` installs and updates a copy in Docker.

An installed copy (in `~/.local/share/usagetop`, `~/Applications/usagetop`, or a
user-writable `/opt/usagetop`) checks GitHub releases a minute after startup and
every six hours, or when asked in Settings › Updates. New releases are verified
by SHA-256 and `usagetop --version` before staging, and go in on "Restart to
update", on quit, or on the next start. Builds run from a build folder or
installed under `/usr` do not update themselves.

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
command line.

`./test/desktops-check.sh [<folder>] [kde|xfce|sway ...]` goes further, with
a throwaway container per desktop: KDE Plasma 6.3 on X11 with KWin compositing
in software, Xfce, and Sway with Waybar (headless, the app under XWayland). It
checks the tray icon, that a click on it opens the panel by the tray, its
menu, KWin's blur, the Waybar module and `usagetop toggle`, and leaves
pictures of each, drawn from the sample data, in `<folder>`. The images it
builds are kept for the next run; `--clean` removes them. Plasma's X11 session
is gone from 6.8 on, so it runs on Debian trixie's 6.3.
