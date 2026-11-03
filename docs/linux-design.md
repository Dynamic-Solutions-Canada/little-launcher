> Current shortcut policy: [whole-launcher OS ownership](adr/0004-whole-launcher-platform-ownership.md) supersedes the original per-item visibility requirements below. Web launchers remain shared.

# Linux Little Launcher design

Ryan confirmed the product design and authorized the isolated KDE capability PoC on 2026-10-27. Agreed requirements are distinct from implementation choices and unverified capabilities. Subsequent steering requires desktop-native visual styling while preserving Windows layout and size conventions.

## Agreed direction

- KDE is the primary desktop target. GNOME is also in scope; the initial development machine used GNOME on Zorin, and development resumed on Fedora KDE as recorded below.
- Linux-native means an application that feels integrated with the Linux desktop. Electron may render settings and launcher windows; the toolkit remains open pending feasibility work.
- Windows is the reference for layouts, control sizes, window sizes, and interaction structure. Flyouts and Settings should match the active desktop environment's visual styling as closely as possible, including colors, corner shapes, borders, typography, and controls. Platform-specific effects such as Windows acrylic are excluded from visual parity. Native styling is not a reason to redesign the product's screens; Windows styling is not a reason to ignore the Linux desktop theme.
- The eventual product includes shortcut and web launchers, multiple launcher icons, editing, and compatible settings and sync, delivered in stages.
- Both shortcut and web flyouts should open beside their own launcher icon without a visible positioning jump. Any desktop-specific compromise requires a product decision before adoption.
- Single-click opening is highly desirable, but its absence does not automatically block release.
- Where better activation needs additional desktop integration, guided setup is acceptable. Double-click opening or a menu's Open launcher command is an acceptable fallback when that integration is unavailable.
- Linux is developed alongside the existing Windows application. Narrow Windows changes are allowed for OS-specific entry compatibility and the agreed active-call/capture/transfer protections; its broader UI and architecture stay unchanged. Compatible data formats and behavior may be shared where practical; a Windows rewrite is outside this effort.
- This agent and conversation own the work. Ryan authorized design documentation in the current workspace after the available Paseo operations did not expose a supported move of this conversation. Implementation placement remains undecided. Changes stay uncommitted.
- The first milestone proves multiple launcher icons, shortcut and web flyouts, stable placement, and activation on KDE/Wayland before building the full editor and sync.
- OS-specific shortcuts that cannot work on the current OS remain in the synced collection but are hidden from its launcher view. Users may add a separate Linux or Windows shortcut in the corresponding position or beside the other OS's entry. There are no machine-local replacement mappings.
- Each web launcher has its own persistent sign-in session by default. Website cookies and credentials stay on the local machine and do not sync between Windows and Linux. Users may explicitly choose a shared local sign-in profile between launchers, matching the options available on Windows.
- Desktop integration settings stay machine-local. Launcher names, contents, bookmarks, and portable preferences sync; Windows-specific settings are preserved even when Linux cannot use them. The earlier local-replacement proposal is superseded by OS-specific shortcut visibility.
- Newly added application shortcuts are automatically assigned to their OS. Ordinary website URLs are cross-platform. Users may change the assignment. A missing executable or disconnected drive does not by itself make an otherwise compatible shortcut disappear.
- Normal launcher views hide incompatible shortcuts. The editor provides a Show other operating systems toggle so users can inspect, edit, and remove entries that remain in the synced collection.
- When OS filtering empties a group or column, normal use hides that structure and closes the gaps. Editing and sync preserve the full layout.
- A shortcut launcher with no compatible shortcuts has no tray icon in normal use, but remains accessible in Settings. Settings persistently explains why it is hidden, and a dismissible notice appears when an import or sync first causes it to be hidden. Startup does not repeatedly show a popup.
- Following the desktop feasibility milestone, web acceptance covers messaging and productivity workloads: persistent sign-ins, notifications while hidden, uploads, downloads, microphone, and camera. Browser extensions and password-manager integration remain a separate decision.
- Ordinary web launchers sleep or unload when hidden. Keep running is an explicit per-launcher choice for continued background activity and notifications, with its resource cost explained. Keep running launchers start in the background with the app; notification delivery must not depend on a prior manual open.
- Browser-extension support may be deferred beyond the first usable Linux release and remains a later milestone. Normal website sign-in is required for the first usable release; extension and password-manager compatibility are separately qualified.
- Initial platform qualification targets x64 and Wayland, with KDE Plasma 6 primary and GNOME secondary. ARM64 and X11 qualification are deferred.
- AppImage is the only offered Linux format, superseding the earlier DEB/RPM plan. Minimum supported baseline is Debian12 / Ubuntu24.04 LTS or newer; initial builds are x64.
- Start Little Launcher at login is enabled by default and exposed as a user-controlled choice, following Windows. Updates preserve the user's selection. This supersedes the proposed opt-in default. Keep running web launchers start whenever the app starts.
- Cross-platform sync may require the Windows compatibility update first. Warn clearly when an older client cannot safely preserve OS-specific settings. Safe round trips through older clients are not assumed.
- Linux checks for newer AppImages automatically by default and exposes manual checks, download and explicit Install and Restart in About. Verify payloads and close protected workloads normally before replacement. Preserve settings, sign-ins and the login startup choice through updates.
- Active calls, microphone/camera capture, and downloads postpone ordinary hidden-page sleep or unload. Explicit confirmation is required before quitting interrupts an active call, capture, or transfer. This requirement applies to Windows as well as Linux. Detection and lifecycle behavior require qualification on each platform.
- If required desktop integration is unavailable, the app may offer a clearly labelled regular-window fallback and explain how to enable anchored flyouts. This fallback does not constitute proof that anchored placement works.
- Switching between private and shared local sign-in profiles preserves the previous profile so switching back can restore its session. Explain that the selected profile may require signing in again; switching does not silently merge or delete browser data.
- Use an isolated KDE test environment for the next PoC, preserving the current GNOME desktop and session.

