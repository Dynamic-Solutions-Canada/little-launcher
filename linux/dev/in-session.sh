#!/usr/bin/env bash
set -euo pipefail
source /work/bus.env
export LANG=C.UTF-8 DISPLAY=:77 XDG_RUNTIME_DIR=/tmp/ll-runtime WAYLAND_DISPLAY=wayland-poc XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=KDE XDG_MENU_PREFIX=plasma- QT_QPA_PLATFORM=wayland LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe QT_QUICK_BACKEND=software
exec "$@"
