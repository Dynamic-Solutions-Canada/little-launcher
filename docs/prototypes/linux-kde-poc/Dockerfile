FROM debian@sha256:62bd423aeee912639ca8ee681c2b762cb3e25137e0a92b20be455898a9f034ad
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update -qq && apt-get install -y --no-install-recommends \
    kwin-wayland plasma-workspace plasma-desktop qdbus-qt6 \
    xvfb xdotool x11-utils dbus-x11 python3 python3-pil python3-xlib \
    libgtk-3-0 libnss3 libasound2t64 libgbm1 libxss1 libatk-bridge2.0-0 \
    ca-certificates curl fonts-dejavu-core \
    && dpkg-query -W > /package-versions.txt \
    && rm -rf /var/lib/apt/lists/*
RUN setcap -r /usr/bin/kwin_wayland
RUN useradd --uid 1000 --create-home --shell /bin/bash probe
USER probe
WORKDIR /work
