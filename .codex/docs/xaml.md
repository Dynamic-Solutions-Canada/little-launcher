> **Scope:** Use when editing XAML files for WinUI 3 controls, Fluent Design, NavigationView pages, or resource dictionaries. Covers WinUI 3 control conventions, resource localization, and Mica/Acrylic backdrop patterns.
> **Governs:** `**/*.xaml` (all XAML across the solution), `**/LauncherSettingsWindow.Sections.cs`, `Controls/LauncherItemsView*`, `Controls/ILauncherItemsHost.cs`, `Controls/WrappingPanel.cs`, `Windows/LauncherItemsEditorWindow.cs`.

# WinUI 3 XAML Conventions

## Namespaces

Standard WinUI 3 pages use these default namespaces:
```xml
xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
```

## Localization

- String resources live in `Resources/Localization/Dictionary-en-US.xaml`
- Reference via `{StaticResource KeyName}` in XAML (resource dictionary is merged in App.xaml)
- In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`
- Always add new string keys to the dictionary when adding UI text

## Pages

- Pages are `Page` objects (not `UserControl`)
- Navigation via WinUI 3 `NavigationView` with `TargetPageType` in XAML
- No MVVM routing framework — direct page type references

## Controls

- Use `Border` with `{ThemeResource CardBackgroundFillColorDefaultBrush}` and `{ThemeResource CardStrokeColorDefaultBrush}` for settings cards
- Use `ToggleSwitch` for boolean settings
- Use `NumberBox` for numeric inputs
- Use `FontIcon` with Segoe Fluent Icons glyphs

## Conditional item-name tooltips

`Controls/ConditionalNameToolTip.cs` is the shared attached behavior for full item names.
Bind `ConditionalNameToolTip.Text` on the template root. When the template shows a label,
set `ConditionalNameToolTip.LabelName` to its named `TextBlock`; the behavior reads
`IsTextTrimmed` and listens to `IsTextTrimmedChanged`, including wrapped, two-line labels.
Omit `LabelName` for an icon-only template, where the name is always available on hover.

For group headers and column captions, attach `ConditionalNameToolTip.Text` directly to the `TextBlock` (or call `SetText` for a code-built label). A TextBlock root is its own label and tooltip target, so no namescope lookup is needed and its tooltip cannot appear over group children. It uses actual `IsTextTrimmed` and the same rename/load/unload cleanup; omitting `LabelName` here does not make the tooltip unconditional.

The behavior attaches the native tooltip to the nearest `SelectorItem`, covering both list
and grid item icons, labels and padding. Its bound text follows renamed/recycled items;
load/unload wiring resolves the current container and releases label subscriptions and any
open tooltip. Keep state with the template, never in a permanent window's container collection.
Do not infer clipping from string length, force layout, change item geometry, or add redundant
tooltips to fully visible names. Native tooltip placement can extend beyond the flyout window.
Existing item focus/tab-stop behavior is preserved.

## Measuring a string, not a control

Layout arithmetic sometimes needs to know how wide a piece of text draws: `FlyoutWindow` sizes its
list columns to the longest label a launcher holds. There is no text-metrics API to ask, so the
answer comes from a `TextBlock` that exists only to be measured:

```xml
<Canvas x:Name="LabelMeasureHost" IsHitTestVisible="False" Opacity="0">
    <TextBlock x:Name="LabelMeasure" TextWrapping="NoWrap" />
