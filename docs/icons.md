> **Scope:** Use when working with app icons, tray icons, shortcut icons, or window icons. Covers which icon files exist, where each icon surface pulls from, and how to update them correctly.
> **Governs:** `Shared/PinSnapshot.cs`, `**/LauncherSettingsWindow*.cs`, `**/MainWindow.xaml.cs`, `**/SettingsWindow.xaml.cs`, `**/LaunchersPage.xaml*`, `**/LauncherShortcut/**`, `**/HomePage.xaml.cs`, `**/FlyoutConverters.cs`, `**/FlyoutWindow.xaml*`, `Controls/LauncherItemsView*`, `Windows/LauncherItemsEditorWindow.cs`, `**/IconGallery.cs`, `**/IconCatalogView.cs`, `**/SelectionReveal.cs`, `**/AutomaticPageIconChoice.cs`, `**/WebFlyoutWindow.IconPreview.cs`, `Services/PortableLauncherIcon.cs`, `Services/FaviconService*.cs`, `Services/TaskbarPinStatusService.cs`, `**/GlyphIconRenderer.cs`.

> **Note on glyph code points:** Segoe Fluent Icons glyphs below are written in `U+XXXX` code-point notation. In C# they are string escapes — e.g. `U+E840` is written `""`.

# Icon System

Little Launcher uses a flat upright rocket as its identity icon. The **app identity** (Start Menu, settings window, Home page) always shows the blue rocket. Users can change each launcher's **tray icon** and **pinned taskbar icon** independently.

Launcher settings groups its icon, read-only custom-file information, tray visibility and taskbar pin control
under **Appearance**. The pin state uses that control row's normal subtitle. The flyout's matching section links to that same owned editor; the icon
chooser, automatic page-icon adoption, and pin snapshot/lifetime behavior are unchanged.

## Icon Surfaces

| Surface | Source | User-configurable? |
|---|---|---|
| **System tray (per launcher)** | `ResolveTrayIcon(Launcher)` → preset PNG, glyph, composite, or custom image | Yes (`Launcher.TrayIconMode`) |
| **Pinned taskbar shortcut** | Immutable `app-icon-{id}-pin{stamp}.ico` in physical AppData via snapshot shortcut or legacy relaunch metadata | Yes (per launcher `TrayIconMode`) |
| **Settings window titlebar** | `settings-icon.ico` (blue rocket + gear overlay) | No; always Blue rocket |
| **Settings window taskbar entry** | `settings-icon.ico` via `WM_SETICON` + `AppWindow.SetIcon(IconId)` | No; always Blue rocket |
| **Settings window Alt-Tab** | `settings-icon.ico` via `AppWindow.SetIcon(IconId)` | No; always Blue rocket |
| **Regular item editor** | Packaged app icon via `WindowChrome.ApplyIcon` (`WM_SETICON` + `AppWindow.SetIcon`) | No, normal app identity; no launcher pin/relaunch identity |
| **Home page app icon** | `Resources/AppIcons/Blue.png` loaded directly | No; always Blue rocket |
| **Start menu shortcut** | `exe,0` (embedded icon) | No; always Blue rocket |
| **Exe embedded icon** | `Resources/LittleLauncher.ico` (compiled into exe) | No; always Blue rocket |
| **Pin-to-taskbar reminder card** | Immutable per-attempt `.ico` drawn with alpha and supplied via `WM_SETICON` in the companion | Yes (per launcher `TrayIconMode`) |
| **Web launcher taskbar button** (regular-window mode) | Per-launcher `app-icon-{id}.ico` via `WM_SETICON`, from `WebFlyoutWindow.LoadWindowIcon` | Yes (per launcher `TrayIconMode`) |
| **Web launcher task switcher** (regular-window mode) | The same file via `AppWindow.SetIcon(IconId)`, set in the same call | Yes (per launcher `TrayIconMode`) |
| **Windows notifications (per launcher)** | `toast-icon-{id}.png` written by `SaveToastIconToAppData(Launcher)` | Yes (per launcher `TrayIconMode`) |
| **Taskbar jump list task (per entry)** | The item's own `.exe` where it has one, else `JumpListIcons\task-{hash}.ico` rasterised from the item's icon or glyph | Follows the item's own icon |

**Background page-icon adoption may never shrink a web launcher's icon.** `web-favicon-{id}.png` is written both by the browser-tab favicon (16-32px, and on some sites it carries unread badges) and by the page's manifest or apple-touch-icon (512px+). The favicon path measures both and declines to replace a larger image with a smaller one, which is what keeps a pinned launcher sharp and stops its identity changing with an unread count. An explicit gallery Confirm instead saves the exact preview, even if it is smaller. See the web-launchers guide.

## Key Files

- **`Resources/LittleLauncher.ico`** — Multi-resolution Blue rocket (16–256px). Embedded into the exe at build time. This is the fallback icon for all surfaces. Generated from `Resources/AppIcons/Blue.png`.
- **`Resources/AppIcons/*.png`** — Preset icon PNGs (Blue, Green, Teal, Red, Orange, Purple). Flat upright rockets stretched 20% horizontally for a wider profile. Copied to output at build time. Loaded at runtime by `ResolveBaseIconBitmap()`.
- **`<AppDataDir>/app-icon-{launcherId}.ico`** — Per-launcher runtime icon. Written by `SaveResolvedIconToAppData(Launcher)`. The first launcher's icon is also copied to `app-icon.ico` by this method.
- **`<AppDataDir>/app-icon-{launcherId}-pin{uniqueStamp}.ico`**: Immutable copy created for each pin attempt. Used by the companion exe for both `LoadImage` (WM_SETICON) and `RelaunchIconResource`. Windows caches icon bitmaps per file path, so each pin gets a unique filename. All pin snapshots are retained because older taskbar pins may still reference them.
- **`<AppDataDir>/toast-icon-{launcherId}.png`** — Per-launcher notification icon, 256px PNG.
  Written by `SaveToastIconToAppData(Launcher)` from the same `ResolveBaseIconBitmap` every
  other surface uses. **PNG, not `.ico`:** the notification platform does not render
  multi-image icon files, so pointing `SetAppLogoOverride` at one shows nothing at all.