## Evidence and limits

The temporary Electron 45.0.0 probe ran on GNOME Shell 46.0, native Wayland, with Zorin AppIndicators enabled. GNOME registered both test icons: L without an explicit menu and M with a menu. Ryan observed that both open the test window on double click; a single click opens M's menu and does nothing visible for L. The window initially appears toward the top left and then moves down and right, remaining somewhat centered. This verifies the current GNOME interaction and a visible placement defect, not KDE behavior or acceptable anchoring.

The installed Zorin extension routes primary double clicks to activation and single clicks to its menu. Removing the menu did not produce single-click opening. The probe and event logs are in `/tmp/little-launcher-gnome-poc`; temporary files are not durable implementation evidence after host cleanup.

A GNOME extension is a candidate integration, not a selected or proven solution for anchored Electron web windows. Single-click delivery and web-window anchoring are separate capabilities. KDE activation and placement must be tested on KDE.

The existing Windows application creates new web launchers on a shared local browser profile and supports private profiles. The approved Linux default is isolation per launcher, with an explicit shared local profile option. No existing Windows profile or session is to be migrated by this design work.

Application shortcut items currently have no persistent item identifier in `LauncherItem`. The local-replacement proposal was dropped in favor of separate OS-specific entries, avoiding a replacement reconciliation feature. The current sync merge explicitly enumerates launcher properties; cross-platform compatibility requires round-trip checks rather than assuming additional metadata will survive existing clients.

The Windows shared item renderer currently binds item collections without an OS-visibility filter. Ryan approved narrow compatibility changes for symmetric OS-specific visibility and later extended Windows scope to active-call/capture/transfer protections, superseding the earlier requirement that Windows remain wholly unchanged. The implementation uses an explicit SupportedOperatingSystems list, a PlatformVisibilityVersion writer marker, detached normal-use projections, and full-source editor storage. Windows model/reorder checks pass, while the WinUI changes remain unqualified until a Windows build transport is available. The interview is complete and implementation is authorized.

## Isolated KDE PoC result

The [retained source and evidence packet](prototypes/linux-kde-poc/README.md) confirms single-click activation for both icons on isolated Plasma 6.3.6 with native Wayland Electron 45 clients. Plain Electron placement requests disagreed with compositor geometry and rendered pixels. A scoped KWin helper demonstrated stable sampled top/bottom-panel placement for shortcut and embedded-web flyouts, and an owned-dialog test drove a correction that preserves parent placement when focus returns. A labelled ordinary-window fallback was also exercised.

The helper uses the click-time cursor as an icon-anchor proxy. Exact icon geometry and the complete production integration remain unproven, as do native application styling, physical desktop acceptance, fractional scale, multiple monitors, and full browser workloads. This is sufficient to continue architectural evaluation, not to declare a production-ready Electron/KWin solution. All private test containers were stopped and removed after preserving evidence; the current GNOME session was not replaced or switched.

## Deferred technical gates

