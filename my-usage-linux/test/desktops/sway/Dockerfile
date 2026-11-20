# Sway with Waybar, headless (wlroots' headless backend, drawn by pixman), and
# XWayland for X11 apps. Used by desktops-check.sh; the app itself is mounted
# in, not built here.
FROM debian:trixie-slim
RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      sway waybar grim xwayland jq x11-utils xdotool dbus fonts-noto-core procps imagemagick python3 fontconfig pango1.0-tools \
 && rm -rf /var/lib/apt/lists/* /usr/share/doc /usr/share/man
