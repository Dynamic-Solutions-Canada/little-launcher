# Linux UI parity review

Windows source is the reference for dimensions, grouping and actions. KDE supplies the palette, font family, window decorations and native controls. The October 29 preview did not establish visual or feature parity; the user rejected both launcher UIs and reported corner placement.

## Earlier November 1 correction (historical snapshot)

Two placement defects were found. Plasma delivers native-pixel SNI coordinates by default on Wayland (`KScreen/XwaylandClientsScale=true`), while KWin uses logical coordinates. The real host is 2560 × 1707 logical at 150%; an actual tray click arrived at (3132, 2541), outside that logical output. The protocol now marks native coordinates explicitly and KWin converts them using its actual output scale, rather than Qt Widgets’ rounded buffer DPR. When the compatibility setting is off, coordinates remain logical. The 150% private compositor verifies native (900, 500) opens a 96 × 80 flyout at (552, 245). Mixed-scale/multiple-output physical acceptance remains open.

The old KWin helper also always chose the nearest monitor edge. That mishandled tray overflow clicks inside the work area and horizontal-panel corner clicks. The helper now follows the Windows `FlyoutWindow.CalculatePlacement` policy: reserved vertical sides take precedence; horizontal panels open against their work-area edge; interior overflow clicks open above the actual activation point. Coordinates remain the tray host's captured coordinates, never a later pointer query. Geometry is clamped on the selected output, including negative-coordinate outputs. The shortcut size watcher retains the edge during content edits. Web user resizing/maximization is left to the compositor.

The 100% isolated KWin observer verifies an interior activation at (900, 500) produces a 96 × 80 flyout at (852, 412), directly above that point. Node tests cover overflow, horizontal corners, vertical panels and negative-coordinate outputs. A private physical tray click also delivered the expected logical coordinates; it is separate from real desktop acceptance.

| Surface | Changes in this revision | Remaining differences requiring work |
|---|---|---|
| Shortcut flyout/editor | Shared Windows-sized geometry, real group containers/full-scope hover, whole-group midpoint and interior/outer-band targeting, group identity cards, in-place toolbar, Move to actions and app settings | External file/URL drops, icon-column edge resizing and Windows live destination text in the native drag image |
| Item form | 560 × 620 host with Apps & web apps and File or link source tabs, searchable embedded native application list, shared name/icon/OS controls | Full icon gallery, website favicon acquisition, app-picker row actions and browser/profile selection |
| Web chrome | 34-pixel header, tab/address/bookmark rows; back/forward state, Home, More, address toggle, pin, minimize in regular mode, maximize/restore and Close; native grouped quick menus and keyboard shortcuts | Graphical anchor picker, contained fullscreen/video controls, favicon/icon-only bookmark presentation |
| Web tabs | Links/new windows become sandboxed tabs in the same profile; select/close/new, automatic strip, optional always-show, per-tab lifecycle/transfer/permission tracking, aggregate quit/removal protection | Tab drag/reorder/context actions, bookmarks-as-tabs and local restart-session restoration |
| Web bookmarks | Existing native tree editing plus visible-item overflow menu instead of clipping or scrolling | In-bar drag/drop, richer folder overlay and all Windows bookmark commands |
| Launcher settings |560px form with reference body/row spacing, native left-title/right-arrow sections, label/subtitle with aligned control, stacked address, combined size row, Done/Escape/close boundary, conditional rows/type sections and shared menu destinations | Full row/control coverage, privacy reset actions and remaining Windows appearance/position options |
| Main Settings | Launcher cards now follow the Windows450-pixel column, left Add action, icon/name/action header, Settings-before-Items order and sibling Items bulk menu; native trailing-chevron rows replace command links | Sharing/preset/browser-bookmark import commands and full coverage/polish of the other Settings pages remain open |

These are tracked gaps, not an assertion that unsupported controls can be silently omitted. Provider sync, Windows qualification, packaging/release and platform support gates remain separate. Windows compilation is deferred by the user. No rendered Windows comparison or complete UI parity is claimed.

## Hover feedback

Passive Settings navigation/list/scroll containers and the intrinsic editor preview no longer use Breeze focus/hover-sensitive frames. Native row selection, text-field focus and actionable button feedback remain intact. The user reported the old broad blue outlines as misleading; this is separate from the intentional shortcut edit-mode outline.