- Qualify supported real KDE and GNOME environments beyond the isolated software-rendered KDE fixture. The host's desktop remains GNOME; desktop packages were installed only in the task-owned container image.
- Visibility metadata, older-client compatibility, and preservation of hidden entries during reorder and sync.
- Detailed classification of portable and machine-local preferences.
- Later browser-extension and password-manager compatibility and detection of active calls/capture/transfers.
- Desktop integration boundaries, guided installation, unavailable integration, and updates.
- Specific supported distribution and GNOME versions, package-specific integration and update behavior, and the AppImage milestone.
- Platform-specific editing, launch actions, recovery, resource use, and acceptance checks.
- Implementation workspace and architecture after feasibility is established.

These gates are explicit unresolved engineering work, not silently selected implementations or claims of support. If a gate requires compromising an agreed product requirement, return to Ryan before adopting that compromise. Publish supported distribution and desktop combinations only after qualifying them.

## Implementation authorization and retained feasibility checklist

The isolated PoC is complete. Ryan subsequently authorized implementation with this agent and conversation as owner, and approved the `linux-native` branch in the existing checkout. Continue there without creating a second workspace or owner. The native implementation uses Qt Widgets for desktop controls and current Electron for web content, as recorded in ADR 0003. The checklist below remains the capability evidence boundary; it does not imply that packaging, provider sync, Windows, or physical desktop acceptance is complete.

The PoC must answer:

1. Does the verified KDE Plasma 6 Wayland environment register multiple launcher icons and deliver single-click activation? Record menu and double-click fallbacks as well.
2. Can both a shortcut flyout and an actual embedded web window open beside the corresponding icon, at the intended size, without a visible placement jump?
3. Does dismiss/reopen behavior work, including focus transitions to owned dialogs, without breaking the user's interaction?
4. Which toolkit or desktop integration is needed for anchored placement? Test Electron as a candidate; do not select it solely because a basic web page renders.
5. What does the regular-window fallback look like when integration is unavailable, and can the app explain the difference?
6. Does the chosen development setup leave the current GNOME session, unrelated applications, and user settings undisturbed?

Report observed behavior, the exact tested environment, remaining limits, and the proposed architecture. An isolated result does not establish real-machine, packaging, GNOME, browser-workload, or Windows acceptance. Additional desktop integration installation or any interruption of Ryan's working session needs authorization within the agreed boundaries. No commits, publishing, or release are authorized by this design interview.

## UI acceptance

Use the existing Windows screens and launcher views as the layout and interaction reference. Compare representative shortcut lists, icon layouts, groups, columns, web chrome, editing, and settings at matching logical sizes. Match the active desktop environment's colors, corner shapes, borders, typography, and control treatment in both flyouts and Settings. Record platform-required differences explicitly rather than substituting a new Linux layout. Pixel-identical Windows effects and Windows file-picker styling are not implied by this requirement. A light/dark toggle alone does not establish native desktop-theme fidelity.

The capability PoC uses deliberately contrasting borders to measure visible window bounds. Its fixed colors and corners are diagnostic styling, not an accepted product appearance or evidence of KDE/GNOME theme integration.

## References

- [Product vocabulary](glossary.md)
- [Native controls and isolated web engine](adr/0003-native-linux-controls-and-isolated-web-engine.md)
- [Linux implementation and current gates](../linux/README.md)
- [Linux and Windows development boundary](adr/0001-linux-and-windows-boundary.md)
- [OS-specific shortcuts and local web sessions](adr/0002-os-specific-shortcuts-and-local-web-sessions.md)
- [Existing global sync contract](sync.md)
- [Existing web launcher behavior](web-launchers.md)
- [Electron tray activation](https://www.electronjs.org/docs/latest/api/tray)
- [Electron window behavior](https://www.electronjs.org/docs/latest/api/browser-window)
- [GNOME extension installation locations](https://help.gnome.org/system-admin-guide/extensions.html)


## Fedora continuation, October 29, 2026

Ryan resumed this owner on Fedora 44 KDE, authorized a GPU-backed isolated KDE container, and deferred the Windows build. The previous temporary desktops did not migrate. A new private virtual Plasma 6.7.5 session uses only the RX 9070 render node, without the physical display, user session bus or home. Native tests and all eleven Electron checks pass. Actual KDE portal capture delivers two 1280 × 800 video frames with image variation, and unconfirmed quit is blocked during capture. The app-owned KWin helper loads and anchors its window on this newer compositor. This resolves the GPU-free fixture streaming blocker; it does not establish physical-session capture, production service workloads, GNOME behavior, full Windows compatibility or automated sync.
