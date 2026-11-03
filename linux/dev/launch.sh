#!/usr/bin/env bash
# Start an owned private desktop; no host display, bus, devices, or browser profiles are mounted.
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
engine_dir=${LITTLE_LAUNCHER_ELECTRON_DIST:-$repo_root/linux/browser/node_modules/electron/dist}
if [[ ! -x "$engine_dir/electron" ]]; then
    printf 'Install the locked project engine with npm ci --prefix linux/browser, or set LITTLE_LAUNCHER_ELECTRON_DIST.\n' >&2
    exit 1
fi
runtime_dir=$(mktemp -d /tmp/little-launcher-native.XXXXXX)
container_name="little-launcher-native-${runtime_dir##*.}"
cp "$repo_root"/linux/dev/{start.sh,session.sh,in-session.sh,capture.py,configure-panel.js} "$runtime_dir/"
docker build -f "$repo_root/linux/dev/Dockerfile" -t little-launcher-linux-dev:local "$repo_root/linux"
docker run -d --name "$container_name" --init --memory 4g --memory-swap 4g --cpus 2 \
    --pids-limit 512 --shm-size 512m --cap-drop ALL --security-opt seccomp=unconfined \
    -v "$runtime_dir:/work" -v "$repo_root:/source:ro" -v "$engine_dir:/opt/electron:ro" \
    little-launcher-linux-dev:local sleep infinity > "$runtime_dir/container-id"
printf '%s\n' "$container_name" > "$runtime_dir/container-name"
docker exec -d "$container_name" sh -c 'bash /work/start.sh > /work/session.log 2>&1'
ready=false
for attempt in $(seq 1 100); do
    if [[ -f "$runtime_dir/bus.env" ]] && docker exec "$container_name" /work/in-session.sh \
        /usr/lib/qt6/bin/qdbus org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell.evaluateScript \
        'print(desktops().length);' 2>/dev/null | grep -q '^1'; then
        ready=true
        break
    fi
    sleep 0.2
done
if [[ "$ready" != true ]]; then
    printf 'Private desktop startup failed. Logs retained in %s\n' "$runtime_dir" >&2
    docker stop "$container_name" >/dev/null
    exit 1
fi
docker exec -d "$container_name" /work/in-session.sh sh -c \
    'export QT_QPA_PLATFORMTHEME=kde LITTLE_LAUNCHER_ELECTRON=/opt/electron/electron; exec /source/linux/bin/little-launcher --data-dir /work/app-data --no-autostart > /work/app.log 2>&1'
printf 'Private desktop: %s\nEvidence and data: %s\nStop only this fixture: docker stop %s\n' "$container_name" "$runtime_dir" "$container_name"