- **`<AppDataDir>/app-icon.ico`** — Canonical icon for shortcuts (always mirrors first launcher's icon). Used by `.lnk` shortcuts and the Settings window.
  **Not by the app's owned dialogs.** `WindowChrome.ResolveAppIconPath` used to prefer this file, which meant the item editor, the text prompt, launcher settings, the bookmark picker and the extension popup all wore whichever launcher sorted first. It reads the packaged `Resources/LittleLauncher.ico` instead; a window wanting a specific launcher's icon passes the path to `ApplyIcon`.
- **`<AppDataDir>/settings-icon.ico`** — Runtime-generated icon: the current app icon composited with a gear glyph overlay (dark circle + white gear in bottom-right corner). Written by `SaveSettingsIconToAppData()`. Used by the Settings window.
- **`<AppDataDir>/LittleLauncherFlyout.exe`** — Copy of the companion exe deployed by `EnsureFlyoutShortcut()` for all build types. Pinning uses this copy.
- **`<AppDataDir>/main-exe-path.txt`** — Breadcrumb file containing the main exe path. Read by the companion exe as a fallback when `FindWindow` fails.
- **`%AppData%\LittleLauncher\icons\`** — User-chosen gallery images (`custom-*`, `selfhst-*`). The path stored on the item/launcher points here.
- **`%AppData%\LittleLauncher\icons\selfhst\`** — The selfh.st **browsing** cache: `index.json` plus one `{reference}.png` per icon the gallery has drawn. Never referenced by settings — see [The selfh.st tab](#the-selfhst-tab).

> **`<AppDataDir>`** = the path returned by `MainWindow.GetPhysicalAppDataDir()`. See [MSIX VFS Redirection](#msix-vfs-redirection) below.

## TrayIconMode Values

`Launcher.TrayIconMode` is a **string** property. Values are defined as constants in `TrayIconModes` (in `Launcher.cs`). A `TrayIconModeJsonConverter` handles reading legacy integer values from older settings files.

| Mode string | Type | Source |
|-------------|------|--------|
| `"Composite"` | Composite (default) | 2×2 grid of first 4 item icons |
| `"Blue"` | Blue rocket | `AppIcons/Blue.png` |
| `"Green"` | Green rocket | `AppIcons/Green.png` |
| `"Teal"` | Teal rocket | `AppIcons/Teal.png` |
| `"Red"` | Red rocket | `AppIcons/Red.png` |
| `"Orange"` | Orange rocket | `AppIcons/Orange.png` |
| `"Purple"` | Purple rocket | `AppIcons/Purple.png` |
| `"Pin"` | Pin glyph | Segoe Fluent Icons `U+E840` |
| `"Star"` | Star glyph | Segoe Fluent Icons `U+E734` |
| `"Heart"` | Heart glyph | Segoe Fluent Icons `U+EB51` |
| `"Lightning"` | Lightning glyph | Segoe Fluent Icons `U+E945` |
| `"Search"` | Search glyph | Segoe Fluent Icons `U+E721` |
| `"Globe"` | Globe glyph | Segoe Fluent Icons `U+E774` |
| `"Custom"` | Custom | User-provided file |
| `"Glyph:X"` | Gallery glyph/emoji | Arbitrary Fluent icon or emoji from the icon gallery |
| `"Glyph:#RRGGBB:X"` | Colored gallery glyph/emoji | Gallery glyph with a custom color |

Preset icons are full-color PNGs — they do **not** change with OS theme.
Glyph presets render in black (light theme) or white (dark theme) and update automatically on theme change.
Gallery selections keep the complete Unicode string, including supplementary code points,
variation selectors and ZWJ sequences. `RenderGlyphBitmap()` keeps the existing GDI path and
custom/theme tint for Fluent icons. Emoji use `Classes/GlyphIconRenderer.cs`: DirectWrite shapes
the complete sequence, then Direct2D draws it with `ENABLE_COLOR_FONT` on a WARP software device.
The renderer fits and centers the visible ink from `IDWriteTextLayout.GetOverhangMetrics`, leaving
room for antialiasing, so glyphs extending outside the layout box are not clipped. Its readback
copies premultiplied BGRA rows into `Format32bppPArgb`; treating those pixels as straight alpha
would produce dark fringes. The same rendering is used for launcher icons, composite item icons
and jump-list icon exports, including background-thread requests. A bounded 32-image cache avoids
repeated device creation for identical exports, and each caller owns its returned bitmap.
Emoji retain their native font colors; the palette applies to Fluent icons only. Legacy encoded
emoji colors remain readable and can fill monochrome characters, but never replace intrinsic
emoji colors. Do not truncate a glyph string with `[0]` or send emoji through GDI `DrawString`.

The native ABI declarations and structures are centralized in `NativeMethods.cs`; no new package
is required. The explicit COM slots in `GlyphIconRenderer` follow Microsoft's SDK `d2d1.h`,
`d2d1_1.h` and `dwrite.h`, including inherited interface methods. See
[Microsoft's color-font documentation](https://learn.microsoft.com/en-us/windows/win32/directwrite/color-fonts).

## How Icon Updates Flow

All icon surfaces derive from a single source of truth: `ResolveBaseIconBitmap(Launcher)`, which returns a 256×256 `System.Drawing.Bitmap`.

1. User changes `TrayIconMode` in LaunchersPage → `Launcher.PropertyChanged` fires
2. `CreateTrayIconForLauncher` subscription → `MainWindow.UpdateTrayIcon(Launcher)`
3. `UpdateTrayIcon(Launcher)` calls `ResolveTrayIcon(Launcher)` → `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcon()` → updates that launcher's native tray icon
4. `UpdateTrayIcon(Launcher)` calls `SaveResolvedIconToAppData(Launcher)` → writes `app-icon-{id}.ico` (and `app-icon.ico` for first launcher)
5. `UpdateTrayIcon(Launcher)` calls `CleanUpStaleIconFiles()` to remove legacy versioned copies while preserving every pin icon snapshot
6. `UpdateTrayIcon(Launcher)` calls `SaveSettingsIconToAppData()` → first launcher's bitmap + gear overlay → `BitmapToIcon()` → writes `settings-icon.ico`
7. `SettingsWindow.RefreshIcon()` reloads `settings-icon.ico` into titlebar, taskbar, and overlay

**`RefreshLauncherIcon` is where a *window* icon is re-read too**, alongside the tray HICON and the
`.ico` write. It is the one method every trigger runs through — a mode or custom-image change, a web
launcher adopting its page's favicon, a theme change re-rendering a glyph, a sync download replacing
the launcher — so `WebFlyoutWindow.InvalidateWindowIcon(id)` hangs off it rather than off a
`PropertyChanged` subscription that would have to list those triggers again.

Without it a regular-window web launcher's taskbar button and switcher entry showed a *frozen* icon:
the handles were loaded on the first show and kept for the life of the window, and for a web launcher
that first show is the worst possible moment to sample — `ShowFlyout` applies the taskbar button
before it starts the browser, so what got captured was the placeholder the launcher had before its
page loaded. The tray then adopted the site's favicon and the window did not.

**A web flyout takes its window icon in its constructor, and that ordering is deliberate.**
`ApplyWindowIcon()` runs where the window's ex-styles are first set, before the window has ever been
shown; the call from `ApplyTaskbarButton` is then only a re-assertion, which is still needed because
WinUI blanks `WM_SETICON` while it finishes initialising (WindowsAppSDK#2730 — measured: the icon
disappears for roughly one frame after the show and `PushWindowIcon` puts it back on activation).
Loading it from the first regular-window show instead left the window on screen for ~250ms carrying
no icon at all. Windows re-reads a window's icon every time it draws one, so its own taskbar,
Alt-Tab and Task View never showed that gap — a third-party switcher that samples once, as the
window appears, did.

Tray icons are registered with Shell_NotifyIcon using a stable `guidItem` derived from `Launcher.Id`. This keeps Windows' per-icon tray visibility/pin preference stable across app restarts, Store updates, and icon/name changes. Only changing a launcher's `Id` causes Windows to treat it as a new tray icon identity.

**Pinned taskbar icons are NOT updated at runtime.** Windows 11's taskbar caches the icon bitmap per AUMID at pin time and does not re-read it. Changing a launcher's icon requires unpinning and re-pinning. Launcher settings warns beside the pin button only when Windows confirms that the launcher is pinned and its effective details changed.

### Key rendering methods

| Method | Purpose |
|---|---|
| `ResolveBaseIconBitmap(Launcher)` | Single source of truth — returns 256×256 bitmap for a launcher's `TrayIconMode`. Handles Composite, Custom, named glyph presets, `Glyph:` gallery modes, and color presets (with fallback to Blue) |
| `RenderGlyphBitmap()` | Renders a complete glyph string: tinted GDI for Fluent icons, `GlyphIconRenderer.RenderEmoji()` for color emoji; shared by launcher and item icon exports |
| `GlyphIconRenderer.RenderEmoji()` | Shapes full Unicode sequences with DirectWrite, fits their ink bounds, draws native color glyphs with Direct2D, and returns an owned premultiplied-alpha bitmap |
| `RenderCompositeIconBitmap()` | Renders a 2×2 grid composite from first 4 launchable items (`"Composite"` mode) |
| `CollectLaunchableItems()` | Collects first N launchable items from a launcher, flattening groups, skipping headings/column breaks |
| `RoundedRectPath()` | Creates a `GraphicsPath` for a rounded rectangle (used by composite background) |
| `TrimAndResizeTo256()` | Trims transparent padding, centers on 256×256 canvas (called for presets + custom images) |
| `BitmapToIcoBytes(Bitmap)` | Converts bitmap to raw multi-resolution ICO byte array (16–256px). Bypasses `System.Drawing.Icon.Save()` which loses multi-resolution data on .NET |
| `BitmapToIcon(Bitmap)` | Calls `BitmapToIcoBytes()` then wraps in `System.Drawing.Icon` |
| `ResolveTrayIcon(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcon()` |
| `SaveToastIconToAppData(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → 256px PNG → write `toast-icon-{id}.png` for Windows notifications |
| `EnsureToastIconSaved(Launcher)` | Static. Returns that path, writing it first if absent — the notification path calls this |
| `SaveResolvedIconToAppData(Launcher)` | `ResolveBaseIconBitmap(Launcher)` → `BitmapToIcoBytes()` → write `app-icon-{id}.ico`; copies to `app-icon.ico` for first launcher |
| `SaveSettingsIconToAppData()` | First launcher's `ResolveBaseIconBitmap()` → gear overlay → `BitmapToIcon()` → write file |
| `RefreshLauncherIcon(Launcher)` | Batch-friendly: updates tray HICON, writes .ico to disk, re-reads it onto any regular-window web flyout (no settings save, no cleanup, no settings window refresh) |
| `UpdateTrayIcon(Launcher)` | Single-launcher convenience: calls `RefreshLauncherIcon` + `SaveSettingsIconToAppData` + `CleanUpStaleIconFiles` + `SettingsWindow.RefreshIcon()` |
| `EnsureLauncherIconSaved(Launcher)` | Static. Ensures `app-icon-{id}.ico` exists; called by pin flow before creating timestamped copy |
| `MigrateStaleIconPaths()` | Clears dead item IconPath values and restores portable launcher image bytes without clearing a missing custom launcher choice |

## Settings Window Icon Strategy

WinUI 3 has a known bug (WindowsAppSDK#2730) where the taskbar ignores `AppWindow.SetIcon()` for
windows in the same process as the exe's embedded icon. The workaround uses three layers:

1. **`SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings")`** — gives the Settings window its own
   taskbar group via the Shell `IPropertyStore` COM API, so the taskbar treats it independently.
2. **`AppWindow.SetIcon(IconId)`** via `GetIconIdFromIcon` interop — sets the app-level icon for
   Alt-Tab and the window's identity.
3. **`WM_SETICON`** (ICON_SMALL + ICON_BIG) — sets the Win32 window icon, re-sent on `Activated`
   to counteract WinUI's framework overrides.

## Pinned Taskbar Icon Strategy

The companion exe is deployed to `<AppDataDir>` by `EnsureFlyoutShortcut()` for **all build types** (portable, MSIX). This gives it a consistent, non-packaged location so the shell treats it like a normal app:

1. **`EnsureFlyoutShortcut()`** copies `LittleLauncherFlyout.exe` from the app directory to `<AppDataDir>`.
2. Writes a `main-exe-path.txt` breadcrumb so the companion exe can launch the main app if it's not running.
3. The companion exe loads `app-icon-{launcherId}.ico` from `AppContext.BaseDirectory` (same `<AppDataDir>`).

### Pin identity (all builds)

Pinning uses one shell identity source at a time:

- On supported Windows versions, the companion creates a physical Start menu snapshot at
  `Programs/Little Launcher Pins/{launcherId}/Little Launcher - {name}.lnk`. It targets the stable
  deployed companion with `--launcher {id}`, a fresh AUMID and an immutable pin icon. The window
  carries that AUMID but no relaunch metadata. `TaskbarManager.RequestPinCurrentAppAsync` requests
  Windows confirmation from the helper's foreground STA.
- Unsupported systems retain manual right-click pinning. Without a snapshot shortcut, window
  relaunch properties supply the command, name and `"iconPath,0"` resource. If a snapshot was created
  but Windows currently disallows a native request, manual pinning uses that shortcut instead.
  Never combine shortcut launch metadata with window relaunch metadata.
- `GetCurrentPackageFullName` must return `APPMODEL_ERROR_NO_PACKAGE`; executable location alone
  does not prove the helper is unpackaged. The helper also checks desktop-support marker
  `ITaskbarManagerDesktopAppSupportStatics` and the documented LAF registry value. Systems still
  requiring an unlock token use manual pinning. A packaged helper must never request the Settings pin.

The explicit pin flow:

1. Commit the name/address and wait at most eight seconds for site-icon adoption.
2. Save the launcher icon, copy it to `app-icon-{id}-pin{uniqueStamp}.ico`, and mint
   `LittleLauncher.Launcher.{id}.{uniqueStamp}`. Current app stamps are random GUIDs; the helper's
   legacy fallback still uses `TickCount64`. Both AUMID and icon filename must change.
3. Minimize launcher settings and launch `--pin --launcher {id} --name {name} --aumid {aumid}
   --icon {path}`. `ProcessStartInfo.ArgumentList` preserves quoted names.
4. Pass the saved identity separately as `--previous-aumid`. The pinning process sets its NEW
   identity once at startup before any UI or WinRT. It launches the same executable with
   `--pin --check-only` in a separate read-only process for the OLD identity. That child verifies
   the intact physical Start link matches the old AUMID, launcher arguments and deployed executable,
   then calls `IsCurrentAppPinnedAsync`. It reuses the guarded STA timer/pump unshown, with a
   five-second deadline and one-second cancellation grace, and never takes foreground or pins.
   `PreviousPinProcess` bounds the whole child to ten seconds and reaps it before any Start snapshot
   replacement. Timeout/unexpected exit remains Unknown; only that owned read-only child may be
   terminated. Failure to stop it aborts the attempt instead of changing the snapshot.
5. A completed false old-state query skips removal instructions. A known pinned launcher whose
   confirmed name/icon/command details are unchanged exits 4; settings shows an inline already-pinned
   status and leaves its identity/resources untouched. Changed details require removing the old pin
   before replacement only when Windows confirms the old pin exists. Unsupported, missing/mismatched
   shortcut, timeout or error stays Unknown diagnostically, but product policy permits a fresh pin
   without an old-pin warning or acknowledgement. History, missing metadata and an earlier pending
   marker alone do not force manual recovery. Users may remove any duplicate themselves. No taskbar
   registry scan is used to infer state, and Unknown is never logged as proof of absence.
6. The old-query process exits before the new snapshot is written. The pinning process never
   adopts the old identity or creates its WinRT manager. Its sole manager uses the NEW startup
   identity. Releasing an old manager or calling `RoUninitialize` is not a documented reset of
   process-scoped caller identity; process isolation avoids relying on such a reset.
   Native requests use a compact real
   foreground `PinWindow` showing the launcher name and "Look for the Windows pinning prompt."
   on a restrained rounded card. `PinWindow.Appearance.cs` supplies Segoe UI text, light/dark themes,
   high-contrast system colours, and per-monitor DPI layout. There is no classic caption strip,
   progress bar or action button. Native STATIC text remains accessible; `DrawIconEx(DI_NORMAL)`
   composites the original icon alpha over the card background, avoiding an `SS_ICON` white box
   without editing user icon files. It closes on the outcome. Only manual fallback shows the
   right-click pin instructions. The foreground owner is
   required by Windows; it must not be replaced with an invisible/offscreen request window.
7. Exit 1 means native success, 2 explicit manual-pin confirmation, 0 cancellation, 3 unknown
   request outcome, and 4 unchanged already pinned. Only 1/2 replace `PinAumid`. Snapshot/pending files remain local. Settings is restored on exit/error only while its host remains open.
8. A completed false result or explicit dialog Cancel restores the previous shortcut and pending marker bytes. Confirmation writes
   `pin-shortcut-{id}.txt` and local `pin-snapshot-{id}.json`: exact identity, display name, executable,
   arguments and SHA-256 of the effective immutable icon bytes. Optional metadata failure is logged;
   unreadable, invalid, missing or mismatched baseline stays unknown rather than unchanged/changed.
   Metadata uses source-generated JSON shared by both builds and is never serialized into settings.

### Live state in launcher settings

`LauncherSettingsWindow.TaskbarPinStatus.cs` owns the normal pin row subtitle and action state.
`TaskbarPinStatusService` asynchronously runs the production `--pin --check-only` phase in a separate
unshown process with the saved OLD AUMID. It does not minimize settings, take foreground, submit a pin,
or mutate shell resources. Only exits 4/5 mean known pinned/not pinned; all other results are Unknown.
The outer deadline is ten seconds, with two seconds to stop/reap only the owned read-only child.

Query on open and return from deactivation, after identity changes, and after unconfirmed/manual
pin outcomes. Cache the result for that identity between those events. Saves, typed names and
`MainWindow.LauncherIconSaved` only recompute effective name/icon/command differences against that
cached state, including automatic favicons and composites. Do not start one helper per keystroke.
Native success and an already-pinned result are fresh live evidence and seed the cache directly.

Children are serialized. A generation rejects stale results, close cancels the child and detaches
all subscriptions, and an explicit pin waits for the read-only child to exit before replacing a
snapshot. If that child cannot be stopped, report a check failure rather than proceeding with a new
pin. An earlier pending record does not select a different row or require manual recovery.

The independent pin helper can finish after launcher settings closes. Confirmed identity is still persisted, but closed-host continuations never restore/focus the HWND, update controls, or open an error dialog on a detached XamlRoot. Pin errors are logged; a dialog-reporting failure cannot escape the async event handler.

`PinAumid` is persisted in local settings and is also carried by the existing full-launcher sync serialization. Existing-launcher `CopyInto` merges preserve the local value, while a newly imported launcher can initially carry the source machine's identity. This release preserves that behavior. Missing local caller registration remains Unknown and uses the standard pin action. The separate snapshot/pending files are never synced.

Known not pinned uses the ordinary pin description with no removal warning. Known pinned with
changed effective details says to unpin and pin again; unchanged details show Pinned and disable
the redundant action. Known pinned with missing metadata does not assert the details changed.
Unknown/legacy state uses the ordinary pin description and enabled action, just like known absence.
This is a product policy, not a claim that an old pin was proved absent. An earlier pending marker or
saved snapshot alone does not show an old-pin warning. One `BuildRow` subtitle (font size 12,
0.5 opacity, wrapping) replaces the old third line and its arbitrary centered width cap.

Microsoft documents `IsCurrentAppPinnedAsync` for the current application, separately from the
foreground/Start-entry requirements for a pin request. Our physical Start-link validation is a
conservative caller-resolution guard, not a documented prerequisite for the state query itself.
Keep missing/mismatched links Unknown: this implementation has no verified general lookup for an
arbitrary saved AUMID after its source registration has disappeared. Do not infer absence from
incomplete taskbar caches. See the [state query contract](https://learn.microsoft.com/en-us/uwp/api/windows.ui.shell.taskbarmanager.iscurrentapppinnedasync).

All `-pin*.ico` snapshots are retained: a new attempt or cancellation does not prove an older pin
stopped referencing its file. This trades a small amount of disk space for preserving older pins.

> Windows caches names by AUMID and icons by identity and path. To change a pinned name or icon,
> remove the old pin and pin again. Native confirmation creates a new pin; it does not automatically
> replace the old entry.

For the legacy manual fallback, `RelaunchIconResource` must be `"path,0"`, never a bare path.
`WM_SETICON` uses the same snapshot, but a correct title-bar icon does not prove the pin resource is
valid. Pin-only diagnostics in `taskbar-pin.log` beside the deployed helper report package status,
eligibility, fallback reason, request start and result, with UTC timestamps. Windows returns false
for both a declined prompt and a denied request; the log does not claim to distinguish them.

See Microsoft's [pinning contract](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/pin-to-taskbar)
and [unpackaged desktop sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/TaskbarManager/CppUnpackagedDesktopTaskbarPin).

### A native request that does not finish

The native request flow has a 45-second deadline followed by a five-second cancellation grace period.
`IAsyncInfo.Cancel` only requests cancellation. A completed true result saves the new identity;
a completed false result restores the previous snapshot. Canceled/error status or a request still
pending after the grace period exits with code 3, preserves its shortcut/icon, and leaves the saved
`PinAumid` unchanged. Unexpected destruction/quit of the reminder card is also unknown; ordinary close requests cannot
stop its deadline timer. The settings window says the result was not confirmed and a pin may still
appear. It does not require removal before retrying. No uncertain outcome is treated as success.

Before replacing the Start snapshot, the helper writes local `pin-pending-{launcherId}.txt` beside
its stable executable, containing the exact attempted AUMID. This also guards against an unexpected
helper exit. The marker is neither serialized nor synced. Regular-window mode rejects a pending
snapshot even if an older confirmation record still matches the saved identity.

A subsequent attempt may request native Windows confirmation again. The marker alone never adds
`--manual` or an old-pin acknowledgement. Only a fresh live Pinned result can require manual removal
of an old pin. `--manual` remains an explicit helper option, and unsupported native systems still use
the manual fallback. Confirmation saves the new identity and clears the current marker. A completed
false/cancelled dialog restores the previous shortcut bytes and earlier marker; an unresolved current
request retains its new snapshot/marker. All immutable pin icon files remain available. Do not treat
`IAsyncInfo.Cancel` or an unknown old-state result as permission to delete late-pin resources.

Pin diagnostics include status heartbeats every 15 seconds, advisory cancellation HRESULTs and the
unconfirmed outcome. Narrow `pin-window` milestones report card creation, appearance, layout and
ready dispatch; native-window logs report timer arming and its first callback. `eligible` means only
preparation succeeded, not that Windows received a pin request. A failed ready-message queue exits
Unknown through cleanup instead of waiting without a timer. Only terminal operations are closed; an active native request is never described
as successfully cancelled merely because `Cancel` returned success. See
[Microsoft's cancellation contract](https://learn.microsoft.com/en-us/windows/win32/api/asyncinfo/nf-asyncinfo-iasyncinfo-cancel).

The startup identity rule follows [SetCurrentProcessExplicitAppUserModelID](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-setcurrentprocessexplicitappusermodelid).
`TaskbarManager.GetDefault` returns the [object active in the current process](https://learn.microsoft.com/en-us/uwp/api/windows.ui.shell.taskbarmanager.getdefault),
not a guaranteed fresh identity object. Process isolation enforces the documented lifetime; it does
not by itself prove that caller caching caused any particular missing prompt.

### Read-only pin-state verification

Run the same old-identity phase without any pin submission:

```text
LittleLauncherFlyout.exe --pin --check-only --launcher {id} --previous-aumid {savedAumid}
```

Exit 4 means known pinned, 5 known not pinned, and 3 unknown. It only reads the old shortcut and
writes diagnostics; it does not replace a shortcut, icon, metadata or saved identity, and does not
show/foreground its query window. The log records `previous-pin: pinned`, `not-pinned`, or an unknown
reason. This reuses the production query path and requires no mouse/keyboard automation.

### Diagnosing a blank pin icon

**`SHGetPropertyStoreForWindow` does not round-trip across processes.** Reading another process's
window property store returns `<EMPTY>` for every key, and *writing* to it returns `S_OK` from both
`SetValue` and `Commit` and still reads back empty. An external tool therefore cannot tell whether
the companion's relaunch properties were applied — do not conclude "the properties are unset" from
an outside read, as that is wrong. To actually check, log the HRESULTs and read the values back
**inside** the companion, in `--pin` mode (which already shows a modal, so it is not on the
latency-critical flyout path).

What is known good: `WM_SETICON` lands (both icon handles are set, which is why the *title bar*
icon looks right — it does not come from the relaunch properties), and the per-launcher `.ico`
exists and loads at the taskbar's requested size. A blank pin was last seen only in a dev
environment that had been run from several paths and switched between packaged and unpackaged
builds; treated as dev-only rather than chased further.

### Testing pin behaviour

Pin identity is **path-based and persistent**, so it cannot be tested from a throwaway directory:
`EnsureStartMenuShortcuts()` re-points the Start Menu shortcut at whatever exe is running, and a
pin records the path it was created from. Run the build from a stable location when testing this
flow. (A temp-folder test run left the Start Menu shortcut aimed into `%Temp%`, which is what
"Windows is searching for LittleLauncherFlyout.exe" looks like afterwards.)

Debug builds also deploy the **framework-dependent** companion (~175 KB apphost + its `.dll`,
`.deps.json`, `.runtimeconfig.json`) where Release deploys the self-contained AOT binary (~1.7 MB).
Both embed the app icon via `<ApplicationIcon>`, so the icon itself is not the difference — but
prefer a Release build when validating anything about pinning.

**Pin snapshots are created only by explicit pinning.** They live under `Little Launcher Pins`,
separate from live web-launcher discovery shortcuts under `Little Launcher`. Startup cleanup removes
only the old root-level flyout shortcuts, never the current pin snapshots.

> **WARNING:** Windows caches pin display names per AUMID in CloudStore. Changing the display name format requires changing the AUMID format to bust the cache. See `docs/architecture.md` and repo memory `msix-taskbar-pinning.md`.

## Adding a New Preset Icon

1. Add the PNG file to `Resources/AppIcons/` (transparent background, square)
2. Add entry to `PresetIcons` dictionary in `MainWindow.xaml.cs` with the next mode number
3. Add a `ComboBoxItem` with colored `Ellipse` + `TextBlock` in `LaunchersPage.xaml.cs` (`BuildIconModeCombo`)
4. Bump the Custom mode number in: `ResolveBaseIconBitmap()` and `BuildCustomIconRow` in `LaunchersPage.xaml.cs`
5. Add `<Content Include="Resources/AppIcons/NewColor.png">` to `.csproj` (or use the existing `*.png` glob)

## The selfh.st tab

`IconGallery`'s fourth tab lists the [selfh.st](https://selfh.st/icons/) icon set, fetched from
GitHub (`index.json`) and jsDelivr (the PNGs). **Everything it touches is cached on disk**, in
`icons\selfhst\` — a folder of its own so that `icons\` stays what it has always been, the icons
actually in use.

- **The index** resolves memory → disk → network, with `SelfhStCatalogTtl` (6h) measured on the
  cached file's write time. The in-memory copy dies with the process, and the tab cannot draw
  anything until the whole index has arrived, so without the disk copy every first visit after a
  restart sat on a spinner. If the fetch fails, a **stale** disk copy is served rather than failing
  the tab, and is treated as current for the rest of the session — otherwise every keystroke in the
  search box re-attempts a download that has just timed out.
- **The icons** are downloaded by `LoadSelfhStThumbnailAsync` and written to
  `icons\selfhst\{reference}.png`, at most `SelfhStDownloadGate` (8) at a time. Binding
  `BitmapImage.UriSource` straight to the CDN URL — which is what this replaced — kept nothing
  between visits, so the same hundred icons came over the wire again on every open.
- **Selecting an icon copies** the browse-cache file to `icons\selfhst-{reference}.png` rather than
  downloading it again. That path is what gets stored, and its `selfhst-` prefix is what
  `IsSelfhStImagePath` / `IsMatchingSelfhStImagePath` (pre-selecting the current icon) and
  `IsAutoFetchedIconPath` (leaving user-chosen icons alone) both key off — do not change the shape
  of that filename without those three.
- Reference names come from a remote index, so they go through `SelfhStFileName` before being used
  as a filename.
- Nothing prunes the browse cache. It only grows by what the user has actually looked at (≤120
  icons per view, a few KB each), which is why that is acceptable and a stored icon is kept
  outside it.

A selfh.st tile download must also match the latest click within its current tab content before it may highlight a tile or replace pending selection. Tab/query/open guards alone do not reject an earlier click that finishes after a later click in the same view. A failed latest download retains the preceding valid pending choice; older in-flight clicks never replace it afterward.

## Regenerating `LittleLauncher.ico`

`Resources/LittleLauncher.ico` is the embedded exe icon. It **must be regenerated** whenever `Resources/AppIcons/Blue.png` changes. The `.ico` is not auto-generated by the build — it's a committed binary.

To regenerate, create a multi-resolution ICO (16, 24, 32, 48, 64, 256px) from `Blue.png` using System.Drawing or any ICO tool, and overwrite `Resources/LittleLauncher.ico`. If the `.ico` is stale, the exe will show an outdated icon everywhere the shell reads it.

## Start Menu Shortcut

Nothing creates a Start Menu shortcut before the app runs: the portable build has no installer, and MSIX registers its own entry from the manifest. On first launch, `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` writes `Programs\Little Launcher.lnk` with the exe's embedded icon, and `UpdateShortcutIcons()` re-stamps it when the user changes the icon. The shortcut must sit directly in `ProgramMenuFolder` (not a subfolder) so the app's runtime management can find and update it — `RemoveLegacyMsiSubfolderShortcut()` clears the subfolder copy the retired MSI used to create.

## Launcher item names on hover

List and large-icon item templates show the full name in a tooltip only when the visible label is actually trimmed. Small-icon items have no label and always offer the name on hover. `Controls/ConditionalNameToolTip.cs` shares the behavior across all three templates, including apps, websites and PWAs, and targets the item container so its icon and padding work too. The same behavior attaches directly to real group-header labels in all three modes and to column captions, targeting only that label and offering its full text when trimmed. Fully visible names have no redundant tooltip. See [xaml.md](xaml.md) for lifecycle conventions.

## Launcher Item Icons (Favicons & App Icons)

`FaviconService.FetchMissingItemIconsAsync(IEnumerable<LauncherItem>)` is the **unified pipeline** for fetching launcher item icons. It handles both websites (favicon download) and apps (exe icon extraction). All bulk import paths must use this single method — do not duplicate the fetch logic.

Entry points that call the pipeline:
- **Startup**: `MainWindow.FetchMissingIconsOnStartupAsync()` — fire-and-forget, covers settings-import-then-restart and machine migration scenarios. Also runs the stale-icon refresh (below).
- **Sync download**: `SftpSyncService.DownloadLauncherItemsAsync()` — awaited before save.
- **File import**: `LauncherBulkOps.ImportItemsAsync()` — awaited before save.
- **Manual add/edit**: calls `FaviconService.FetchAndCacheAsync()` / `GetApplicationIcon()` directly for the single item in the dialog.
- **Drag-and-drop into the flyout**: `DroppedItemFactory.EnrichAsync()` — runs *after* the items are inserted and visible, so the drop itself never waits on the network. Dropped items carry a Segoe Fluent placeholder glyph (open / globe / folder / page) until the fetch lands.

### Getting a *good* icon, not just an icon

Two paths fetch a site's icon, and both must aim higher than the browser favicon:

- **Launcher items and bookmarks** — `FaviconService` reads the page HTML, prefers the **web app
  manifest**'s largest icon, then `<link rel="icon|apple-touch-icon">` picking the largest declared
  `sizes`, then the Google favicon service, then `/favicon.ico`. An `apple-touch-icon` with no
  `sizes` attribute is scored **180** rather than 0 — it is 180px by convention, and scoring it zero
  let a 16px shortcut icon that merely appeared first in the HTML win.
- **Web launchers** — `CoreWebView2.GetFaviconAsync` returns what the page declared for a *browser
  tab*, commonly 32 or 64px, and a tray icon (and far worse, a taskbar pin) is drawn much larger
  than that. Upscaling it is why a freshly created web launcher looked soft enough that picking a
  replacement by hand was the obvious workaround. `AdoptHighResPageIcon` takes a better one instead:
  the page bridge finds the best icon the site declares — manifest, then apple-touch-icon, then
  sized link icons — and **fetches it in the page**, because those assets sit behind the same login
  as the page and a host-side fetch gets a redirect. Anything under 96px is ignored, since it is not
  worth replacing Chromium's favicon with something no bigger. It still only ever replaces an icon
  that is ours to replace (`MayAdoptPageIcon`), and never in bar mode, where the tray icon stands
  for several sites rather than one.

### Stale icon refresh

`FaviconService.RefreshStaleItemIconsAsync()` re-fetches **auto-fetched** item icons whose cached file is older than `FaviconService.IconMaxAge` (7 days), so favicons, extracted app icons, and PWA icons don't rot as sites and apps update. Rules:

- **Custom icons are never touched.** Auto-fetched vs custom is decided by `IsAutoFetchedIconPath()`: auto-fetched icons all live in the `favicons` cache folder (`{host}.png`, `app_{name}.png`, `pwa_{aumid}.png`); user-chosen images live in the separate `icons` folder (`custom-*`, `selfhst-*`) or an arbitrary path.
- **Failures keep the existing file**; `FetchAndCacheCoreAsync` only writes on success, so icons only ever improve. A failed fetch leaves the old timestamp, so it's retried on the next check.
- Items sharing a cache file (same host) are deduped per pass via a shared visited-path set.
- Triggered from `MainWindow`: on startup (inside `FetchMissingIconsOnStartupAsync`) and by `_iconRefreshTimer`, a daily `System.Threading.Timer` (the tray app stays resident for weeks, so launch-only would not be enough). The timer callback marshals to the UI thread via `App.MainDispatcherQueue` before touching `IconPath`. After any change, `ApplyItemIconChanges()` saves settings, invalidates flyouts, and re-renders tray/settings icons (Composite mode embeds item icons).
- **Cache-busting:** the refresh rewrites cache files in place (same path), so the item-icon `BitmapImage` loads in `IconPathToImageConverter` and `LauncherItemsView.CreateIconTile` set `BitmapCreateOptions.IgnoreImageCache`; without it, WinUI's per-URI decoded-image cache keeps showing the old bitmap for the rest of the session.

The add/edit dialog's unified app/PWA picker shows its own **transient list icons** (not the persisted item icon): `AppPickerService.LoadIcons` extracts them via `ShellIcons.Extract` (`IShellItemImageFactory`, 32px BGRA → `WriteableBitmap`) on a background STA thread. These are display-only — the item's saved `IconPath` is still produced by the `FaviconService` calls above when a row is selected.

**Gotcha:** `IShellItemImageFactory.GetImage` returns `E_PENDING` (0x8000000A) the first time it's asked for an icon the shell hasn't rasterized into its cache yet — common for a freshly-seen PWA/Store tile. `ShellIcons.Extract` **must retry** on `E_PENDING` (short sleeps) or those icons silently come back blank while already-cached ones (e.g. a PWA you've launched before) work. This is why a PWA can show no list icon for no obvious reason.

**Raster-only:** `FetchAndCacheCoreAsync` saves whatever it downloads to a `.png` file rendered by WinUI `BitmapImage`, which **cannot decode SVG**. A site whose favicon/manifest icon is an SVG would otherwise be cached as a `.png` that displays blank. `RasterOrNull`/`IsRasterImage` (magic-byte check for PNG/JPEG/GIF/BMP/ICO/WEBP) rejects SVG and other non-raster payloads at each fetch step, so websites continue down the favicon fallback chain and **PWAs fall back to the shell-extracted icon** (`GetPwaIconFromShell`) — which is why a PWA could show its list icon (shell) but a blank selection/preview icon (the rejected SVG web icon). `GetPwaIconFromShell` requests 64px and, like the 32px list path, must retry on `E_PENDING`.

For Chromium PWAs, prefer the site's own icon/manifest asset when the AUMID encodes a domain (for example `example.com-HEX_hash!App`). The shell image factory often returns a softer rasterized bitmap than the original web icon. `GetBestPwaIconAsync()` handles this preference, but it must reject off-origin login redirects and skip generic Google favicon-service fallbacks for PWAs; when the site does not expose a real app icon, it should fall back to `GetPwaIconFromShell()`.

After any bulk icon change, call `FlyoutWindow.InvalidateItems()`. That compatibility entry point refreshes every registered `LauncherItemsView`, including open regular editors, and composite tray icons. Forced invalidation rebuilds containers even when an in-place cache rewrite keeps the same path. Closed regular editors unregister their view; native popup views remain for warm reopening.

## Gotchas

- **MSIX VFS redirection** — see the [dedicated section below](#msix-vfs-redirection). All file paths referenced by external processes (shell `.lnk` files, companion exe) **must** use the physical path from `MainWindow.GetPhysicalAppDataDir()`, never raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX and invisible to the shell.
- **`LauncherItem.IconGlyph` must be a Unicode character**, not a text name. Use `""` (globe, `U+E774`) for websites and `""` (open, `U+E8E5`) for apps. Text strings like `"Globe24"` render as rectangle tofu in `FontIcon`.
- **`LauncherItem.IconGlyph` can hold a complete emoji sequence**, for example `"🚀"`, `"👩‍💻"` or `"❤️"`. Use `IconGallery.IsFluentGlyph()` to distinguish Fluent PUA glyphs from emoji. Fluent previews use `FontIcon`; emoji previews use `TextBlock` with `FontFamily = "Segoe UI Emoji"` and `IsColorFontEnabled = true`. Bitmap exports must use `GlyphIconRenderer`, including composite and jump-list icons.
- **Icon Gallery** (`Classes/IconGallery.cs`) has one shared core for item and launcher pickers. The item wrapper exposes App Icons, Glyphs, Emoji and selfh.st plus Reset; the launcher wrapper replaces App Icons with Launcher Presets (Composite, app colors and, for a web launcher, Use page icon). Both retain search, selection highlighting, Confirm/Cancel, attribution and the same pinned confirmation footer. Browse for image is an option inside the first App Icons/Launcher Presets tab only. `getCurrentSelection` is read on every opening, including after Confirm or Cancel; selecting a tile remains pending until Confirm. Custom images also remain valid pending selections when no gallery tile represents them. Empty paths and glyphs do not enable Confirm.
- **Gallery geometry uses the monitor work area, not owner-window height.** Callers supply their actual HWND. `ShouldConstrainToRootBounds = false` lets the picker escape compact settings windows; a definite monitor-capped height gives its star-row content host a finite viewport with no minimum height. Large symbol catalogs use a directly hosted `GridView` with its own scroller; the small preset/image lists use a sibling `ScrollViewer`. Never put the catalog view inside that scroller. Search, tabs, palette, attribution and Confirm/Cancel remain outside that shrinking viewport. Browse belongs inside the first tab's scrolling options. Physical monitor bounds are converted to DIPs using the owner window's DPI, including scaling on secondary monitors. The virtualized symbol grid wraps to the available width, with at most nine columns. Small image/preset/selfh.st lists use responsive wrapping within a horizontally constrained scroller. Full-width option text has a finite Star column and wraps rather than widening the extent.
- **All icon Browse actions use `IconGallery.PickImageAsync(ownerHwnd)`**, accepting ICO, PNG, JPG, JPEG and BMP. In launcher settings, the gallery and custom-file row share the same browse/commit path. Files are copied into the physical AppData directory before the launcher switches to Custom; confirming the existing file skips a copy onto itself. The preview and filename refresh together, and file/picker failures are reported. Cancellation leaves the saved selection unchanged. selfh.st selections keep their existing cache and attribution behavior.
- **A notification's icon comes from `toast-icon-{id}.png`, not the adopted page icon.** It used
  to read `web-favicon-{id}.png` directly, which is written *only* for a web launcher nobody
  has chosen an icon for — so a launcher wearing a custom image or a glyph had no file at
  that path, `SetAppLogoOverride` was skipped, and Windows fell back to Little Launcher's own
  logo. Every toast showed the app's icon rather than the site's, upscaled from the small
  manifest logo and blurry with it. Resolving through `ResolveBaseIconBitmap` fixes both at
  once: the right icon, at 256px.
- The bundled `.ico` is the Blue rocket only — it's the exe identity icon and fallback.
- Tray visibility state is now tied to each launcher's stable GUID identity via `NOTIFYICONDATA.guidItem`, not the runtime-assigned `uID`. Launcher reordering, name changes, and icon changes do not reset the user's tray preference; deleting/recreating a launcher still will.
- `SaveResolvedIconToAppData()` always writes an `.ico` for all modes (including mode 0). There is no "delete and fall back to exe icon" path.
- The companion exe (`LauncherShortcut/Program.cs`) loads `app-icon.ico` from `AppContext.BaseDirectory` for the pin dialog via `LoadImage` + `WM_SETICON`.
- `BitmapToIcoBytes()` / `BitmapToIcon()` produce multi-resolution ICO (16, 24, 32, 48, 64, 256) so tray icons render correctly at all DPI scales. `BitmapToIcoBytes()` writes raw ICO bytes directly — do NOT use `System.Drawing.Icon.Save()` which loses multi-resolution data on .NET.
- `MainWindow` listens for `UISettings.ColorValuesChanged` and refreshes the tray icon, `app-icon.ico`, and SettingsWindow icon when the OS theme changes. Uses `RefreshLauncherIcon()` (batch-friendly) for all launchers, then does a single `SaveSettingsIconToAppData` + `CleanUpStaleIconFiles` + `RefreshIcon` pass.
- `MigrateStaleIconPaths()` runs before `SetupTrayIcons()`: item paths retain their existing cleanup, while custom launcher bytes are materialized locally and unavailable launcher paths/modes are retained.
- When the user changes `TrayIconMode`, `UpdateTrayIcon()` also calls `SettingsWindow.RefreshIcon()` to update the settings window titlebar immediately.

## MSIX VFS Redirection

MSIX packages use **filesystem virtualization (VFS)**. When the packaged app calls `Environment.GetFolderPath(ApplicationData)`, it returns the standard `%AppData%` path, but writes are **silently redirected** to a package-specific location:

```
%LocalAppData%\Packages\<PackageFamilyName>\LocalCache\Roaming\
```

Files written to this redirected path are **invisible** to external processes (the Windows shell, the companion exe, any other non-packaged process) that read from the standard `%AppData%` path.

### The Problem

Icon files (`app-icon.ico`, `settings-icon.ico`) and the companion exe (`LittleLauncherFlyout.exe`) are written by the MSIX-packaged app. The shell reads `.lnk` shortcut `IconLocation` fields and the companion exe loads icon files — both operate outside the VFS.

### The Solution: `GetPhysicalAppDataDir()`

`MainWindow.GetPhysicalAppDataDir()` returns the **real filesystem path** that external processes can see:

- **MSIX**: `ApplicationData.Current.RoamingFolder.Path` → resolves to the `LocalCache\Roaming` path
- **Unpackaged (portable)**: `Environment.GetFolderPath(ApplicationData)` → standard `%AppData%` (no redirection)

### Rules

1. **Any path that will be referenced by the shell or external processes** (`.lnk` IconLocation, companion exe file reads, Start Menu shortcuts) → use `GetPhysicalAppDataDir()`.
2. **Internal-only paths** (settings.xml read/written only by the main app, favicon cache) → `Environment.GetFolderPath(ApplicationData)` is fine because VFS redirects reads too within the same package context.
3. **Companion exe** should use `AppContext.BaseDirectory` to find `app-icon.ico` — since the exe resides in the physical AppData dir, this resolves correctly for both MSIX and unpackaged.
4. **Never hardcode `%AppData%\LittleLauncher`** in any code path that writes files referenced externally. Always call `GetPhysicalAppDataDir()`.

## Virtualized symbol catalogs

`Classes/IconCatalogView.cs` displays both the 427 Fluent entries and 496 emoji entries.
`IconGallery` passes the existing category arrays, name filter, current glyph and tint to it.
Filtering builds lightweight data records only. `CollectionViewSource` projects named groups
into a `GridView` whose `ItemsWrapGrid` uses `CacheLength = 1` (one viewport of total prefetch,
rather than the default four). Its own scroller receives the finite star-row viewport directly.
The framework creates and recycles nearby tiles instead of creating the whole catalog when a
tab opens. There is no background-thread XAML construction or fake loading spinner.

Category headers remain visible when browsing and hidden during search, matching the previous picker. Tiles retain their existing sizes (Fluent glyphs 18px, emoji 22px inside 40px tiles), full Unicode strings, color fonts, tooltips and accent selection outline. Native single selection keeps keyboard navigation and focus visuals. The native presenter's selected-border properties use the stock GridView theme resources for normal, hover, pressed, disabled and inner strokes; selection does not change the container's fixed zero border or tile geometry. Native GridViewItem selection drives the outline on opening and recycling, while the container callback reapplies accessible names, tint and tooltips. Do not compare container.Content during ContainerContentChanging phase zero: WinUI 1.8.5 raises that event before assigning the incoming content. Tab changes and close clear the source and invalidate old work. The selfh.st async selection callback also checks its request is still current after caching. Tabs and the symbol grid have meaningful automation names for assistive tools.

Use actual realized `GridViewItem` counts to verify virtualization while opening and scrolling,
not merely the presence of `ItemsWrapGrid`. Counts should remain well below the complete catalog
at this gallery's normal height. Verify the last categories remain reachable, search stays usable,
selection survives recycling, and Cancel/reopen restores the saved glyph and color. See
[Microsoft's WinUI collection performance guidance](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview)
and the [App SDK 1.8 CacheLength contract](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemswrapgrid.cachelength?view=windows-app-sdk-1.8).

## Automatic page icon choice

Use page icon is a web-only pending choice inside Launcher Presets, not a separate settings-row
button. `IconResult.UsePageIcon` distinguishes it from an ordinary image or Composite preset.
Both availability and the current adopted selection are read from the live launcher on every
opening, including after changing launcher type in the same settings window. The option is
selected even before a managed favicon has been fetched.

`AutomaticPageIconChoice` displays the actual decoded managed image, or an explicit missing,
loading or failed state. Opening reads the saved file only. Retry obtains a raster from an
already loaded, awake, owned tab at the launcher's current canonical address first. If that
cannot supply one, it tries the actual site directly over HTTP, without starting, resuming or
navigating a browser. A failed retry retains the last good preview. Preview bytes and
their source URL stay in memory until Confirm; Retry never calls the mutating adoption methods.
Closing cancels the request and invalidates its generation. Address/type changes reject results.

`WebFlyoutWindow.IconPreview.cs` shares the bridge's exact `bestIconUrl` discovery function,
fetching through the loaded page's authenticated context without posting an adoption message.
CDP and favicon reads have bounded waits, reject suspension/navigation and cap payloads at
512 KiB. The expanded service-worker bridge must remain unchanged by extraction.

`FaviconService.IconPreview.cs` provides the direct fallback as bytes only. It shares existing
HTML/manifest/icon selection with optional preview request policy, skips all local cache reads
and writes, and never invokes the Google favicon service. Requests use `Cache-Control: no-cache,
no-store`, cancellation for headers/body reads, a linked 15-second direct-attempt deadline,
512 KiB manifest/image limits and the existing 64 Ki-character HTML read. The chooser's complete
request remains bounded to 20 seconds, including any preceding loaded-page wait. Ordinary item
favicon fetches pass no preview options and keep their existing cache/fallback behavior.

HTTP does not inherit WebView sign-ins. A failed, unsupported or timed-out result retains the
last good preview and saved custom icon, with text asking the user to open the launcher, sign
in if needed, then retry. No browser is initialized or resumed automatically. The captured
launcher address/type and open/request generation still gate every result before use.

Confirm first validates the preview's exact launcher address/type, then `Classes/PageIconPreview.cs` atomically writes the exact decoded pending bytes to `web-favicon-{id}.png`. Explicit confirmation accepts a smaller image; the larger-icon safeguard applies only to background page adoption. Without a saved or fetched raster it restores the empty Composite adoption state so the next page load can provide an icon. Never copy that managed file into `custom-tray-icon-*`, which would disable subsequent automatic adoption.

The settings preview refreshes immediately after confirmation. The caller explicitly refreshes tray/window/derived icons even when the managed path and Custom mode are unchanged, since those assignments raise no property notifications. The launcher card bypasses the URI image cache when reloading a managed file. Cancel leaves the chosen custom icon untouched, and shortcut/item pickers never show the page option.

`dotnet run --project tests/PageIconChoiceRegression` checks the production choice lifecycle and atomic writer with synthetic smaller transparent and larger opaque PNGs, including Confirm/reload, Cancel, failed Retry, source changes and disposal. Its UI adapters allow Linux execution; actual WinUI decoding and native icon surfaces still require Windows acceptance. See the test README for that checklist.

Native validation on the dedicated WINDOWS-DEV VM passed with the combined clean 1.41.0.4 Developer MSIX and a synthetic localhost fixture: Retry left the opaque 512px saved file unchanged, Confirm persisted the exact transparent 32px PNG, the settings preview and reopened chooser displayed it, and Retry followed by Cancel retained its hash. The coordinator supplied results and screenshots, retained locally in the ignored `.artifacts/native-icon-retry` directory. This validates native settings/chooser decoding and managed-file persistence, not private Outline discovery, app restart, other native icon surfaces, high contrast, accessibility, or native URL/type-change behavior. See `tests/PageIconChoiceRegression/README.md` for the remaining acceptance scope.

## Restoring and revealing the saved selection

On opening, the saved glyph selects its own catalog tab and native selected item. Saved presets,
page adoption and custom images select their corresponding options; unmatched custom images are
represented by the Browse choice. Browse is only in the first options tab for both picker kinds.
The item picker's App Icons tab is therefore first, followed by Fluent, Emoji and selfh.st.

`SelectionReveal.AfterLayout` is shared by catalog and option selections. It waits for a live
viewport and a layout pass, then dispatches a one-shot reveal at low priority. Catalog selections
use `GridView.ScrollIntoView`; option buttons use `StartBringIntoView`. Source/request versions,
current selection and open state guard the callback; tab changes, typing and close cancel it.
Do not reset the scroller to zero unconditionally from `Flyout.Opened`, which would undo the
restored selection. This does not change selection or move keyboard focus.

For selfh.st, reserve a slot for the saved match if it lies beyond the first 120 unfiltered results.
Keep at most 120 choices/thumbnails and preserve search filtering. Async preselection can then
highlight and reveal the saved option after its content attaches. Match bundled and selfh.st
images against the exact persisted cache directory and filename contract, not arbitrary user
basenames. A custom file called `app-blue.png` outside the app cache is still a custom image.
`IconCatalogGroup.ToString` returns its category name so automation does not expose a helper type.

Small image lists use `VariableSizedWrapGrid`; large symbol catalogs retain virtualized
`ItemsWrapGrid`. The former is intentionally limited to small/bounded lists and must never
replace the catalog's virtualizing panel. See the App SDK 1.8 contracts for
[ScrollIntoView](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.listviewbase.scrollintoview?view=windows-app-sdk-1.8)
and [VariableSizedWrapGrid](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.variablesizedwrapgrid?view=windows-app-sdk-1.8).


## OS-specific tray visibility

A nonempty shortcut launcher whose normal OS projection has no entries hides its tray icon without changing `NIconHide`, launcher ID, or saved items. An empty new launcher and web launchers remain reachable. `MainWindow.UpdateTrayIconVisibility` combines this derived policy with explicit hiding; item persistence and icon refresh re-evaluate it. Settings cards explain foreign-only hidden launchers. Linux owns equivalent StatusNotifierItem connections and keeps its Settings route available even if all launcher icons are hidden. The new Windows native behavior remains unqualified until the Windows build bridge is available.

## Native Linux launcher-card header

`linux/little_launcher/settings.py` uses the existing `LauncherIcons.resolve` result at16 logical pixels beside each launcher name. Header move/delete actions use the native icon theme with Qt standard fallbacks. These are Settings header controls, not new tray identities or saved icon choices. The passive card and reference-sized action rows live in `launcher_card.py`.

## Syncing custom launcher images

`Services/PortableLauncherIcon.cs` and `linux/little_launcher/portable_icons.py` transfer `Launcher.CustomTrayIconData` alongside Custom mode. The payload captures the selected file on a detached model (1 MiB maximum), validates raster signatures/base64 and materializes into `icons/sync-{sha256}` with a format-appropriate ICO or content-detected image suffix. Windows uses `GetPhysicalAppDataDir`. All transports use this common payload boundary; the live model is not mutated during serialization. Linux import/OneDrive download performs the equivalent materialization before publishing the document.

A missing local path no longer clears a launcher's custom selection at Windows startup. An older-peer merge with no bytes/unavailable path retains a valid local custom copy. A new file choice clears old embedded content, while JSON deserialization preserves it independent of field order. Existing path-only cloud copies need a new upload from a device retaining the original file; already-lost image content cannot be recovered from the path. This fixes custom-image portability, not unsupported Linux Fluent glyph rendering. See [sync.md](sync.md) for account boundaries and the linked-production-code regression suite.


## Native Linux bookmark bar icons

`linux/browser/bookmark_icons.cjs` provides a bounded per-launcher/profile memory cache for same-site favicons. The session fetch omits credentials and redirects, limits responses to256KiB and five seconds, accepts raster icon MIME types and rejects oversized decoded dimensions before resizing to32px PNG. Two requests run concurrently;128 site entries cap cache/queue growth. Saved `IconPath` files from another OS are never opened or replaced. Loaded-page `page-favicon-updated` metadata refreshes the `/favicon.ico` fallback, including declared CDN HTTP(S) raster icons with no credentials or HTTPS downgrade. Repeated URLs revalidate rather than retaining a stale image. Unsupported Electron raster formats use bounded native Qt normalization through request tokens; late replies cannot overwrite newer icons. Trusted chrome accepts only data images and draws16px fixed slots with folder/globe vectors when missing. No third-party service, manifest discovery or disk cache is implemented.

Caption visibility uses the Windows properties `WebBookmarkIconsOnly` and per-entry `IconsOnly`. Global mode takes precedence and disables the per-entry checkbox without changing its saved choice. Tooltips and accessible names retain captions and URLs, and icon arrival does not change button geometry. Browser icon presentation remains distinct from portable tray-icon bytes. See [the detailed parity audit](linux-parity-audit.md).


## Native Linux launcher window icons

`LauncherIcons.window_png` rasterizes the selected color/custom/composite/fallback icon to a64px request and returns bounded PNG presentation data. BrowserHost passes it separately from portable settings and excludes raw CustomTrayIconData/Items. `browser/window_icon.cjs` accepts bounded PNG data with dimensions up to128px (allowing device-scale rendering), decodes it and falls back to the packaged app icon if invalid. BrowserWindow receives it at creation/remapping and setIcon updates live configurations. Custom files that decode to no icon also fall back through the native resolver. Qt Launcher Settings, shortcut flyouts and regular editors use that resolver too.

Private KDE Wayland screenshots validate distinct actual green/purple icon pixels despite a shared temporary application identity and no installed preview desktop entry. The earlier generic W was caused by omitting a native icon; desktop registration is not needed to fix the titlebar on this engine/compositor. Per-launcher taskbar/pin identity remains separate. Trusted tab chrome also displays validated same-site favicons in fixed16px slots, with globe fallback and unchanged label geometry.

## Native Linux preset and Unicode chooser

`icon_glyphs.py` maps six semantic presets through the desktop icon theme with vector fallbacks and understands the Windows legacy numeric ordering only at rendering time. Full `Glyph:` Unicode strings, including joined emoji and optional `#RRGGBB` color, are shaped by Qt. Known Fluent aliases map to their semantic preset; arbitrary private-use glyphs require an already installed Segoe Fluent Icons font. Unsupported values remain in the document and render a fallback; no proprietary font is bundled.

`icon_picker.py` is a pending native preset/symbol/emoji/selfh.st/custom-image chooser. It restores the saved tab, glyph and color; Confirm applies one guarded launcher change and Cancel preserves the original. This is a native subset, not the full Windows Fluent/selfh.st catalog. Native file/folder shortcuts use `QFileIconProvider` after custom/site/desktop resolution. `NativeThemeMonitor` compares palette/font/style/icon-theme snapshots before refreshing tray, Qt and browser icons, avoiding a theme-event refresh loop. See the current parity audit and `dev/verify_icon_picker.py` for synthetic pending-state verification.

## Linux shortcut and public website icon selection

Shortcut rendering honors a decodable IconPath first, then IconGlyph/IconColor and native fallback. Glyph RGB/ARGB colors and joined Unicode strings are retained; legacy Open24/Globe24 are render aliases, never document rewrites. ShortcutDialog holds pending icon fields until Save. IconPicker supports item and launcher modes and restores preset/color/image choices. Unsupported private-use glyphs stay intact; full Fluent/selfh.st catalogs remain open.

`site_icon.py` fetches only an explicit public origin favicon with no cookies, reused authentication or redirects. Replies are bounded to256KiB/five seconds and dimensions1024px before64px PNG normalization. Bound QObject slots handle cancellation/late replies safely. Fetch only previews; Confirm atomically writes a hash-named image in the controlled icon cache. Launcher selections use existing portable bytes; custom shortcut images remain path-only and are not yet cross-device portable. No third-party fallback or manifest search is used.

## Website icon and single chooser parity

Launcher Settings now has one icon-preview/Choose icon button. Its old mode combo and separate custom-file entry point are internal only; all visible changes go through the pending chooser. Composite is offered for shortcut launchers, using the actual Items to preview, and omitted for web launchers. Existing web Composite metadata is retained until the user chooses a replacement.

Electron observes page-declared favicon URLs only for the launcher home origin. CDN-hosted icons can be fetched without cookies through the existing bounded raster loader; only bounded raster image bytes cross the inherited pipe; native Qt validates and normalizes them to64px PNG. Candidates are capped at three, reject embedded credentials/nonHTTP/downgrades, and stale navigation results are ignored. BrowserHost revalidates current launcher/home identity, byte/base64 and raster bounds before keeping a device-local memory preview. Removing a launcher clears its cache. No page DOM, sign-in data or arbitrary renderer IPC is exposed.

For a loaded web launcher, Website icon offers a visible Use website icon preview. Open the launcher and let it load first if that option is not yet available; the public favicon fetch and file browsing remain alternatives. Settings binds the preview to its captured home address. Confirm alone writes a hash-named custom image and performs the guarded settings change, flowing through existing portable launcher-icon bytes. Cancel preserves the saved choice. Full Fluent/selfh.st catalog parity remains open.

Loaded-site bookmark icons follow browser-observed same-host www/non-www and HTTP-to-HTTPS redirects through a bounded local alias map; unrelated login origins and HTTPS downgrades cannot replace the original site's icon. YouTube's real non-www bookmark now uses the www loaded-page favicon. Up to three declared raster candidates are tried rather than giving up on a failed first declaration. The extension toolbar control uses a theme-colored vector puzzle piece; pinned extension buttons retain their packaged icons.

## Linux selfh.st gallery and portable glyph previews

`linux/little_launcher/selfhst.py` reads the same GitHub index and jsDelivr PNG collection as Windows. Cache lookup uses memory/disk/network, six-hour index age, stale offline fallback and explicit retry. The public network manager never reuses cookies/authentication or follows redirects; catalog bytes are capped at3MiB/10,000 entries, safe references become cache filenames, and PNGs at256KiB/1024px. Four icon requests run concurrently,128 decoded source entries cap memory, and search renders at most120 native tiles. The chooser includes attribution/license links, accessible names, selected highlighting and a pinned native Confirm/Cancel footer. Failed or late downloads cannot replace a newer pending choice. Cancel only leaves the browsing cache; Confirm alone creates a hash-named chosen image. Browse image is in the first presets tab. New local custom images are normalized to bounded PNG for cross-platform consumption.

CustomTrayIconData still transfers selected image bytes; per-device paths are only materializations. Both merge paths retain a usable local custom image when an older peer supplies no bytes and an unresolved path. `TrayIconPreviewData` plus matching `TrayIconPreviewMode` additionally transfers the actual glyph/emoji rendering, avoiding missing licensed Windows fonts on Linux. Both clients preserve matching received pixels instead of replacing them with a platform fallback. PNG header/dimensions and byte bounds are checked before decoding; changing the mode invalidates the old preview. Windows source produces native256px glyph PNGs; Linux uses the native Qt glyph renderer only when available. No proprietary Windows font is bundled. Both full Windows Release architectures now compile/package; native rendering acceptance on Windows/laptop remains separate. Portable production model/sync cases also run in isolated .NET10.


## Icon chooser layout parity follow-up

Windows Launcher Settings has no separate custom-image Browse button. Its custom-file row only identifies the current saved image; Browse for image remains inside the first chooser tab, using the existing image-selection commit path.

Linux `icon_picker.py` now follows the compact Windows gallery structure: a shared search field above the tabs, scrollable Launcher presets/App icons, Symbols, Emoji and selfh.st content, and a pinned native Confirm/Cancel footer with a small pending preview. Website choices and Browse for image are in the first tab instead of a separate website destination. Symbol color swatches precede the symbol grid; choosing another symbol retains the pending color, while Emoji keeps its native colors. Search covers local labels and selfh.st name/tag/category metadata. Qt owns the palette, tabs, corners and dialog-button ordering. The symbol catalog remains a native subset, not a claim of the complete Windows Fluent catalog or a bundled Windows font.

Saved Unicode choices restore to Symbols or Emoji. Controlled local selfh.st filenames restore their catalog selection and reveal it after layout; a saved match beyond the first120 reserves a visible slot without exceeding that bound. Custom Browse and page choices retain pending selection and Confirm-only writes. `dev/verify_icon_chooser_layout.py` qualifies actual KDE geometry, search, web-only choices, symbol color persistence, overflow and Cancel; the live selfh.st picker/portable-byte fixture and all152 native/data regressions pass. The settings opening-position combo also now shrinks within its existing200px preferred width so its adjacent Choose button cannot force a narrow form beyond the viewport.

The swatches paint bounded colored raster icons rather than changing text foreground roles, which Breeze can override. The real KDE driver checks each displayed button’s center pixel against its color. The current signed x64 Release was installed in the Windows VM: native UI Automation verified the396px search control, exactly one chooser-owned Browse action, Cancel returning to Settings, and no external Browse button. SignatureKind Developer and the executable inside its InstallLocation were verified. Windows capture through the RDP framebuffer was unavailable; acceptance here is native control/geometry verification, not a screenshot comparison.

Installed follow-up: the final color-swatch AppImage replaced the same user file and relaunched one native KDE/Breeze owner. Mounted sources/image digest and exact settings, permission and extension-catalog hashes were verified; OneDrive metadata and startup preference were retained. Evidence: `.artifacts/linux-native/icon-chooser-parity/installed-update.json`. The Windows change is installed in the test VM; this task did not refresh the earlier laptop Downloads package.
