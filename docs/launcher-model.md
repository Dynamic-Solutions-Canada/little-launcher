> **Scope:** Use when adding a launcher kind or item type, or changing how groups, headings and column breaks behave.
> **Governs:** `**/Models/Launcher.cs`, `**/Models/LauncherItem.cs`, `Controls/LauncherItemsView*`.

# Launcher kinds and item types

## Launcher kinds

| Kind | `Launcher.Kind` | Tray click opens | Editing |
|---|---|---|---|
| Shortcuts | `LauncherKinds.Items` (0, default) | `FlyoutWindow` with the launcher's items | Shared item view in the tray flyout or regular Settings-origin editor |
| Web page | `LauncherKinds.Web` (1) | `WebFlyoutWindow` on the resolved `Launcher.WebAddress`, with independent `WebHomeUrl` and a separate bookmark bar | Launcher settings grouped into General, Appearance, Size and position, Behavior, and Privacy and sign-ins, with matching quick controls and section links in the flyout menu. Advanced holds troubleshooting actions; the bookmark bar supports direct editing |

A web launcher has no items, so item editing, sharing and bulk operations are hidden for it. See [web-launchers.md](web-launchers.md).

## Launcher item types

| Type | `IsWebsite` | `IsPwa` | `Path` | `Arguments` | Launch method |
|---|---|---|---|---|---|
| Website | `true` | `false` | URL | none | `UseShellExecute` or app-window mode |
| Application | `false` | `false` | exe path | optional args | `Process.Start(Path, Arguments)` |
| Progressive Web App | `false` | `true` | AUMID (e.g. `domain-HEX_hash!App`) | none | `explorer shell:AppsFolder\{Path}` |
| Heading | | | | | Not launchable (visual divider, renamed from Category) |
| Group | | | | | Not launchable (collapsible parent containing child items/headings via `Children` collection) |
| Column Break | | | | | Not launchable (splits the flyout into a new side-by-side column; `IsColumnBreak = true`) |

## Groups

Groups have a `Children` (`ObservableCollection<LauncherItem>`) collection for child items and headings. The shared `LauncherItemsView` templates render their child lists in both native and regular hosts. `LauncherItem.IsExpanded` remains transient (`[JsonIgnore]`, defaults `true`); extracting a view must not change that model state or serialization.

## Column breaks

Column breaks (`IsColumnBreak = true`) are invisible sentinels in the flat `Items` list. `LauncherItemsView.BuildColumnLists()` derives side-by-side column collections; `SyncColumnsToFlatList()` flushes reorder operations back to the flat list. Structural edits instead mutate `Launcher.Items` and rebuild derived columns. Column widths come from shared layout arithmetic and string measurement, preserving identical item geometry in the native popup and the regular scrolling editor. Add or remove columns through the shared edit commands. Create a sentinel with `LauncherItem.CreateColumnBreak()`.

## Editing

While editing, item and group context menus offer move, edit or rename, and remove actions; the shared hover pencil is an overlay that does not affect layout. Move to lists groups and other launchers for cross-launcher moves. Read-only shared subscribers cannot edit. Per-launcher bulk export and import stay on the separate ellipsis action beside Settings Items. Drag-and-drop rules are in [drag-drop.md](drag-drop.md).

## Progressive Web Apps

PWAs are auto-detected by enumerating `shell:AppsFolder` for Chromium-registered app entries (AUMIDs matching `{domain}-{HEX}_{hash}!App`). Icons are fetched from the PWA domain via `FaviconService.FetchAndCacheAsync()`; see [icons.md](icons.md).
