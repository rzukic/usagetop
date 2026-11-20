# KDE Plasma on X11 on a virtual display: plasmashell with its tray, and KWin
# compositing with Mesa's software OpenGL (llvmpipe). Debian trixie, for
# Plasma 6.3: from 6.8 on, Plasma has no X11 session. Used by
# desktops-check.sh; the app itself is mounted in, not built here.
FROM debian:trixie-slim
RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      plasma-desktop plasma-workspace kwin-x11 kded6 dbus dbus-x11 xvfb libgl1-mesa-dri libglx-mesa0 \
      xdotool x11-utils x11-apps imagemagick fonts-noto-core breeze-icon-theme procps \
 && rm -rf /var/lib/apt/lists/* /usr/share/doc /usr/share/man
