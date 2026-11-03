# Component and namespace map

Paths in this reference are relative to the LittleLauncher project unless stated otherwise.

`Models/WebLauncherPresets.cs` owns messaging service creation templates. The Add Launcher
menu creates independent, editable web launchers from them and opens their sign-in pages.

`Windows/WebFlyoutWindow.VideoFit.cs` owns optional fitting of contained fullscreen to a video's
intrinsic aspect ratio, including monitor bounds, pending-fit cancellation and temporary sizing.

`Windows/WebFlyoutWindow.FullscreenTitleBar.cs` owns the fullscreen header overlay,
top-edge hover detection, a contained-fullscreen maximize control that preserves page fullscreen,
and returning its existing controls to the normal chrome on exit.

`Windows/WebFlyoutWindow.ContentFullscreen.cs` owns the conditional fullscreen-video header
action, visible-target detection, and the user-initiated browser fullscreen request.

`Windows/AppPickerRowActions.cs` owns the unified app picker's full-target tooltips and
keyboard-accessible row context menus. Open shares `FlyoutWindow.Launch`; containing-folder
is available only for existing filesystem parents. Duplicate labels are computed against
the full catalog in `ItemEditorWindow` and exposed by `AppPickerEntry` without changing saved names.

`Windows/LauncherSettingsSections.cs` owns the ordered IDs and localized section labels shared
by launcher settings and the web flyout menu. `LauncherSettingsWindow.Sections.cs` assembles
sections with their initial expansion defaults and reveals explicit destinations; the
`WebGeneral`, `WebAppearance`, `WebSize`, `WebBehavior`, and `WebPrivacy` partials hold the
focused web control builders.
`WebFlyoutWindow.SettingsMenu.cs` provides quick controls and links into the same owned settings
window. Advanced retains menu-only troubleshooting.

`SettingsWindow.ItemEditing.cs` opens `Windows/LauncherItemsEditorWindow.cs`, a regular owned Mica window with normal title bar/app icon, shared toolbar, two-axis viewport and external Done. `Controls/LauncherItemsView` is the single item renderer/edit implementation shared with the native flyout through `ILauncherItemsHost`. Settings cards/navigation remain visible normally. The editor preserves intrinsic item geometry and scrolls rather than reflows; edits save immediately. Done/Escape/close ends it, while minimize/app switching retains it. Same-launcher tray/taskbar entry focuses it or its owned dialog; different launchers can edit independently. New-launcher setup opens the same regular editor at the captured setup bounds/monitor, owned by Main Settings. No persisted geometry or launcher identity changes are involved.

Web launcher cards use a single **Settings** row with the address or **No web address set** as its subtitle, opening the same launcher settings window and refreshing the cards on return.


## Behavior and flow

Read [architecture.md](architecture.md) for application routing, windows, persistence, icons, sync, taskbar integration and packaging. This map identifies the owning modules; the topic guides describe their contracts and conventions.

## Key namespaces

