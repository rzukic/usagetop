# Xfce on X11 on a virtual display: xfwm4 compositing, xfdesktop, and
# xfce4-panel with its status tray. Used by desktops-check.sh; the app itself is
# mounted in, not built here.
FROM debian:trixie-slim
RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      xfwm4 xfce4-panel xfdesktop4 xfconf xfce4-settings dbus dbus-x11 xvfb \
      xdotool x11-utils x11-apps imagemagick python3 fonts-noto-core adwaita-icon-theme librsvg2-common procps \
 && rm -rf /var/lib/apt/lists/* /usr/share/doc /usr/share/man
