# Throwaway KDE launcher capability probe

The isolated PoC confirms that KDE Plasma 6 can open both launcher types with a single tray click. Plain Electron on native Wayland does not place the flyouts at its requested coordinates. A small KWin script demonstrates stable panel-adjacent placement for both a shortcut flyout and a real embedded website, using the click-time cursor as an anchor proxy. This is capability evidence, not a production implementation or a complete icon-geometry integration.

## Environment

Debian trixie, Plasma/KWin 6.3.6, Qt 6.8.2, Electron 45.0.0, x64, native Wayland clients. KWin runs nested on a private 1280 by 800 Xvfb framebuffer with software rendering. The X server's only top-level child is the nested KWin compositor, not an Electron X11 window. The pinned container base is `debian@sha256:62bd423aeee912639ca8ee681c2b762cb3e25137e0a92b20be455898a9f034ad`. Exact package versions are in [package-versions.txt](evidence/package-versions.txt).

The container has its own display, D-Bus session, home, and application profile. It mounts only the task runtime and a read-only Electron distribution, never the host's display, devices, session bus, or desktop configuration. It is limited to two CPUs, 4 GiB memory, and 512 processes, with all container capabilities dropped. Docker's outer seccomp profile is disabled for Chromium's nested namespaces; the app is not passed `--no-sandbox`, and its observed renderer processes enable their own seccomp filtering. See [sandbox observations](evidence/sandbox-observation.json) and [container mounts](evidence/container-mounts.json). This is a development setup, not distribution packaging or a security review.

## Observed results

All coordinates below are pixels on the private 1280 by 800 framebuffer, with a top-left origin. Elapsed times are test-relative durations, not clock times.

| Case | Observed result |
|---|---|
| Multiple icons | Both shortcut and web icons registered in Plasma's real StatusNotifierWatcher. |
| Primary click | A single left click opened each flyout. |
| Native tray menu | A right click displayed the Plasma-owned menu. |
| Plain Electron shortcut window | Electron reported a request at `(8, 8)` while compositor geometry and pixels showed `(494, 224)`. |
| Plain Electron web window | Electron reported a request at `(8, 8)` while compositor geometry and pixels showed `(520, 239)`. |
| Helper, bottom panel, shortcuts | All visible target samples had bounds `(980, 440, 292, 304)`; first target visibility was sampled at 74.9 ms. |
| Helper, bottom panel, web | All visible target samples had bounds `(752, 264, 520, 480)`; first target visibility was sampled at 60.1 ms. |
| Helper, top panel, shortcuts | All visible target samples had bounds `(980, 56, 292, 304)`; first target visibility was sampled at 70.0 ms. |
| Helper, top panel, web | All visible target samples had bounds `(752, 56, 520, 480)`; first target visibility was sampled at 78.1 ms. |
| Dismiss and reopen | Clicking away hid the flyout; single-click reopening remapped it and reapplied placement. |
| Owned chooser | The shortcut flyout remained visible while its owned file chooser was open and after cancellation. The final helper did not move it on return. |
| Clean startup | The one-command launcher started a fresh private desktop, registered both icons, loaded the website, and reproduced anchored shortcut opening without manual repairs. |
| Regular-window fallback | Both launcher types opened as decorated KDE windows with an explicit unavailable-integration label; losing focus left the regular window open. |

Each final/top placement case collected 60 raw framebuffer samples before PNG encoding. No second visible target position was observed within those samples. This does not establish that no shorter transient exists between samples or that these software-rendered timings are production performance. The earlier 24-frame cases and superseded helper trials are retained in the raw results, but the `final-*` and `top-*` cases qualify the final helper.

The website was loaded into a sandboxed Electron `WebContentsView` from `https://example.com/`. This proves an embedded remote page, not messaging, calls, notifications, password managers, or full browser compatibility. The shortcut list uses stub rows rather than launching real applications.

## Screenshots and raw evidence

The raw `evidence/` directory and generated `SHA256SUMS` packet are local verification artifacts ignored by Git. The links below work only in a checkout that retains this probe run; the observed results above and reusable probe source remain versioned.

- [Plain Electron shortcut placement](evidence/vanilla-shortcuts.png) and [web placement](evidence/vanilla-web.png).
- [Bottom-panel shortcut flyout](evidence/final-shortcuts.png) and [embedded web flyout](evidence/final-web.png).
- [Top-panel shortcut flyout](evidence/top-shortcuts.png) and [embedded web flyout](evidence/top-web.png).
- [Owned-dialog return](evidence/owned-dialog-return.png) and [native tray menu](evidence/native-menu.png).
- [Regular shortcut fallback](evidence/regular-fallback-shortcuts.png), [fallback after losing focus](evidence/regular-fallback-after-blur.png), and [regular web fallback](evidence/regular-fallback-web.png).
- [Target-specific result summaries](evidence/results.json), paired `*-frames.json` observations, [compositor log](evidence/kwin.log), and [application events](evidence/events.jsonl).

