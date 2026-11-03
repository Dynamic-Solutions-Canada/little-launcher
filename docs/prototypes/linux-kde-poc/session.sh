#!/usr/bin/env bash
set -euo pipefail
export LANG=C.UTF-8
export DISPLAY=:77
export XDG_RUNTIME_DIR=/tmp/ll-runtime
export XDG_SESSION_TYPE=wayland
export XDG_CURRENT_DESKTOP=KDE
export KDE_FULL_SESSION=true
export QT_QPA_PLATFORM=wayland
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export QT_QUICK_BACKEND=software
export WAYLAND_DISPLAY=wayland-poc
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
printf 'export DBUS_SESSION_BUS_ADDRESS=%q\n' "$DBUS_SESSION_BUS_ADDRESS" > /work/bus.env
kwin_wayland --x11-display :77 --width 1280 --height 800 --socket wayland-poc --no-lockscreen --no-global-shortcuts --no-kactivities > /work/kwin.log 2>&1 &
echo $! > /work/kwin.pid
for i in $(seq 1 100); do
  test -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" && break
  sleep 0.1
done
test -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY"
dbus-update-activation-environment DISPLAY WAYLAND_DISPLAY XDG_RUNTIME_DIR XDG_SESSION_TYPE XDG_CURRENT_DESKTOP QT_QPA_PLATFORM LIBGL_ALWAYS_SOFTWARE QT_QUICK_BACKEND
/usr/lib/x86_64-linux-gnu/libexec/kactivitymanagerd > /work/activities.log 2>&1 &
/usr/lib/qt6/bin/qdbus org.kde.kded6 /kded org.kde.kded6.loadModule statusnotifierwatcher
plasmashell --no-respawn > /work/plasma.log 2>&1 &
echo $! > /work/plasma.pid
wait
