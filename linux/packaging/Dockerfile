# Debian 12 sets the glibc 2.36 baseline. Only this container receives build dependencies.
FROM node@sha256:be8204d4bd327363357f532187b752ff62759c31bedf98e9e18fc08c9cbd605c
ENV DEBIAN_FRONTEND=noninteractive PIP_DISABLE_PIP_VERSION_CHECK=1
RUN apt-get update -qq && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv patchelf squashfs-tools ca-certificates \
    libsecret-tools libnotify4 libglib2.0-bin libgtk-3-0 libnss3 libasound2 libgbm1 libxss1 \
    libatk-bridge2.0-0 libxcb-cursor0 libxkbcommon-x11-0 libegl1 libopengl0 libgl1 libglx0 \
    libxcb-glx0 libxcb-icccm4 libxcb-image0 libxcb-keysyms1 libxcb-render-util0 \
    libxcb-shape0 libxcb-xinerama0 libxcb-xkb1 libibus-1.0-5 \
    libwayland-client0 libwayland-cursor0 libwayland-egl1 \
    qdbus-qt6 desktop-file-utils \
    && rm -rf /var/lib/apt/lists/*
RUN python3 -m pip install --break-system-packages --no-cache-dir --target /opt/little-launcher-site \
    PySide6-Essentials==6.11.2 shiboken6==6.11.2 dbus-next==0.2.3
WORKDIR /source
