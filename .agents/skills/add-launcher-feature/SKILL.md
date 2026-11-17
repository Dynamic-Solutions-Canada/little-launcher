---
name: add-launcher-feature
description: Add a feature to launcher items across the LauncherItem model, the shared item view, the flyout and the item editor. Use when shortcut items need a new property or behaviour.
---

# Add a launcher feature

Goal: a launcher item feature that works in both item hosts, saves and syncs, and has translated text.

1. Read [docs/launcher-model.md](../../../docs/launcher-model.md) and [docs/drag-drop.md](../../../docs/drag-drop.md).
2. Extend `LittleLauncher/Models/LauncherItem.cs` with the new properties and their defaults. Keep transient state `[JsonIgnore]`.
3. Update the shared `LittleLauncher/Controls/LauncherItemsView*` renderer and edit behaviour, so both the tray flyout and the regular editor show it.
4. Add the editing controls to `LittleLauncher/Windows/ItemEditorWindow.cs`.
5. Add any new text to the shared catalogs as described in [docs/localization.md](../../../docs/localization.md), in `en-US` and all six other catalogs.
6. If Linux shows launcher items the same way, mirror the change there (see [docs/linux.md](../../../docs/linux.md)), and check that sync keeps the new property ([docs/sync.md](../../../docs/sync.md)).
7. Run `npm run verify:linux`, then build with the `desktop` skill.

Report: the model change, the hosts and editor updated, the sync and Linux impact, and the build and check results.