| Namespace | Contents |
|---|---|
| `LittleLauncher` | App, MainWindow, SettingsWindow (+ `SettingsWindow.ItemEditing.cs`, regular item-editor orchestration) |
| `LittleLauncher.Classes` | NativeMethods, ThemeManager, IconGallery (one shared item/launcher chooser), IconCatalogView (grouped, virtualized Fluent/emoji tiles), SelectionReveal (cancellable reveal after layout), AutomaticPageIconChoice (pending automatic-icon preview and retry UI), PageIconPreview (source-bound bytes and exact atomic Confirm storage), GlyphIconRenderer (DirectWrite/Direct2D color emoji bitmap exports), ShellIcons, WindowChrome |
| `LittleLauncher.Classes.Settings` | SettingsManager |
| `LittleLauncher.Controls` | WrappingPanel (natural-width control rows, used by regular editor actions), LauncherItemsView (+ `.Platform.cs` (OS container visibility in full-source editing), `.Rendering.cs`, `.Editing.cs`, `.Hover.cs` (passive full-group/item cues and pointer subscriptions), `.DragDrop.cs`, `.DropTargets.cs` (shared real-group edge/interior targeting and passive feedback), `.DragPreview.cs` (mode-sized in-view drag card and live hint above it), `.ExternalDrop.cs`, shared templates/item geometry/commands), ILauncherItemsHost (window/modal/completion boundary), LauncherItemReorder (window-free move validation and column flattening), LauncherDropGeometry (numeric real-group boundary policy), ConditionalNameToolTip (shared full-name hover behavior for hidden or trimmed item labels), PackedIconPanel, TabStripPanel (the tab strip's squeeze-to-fit row), OverflowStripPanel (the bookmark bar's row, and how many bookmarks it could not fit) |
| `LittleLauncher.Models` | LauncherItem (its `ToString()` is the accessible name for every flyout row), Launcher, WebBookmark, BrowserExtension, SshConnectionProfile, SyncProviders, SharedSyncModes, LauncherActions (the numbered commands a taskbar jump list carries through the companion exe) |
| `LittleLauncher.Pages` | All settings pages, LauncherBulkOps (item export/import, bookmark import), BookmarkPicker (searchable single-bookmark chooser, in a `ContentDialog` for full-size windows) and BookmarkPickerView (the chooser itself, with no opinion about its host) |
| `LittleLauncher.Services` | LauncherSyncService (global-sync entry point), SftpSyncService, CloudSyncService (+ `ICloudFileStore`, OneDriveFileStore, GoogleDriveFileStore, WebDavFileStore, OAuthPkceClient, ProtectedStore, CloudTokenStore, CloudSyncCredentials), FolderSyncService, CloudFolderService, LauncherPayload, PortableLauncherIcon (bounded custom-image/glyph-preview payload/cache files), AutoSyncService, FaviconService (+ `FaviconService.IconPreview.cs`, cancellable direct-site preview bytes with no cache mutation or Google fallback), UpdateService, AppPickerService, AppCatalog, BrowserCatalog, BookmarkImport, DroppedItemFactory, StartMenuShortcutService (per-web-launcher Start Menu shortcuts), WebProfileCleanupService (finds and deletes WebView2 profile folders nothing points at), JumpListService (the taskbar jump list on each pinned launcher's button), TaskbarPinStatusService (bounded isolated read-only live pin state), WebViewEnvironments (one CoreWebView2Environment per user-data folder, shared by every browser on it), BrowserExtensionService (the extension list, CRX/ZIP unpacking, and loading them onto each profile) |
| `LittleLauncher.ViewModels` | UserSettings, AppPickerEntry |
| `LittleLauncher.Windows` | FlyoutWindow (+ `FlyoutWindow.EditMode.cs`, native edit chrome; `FlyoutWindow.ItemsHost.cs`, shared-view host and compatibility invalidation; `FlyoutWindow.SetupPlacement.cs`, optional transient native anchor), LauncherItemsEditorWindow (regular Settings-origin host, same shared view and owned secondary dialogs), WebFlyoutWindow (+ `WebFlyoutWindow.Tabs.cs` ; every browser the flyout owns, the tab strip, its per-tab right-click menu and new-window handling; `WebFlyoutWindow.Permissions.cs` ; site permissions and page notifications; `WebFlyoutWindow.ServiceWorker.cs` ; the service-worker bridge, notification actions and inline reply; `WebFlyoutWindow.TaskbarButton.cs` ; regular-window mode, its taskbar button and pin identity; `WebFlyoutWindow.MoreMenu.cs` ; the header's "…" menu of per-moment launcher options; `WebFlyoutWindow.PositionPicker.cs` ; the graphical anchor picker that drops out of the maximize button on hover; `WebFlyoutWindow.Bookmarks.cs` ; the bookmark bar, and adding, editing, reordering and removing bookmarks from inside the flyout; `WebFlyoutWindow.Shortcuts.cs` ; the browser keyboard shortcuts and the sheet that lists them; `WebFlyoutWindow.Extensions.cs` ; catching a store install and standing in for the missing extension toolbar; `WebFlyoutWindow.Session.cs` ; the tabs a launcher had open, restored on its next open; `WebFlyoutWindow.BrowserPages.cs` ; the `chrome://`/`search://` addresses a real browser answers and WebView2 does not; `WebFlyoutWindow.StripDrag.cs` ; the drag source a `Button` cannot be on its own, shared by the tab strip and the bookmark bar; `WebFlyoutWindow.FolderPopup.cs` ; the bookmark-folder lists, drawn as an overlay in the window's own tree because a `Popup` can never receive a drag), LauncherPanels, EditToolbarWindow, ItemEditorWindow, AppPickerRowActions (app-picker row tooltips and context actions), TextPromptWindow, LauncherSettingsSections (shared ordered destinations), LauncherSettingsWindow (+ `LauncherSettingsWindow.Sections.cs`, section assembly and reveal; `LauncherSettingsWindow.Web*.cs`, focused web editors; `LauncherSettingsWindow.TaskbarPin.cs`, companion lifecycle; `LauncherSettingsWindow.TaskbarPinStatus.cs`, live cached state and effective-detail comparison), ShortcutsWindow, ExtensionPopupWindow (an extension's own popup, and the window an extension gets when it asks for one), BookmarkPickerWindow (the browser-bookmark chooser, for the flyout, which cannot host a `ContentDialog`) |
| `LauncherShortcut` | Program (launch signaling), TaskbarPinning (live old identity, native request and manual fallback), PreviousPinProcess (isolated bounded old-identity query), PinWindow (+ `PinWindow.Appearance.cs`, native reminder card/query window), PinShortcut (Start snapshot and rollback), ShellInterop (AOT-safe COM), NativeMethods (native imports) |
| `little_launcher` (Linux) | PlacementBridge (KDE/device-local coordinates), AnchorPicker (native pending grid), WebSessions (device-local tab addresses), BookmarkPicker/BookmarkSource (native read-only browser selection), CloudSyncPage/Destination (native Windows-reference provider layout), OneDrive/SecretStore (PKCE, cloud HTTP and desktop credentials), LauncherSync/SyncJob (guarded application and worker lifetime), LauncherApplication (lifetime and routing), LauncherController (full-document transactions), DocumentStore (atomic lossless storage), BookmarkTree (Qt-independent bookmark source paths and mutations), BookmarkActions (native bookmark forms and guarded saves), ItemsEditor and ShortcutFlyout (regular/in-place hosts), LauncherGeometry/GroupPlacement (reference layout and full-group bounds), ItemGroup/GroupOverlay (semantic containers and passive drop feedback), LauncherItemsView and ItemButton (shared native rendering), ItemEditingSession (full-source commands), ItemDrag and ItemDropArea (session-bound drag targets), ItemEditToolbar and ActionFlowLayout (compact/regular controls), FloatingEditToolbar (owned popup bar), NameDialog (shared compact text prompt), BookmarkAddressDialog (validated single-address prompt), BrowserHost (private Electron pipe), ApplicationList (shared searchable native app source), LauncherDialog/SettingsRow/SectionHeader (launcher-specific native sections and Done boundary), LauncherCard/LauncherCardsColumn/LauncherActionRow (native Windows-reference Settings cards) |
| `LittleLauncher.Shared` | PinSnapshot and its source-generated JSON context, linked into both projects for local confirmed name, effective icon-byte and launch-command comparisons |

**Note:** The `LittleLauncher.Windows` namespace shadows the WinRT `Windows.*` namespace. Use `global::Windows.` prefix when accessing WinRT types (e.g. `global::Windows.Graphics.PointInt32`).

**Note:** `LittleLauncher.Models.Launcher` conflicts with other framework types. Use `using Launcher = LittleLauncher.Models.Launcher;` at the top of any file where disambiguation is needed.


## Native Linux implementation

The separate `linux/` tree contains a PySide6/Qt Widgets desktop host and a current Electron browser process. Read [linux.md](linux.md) before modifying native Linux UI, persistence, desktop integration, browser boundaries, or packaging. `linux/README.md` records runnable verification and the remaining release gates. Whole shortcut-launcher ownership uses observable `Launcher.Platform` and Linux `launcher_supported`. Foreign launchers stay visible but disabled. Item-level OS projection and its Windows helper/partial were removed; all original items are retained and edited without OS handling.

## Portable icons, OneDrive and device disable

Windows `Services/PortableLauncherIcon` captures bounded `Launcher.CustomTrayIconData` on detached sync payloads and restores it under physical AppData before merging. Missing remote paths no longer erase a valid local custom icon. Local `UserSettings.DisabledLauncherIds` gates tray visibility, centralized activation, panel lifetime, warm-up/queued background preload and Start Menu shortcuts; its state is absent from global sync.

Linux `OneDrive`, `SecretStore` and `SafeRedirect` in `onedrive.py` own app-folder HTTP, PKCE loopback auth and desktop-wallet storage. `LauncherSync`/`SyncJob` in `sync.py` own worker lifetime, backups, revision checks, conflict/compatibility guards and optional timers. `portable_icons.py` owns portable bytes/cache files. The controller's separate local notification applies disable state without modifying the portable document. See [sync.md](sync.md).

## Native parity follow-up ownership

Linux `sync_page.py` contains `CloudSyncPage` and `Destination` for the Windows-reference account/provider layout. `web_preferences.py` owns the trusted browser mutation policy. Electron `browser_identity.cjs` owns the actual Chromium UA, and `web_menu.cjs` owns Windows-ordered menu construction. `window.cjs` applies mode/size/zoom/reload policies without losing live tab identity; `profiles.cjs` owns explicitly confirmed profile maintenance. The temporary preview uses unique application identities and a changing ID allowlist so downloaded launchers receive the same compositor placement without affecting installed app windows.

The reported main-process crash is an optional-network-metadata failure: Electron WebRequest can omit `webContentsId` for service-worker requests. `browser/request_contents.cjs` resolves only live supplied contents or bounded positive integer IDs and handles disappeared renderers. `Profiles.requestOwners` attributes renderer requests directly and conservatively protects matching session views for worker uploads until completion. Missing senders never resolve to an unloaded tab; capture-frame lookup also rejects detached/missing frames safely. `dev/verify_service_worker.cjs` reproduces an actual worker fetch without a renderer ID, verifies completion and hidden-page upload protection, and confirms no remote control bridge. The supplied crash is fixed; fresh unsigned-in WhatsApp and the saved Edsby HTTPS origin load without unsupported-browser rejection. Signed-in service flows still need user acceptance. See [Electron's optional WebRequest metadata](https://www.electronjs.org/docs/latest/api/web-request).


Linux `browser/bookmark_icons.cjs` owns `BookmarkIcons`, a bounded credential-free, site-keyed favicon presentation cache with loaded-page CDN refresh and token-scoped native raster normalization, separate from synced bookmark metadata and portable tray icons. Trusted chrome renders the shared global/per-entry icon-only rule with full tooltips/accessibility. Regular-window placement uses a temporary `LittleLauncherWindow` caption until activation and preserves native KDE title/controls and ordinary taskbar/focus behavior. See [linux.md](linux.md) and [icons.md](icons.md).


Linux `browser/tab_menu.cjs` builds tab commands in Windows order. `web_actions.cjs` owns live-ID callbacks, background duplication, sequential protected bulk closes and bounded in-memory reopen history; `window.cjs` rejects duplicate pending closes. `forms.py` supplies `BookmarkAddressDialog`, and `bookmarks.py` owns separate caption/address/home transactions. Home changes compare tree and home preimages and preserve the bookmark tree. `dev/verify_context_actions.cjs` qualifies renderer dispatch and actual pending-upload close protection using only disposable private data.


Second Linux parity ownership: `icons.py`/`browser/window_icon.cjs` carry bounded rendered window icons through presentation, independent of synced file paths. `web_sessions.py` owns device-local tab persistence; `BrowserHost` forwards only current identities and `window.cjs` owns deduplicated save/restore/final-close behavior. `browser_bookmarks.py` owns read-only source projection, and `bookmark_picker.py` owns the browser/profile/search picker. `strip_drag.cjs` resolves bar projections to full source paths, with `BookmarkTree.reorder` applying guarded moves. Launcher settings advances its original preimage after each completed live edit. See [linux.md](linux.md).

Linux `external_drop.py` owns standard native URI-drop validation/feedback and commits via `ItemEditingSession.add_external`. `icon_glyphs.py` owns semantic/legacy/Unicode rendering, while `icon_picker.py` owns pending native choice. The native theme monitor deduplicates actual changes before application refresh. Remote page zoom is isolated per WebContents even when sessions/sign-ins are shared; trusted toolbar scale remains fixed.

Third Linux parity ownership: `positions.py` contains the native QtDBus `PlacementBridge` and device-local coordinates; `anchor_picker.py` owns the pending native position grid. `browser/bookmark_overlay.cjs` owns the local folder/overflow native view, `bookmark_overlay.js` its nested UI, and `bookmark_drag.js` the shared host-bound local drag session. `browser/fullscreen.cjs` owns page fullscreen and the trusted exit/maximize surface; `fullscreen_geometry.cjs` owns ratio bounds. All local IPC routes pass `WebLauncher.trustedToolbar/handleToolbar`, including strict main-frame checks, and remote pages remain bridge-free. See [linux.md](linux.md).

## Linux AppImage and shortcut continuation

| Component | Responsibility |
| --- | --- |
| `linux/little_launcher/shortcut_import.py` | Validated browser-bookmark bulk append through the guarded full-document controller |
| `linux/little_launcher/site_icon.py` | Explicit bounded credential-free public favicon preview; Qt reply lifetime |
| `linux/little_launcher/updates.py` | Stable GitHub release/asset policy, verified staging, atomic AppImage replacement and recovery backup |
| `linux/little_launcher/update_controller.py` | UpdateJob/UpdateController worker lifetime, local check preference and normal-quit restart intent |
| `linux/little_launcher/update_page.py` | Native About UpdateControls with wrapping actions |
| `linux/packaging/` | Sole Linux format: private-runtime x64 AppImage, baseline Debian12, locked type-2 runtime and relocated bootstrap |

Settings About owns UpdateControls; System retains default-on user login startup. AppImage desktop entries point to the stable file; temporary previews never install startup. `app.py` releases the single-instance server/lock before invoking update installation/restart. See [installer.md](installer.md#native-linux-appimage-delivery) and [linux.md](linux.md).

## Linux browser extensions and site permissions

| Component | Responsibility |
| --- | --- |
| `linux/browser/permission_store.cjs` | Atomic, bounded per-profile/per-origin durable notification decisions |
| `linux/browser/page_notifications.cjs` | Standard page Notification/Permissions presentation, authenticated notification-only CDP transport and native click ownership |
| `linux/browser/extension_host.cjs` | Managed code catalog, per-session restore/suspend, popup lifetime and revocation |
| `linux/browser/extension_registry.cjs` | Registered remote-view identity, selection and session-keyed extension RPC dispatch |
| `linux/browser/vendor/browser-extension-compat/` | Attributed extension API compatibility source and supporting navigation helpers |
| `linux/browser/build-vendor.cjs` | Reproducible build-only TypeScript transpilation for packaged compatibility scripts |
| `linux/little_launcher/extension_packages.py` | Generic bounded local ZIP/folder import into hash-named managed packages |
| `linux/little_launcher/extensions_page.py` | Native management dialog, profile selector and package-worker lifetime |

System and per-launcher More open the native dialog. BrowserHost carries only trusted parent management commands; extension pages/workers use their own authenticated profile boundary and cannot access the app UI bridge. See [linux.md](linux.md#linux-site-permissions-and-extensions).

Linux `worker_notifications.cjs` authenticates and delivers service-worker notifications; `notification_worker_preload.cjs` implements the standard worker API and event callbacks in a sandboxed compatibility preload. `notification_frame.cjs` implements page registration calls through normal web MessageChannels. Worker client focus/open compatibility requires a current native notification-click task. PageNotifications has a separate bounded standard-notification-only CDP transport; ordinary pages retain no Node/preload or general app IPC. WebLauncher owns tab activation without remap/reload, inert renderer bootstrap and readiness/lifecycle guards. See the Linux guide for grant/rate/lifetime boundaries.

Linux `browser_identity.cjs` additionally owns the user-requested Google bare-Chrome experiment: a serialized accounts.google.com-only Chromium emulation override with normal identity restored before leaving; Profiles keeps outgoing headers consistent without reloading or replaying navigation. `BookmarkIcons` owns bounded aliases for observed same-host redirects and declared-raster candidate fallback. `dev/verify_google_identity.cjs` and `dev/verify_public_favicons.cjs` use disposable profiles and public unsigned-in pages; neither proves successful real account authentication.

Linux `selfhst.py` owns a bounded, credential-free native catalog/PNG browsing cache; `IconPicker` owns search, pending selection and Confirm-only permanent files. `portable_icons.py`/Windows `PortableLauncherIcon` carry custom image bytes and exact-mode PNG glyph previews, and preserve available local images when an older peer sends only unresolved paths. Windows activation uses `Launcher.IsSupportedOnThisOperatingSystem` through UserSettings.IsLauncherEnabled; Linux activation uses the equivalent controller guard. See icons.md and user-settings.md.

The native shared shortcut view also delegates editing-only edge gestures to `linux/little_launcher/item_resize.py`. Its detached live preview never persists; `ItemEditingSession` owns the release-only transaction.

Linux manual sync uses `LauncherSync.prepare_upload`/`confirm_upload` and `SettingsWindow._review_upload` for checked native review; automatic paths share complete-document fingerprint classification and remain conflict-conservative. `SettingsManager.IsFirstRun` and `MainWindow.ApplyFirstRunStartupAsync` own one-time Windows login startup, while SystemPage reads actual OS state. See sync.md and installer.md for boundaries and qualification.
