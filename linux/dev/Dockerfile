# Distro Qt matches the desktop integration ABI; never mix pip Qt wheels with these plugins.
FROM debian@sha256:62bd423aeee912639ca8ee681c2b762cb3e25137e0a92b20be455898a9f034ad
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update -qq && apt-get install -y --no-install-recommends \
    kwin-wayland plasma-workspace plasma-desktop qdbus-qt6 \
    xvfb xdotool x11-utils dbus-x11 python3 python3-pil python3-xlib \
    python3-pyside6.qtwidgets python3-pyside6.qtnetwork python3-pyside6.qtdbus \
    python3-pyside6.qtwebenginewidgets python3-pyside6.qtwebenginecore python3-dbus-next \
    plasma-integration qt6-gtk-platformtheme pipewire wireplumber \
    xdg-desktop-portal xdg-desktop-portal-kde \
    libgtk-3-0 libnss3 libasound2t64 libgbm1 libxss1 libatk-bridge2.0-0 \
    ca-certificates curl fonts-dejavu-core libglib2.0-bin \
    && dpkg-query -W > /package-versions.txt \
    && rm -rf /var/lib/apt/lists/*
# KWin's optional scheduling capability prevents execution under cap-drop ALL; remove it here.
RUN setcap -r /usr/bin/kwin_wayland && useradd --uid 1000 --create-home --shell /bin/bash probe
COPY desktop/*.desktop /usr/share/applications/
COPY resources/little-launcher.png /usr/share/icons/hicolor/256x256/apps/io.github.RyanEwen.LittleLauncher.png
USER probe
WORKDIR /source/linux
