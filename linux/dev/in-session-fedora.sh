#!/usr/bin/env bash
set -euo pipefail
source /work/bus.env
export LANG=C.UTF-8 XDG_RUNTIME_DIR=/tmp/ll-runtime XDG_SESSION_TYPE=wayland
export XDG_CURRENT_DESKTOP=KDE XDG_MENU_PREFIX=plasma- QT_QPA_PLATFORM=wayland
export QT_QPA_PLATFORMTHEME=kde WAYLAND_DISPLAY=wayland-poc
exec "$@"
