> **Scope:** Use when modifying shared launcher drag-and-drop, edit mode or its window hosts. Covers the custom drag-drop system, cross-list moves, insertion indicators, the edit-mode geometry contract, and known WinUI 3 limitations.
> **Governs:** `Controls/LauncherItemsView.xaml*`, `Controls/LauncherItemsView.*.cs`, `Controls/ILauncherItemsHost.cs`, `Windows/FlyoutWindow.xaml*`, `Windows/FlyoutWindow.EditMode.cs`, `Windows/FlyoutWindow.ItemsHost.cs`, `Windows/FlyoutWindow.SetupPlacement.cs`, `Windows/LauncherItemsEditorWindow.cs`, `SettingsWindow.ItemEditing.cs`, `Windows/EditToolbarWindow.cs`.

# Launcher Drag-and-Drop & Edit Mode Conventions

All launcher item editing uses `LauncherItemsView`. The former `LauncherItemsPage` remains deleted; add behavior to the shared view rather than building another implementation.

## Why custom drag-drop (not CanReorderItems)

WinUI 3's `CanReorderItems` takes full internal control of `DragOver` and `Drop` events. Its
built-in handlers evaluate the drag source at a low level and **cannot be reliably overridden**
— even `AddHandler` with `handledEventsToo` doesn't work consistently. This makes cross-list
drag-drop (between column lists and group child lists) impossible with `CanReorderItems`.

**Solution:** All ListViews use `CanDragItems="True"` (not `CanReorderItems`) with fully custom
`DragOver`, `DragLeave`, `Drop`, `DragItemsStarting`, and `DragItemsCompleted` handlers.

**The same rule reaches a second surface.** A web launcher's bookmark bar and its folder lists
(`WebFlyoutWindow.Bookmarks.cs`, `WebFlyoutWindow.FolderPopup.cs`) move bookmarks between the bar,
a folder, and a folder inside a folder - which is the cross-collection case above, one type over.
They reuse this file's shapes rather than inventing others: a Y-midpoint drop index, a drawn caret
instead of shuffled children, and a tint for "into this one" rather than a caption. Two findings
from building it are worth knowing before touching either:

- **A `Button` is not a drag source on its own.** It captures the pointer on press for its own click
  handling, so `CanDrag` never fires - `ListViewBase` is what makes it work for list items.
  `WireStripDragSource` starts the drag by hand past a threshold, which is also what keeps the click.
- **A drop target must be in the window's own visual tree.** A `MenuFlyout` cannot host a drag source
  at all, and a `Popup` renders and clicks perfectly while never receiving a drag - measured, with
  `DragOver` not firing once. XAML registers the *window* as the drop target. That is why the folder
  lists are an overlay in the flyout's root grid rather than a popup, at the cost of being clamped to
  the window. See web-launchers.md.

## Edit mode

Dragging is an **edit-mode-only** affordance. Both `DragItemsStarting` handlers cancel when `_isEditMode` is false. Entry is the hover-revealed pencil overlay; exit is the toolbar's Done button, Escape (first press exits edit mode, second dismisses), or hiding the flyout. Settings-origin editing starts in edit mode in a regular window; its external Done/Escape/close finishes that window, retaining the same immediate-save behavior.

### Geometry contract

Tray placement recognizes reserved left/right taskbar strips before checking top/bottom
proximity. Side taskbars place the flyout beside the taskbar and vertically centered on the
invocation point, clamped to the monitor work area. Top/bottom taskbars and interior overflow
popup clicks retain their existing below/above placement. Web flyouts follow the same rule.

Edit mode may grow the flyout's **height**. It must never change its **width**, or the size of
any item or group.

