# LittleLauncher/Windows

`FlyoutWindow` is the native per-launcher popup. `LauncherItemsEditorWindow` is the regular Settings-origin item editor, matching Launcher Settings Mica/chrome/app icon. Both host the same `Controls/LauncherItemsView` implementation through `ILauncherItemsHost`; do not duplicate item templates or editing logic, or reparent a native popup into another window. The regular host owns its inline toolbar, scrolling viewport and external Done, with immediate saving and no blue window outline. Settings remains normal behind it. Same-launcher invocation focuses the editor or its owned dialog, and host teardown closes its secondary windows and unregisters the view.

Native `ExitEditMode` restores flyout activation after hiding the non-activating toolbar. Editing can survive deactivation, and clicking floating Done does not activate its owner; without restoration, the already-inactive popup cannot receive the next deactivation and remains stuck open. Keep this in the native host. Regular Done closes its editor and must not acquire native activation or dismissal behavior. The guarded native regression suite covers direct Done after a foreign-window switch and the body-return control.

The regular editor stretches the shared toolbar presenter across its padded body. The view uses that finite viewport width for a wrapping add-action column and a separate settings icon pinned at the top right. Its existing footer Done shares the same visible right edge. Native toolbar sizing and intrinsic item geometry remain independent of this regular-window layout.

Read this before touching drag/drop, edit mode, column logic, or flyout sizing. It covers the geometry contract (edit mode may grow height, never width or item size), the `_launcher.Items` vs `_columnLists` source-of-truth rule, and why height is computed arithmetically rather than measured:

Read and follow [drag-drop.md](../../docs/drag-drop.md) before changing the code described above.

It uses a transparent backdrop and custom drag handlers. Follow the WinUI 3 XAML conventions — including the "owned windows, not ContentDialog" rules that govern `ItemEditorWindow`, `TextPromptWindow`, and `LauncherSettingsWindow`:

Read and follow [xaml.md](../../docs/xaml.md) before changing the code described above.

Flyout item rendering, favicon/app-icon fetching, and `InvalidateItems()` are part of the icon pipeline — read [docs/icons.md](../../docs/icons.md) (and `FlyoutConverters.cs` guidance there) when changing how items or icons render.

`WebFlyoutWindow` is the other kind of flyout: a WebView2 on a launcher's URL, routed to by
`LauncherPanels`. Read this before changing its lifecycle — the browser is deliberately built late
and torn down early, and several WinUI WebView2 limits are worked around there:

Read and follow [web-launchers.md](../../docs/web-launchers.md) before changing the code described above.

The same guide governs `WebFlyoutWindow.FullscreenTitleBar.cs`, including its fullscreen
header overlay and hover lifecycle.
It also governs `WebFlyoutWindow.ContentFullscreen.cs`, which detects visible video targets
and requests page fullscreen from the header.
`WebFlyoutWindow.IconPreview.cs` follows both [web-launchers.md](../../docs/web-launchers.md)
and [icons.md](../../docs/icons.md). Preview reads must not start/resume/navigate a browser
or adopt icons before Confirm.

`LauncherSettingsWindow.TaskbarPin.cs` owns companion pin requests. Its
`LauncherSettingsWindow.TaskbarPinStatus.cs` partial owns live pin state, effective-detail comparison
and the normal row subtitle/button. Read [icons.md](../../docs/icons.md) and
[xaml.md](../../docs/xaml.md) before changing them. Query the old identity through the isolated
read-only helper on open/activation, not on every name/icon edit. Keep generation/cancellation and
all subscriptions scoped to the window lifetime, and reap the query before pin replacement.
Effective pin metadata remains local-only in `Shared/PinSnapshot.cs`.
