#!/usr/bin/env bash
# Explicit GPU opt-in; never connect this fixture to the host display or session bus.
set -euo pipefail
if [[ $# != 2 || "$1" != --render-device || "$2" != /dev/dri/renderD* ]]; then
    printf 'Usage: %s --render-device /dev/dri/renderD128\n' "$0" >&2
    exit 2
fi
repo_root=$(cd -- "$(dirname -- "$0")/../.." && pwd)
engine_dir=${LITTLE_LAUNCHER_ELECTRON_DIST:-$repo_root/linux/browser/node_modules/electron/dist}
if [[ ! -x "$engine_dir/electron" ]]; then
    printf 'Install the locked project engine or set LITTLE_LAUNCHER_ELECTRON_DIST.\n' >&2
    exit 1
fi
runtime_dir=$(mktemp -d /tmp/little-launcher-fedora.XXXXXX)
container_name="little-launcher-fedora-${runtime_dir##*.}"
cp "$repo_root"/linux/dev/{session-fedora.sh,in-session-fedora.sh,private_input.py,capture_wayland.py} "$runtime_dir/"
docker build -f "$repo_root/linux/dev/Dockerfile.fedora" -t little-launcher-fedora-dev:local "$repo_root/linux"
docker run -d --name "$container_name" --init --memory 4g --memory-swap 4g --cpus 2 \
    --pids-limit 512 --shm-size 512m --cap-drop ALL --security-opt seccomp=unconfined \
    --security-opt label=disable --device="$2" \
    -v "$runtime_dir:/work" -v "$repo_root:/source:ro" \
    little-launcher-fedora-dev:local sleep infinity > "$runtime_dir/container-id"
printf '%s\n' "$container_name" > "$runtime_dir/container-name"
# Docker copies package ownership as root, retaining the correctly configured sandbox helper.
docker cp "$engine_dir" "$container_name:/opt/electron"
docker exec -d "$container_name" sh -c 'dbus-run-session -- bash /work/session-fedora.sh > /work/session.log 2>&1'
ready=false
for attempt in $(seq 1 100); do
    if [[ -f "$runtime_dir/bus.env" ]] && docker exec "$container_name" /work/in-session-fedora.sh \
        qdbus-qt6 org.kde.KWin /KWin org.kde.KWin.supportInformation > "$runtime_dir/compositor-info.txt" 2>/dev/null \
        && docker exec "$container_name" /work/in-session-fedora.sh qdbus-qt6 org.kde.plasmashell \
            /PlasmaShell org.kde.PlasmaShell.evaluateScript 'print(desktops().length);' 2>/dev/null | grep -q '^1' \
        && docker exec "$container_name" /work/in-session-fedora.sh qdbus-qt6 org.kde.StatusNotifierWatcher \
            /StatusNotifierWatcher org.kde.StatusNotifierWatcher.IsStatusNotifierHostRegistered 2>/dev/null | grep -q '^true'; then
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
printf 'Private Fedora desktop: %s\nEvidence: %s\nStop only this fixture: docker stop %s\n' "$container_name" "$runtime_dir" "$container_name"