- In the native popup, the **toolbar floats in its own window** (`EditToolbarWindow`) above the flyout, so it contributes no height at all and cannot crowd the launcher's content. It is `WS_EX_NOACTIVATE` so clicking it does not steal focus, and the flyout drops its always-on-top flag while an owned editor is open; ownership alone does not beat a topmost owner. Keep its right edge aligned with the launcher, subject to the existing monitor work-area clamp. Reposition it **after** the flyout settles: `ResizeWindowToCurrentContent` moves the window with `SWP_ASYNCWINDOWPOS`, so reading its rect immediately returns the *old* bounds.
- The native popup **edit-mode border tint** uses the DWM border colour (`DWMWA_BORDER_COLOR`), not a XAML border; a `BorderThickness` on `RootGrid` would inset the content.
- `GetFlyoutWidth()` sums column widths arithmetically and never measures laid-out content, so anything wider than it is **clipped**. In list mode the column width itself comes from `ComputeListColumnWidth()`, which measures label *strings* (see "Sizing a list column" below). That is a different thing from measuring the tree, and the only input to the arithmetic that is not known up front.
- Per-column headers and the empty-launcher placeholder *do* add height, and both are counted in the arithmetic (`CurrentColumnHeaderHeight`, `CurrentEmptyPlaceholderHeight`). Anything that reserves space must be added there, or the content overflows the window; an empty column's drop-target `MinHeight` is skipped when the whole launcher is empty precisely because only the placeholder is accounted for.
- Per-item/group affordances must be **non-reflowing**: use `Background` and `CornerRadius` only, which have no layout effect. A border was tried and rejected; compensating for it with negative padding only works when the container already has padding on every side to give back, which the icon-mode container style does not.
- Empty groups get a `MinHeight` drop target in edit mode. This grows height only. In icon mode it costs nothing extra: `MeasureIconModeHeight` already reserves one row for an empty group via `Math.Max(1, …)`.

### Shared view and window hosts

`Controls/LauncherItemsView.xaml` owns the item templates and exact launcher layout for both `FlyoutWindow` and `LauncherItemsEditorWindow`. Its rendering, editing, drag/drop and external-drop partials share the same item/column/group state and immediate persistence. `ILauncherItemsHost` keeps native placement, modal ownership, completion and resize policy outside that view. Each host has its own view instance; do not transfer live visuals across XamlRoots or duplicate the renderer.

Settings cards open `LauncherItemsEditorWindow`, a normal owned Mica window matching `LauncherSettingsWindow`: shared title bar, application icon/theme, 24-DIP body padding and an external accent Done button. The shared toolbar is inside that window, above a two-axis viewport. Its four regular buttons show icons and labels and wrap naturally; the native popup keeps its compact fixed-slot toolbar. The launcher retains its intrinsic item/column geometry and scrolls when larger than the viewport. The regular host wraps only the intrinsic preview in a subdued themed fill/neutral 1-DIP outline. That Border sits outside the view dimensions and adds only its stroke to the scroll extent; it must never become a border on RootGrid, stretch across the viewport, change item dimensions or alter local drop coordinates. There is no blue outer edit outline in this host. Changes save immediately; Done, Escape and close finish editing. Minimize and app switching retain the editor. Main Settings navigation/cards remain normal behind it, without a quiet surface or scrim.

One regular editor may be open per launcher, with independent windows allowed for different launchers. Reopening the same launcher from Settings, tray or taskbar focuses its editor or active owned dialog. A native edit session is ended and parked before a regular editor opens. Native tray editing otherwise retains its popup, blue DWM border, separate floating toolbar and animation. Item, group-name and launcher-settings dialogs keep their existing owned-window implementations; the regular editor's HWND owns them and its disabled Control scope covers keyboard and pointer input until the dialog closes. Closing the editor or its Settings owner closes its owned dialog, invalidates pending session work and releases shared-view registrations before native destruction.

New shortcut launchers use this same regular editor after launcher settings close. Its initial centre and monitor come from the actual captured setup-window rectangle, clamped to the work area with that monitor's DPI; the surviving Main Settings window owns it. This placement is transient. Web launchers still skip item editing.

The primary **Items** row opens that regular editor directly. Its independent sibling **…** button contains only export, import and bookmark import. Read-only subscribers cannot edit/import, but the secondary button stays enabled for export.

`ThemeManager` updates all open regular item editors. `FlyoutWindow.InvalidateItems` remains the compatibility entry point for icon/import/sync changes and refreshes every registered shared view plus the composite tray icons. Dispose removes the view registration and hover/container state.

