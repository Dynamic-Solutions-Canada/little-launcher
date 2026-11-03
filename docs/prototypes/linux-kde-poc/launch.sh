#!/usr/bin/env bash
# THROWAWAY: starts a private container display, never the host's desktop session.
set -euo pipefail
source_dir=$(cd -- "$(dirname -- "$0")" && pwd)
runtime_dir=$(mktemp -d /tmp/ll-kde-poc.XXXXXX)
container_name="ll-kde-poc-$(basename "$runtime_dir" | cut -d. -f2)"
for file in Dockerfile .dockerignore package.json package-lock.json main.cjs preload.cjs renderer.js shortcuts.html web-shell.html start.sh session.sh in-session.sh observe.js anchor.js capture.py experiment.py summarize.py configure-panel.js; do
  cp -- "$source_dir/$file" "$runtime_dir/$file"
done
chmod +x "$runtime_dir/"*.sh
if [[ -n "${LL_POC_ELECTRON_DIST:-}" ]]; then
  electron_dist=$(realpath -- "$LL_POC_ELECTRON_DIST")
else
  npm ci --prefix "$runtime_dir" --cache "$runtime_dir/npm-cache" --no-audit --no-fund
  electron_config_cache="$runtime_dir/electron-cache" node "$runtime_dir/node_modules/electron/install.js"
  electron_dist="$runtime_dir/node_modules/electron/dist"
fi
docker build -t little-launcher-kde-poc:20261027 "$runtime_dir" > "$runtime_dir/build.log" 2>&1
docker run -d --name "$container_name" --init --memory 4g --memory-swap 4g --cpus 2 --pids-limit 512 --shm-size 512m --cap-drop ALL --security-opt seccomp=unconfined \
  -v "$runtime_dir:/work" -v "$electron_dist:/opt/electron:ro" little-launcher-kde-poc:20261027 sleep infinity
printf '%s\n' "$container_name" > "$runtime_dir/container-name"
trap 'status=$?; if ((status != 0)); then printf "Probe failed. Logs: %s\n" "$runtime_dir"; docker stop "$container_name" >/dev/null 2>&1 || true; fi' EXIT
docker exec -d "$container_name" sh -c 'exec bash /work/start.sh > /work/session.log 2>&1'
ready=false
for attempt in $(seq 1 120); do
  if [[ -f "$runtime_dir/bus.env" ]] && docker exec "$container_name" /work/in-session.sh /usr/lib/qt6/bin/qdbus org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell.evaluateScript 'print(desktops().length);' 2>/dev/null | rg -q '^1'; then
    ready=true
    break
  fi
  sleep 0.25
done
[[ "$ready" == true ]]
docker exec "$container_name" /work/in-session.sh /usr/lib/qt6/bin/qdbus org.kde.plasmashell /PlasmaShell org.kde.PlasmaShell.evaluateScript "$(cat "$runtime_dir/configure-panel.js")"
docker exec "$container_name" /work/in-session.sh /usr/lib/qt6/bin/qdbus org.kde.KWin /Scripting org.kde.kwin.Scripting.loadScript /work/observe.js ll-observe
if [[ "${1:-}" != '--baseline' && "${1:-}" != '--regular-window' ]]; then
  docker exec "$container_name" /work/in-session.sh /usr/lib/qt6/bin/qdbus org.kde.KWin /Scripting org.kde.kwin.Scripting.loadScript /work/anchor.js ll-anchor
fi
docker exec "$container_name" /work/in-session.sh /usr/lib/qt6/bin/qdbus org.kde.KWin /Scripting org.kde.kwin.Scripting.start
docker exec "$container_name" python3 -c 'from PIL import Image,ImageDraw,ImageFont; font=ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",24); [(lambda im,letter,name: (ImageDraw.Draw(im).text((7,0),letter,font=font,fill="white"),im.save("/work/"+name+".png")))(Image.new("RGBA",(32,32),color),letter,name) for letter,name,color in [("S","shortcuts","#0b96be"),("W","web","#d88b14")]]'
window_mode=flyout
if [[ "${1:-}" == '--regular-window' ]]; then window_mode=regular; fi
docker exec -d -e LL_POC_WINDOW_MODE="$window_mode" "$container_name" /work/in-session.sh sh -c 'unset ELECTRON_RUN_AS_NODE; exec /opt/electron/electron --ozone-platform=wayland /work/main.cjs > /work/electron.log 2>&1'
ready=false
for attempt in $(seq 1 80); do
  if [[ -f "$runtime_dir/events.jsonl" ]] && rg -q '"event":"ready"' "$runtime_dir/events.jsonl"; then ready=true; break; fi
  sleep 0.25
done
[[ "$ready" == true ]]
docker exec "$container_name" python3 /work/capture.py /work/desktop.png
printf '\nPrivate KDE PoC is running.\nRuntime and logs: %s\nContainer: %s\nStop only this PoC: docker stop %s\n' "$runtime_dir" "$container_name" "$container_name"
