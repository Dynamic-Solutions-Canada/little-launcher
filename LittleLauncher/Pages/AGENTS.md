# LittleLauncher/Pages

Settings pages are WinUI 3 `Page` objects navigated via `NavigationView`. Follow the XAML conventions for all `.xaml` here.

Read and follow [xaml.md](../../docs/xaml.md) before changing the code described above.

**There is no item renderer here.** `Controls/LauncherItemsView` shares rendering/edit behavior between the native flyout and regular `Windows/LauncherItemsEditorWindow`; the former `LauncherItemsPage` remains removed. Settings Items and new-launcher Next open that regular owned editor, preserving captured setup placement for the latter. `LaunchersPage` keeps only launcher-level concerns; cards, sharing, and the per-launcher bulk operations in `LauncherBulkOps` (export, import, bookmark import). Per-launcher settings open in `LauncherSettingsWindow`, shared with the flyout.

Shortcut cards use a direct **Items** edit button and a separate sibling ellipsis button for
export/import/bookmark import. Reuse `CreateLauncherActionButton` for the Settings/Items
chevron row shape. Keep the secondary button enabled for read-only subscribers so export
remains available, while editing and imports remain disabled. Do not nest interactive buttons.
Web launcher cards have one **Settings** row, showing their web address or **No web address set**
as its subtitle. It opens the existing launcher settings window; do not add a second address row.

`LaunchersPage` drives per-launcher tray/pin icons. When editing it, follow the icon system conventions: [docs/icons.md](../../docs/icons.md).

Its messaging preset creation flow also follows [web-launchers.md](../../docs/web-launchers.md).

`SyncPage` is the UI for global sync and swaps panels per sync provider. When editing it, follow the sync conventions: [docs/sync.md](../../docs/sync.md).

## Bookmark UI

Two surfaces read browser bookmarks, sharing `BookmarkImport.ReadBookmarks` / `.Flatten` and
`BrowserCatalog` but nothing of their presentation:

| | `LauncherBulkOps.ImportBookmarksAsync` | `BookmarkPicker.PickAsync` |
|---|---|---|
| Answers | "which of these do I want?" | "where is the one I already know?" |
| Shape | folder tree, multi-select | flat list, single-select |
| Search | filters the tree, keeping folders with a matching descendant | filters the flat list |

**In the tree selector, selection lives in a `HashSet<BookmarkNode>`, not in the `TreeView`.**
Filtering rebuilds the nodes, so a selection held only by the control is destroyed on the next
keystroke — tick three bookmarks, search for something else, and those three never get imported.
The tree is authoritative only for the rows it is currently showing; `SyncChosenFromTree` merges
that back without touching anything filtered out. Verified: select 4 matches of one search, switch
the search, select 1 more, clear the search → 5 selected.