External payload reads capture the edit-session generation and resolve their insertion position before awaiting. They always complete the drag deferral, and reject a closed session or a derived target discarded during rebuilding. Once insertion has saved, enrichment remains valid after Done: persist and invalidate only live item launchers still holding those item objects, including a new destination after a move. Removed/replaced items and deleted/web launchers trigger no stale save. Keep structural edits flushing from `_launcher.Items`, and reorder edits flushing from derived columns. Late item-dialog results cannot act on an ended session; a closed item dialog also checks its lifetime before committing an awaited website title.

The native popup still supports an optional transient setup anchor through `FlyoutWindow.SetupPlacement.cs`; current Settings-origin setup uses the regular editor instead. Its normal openings clear native setup state. Floating-toolbar repositioning remains dispatched after asynchronous native resizing and uses the popup owner's DPI.

### Height measurement

`LauncherItemsView.MeasureContentHeight` / `MeasureIconModeHeight` compute intrinsic height **arithmetically**. The regular editor sets that exact extent inside its viewport; only the native host clamps height to the monitor. This separation must not change item geometry. Arithmetic is also required because forcing a layout pass on a window hidden via `ShowWindow(SW_HIDE)` while another WinUI 3 window is active causes a fatal `ExecutionEngineException`. Do not "fix" this by calling `UpdateLayout()`.

> The flyout is no longer hidden at all — dismissing it now parks it off the virtual screen
> (`ParkOffScreen`, see `ARCHITECTURE.md`) so its composition surfaces survive. That removes the
> crash *hazard*, but the arithmetic stays: the reasons below are about correctness, not the
> crash, and a dismissed window is still the wrong thing to measure.