## Verification

Run the Python/Node suites, then the actual engine and compositor drivers in the authorized isolated Fedora fixture:

```bash
QT_QPA_PLATFORM=offscreen PYTHONPATH=linux python3 -m unittest discover -s linux/tests -v
node --test --test-isolation=none linux/tests/*.test.cjs
docker exec CONTAINER /work/in-session-fedora.sh python3 /source/linux/dev/verify_placement.py
docker exec CONTAINER /work/in-session-fedora.sh /opt/electron/electron /source/linux/dev/verify_web_ui.cjs
docker exec CONTAINER /work/in-session-fedora.sh python3 /source/linux/dev/verify_browser.py
```

The web UI driver checks actual new-window adoption, absent remote Node/preload access, shared same-launcher session, inactive capture protection, returning without navigation and restoration after idle unloading. It caught and guards a delayed old-WebContents destruction race. Lifecycle commands are serialized so an old freeze response cannot leave a reopened page frozen. Native transactions guard stale view paths and atomically recheck cross-launcher destinations, preserving hidden rows and unknown fields. Artifacts are retained under ignored `.artifacts/linux-native/parity-followup/`.

API references: [Plasma popup coordinate conversion](https://github.com/KDE/plasma-workspace/blob/master/applets/systemtray/systemtray.cpp), [KWin scripting](https://develop.kde.org/docs/plasma/kwin/api/), [Electron WebContentsView adoption](https://www.electronjs.org/docs/latest/api/web-contents-view), [Electron window-open handler](https://www.electronjs.org/docs/latest/api/structures/window-open-handler-response). The private fixture never mounts the user's display, session bus or home. Physical tray placement, visual acceptance and broader UI parity remain review gates.

The launcher-card correction removes the extra Open row and moves deletion out of the ellipsis into the header. The item bulk menu now exports/imports the same JSON item-list shape as Windows. Replace/Append operates on the full saved source and rejects stale launcher state. Native Breeze renders at 900px and620px verify normal and narrow layouts;46 Python checks pass. Prior RPM evidence predates this source-preview revision.

The settings-form correction places existing supported controls in the Windows row order and grouping; it does not add fake controls for missing privacy/position/browser features. Settings apply once at the Done/close boundary, whereas Windows has immediate commits for individual controls. Shortcut click-away now has compositor-backed dismissal plus initial focus; editing/owned interactions remain exempt. The separate-app private fixture passes, but also passed without the focus change, so exact reproduction of the reported physical-session issue and user visual acceptance remain open.

Group feedback now addresses actual container semantics. 55 Python checks and all six private mode/host drag scenarios pass. Hidden rows and unknown group fields survive whole-group movement and membership changes. Native Wayland drag image captions remain static despite QDrag.setPixmap calls; the target scope/caret updates, and this limitation is not treated as Windows drag-preview parity. The complete shortcut Done-after-app-switch cycle now verifies restored owner activation and a subsequent real outside click.

OneDrive now has a native Cloud sync page for sign-in/out, manual download/upload and optional automatic sync. It is a functional OneDrive subset rather than full Windows provider-page parity. Each Launchers-list card now exposes the same Enabled on this device switch as the Windows card header. Unlike the staged portable Linux form, this local choice acts immediately and retains the portable revision. New custom-icon payload content is materialized locally; legacy path-only cloud files and unsupported Fluent glyph rendering remain explicit limitations. Live-account and full WinUI verification are pending.

## Imported-launcher follow-up

The follow-up audit corrects the Windows-path-induced form overflow, moves Cloud Sync to the Windows provider/account/shared-action structure, and adds real supported web settings/menu controls. Home, Settings and About now share scrolling reference margins and card patterns. Account state is persistent and handles reauthentication/credential loss; additional providers are clearly unavailable. Browser identity uses the actual Chromium Chrome token, both hosts use Wayland, and a hidden-loading freeze race is guarded. Native KWin evidence covers downloaded IDs and the first visible opening of background-preloaded content. The dynamic preview scope also restricts unique application identities.

See the [current audit](linux-parity-audit.md) for precise coverage and open areas. Full UI parity is still not complete: full gallery/import, additional providers, remembered positions, fullscreen/video fitting, saved logins, extensions, profile cleanup and platform qualification remain. Regular Wayland always-on-top is controlled by KDE rather than an unsupported Electron call. 83 Python and 21 Node cases, actual browser/UI fixtures and compositor placement pass. Real signed-in service acceptance remains a user review step; the supplied Electron main-process exception is reproduced and fixed.


Bookmark follow-up adds the saved launcher-wide/per-entry icon-only rule, fixed icon slots, tooltip names, direct same-site favicons and measured compact overflow. A native KDE frame suppresses duplicate app title/window controls. The compositor helper now handles regular-window opening anchors too; the fixture verifies Bottom center using frame geometry. The [detailed bookmark audit](linux-parity-audit.md#bookmark-and-window-audit-follow-up) records the missing context commands, drag/import, persistent icon caching and tab/session features instead of treating these as complete. Current checks:84 Python,22 Node, expanded actual-renderer UI and compositor placement.


The context-action continuation completes the bookmark navigation/copy/home commands and separate name/address edits, plus Windows-ordered tab menus, background duplication, bounded reopen history and modifier/middle-click bookmark opening. The actual private engine verifies inactive-tab identity and preservation of a real pending upload when Close other tabs is declined.86 Python and24 Node cases pass. Bookmark/tab dragging, browser import, persistent tab sessions and other gaps in the [audit](linux-parity-audit.md) remain open.


## Second audit: current source

The [current source audit](linux-parity-audit.md#second-audit-historical-snapshot) supersedes the original table's remaining-gap claims. This pass adds native launcher window icons, local restart tabs, tab favicons and missing keys, temporary blank-tab address UI, immediate guarded launcher settings, a native browser/profile bookmark picker, final-tab close/hide semantics and internal tab/bookmark drag moves.97 native/data tests and25 Node cases pass, with actual-engine restore/context/UI checks, native picker evidence and distinct Wayland titlebar icon pixels. Physical Ctrl+wheel input, cross-window/folder-overlay dragging and broader unsupported areas remain explicitly open.

The second audit also adds standard native external file/URL drops (Copy/Link, Linux-only file references), desktop file-type icons, and a pending native preset/emoji/text/color/custom-image chooser. Legacy numeric icon modes render without rewriting portable metadata; unsupported private-use glyphs remain saved. Independent per-WebContents zoom keeps shared sign-ins without shared page scale, and navigation reapplies the selected percentage. Current verification is 103 native/data tests, 26 Node cases, all 12 actual-engine lifecycle checks, expanded renderer/session/context checks, native picker screenshots, six native group-drag modes/hosts and a real native external file drop. Physical Ctrl+wheel remains unqualified and is omitted from the help sheet. Complete catalog, overlay, fullscreen, positioning and release gaps remain explicitly listed in [the parity audit](linux-parity-audit.md).

The third parity pass implements nested draggable bookmark folder/overflow overlays above the isolated page, KDE device-local last-dragged positions through a scoped QtDBus bridge, a native opening-position grid, and contained/display page fullscreen with trusted exit/maximize controls and intrinsic video fitting. Actual private compositor pointer drag and titlebar move/reopen checks pass; fullscreen uses synthetic local media. Current source verification is107 native/data tests and29 Node cases plus real-engine regression drivers. QtDBus is now a runtime module requirement; Debian packaging declares `python3-pyside6.qtdbus`. Existing installed KWin helpers require explicit update; the real preview reloads only its own temporary helper. Cross-window drags and broader catalogs, services and release gates remain open in [the audit](linux-parity-audit.md).

## Current fourth pass

Shortcut icon selection/rendering, public site-icon preview, multi-select browser bookmark import and native app-picker context commands now have functional native implementations. About adds working AppImage update controls. Current source passes137 native/data tests and29 Node cases; actual private pointer/UI and packaged replacement/restart checks pass. Earlier tables describe historical gaps; [the current audit](linux-parity-audit.md#fourth-pass-shortcut-editing-and-appimage-delivery) lists what remains. AppImage is the sole Linux format; [delivery and startup](../linux/README.md#linux-appimage-delivery) describes the runtime baseline and qualification limits.
