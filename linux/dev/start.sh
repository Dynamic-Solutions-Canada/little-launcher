#!/usr/bin/env bash
set -euo pipefail
Xvfb :77 -screen 0 1280x800x24 -nolisten tcp -ac > /work/xvfb.log 2>&1 &
for i in $(seq 1 50); do
  DISPLAY=:77 xdpyinfo >/dev/null 2>&1 && break
  sleep 0.1
done
exec dbus-run-session -- bash /work/session.sh