**Compute, don't measure.** Three separate height bugs came from measuring: a "learned" chrome
constant that fed its own output back into the arithmetic and grew without bound; a
`ContentStack` measure taken straight after a rebuild, before the new containers were laid out;
and a double-counted margin (`DesiredSize` already includes the element's own margin). Edit
chrome heights are now derived from geometry that is known up front — toolbar rows × slot,
header height × 1.

### Sizing a list column

List columns used to be a flat 175px each, which a column of one-word shortcuts spent mostly on
nothing. `ComputeListColumnWidth()` now sizes them to the longest label the launcher holds, capped
at `MaxListColumnWidth` (that same 175, so nothing is ever wider than it used to be) and floored at
`MinListColumnWidth`.

- **One width for all of them.** Columns of differing widths read as a broken table, and the flyout
  has no column rules to make ragged edges look deliberate.
- **This measures a string, not a tree.** `MeasureLabelWidth` sets text on `LabelMeasure` (a
  `TextBlock` parked in a zero-sized `Canvas` in the flyout's own tree, so it resolves the same
  font, theme and scale the real rows do) and reads its `DesiredSize`. Nothing reads a
  *container's* size, so no result can feed back into the geometry that produced it, which is the
  failure the rule above is about.
- **The chrome around a label is a constant, not a measurement.** `ListItemChromeWidth`,
  `ListGroupHeaderChromeWidth` and `LauncherTitleChromeWidth` mirror padding and margins that live
  in the XAML; each of those three places carries a comment pointing back at its constant.
- **The answer is cached in `_listColumnWidth`, decided once per rebuild.** Two things have to
  agree on it: the columns (`CreateColumnListView`) and the window sized around them
  (`GetFlyoutWidth`), the second of which is read before the flyout has ever been laid out.
- **A measurement of zero means "no answer", never "no width".** The first rebuild runs from the
  constructor, where a `TextBlock` has nothing to lay text out against; that pass keeps the full
  width, and `RootGrid_Loaded` retakes it once and unhooks itself. The parked window's own size
  does not matter, because every `Toggle` resizes from `GetFlyoutWidth()`.
- **An empty launcher keeps the full width** for the same reason: nothing to measure is not a
  reason to shrink, and the empty-state placeholder is sized against the flyout it sits in.

Where a real measurement genuinely is needed, take it **after layout**, never during
construction or mid-animation. `LauncherSettingsWindow` sizes itself on `Loaded` for this
reason; sizing during the constructor read a `DesiredSize` of zero and produced a full-height
window. Likewise, entering edit mode from the settings app waits out the open animation:
`ShowAnimated` drives geometry on a rendering loop, and a resize partway through is overwritten
by the animation's next frame.

## Source of truth

`_launcher.Items` is authoritative. `_columnLists` is a **derived view**, rebuilt from the flat
list by `BuildColumnLists()`.

| Operation | Mutates | Persists via |
|---|---|---|
| Add / remove / rename | `_launcher.Items` (or `group.Children`) | `PersistStructuralChange()` |
| Drag reorder | `_columnLists` | `PersistFlyoutReorder()` |

**Critical:** `PersistStructuralChange()` must **not** call `SyncColumnsToFlatList()`. That
method clears `_launcher.Items` and regenerates it from `_columnLists`, so calling it after a
structural edit resurrects anything just removed — this is exactly how remove was once broken.
Drag-drop is the opposite case and must flush the other way.

Cross-launcher Move to actions accept only live, writable item launchers, excluding web launchers and read-only subscribers. Check that policy when constructing the menu and again before removing the source item. Resolve the current source collection by item identity; an invalid destination or missing source aborts without mutation or persistence.

**Identity matters as much as content.** Every operation in the table finds a row's item **by
reference** (`FindParentCollection`), and `_columnLists` holds the very objects that were in
`_launcher.Items` when the panel was last built. Anything that swaps those objects for
equal-valued copies breaks all of them at once: remove, move up/down, move to and edit find no
parent and return silently, and a drag reorder flushes the stale set back over whatever replaced
it. A sync download used to do exactly that on every tick, which disabled item editing in every
flyout until the app was restarted; see [sync.md](sync.md) for the guard that stops it.
`FindParentCollection` logs a warning when a row's item is missing, because the only other
symptom is a menu entry that does nothing.

## Architecture

### Multi-column layout

The flat `Items` collection is split at `IsColumnBreak` sentinel items into per-column
`ObservableCollection<LauncherItem>` lists. Each column gets its own `ListView` (list mode) or
`GridView` (icon mode) created in `CreateColumnListView()`.

`BuildColumnContainer` binds its wrapper Width one way to the list/grid Width, keeping captions within the arithmetic item-column width while following density changes and initial list-label sizing. Keep the Auto header/Star content rows and stretched list so empty column space remains a drop target. Column captions and real group names stay visible with native ellipsis; `ConditionalNameToolTip` targets each caption itself and offers the full text only when actually trimmed.

### Synthetic groups

In icon mode, consecutive ungrouped items are wrapped into **ephemeral** groups
(`WrapUngroupedItemsIntoSyntheticGroups`) so loose icons pack into a wrapping grid. These are
tracked in `_syntheticGroups`, unwrapped again by `SyncColumnsToFlatList()`, and must **never**
be renamed, removed, reordered, or persisted. Guard with `IsEditableGroup(item)`.

### Drag surfaces

| Surface | Tag | Source collection | Notes |
|---|---|---|---|
| Column items | column index (`int`) | `_columnLists[idx]` | Cross-column drag-drop |
| Group children | the group `LauncherItem` | `group.Children` | Rejects `IsGroup`/`IsColumnBreak` drops (no nesting) |

### Shared state fields

- `_dragItem` — the `LauncherItem` being dragged
- `_dragSourceCollection` — the collection it came from
- `_lastIndicatorContainer` / `_lastIndicatorListView` — insertion-indicator restore state

### Drop index calculation

`GetDropIndex(ListViewBase, DragEventArgs)` dispatches to `GetDropIndexGrid` when the panel is
an `ItemsWrapGrid` or `PackedIconPanel` (row-band hit test, then X-midpoint); otherwise it scans
Y-midpoints.

**Critical:** When reordering within the same collection, removing the dragged item shifts
subsequent items up by one. The drop handlers must adjust: if the original index was before the
drop index, decrement `dropIndex` by 1 after removal. This applies to both
`ColumnListView_Drop` and `GroupChildList_Drop`.

## External drops (Explorer, desktop, Start Menu, browsers)

`LauncherItemsView.ExternalDrop.cs` accepts drags that did **not** start inside the flyout, turning files, shortcuts and links into new items. `Services/DroppedItemFactory.cs` does the payload → `LauncherItem` mapping.

**Routing:** every per-list `DragOver`/`Drop` handler treats `_dragItem == null` as "this drag
came from outside" and forwards to `ExternalDragOver` / `ExternalDrop`. There is no separate
event wiring for the lists — the same `AllowDrop` surfaces serve both.

`RootGrid` has its own `AllowDrop` + `RootGrid_DragOver` / `RootGrid_Drop`. It is not redundant:
an **empty launcher has no list big enough to hit**, since the empty-column `MinHeight` drop
target is skipped when the whole launcher is empty. Root handlers bail out when `_dragItem` is
set, and only ever run for drags that missed every list (the lists mark their events handled).

**Edit-mode-only, for a load-bearing reason.** The flyout dismisses on `Deactivated`, so outside
edit mode it is already gone by the time the user has switched to Explorer to pick something up.
`SuppressDismiss` is what makes it a drop target at all.

**Persist direction matters** — the same trap as everywhere else in this file:

| Drop lands on | Target collection | Persist via |
|---|---|---|
| A column or group list | `_columnLists[i]` / `group.Children` (derived) | `PersistFlyoutReorder()` |
| Flyout empty space | `_launcher.Items` (source of truth) | `PersistStructuralChange()` |

Using `PersistFlyoutReorder` for the root case would regenerate the flat list from column lists
built *before* the drop and silently discard the new items.

**Deferral and ordering.** `Drop` is synchronous but reading the payload is not, so it takes an
`e.GetDeferral()`. Two rules: resolve `GetDropIndex` **before** going async (`e.GetPosition` is
only meaningful while the event is on the stack), and complete the deferral as soon as the
payload is read — Explorer spins its drag loop until then. Website titles and icons are fetched
*after* the deferral completes and after the items are already visible, then persisted again;
`LauncherItem` is observable, so they fill in live.

**Payload mapping** (`DroppedItemFactory`):

| Dropped | Becomes |
|---|---|
| `.lnk` | The resolved target + its arguments, so it matches the same app picked from the item editor and survives the shortcut being deleted. Unresolvable shortcuts (Store apps, control-panel targets) keep the `.lnk`, which still shell-executes. |
| `.url` | A website item; the filename wins over the page `<title>`, since the user named it. |
| `.exe` / `.com` / `.bat` / `.cmd` | An application item, named from the exe's product name. |
| Folder | An application item launched through `explorer.exe`. |
| Any other file | An application item launched via `ShellExecute`. |
| Browser link or tab | A website item named from the page title (host until the fetch lands). |

**The Windows 11 Start Menu cannot be a drag source here — confirmed, not assumed.** Its data
package carries no `StorageItems` at all (not even for plain Win32 apps): the shell item lives in
the `Shell IDList Array` clipboard format, which WinUI 3's `DataPackageView` does not surface.
All that reaches `DataPackageView` is the app's *name* as text.

This is why `CanAccept` refuses text-only payloads. An earlier version accepted them, and the
result was the worst possible behaviour: Start Menu drags showed a confident "Add to launcher"
drop cursor and then silently added nothing. **A drop cursor is a promise — do not accept a
format you cannot turn into an item.** `CreateItemsAsync` still reads text as a *fallback* for a
source that advertises a web link but fails to produce it; text alone never triggers acceptance.

Dragging from `%AppData%\Microsoft\Windows\Start Menu\Programs` in an Explorer window works
fine — those are real `.lnk` files. Supporting the Start Menu itself needs the raw OLE
`IDataObject`, below the XAML layer.

Icon fetching goes through `FaviconService.FetchMissingItemIconsAsync` like every other import
path — see [icons.md](icons.md). Do not add a parallel fetch here.

## Visual feedback

`ShowInsertionIndicator(ListView, int)` sets an accent-coloured border on the target container.
In list mode this is a horizontal line; in icon-grid mode it's a vertical line **compensated by
subtracting the border's width from the container's own padding on the same side**, so the
container's outer dimensions stay constant and the grid doesn't reflow.
`ClearInsertionIndicator()` restores border, padding and margin from the saved `_lastIndicator*`
fields.

**Always clamp the compensation at zero** (`Math.Max(0, padding.Left - 3)`), never write a bare
negative `Thickness`. WinUI **throws** `ArgumentException` on a negative `Control.Padding` rather
than clamping it, and the icon-mode container style has no padding to give back — so the grid
branch used to kill the entire drag with an unhandled exception on every `DragOver` tick. Where
the container has no padding, the indicator costs 3px of reflow; that is the accepted trade.

`DragUIOverride.Caption` shows contextual text ("Move above X", "Move into X", "Move to end").

## Container item lookup

These lists are populated via `ItemsSource`, which puts the item in each container's
**`Content`** and leaves `DataContext` **null**. Use `GetContainerItem(container)` (Content
first, DataContext as fallback) — checking `DataContext` silently rejects every item. Always
re-read on each event rather than capturing, since containers are recycled.

## Pointer events

`LauncherItemsView.Hover.cs` owns the passive drag/edit cues shared by the native popup and regular editor. A drawn six-dot grip leads each row/header and the pencil trails it; large-icon tiles use top corners and 40-DIP tiles use compact opposite corners. Group cues use only header bounds. Each cue has an opaque dynamic `SolidBackgroundFillColorBaseBrush` backing and themed stroke/text, with a local clip preventing overdraw. List/header labels temporarily reserve cue space through margins, retaining native ellipsis and actual-trimming tooltips; restore their original local Margin on pointer exit, recycling, unload, rebuild and close. The overlay is not hit-testable or an accessibility control: the entire item retains its click-to-edit and drag behavior, conditional name tooltip, drag threshold and child-over-group priority. Read-only items, synthetic groups and column breaks do not gain an edit cue. Keep decoration outside item templates and layout arithmetic; do not introduce handle-only dragging or a competing tooltip.


`ListViewItem` marks `PointerMoved` as **handled** for its own hover visuals, so a handler
attached to `RootGrid` never fires over an actual item — only over empty space. Attach hover
handlers to the **containers** (via `ContainerContentChanging`, plus an immediate pass over
`ContainerFromIndex` for already-realised ones). In nested lists the child's handler runs first,
so set `e.Handled = true` to stop the enclosing group claiming the hover.

## Common pitfalls

1. **Never use `CanReorderItems`** for ListViews that need cross-list drag-drop.
2. **Always adjust drop index** when removing from the same collection before inserting.
3. **`RebuildColumnsPanel()` re-creates all containers** — any visual state must be model-backed
   or re-applied in `Loaded` / `ContainerContentChanging`.
4. **Groups cannot be dropped into other groups** — `GroupChildList_DragOver` rejects `IsGroup`.
5. **Never persist synthetic groups.**
6. **Never call `SettingsManager.SaveSettings()` alone** for an item change — always pair it with
   `AutoSyncService.NotifyItemsChanged()` (use `PersistFlyoutItemChanges` / `PersistFlyoutReorder`),
   or a periodic sync download will silently revert the edit.
7. **Column breaks are invisible** — they exist only as sentinel items in the flat list.
8. **`_dragItem == null` means an external drag**, not a bug — don't "fix" a drag handler by
   early-returning on it without routing to the external-drop path.
9. **Anything that tracks realised containers in a field must be cleared on rebuild.** The
   `FlyoutWindow` instances are **permanent** (one per launcher in the static `_instances`
   map), so a per-container collection that only ever grows pins every container — and its
   native composition surface — for the life of the app. `_hoverWiredContainers` did exactly
   this and leaked gigabytes over a day (the periodic auto-sync rebuilds the flyout every few
   minutes). `RebuildColumnsPanel` now calls `ClearHoverWiring()` alongside
   `_editStyledContainers.Clear()` / `_loadedIconChildLists.Clear()`; detach the pointer
   handlers too, not just clear the set, so the WinRT event registrations release as well.
