# A Linux with a virtual display and two kinds of desktop to try the app on:
# i3 with nothing compositing, and openbox with picom compositing. Used by
# desktop-check.sh; the app itself is mounted in, not built here.
FROM debian:bookworm-slim
RUN apt-get update -qq \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends \
      xvfb x11-utils x11-apps i3-wm openbox picom procps \
      libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 libxext6 \
 && rm -rf /var/lib/apt/lists/*