</Canvas>
```

- **In the tree, not beside it.** An unparented element has no `XamlRoot`, so it resolves neither
  the window's font nor its scale, and it is the *real* font that decides the width. Parked in the
  window's own tree it inherits both.
- **A `Canvas` is what makes it free.** It measures children against infinity and reports no size of
  its own, so nothing here reaches the surrounding layout.
- Set the properties that matter (`FontSize`, `FontWeight`, `Text`), call `InvalidateMeasure()`,
  then `Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))` and read
  `DesiredSize.Width`.
- **Treat zero as "no answer", not as "no width".** Before the tree is live there is nothing to lay
  text out against, and a caller that reads that as a real width will size something to nothing.

This is not the measure-after-layout trap: it measures a string, never a laid-out container, so no
result can feed back into the geometry that produced it. See the "compute, don't measure" rule in
[drag-drop.md](drag-drop.md).

## Data Binding

- Bind to `SettingsManager.Current.<Property>` for settings
- Use `Mode=TwoWay` for editable settings
- Use `UpdateSourceTrigger=PropertyChanged` when immediate feedback needed

## Backdrops

The web launcher's fullscreen titlebar reuses its existing header by moving it from the chrome
stack into the root grid's content row as a top-aligned overlay. Its hover visibility must not
reveal the address, tab, or bookmark bars, which explicitly check fullscreen state. See
[web-launchers.md](web-launchers.md) and `WebFlyoutWindow.FullscreenTitleBar.cs`.

- **SettingsWindow** uses `MicaBackdrop`
- **FlyoutWindow** uses a transparent backdrop

## Code-Built Dialogs

The add/edit item form in `ItemEditorWindow` is built entirely in C# (not XAML). Conventions:

- All input controls (`TextBox`, `ComboBox`, `ToggleSwitch`) use `HorizontalAlignment = HorizontalAlignment.Stretch` to fill the dialog width uniformly.
- The form container is a `StackPanel` with `MinWidth = 460`, wrapped in a `ScrollViewer` so a tall form never overflows off-screen.
- When a row needs a stretch input + a fixed button (e.g. path + Browse), use a `Grid` with `Star` + `Auto` column definitions instead of a horizontal `StackPanel`.
- Labels are created via a `Label(string)` helper that returns a styled `TextBlock`.

### Unified app/PWA picker

There is **no type dropdown**. The target is chosen via a two-item `SelectorBar` tab strip — modelled on the picker in the sibling `CopilotRekey` project:

- **"Apps & web apps"** tab — a single searchable list (`AutoSuggestBox` + `ListView`, fixed `Height = 260`) showing installed applications and PWAs together, each with an icon.
- **"File or link"** tab — the manual path/link `TextBox` + Browse, the arguments box, and the website "Open as app window" toggle + browser/profile pickers.

`Name` and `Icon` live below the tab content (shared across both tabs). `ShowTabPanel(tag)` toggles the two panels' visibility, keeps `tabBar.SelectedItem` in sync, and re-derives the target; the `SelectorBar.SelectionChanged` handler routes through it (a `populating` flag guards re-entrancy, same pattern as `_initializing` in `UserSettings`).

- The list is fed by `AppPickerEntry` rows (display name, `LaunchPath`, `IsPwa`, icon target). The catalog combines `GetInstalledApplications()` + `GetInstalledPwas()` and is built on a **background STA thread** via `AppPickerService.RunStaAsync` (the shell enumeration is expensive and apartment-threaded), so the dialog opens instantly and the list fills in.
- Duplicate names are detected case-insensitively against the complete catalog before filtering. Their list-only `DisplayName` includes the executable filename, for example `Steam (Steam.exe)` and `Steam (uninstall.exe)`; virtual shell entries use their activation target instead. The original `Name` still seeds the saved item's name. Filtering never changes these labels or removes catalog entries.
- Every row has a tooltip with its full launch target (including the `shell:AppsFolder\` prefix for PWAs). `Windows/AppPickerRowActions.cs` attaches the tooltip and a `ContextFlyout` to the `ListViewItem` during `ContainerContentChanging`, clearing both when containers are recycled. This covers the entire row and supports right-click and the keyboard context-menu gesture.
- The row menu offers **Open** and **Open containing folder**. Open immediately uses `FlyoutWindow.Launch`, including for uninstallers, with no confirmation or item save. It uses the editor's arguments when the row matches the current target; other rows use their unchanged catalog payload. Containing-folder is disabled for PWAs, virtual shell targets and missing filesystem parents. Opening the menu or invoking an action does not programmatically select the row or overwrite the editor's target.
- Icons stream in asynchronously via `AppPickerService.LoadIcons` → `ShellIcons.Extract` (an `IShellItemImageFactory` 32px BGRA extraction). The `ListView` `DataTemplate` is built from a XAML string with `XamlReader.Load` and binds `{Binding Icon}` (`ObservableObject` change notification), since the dialog is code-built.
- **Target resolution:** Selecting or preselecting an app also fills the File or link path, so switching tabs to enter arguments retains the selected target. `ResolveTarget()` keys off the active tab — `custom` → the typed path/link, retaining the picked entry's PWA type while its path matches and otherwise classifying it with `LooksLikeWebUrl` / `LooksLikeFilePath`; `list` → the remembered `pickedEntry`, even if search filtering hides its row. It returns `(path, isPwa, isWebsite)`; `SyncDerived()` pushes that into the derived flags and the app-window options' visibility.
- Stored values are unchanged from before (PWA → AUMID in `Path` + `IsPwa`; Store app → `shell:AppsFolder\…` path; exe → file path; website URL → `IsWebsite`), so launch behaviour in `FlyoutWindow` is preserved.

## Settings rows (code-built)

`LauncherSettingsWindow.BuildRow` lays out a label/subtitle in a `Star` column with the control in
an `Auto` column. Two rules keep it from collapsing:

- **The control is centred vertically, never stretched.** A control with the default alignment
  grows to the row's height, and the row is as tall as its label — so a subtitle that wraps to two
  lines silently inflates the input beside it. `BuildRow` sets `VerticalAlignment.Center` on every
  control it lays out, so this cannot come back per-row.
- **Anything with long, open-ended content uses `BuildStackedRow` instead** (label above,
  full-width control below). An `Auto` column sizes to its content, so a control holding a URL
  claims the width the text wants and starves the `Star` label column — a long web address once
  squeezed "Web Address" into a three-character ribbon of wrapped text and stretched the box down
  the whole height of it.

## NumberBox: inline spin buttons eat the value

`SpinButtonPlacementMode="Inline"` spends about 64px on the two buttons, and the clear "✕" the
TextBox template adds **on focus** takes ~30 more. At `Width="130"` that left nothing for the digits:
the value was visible until you clicked into the field and then vanished behind its own chrome.

Use `Compact` (the placement every other NumberBox in this app uses — the buttons appear over the
box rather than reserving width) and leave at least ~140px. If `Inline` is genuinely wanted, budget
the buttons *and* the focus-only clear button, not just the digits.

## Owned windows, not ContentDialog

UI opened from the flyout (`ItemEditorWindow`, `TextPromptWindow`, `LauncherSettingsWindow`, `BookmarkPickerWindow`, `ExtensionPopupWindow`) uses standalone `Window`s. A `ContentDialog` renders inside its host window's content area and **cannot overflow the HWND** — hosted in a flyout that is often ~175px wide and ~130 dips tall, even a one-field dialog gets its input and buttons clipped.

Conventions for these windows:

- `ExtendsContentIntoTitleBar = true` with `WindowChrome.BuildTitleBar(...)`. A default WinUI title bar does **not** follow the app's `RequestedTheme`, so it renders light chrome over dark content.
- `WindowChrome.ApplyIcon(hwnd)` for the app icon. Both paths are needed: `WM_SETICON` drives taskbar/Alt-Tab, `AppWindow.SetIcon` drives the title bar.
- Set the owner via `SetWindowLongPtr(GWLP_HWNDPARENT)`, **and** have the flyout drop its `IsAlwaysOnTop` flag while the window is open — ownership alone does not beat a topmost owner.
- Size to content. Surplus height shows as a large empty gap, because the form is top-aligned above a bottom-anchored button row.
- When a following setup step needs the closing window's location, capture its actual HWND bounds before closing. `LauncherSettingsWindow.ShowAsync` exposes an optional `onClosing` callback for this; its Done/Escape path captures explicitly, and `AppWindow.Closing` covers system close actions. The close completion result and immediate-save behavior remain unchanged.
- Don't leak the window: the flyout tracks the open editor and closes it when edit mode ends, so an orphan can't commit into a launcher the user has navigated away from.
- **A dialog that both a settings window and the flyout need is split from its host.** `BookmarkPicker` was a `ContentDialog`; it is now `BookmarkPickerView` (the chooser) plus two hosts: the dialog for full-size windows, `BookmarkPickerWindow` for the flyout. Copying the chooser into a second window would have been the third place browser bookmarks are read.

For a settings row with changing status, reuse `BuildRow(title, TextBlock, control)` and update its
normal subtitle. Keep sibling font size 12, 0.5 opacity and wrapping, label/control spacing and vertical
centering. Do not add a third line with its own centered `MaxWidth`; the taskbar pin row uses the
same subtitle for its description, live state and replacement warning.


### Shared launcher view and regular item editor

`LauncherItemsEditorWindow` is a regular owned window matching `LauncherSettingsWindow`: `MicaBackdrop`, `WindowChrome.BuildTitleBar`/`ApplyIcon`, saved theme, 24-DIP body padding and an external accent Done button. Its three add actions use regular icon-and-text buttons in WrappingPanel inside a finite Star column, wrapping at natural widths while preserving child/tab order. An independent Auto column holds a 32-DIP settings icon at the top right, wired through the shared tooltip/accessible-name helper. The toolbar presenter stretches across the padded body and UpdateToolbarWidth supplies that full finite width; settings must not flow into another row. Done remains in the conventional footer. The item view preserves intrinsic dimensions and scrolls rather than reflows. A preview-only rounded Border uses dynamic `CardBackgroundFillColorDefaultBrush` and `CardStrokeColorDefaultBrush` ThemeResource references with a 1-DIP neutral stroke. Its dimensions equal the unchanged item view plus the outer stroke, so the surface never fills the viewport or insets items. The regular host has no blue outer edit outline; the native popup keeps its DWM outline and floating toolbar. Main Settings navigation/cards remain normal behind the owned editor.

`Controls/LauncherItemsView` shares one renderer/edit implementation through `ILauncherItemsHost`, using a distinct control instance per host and no live transfer across XamlRoots. Existing owned item, group-name and launcher-settings dialogs use the active host HWND and keep their own XamlRoot/file-picker behavior. A disabled `ContentControl` ancestor scopes pointer/keyboard/accessibility input while a secondary dialog is open; restore its enabled state and prior focus after the dialog, without taking foreground from another app. Done/Escape/close finishes editing with changes already saved. Minimize/app switching retains the regular editor; same-launcher entry focuses it or its owned modal.

Keep the same keyed resources, templates, bindings, events and `ConditionalNameToolTip` wiring in all three item modes. `UserControl` instances share implementation, not live visual trees. Natural height arithmetic and string-only label measurement remain in the shared view; monitor clamping belongs to the popup host, and window/viewport sizing never changes item geometry.

`LaunchersPage.CreateLauncherActionButton` keeps Settings and Items labels, subtitles, padding and chevrons consistent. Web cards use it once for **Settings**, with their address or **No web address set** below. Shortcut cards use sibling primary Items and secondary ellipsis buttons, never nested interactive controls. Explicit names/tooltips cover both actions; read-only subscribers retain secondary export while edit/import stay disabled.

## Flyouts and menus inside the flyout window

A `MenuFlyout` opened from the web flyout's header hits the same wall as `ContentDialog`, and needs
two things the default does not give:

- **`ShouldConstrainToRootBounds = false`.** The window is often 400px wide with 34px of chrome, so
  a constrained menu is clipped exactly like a `ContentDialog` would be.
- **Pin the flyout while it is open.** Once unconstrained the menu lives in a popup of its own, so
  opening it *deactivates* the flyout — which dismisses it and takes the menu down in the same
  motion. `WebFlyoutWindow` sets `_isMenuOpen` from `Opened`/`Closed` and tests it in both
  dismissal guards, alongside `_isModalOpen`. Clear it on `Closed`, which also covers the
  dismissed-by-clicking-away case.

This is the same failure mode as the owned-window rule above: anything that takes focus away from a
window that dismisses on focus loss has to say so first.

Two more, for menus whose rows answer more than a plain click:

- **A right-click on a `MenuFlyoutItem` has to be answered on the pointer press**, added with
  `AddHandler(UIElement.PointerPressedEvent, ..., handledEventsToo: true)`. A `MenuFlyoutItem` marks
  *every* pointer press handled for its own visual states, whichever button it was, and a handled
  press never becomes the right-tap that raises `ContextRequested`, so that event is never raised
  inside a menu at all. A `Button` takes only the left press, which is why the same wiring works on
  ordinary controls and silently does nothing on a menu row. Middle-click needs the same treatment.
  `ContextRequested` is still worth keeping for the context-menu key, guarded on `TryGetPosition`
  returning no position so the two paths cannot answer one gesture twice.
- **Never mutate an open `MenuFlyout`'s `Items`.** Every way of doing it breaks something, and none
  of them break at the moment of the change: swapping a row (for a `MenuFlyoutSubItem`, say) leaves
  a menu that never light-dismisses again, so it sits on screen through every click outside it;
  assigning over an entry (`Items[at] = ...`) removes before it adds, which empties a single-row
  menu for an instant and closes it outright; and `Clear` takes the presenter down mid-gesture.
  Build the menu you want and show it instead, letting the new one light-dismiss the old, which is
  also the only way to keep a menu under the pointer at all times: the moment there is none, the
  cursor falls through to whatever is beneath and does not change back until the pointer moves.
- **A `MenuFlyoutSubItem` cannot be opened from code.** `MenuFlyoutSubItemAutomationPeer`'s `Expand`
  is the only public way in and it goes around the framework's cascading-menu bookkeeping: the
  submenu comes up against bounds the row does not have yet, in the corner of the window, and
  afterwards neither menu can be light-dismissed. A submenu only opens on hover or a click, so a
  gesture that has to *show* something immediately cannot be built out of one.

## A popup cannot receive a drag

Menus and flyouts escape a small window because they are hosted outside it (`ShouldConstrainToRootBounds
= false`, above). That hosting is exactly why **a `Popup` can never be a drop target**: XAML registers
the *window* for drag-and-drop, so a popup renders, hit-tests and clicks normally while its `DragOver`
never fires once. Measured twice on the web flyout's bookmark folders, with light dismiss on and off;
dragging *out* of the popup worked throughout, because the receiving element was in the window's tree.

A `MenuFlyoutItem` cannot be a drag *source* either - it is not an element `CanDrag` applies to, and
menu rows mark every pointer press handled.

So anything that must be dragged into lives in the window's own tree, as an overlay spanning the root
grid, and is clamped to the window in exchange. Anything that merely has to be clicked can stay a
popup and overflow. `WebFlyoutWindow.FolderPopup.cs` records the whole chain.

## Expander rows for on/off + settings

`SyncPage` renders each sync destination as an `Expander`: icon, name and a live status line in
the header, a `ToggleSwitch` on the right, and that destination's settings as the content.

Two rules, both learned the hard way:

- **Enabled and expanded must be separate, visible affordances.** An earlier version used a
  selected-card highlight to mean "its settings are shown below", and it was not discoverable — a
  border colour reads as *state*, not as *"click me, there is more"*. The chevron says it outright,
  and the settings appear inside the thing you clicked rather than detached at the bottom.
- **Setting `ToggleSwitch.IsOn` from code raises `Toggled` exactly as a click does.** Any refresh
  that syncs toggles from settings must be wrapped in an `_initializing` guard, or it writes the
  settings back over themselves.

Give the expanders `HorizontalAlignment="Stretch"` *and* `HorizontalContentAlignment="Stretch"` (a
shared `Style` is easiest) or they size to their content and the list looks ragged.

### Settings section expansion defaults

In launcher settings, General, Appearance, and Size and position start expanded; Behavior and
Privacy and sign-ins start collapsed. All five Sync destination sections still start expanded.
Users may collapse and reopen sections; ordinary expansion does not request scrolling.
Sync expansion is independent of provider enablement, sign-in and stored connection settings.

An explicit launcher section link expands its destination if needed and calls the framework's
`StartBringIntoView` once after the form is loaded. A section hidden by the launcher type falls
back to General. Use normal framework scrolling for this navigation, without animation waits,
temporary scroll extent, or an automatic reveal handler on the expanders.

Advanced remains the web flyout's troubleshooting submenu, closed until opened. There is no
Advanced section in the settings form.

## Drag-and-Drop (FlyoutWindow)

ListViews use `CanDragItems="True"` with custom handlers — **never `CanReorderItems`**, which cannot be overridden for cross-list drops. Dragging is gated behind flyout edit mode. See [drag-drop.md](drag-drop.md) for full details.

## Groups

Groups render a heading plus a nested child `ListView`. In icon mode, consecutive ungrouped items are wrapped into ephemeral **synthetic groups** so loose icons pack into a wrapping grid; these are never persisted and never editable. See [drag-drop.md](drag-drop.md).

## Shared icon gallery

`IconGallery.CreateFlyout` and `CreateLauncherIconFlyout` are option wrappers over one gallery
core. Keep sizing, selection, Browse, Confirm/Cancel and search behavior in that shared core.
Use the actual owner HWND for the file picker and monitor/DPI lookup. A compact new-launcher
settings window must offer the same complete gallery as an existing launcher's settings.

The picker sets `ShouldConstrainToRootBounds = false` and caps its complete, definite height
to the monitor work area in DIPs. Its star row contains a finite `Grid` host with `MinHeight = 0`;
footer actions remain in Auto rows outside it. Avoid fixed list minimums or owner-height caps
that force the footer off-screen. Emoji `TextBlock` previews explicitly enable color fonts and
use Segoe UI Emoji. The color palette is for Fluent glyphs only.


Use an explicit supported placement such as `Bottom`. In Windows App SDK 1.8,
`FlyoutPlacementMode.Auto` is exposed by the enum but rejected by WinUI's show-time
`ValidateFlyoutPlacementMode`, returning `E_INVALIDARG` before the Opening event. The framework
can still choose a fitting side with explicit `Bottom`; do not replace it with `Auto`.


Large local symbol catalogs use `IconCatalogView`: a grouped `GridView` with `ItemsWrapGrid` and `CacheLength = 1`, directly in that finite host. The small-list `ScrollViewer` is its sibling, never its parent. An outer scroller or Auto/infinite measure defeats UI virtualization even when the panel type is virtualizing. Data filtering runs synchronously over small plain records; WinUI creates/recycles only the nearby UI tiles on its UI thread. Reuse this view for both Fluent and emoji catalogs. Clear the source on close.

For symbol selection outlines, use the native ListViewItemPresenter selected-border properties with the stock GridViewItem theme resources, including pointer-over, pressed, disabled and inner-border brushes. Keep the container BorderThickness fixed at zero so selection chrome does not change content geometry; leave keyboard/focus behavior and selected foreground tint native. ContainerContentChanging phase zero may run before Content is assigned, so container.Content is not a reliable selection identity there. Follow the [WinUI 1.8.5 GridViewItem template](https://github.com/microsoft/microsoft-ui-xaml/blob/468c624ca9131f60059eab9f92ec3dc33404edda/src/controls/dev/CommonStyles/GridViewItem_themeresources.xaml) and [container preparation ordering](https://github.com/microsoft/microsoft-ui-xaml/blob/468c624ca9131f60059eab9f92ec3dc33404edda/src/dxaml/xcp/dxaml/lib/ListViewBase_Partial_ContainerPhase.cpp).

Use page icon belongs in the web launcher's presets tab and follows Confirm/Cancel. Check its
availability from the current launcher kind on each opening. Give icon-only tab buttons and
compound-content choices explicit automation names; a tooltip alone is insufficient.


Browse is a full-width image option inside the first App Icons/Launcher Presets tab, never a
cross-tab footer link. Keep Confirm/Cancel pinned. Constrain the small-list scroller horizontally
and use responsive wrapping for app-color and bounded selfh.st tiles. Page/Composite/Browse labels
use a finite Star column so text wraps instead of enlarging the horizontal scroll extent.

Restored selections are revealed with `SelectionReveal.AfterLayout`: a cancellable one-shot
layout subscription followed by a guarded low-priority callback. Use native `ScrollIntoView` for
virtualized catalog entries and `StartBringIntoView` for option buttons. Re-sourcing, tab/query
changes and close invalidate the callback. Never add an unconditional top reset in Opened.
Category group ToString values must be their human-readable names for automation.

`AutomaticPageIconChoice` keeps the selectable image/status in a finite Star column and a
separate named Retry action in an Auto column. Missing/failed images have explicit text; do not
present a globe as successful preview. Decode actual bytes before publishing success, retain the
last good preview after a failed retry, and guard each awaited completion by request generation,
current URL/kind and open lifetime. The latest undisposed request stops its own animation even
when the source changed; that state shows the address/type-change message and disables Retry
and the stale page choice until reopening. Newer generations retain ownership of their own UI.
Dispose cancels work and stops the loading animation. The
option instance survives tab/query refreshes within an opening but is disposed on close. Only
Confirm passes its memory preview to the managed-file adoption path. Browse retains its existing
file-picker commit flow and does not gain page-only Retry behavior. Retry may fetch directly
from the actual site after the existing loaded-page path fails; failure text explains that the
launcher may need to be opened and signed in. Keep the last good preview and saved custom icon,
without showing a placeholder as success or changing pending selection on a failed request.

### Item edit and drag overlays

The shared item view draws hover and insertion cues in its RootGrid overlay plane, with IsHitTestVisible false and raw accessibility view. Real group hover outlines the full group, but header controls and child hover remain separate. Grip/pencil cues use rounded translucent scrims and theme accent glyphs; a HighContrast theme dictionary supplies opaque system window/text colors. Exact label margins are borrowed/restored for native ellipsis without changing item dimensions. Container entry/movement share innermost-item resolution. Every hover handler leaves pointer Handled unchanged: WinUI can carry native enter state into a following press, otherwise suppressing the item's pointer capture and drag start.

The shared DropTargets partial distinguishes real-group child insertion with a low-opacity full-group tint and inset caret from sibling insertion with no tint and a full-width outer caret. Its theme dictionaries retain a transparent fill and system highlight outline in high contrast. Both overlays are passive and cleared together at every drag/session exit. For XamlReader-built receiving-group cues, declare themed brushes in a wrapper Grid.Resources before the child Border consumes them. Root attributes cannot forward-reference resources declared later on that same element. Guard construction so a cue failure preserves the valid drop and insertion caret. The same receiving overlay contains the final-child gradient scrim, whose themed brush is declared before its child consumer. Keep most of the child clear and ramp the fade toward the overlapping inset marker using its actual mode-specific position. Transparent gradient stops retain the opaque themed stop's RGB to avoid a dark fringe in light theme; opaque stops retain their ThemeResource bindings. Compute its margin relative to the already-arranged group container, since the new overlay Margin may not be arranged yet. Keep tint, gradient and transparent outline as separate sibling layers in that paint order; the final outline must remain crisp above the gradient. The inset caret stays in the higher RootGrid plane. Never change the target child Opacity or layout. Its end marker stays inside the last child; the full-width after-group marker stays outside the group. High contrast omits the fade while preserving explicit cues. Insertion carets never change container borders or padding. The bounded drag card uses normal layout in an off-content zero-size Canvas, then RenderTargetBitmap and DragUIOverride on this view's targets. It has no input or independent renderer and does not force UpdateLayout. Read [drag-drop.md](drag-drop.md) for routing, cancellation and native verification requirements.

Real group headers share a transparent padded Grid style. Moving the former 8,4,8,4 outer margin to Padding preserves content geometry while giving the whole passive pencil area hit-test ink; do not add overlay buttons. The drag bitmap combines a mode-sized name/count body and a live hint row above it. Native caption positioning is unavailable, so normal feedback hides native caption/glyph; a current-event render failure can restore native feedback. Cache by caption/mode, bound deferrals and guard both application and fallback against stale token/target versions. Native Done restores owner activation after its non-activating toolbar is hidden; regular Done still closes its own editor.
