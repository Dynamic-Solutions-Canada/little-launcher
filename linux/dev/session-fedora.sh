#!/usr/bin/env bash
# A virtual KDE session, using only the explicitly passed render device.
set -euo pipefail
export LANG=C.UTF-8
export XDG_RUNTIME_DIR=/tmp/ll-runtime
export XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=KDE KDE_FULL_SESSION=true
export XDG_MENU_PREFIX=plasma- QT_QPA_PLATFORM=wayland WAYLAND_DISPLAY=wayland-poc
export QT_QPA_PLATFORMTHEME=kde
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"
printf 'export DBUS_SESSION_BUS_ADDRESS=%q\n' "$DBUS_SESSION_BUS_ADDRESS" > /work/bus.env
# Grant input and screenshot access only to the interpreter in this disposable session.
mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/little-launcher-fixture.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Little Launcher private QA
Exec=/usr/bin/python3
NoDisplay=true
X-KDE-Wayland-Interfaces=org_kde_kwin_fake_input
X-KDE-DBUS-Restricted-Interfaces=org.kde.KWin.ScreenShot2
DESKTOP
python3 -m pywayland.scanner -i /source/linux/dev/protocols/fake-input.xml -o /work/protocols
kbuildsycoca6 --noincremental > /work/service-index.log 2>&1
pipewire > /work/pipewire.log 2>&1 &
pipewire_ready=false
for attempt in $(seq 1 100); do
    if python3 -c 'import os,socket; connection=socket.socket(socket.AF_UNIX); connection.connect(os.environ["XDG_RUNTIME_DIR"]+"/pipewire-0"); connection.close()' >/dev/null 2>&1; then
        pipewire_ready=true
        break
    fi
    sleep 0.1
done
test "$pipewire_ready" = true
wireplumber > /work/wireplumber.log 2>&1 &
kwin_wayland --virtual --width 1280 --height 800 --socket wayland-poc --no-lockscreen > /work/kwin.log 2>&1 &
for attempt in $(seq 1 100); do
    # Socket files survive a stopped container. Probe this new session's actual service.
    qdbus-qt6 org.kde.KWin /KWin org.kde.KWin.supportInformation >/dev/null 2>&1 && break
    sleep 0.1
done
qdbus-qt6 org.kde.KWin /KWin org.kde.KWin.supportInformation >/dev/null
dbus-update-activation-environment WAYLAND_DISPLAY XDG_RUNTIME_DIR XDG_SESSION_TYPE XDG_CURRENT_DESKTOP XDG_MENU_PREFIX QT_QPA_PLATFORM QT_QPA_PLATFORMTHEME
/usr/libexec/xdg-desktop-portal-kde > /work/portal-kde.log 2>&1 &
/usr/libexec/xdg-desktop-portal --replace > /work/portal.log 2>&1 &
qdbus-qt6 org.kde.kded6 /kded org.kde.kded6.loadModule statusnotifierwatcher
plasmashell --no-respawn > /work/plasma.log 2>&1 &
wait