The [tested flyout source](evidence/tested-flyout-main.cjs) and [final helper](evidence/tested-anchor.js) are retained beside the observations. The runnable source also adds the later regular-window mode and removes Electron's default application menu. Its fallback runtime events are preserved separately in [fallback-events.jsonl](evidence/fallback-events.jsonl). [SHA256SUMS](SHA256SUMS) binds the final packet's source and evidence bytes.

Raw logs contain explicitly labelled UTC timestamps. Any raw container clock displays within captured screenshots are also UTC. No host desktop screenshots were taken. Browser profiles and caches are excluded from this packet.

## What the helper does and does not establish

`anchor.js` matches only this probe's application identity and two exact flyout captions. It places a newly mapped window using KWin's usable screen area and the current pointer position. It handles the demonstrated top and bottom panel cases and clamps within the work area. The initial experiment also moved windows on activation; the owned-dialog test exposed an unwanted jump, so the final script places only on window creation.

The cursor is a test proxy for the icon anchor. Exact icon geometry, pointer movement between click and mapping, keyboard/menu activation, multiple monitors, fractional scale, other panel layouts, custom desktop themes, and production installation/update behavior are unqualified. They require an explicit app/desktop integration contract before claiming complete anchored-flyout support. The helper is installed only in the disposable desktop and is not a shipped plugin.

## Native appearance requirement

Windows remains the reference for sizes, layout, and interaction structure. The application content must follow the active desktop environment's colors, corner shapes, borders, typography, and control treatment in flyouts and Settings. The probe's bright borders measure placement; its fixed CSS is not an accepted appearance. Settings was not implemented in this capability probe.

Electron's [nativeTheme API](https://www.electronjs.org/docs/latest/api/native-theme) exposes theme information but does not by itself style an HTML settings UI like KDE or GNOME. KDE also provides a [Qt platform integration](https://github.com/KDE/plasma-integration) for palette, style, fonts, and related desktop behavior. The engineering recommendation is to keep desktop integration and browser hosting as separate responsibilities, and qualify a native toolkit or a genuine desktop-theme bridge before choosing the full UI architecture. This is a recommendation, not a selected replacement for Electron.

## Run the private desktop

Host prerequisites are Docker, Node/npm, Bash, and ripgrep. The runner installs no host desktop packages. Electron is pinned to the stable version checked during the PoC; packages are selected from the pinned base distribution's configured repositories. The exact observed dependency versions are frozen as evidence, rather than claiming future repository contents are identical.

```bash
./launch.sh
```

The command builds the private image, starts a separate desktop, loads the probe helper, and prints the container name and temporary runtime directory. It does not open a host window. `./launch.sh --baseline` omits the helper to demonstrate plain Electron placement. `./launch.sh --regular-window` omits the helper and selects a clearly labelled ordinary decorated-window fallback. To reuse an existing Electron distribution, set `LL_POC_ELECTRON_DIST` to its absolute `dist` directory.

Capture and input commands must target only the printed container and its private session:

```bash
docker exec <container> python3 /work/capture.py /work/desktop.png
docker exec <container> /work/in-session.sh python3 /work/experiment.py <case-name> <observed-icon-x> <observed-icon-y>
docker exec <container> python3 /work/summarize.py
docker stop <container>
```

Inspect the current private screenshot before supplying icon coordinates; the captured fixture's coordinates are not a general desktop contract. The runner preserves logs in its printed temporary directory. Stop the exact printed container when done, then remove that stopped task-owned container if desired. Never run these D-Bus or input commands against the user's live desktop.

## Remaining acceptance gates

This completes the isolated capability experiment with bounded results. It does not complete the Linux application. Exact icon anchoring, native visual fidelity, the full web workload, supported real KDE/GNOME environments, package and AppImage integration, sync round trips, Windows changes, and production workspace placement remain gates. No host desktop changes, commits, installs into the user's desktop session, or releases were performed.

All three task-owned private test containers were stopped and removed after preserving evidence. The reusable container image and temporary logs remain available; GNOME and unrelated applications were left running.

The results align with Electron's documented [Wayland window positioning limits](https://github.com/electron/electron/blob/main/docs/api/base-window.md) and KWin 6.3's exposed [workspace API](https://github.com/KDE/kwin/blob/Plasma/6.3/src/scripting/workspace_wrapper.h) and writable [window frame geometry](https://github.com/KDE/kwin/blob/Plasma/6.3/src/window.h). KDE and GNOME remain separate qualification targets.
