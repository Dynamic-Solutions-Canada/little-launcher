---
name: "add-setting"
description: "Add a new observable setting property to UserSettings with serialization, change handler, and optional UI binding."
---

# Add a setting

Add a new setting property to the Little Launcher application.

## Steps

1. In `LittleLauncher/ViewModels/UserSettings.cs`:
   - Add an `[ObservableProperty]` field in the appropriate category section
   - If the setting needs a side-effect, implement `partial void On{PropertyName}Changed` with `_initializing` guard

2. The property auto-serializes to `settings.json` — no additional serialization code needed

3. If the setting needs UI:
   - Add a control in the relevant settings page XAML
   - Bind to `SettingsManager.Current.{PropertyName}` with `Mode=TwoWay`
   - Add localization string keys to `Resources/Localization/Dictionary-en-US.xaml`

4. Build and verify through the [sideloaded Release MSIX workflow](../rebuild-desktop/SKILL.md)

See [docs/user-settings.md](../../../docs/user-settings.md) for the full UserSettings conventions.
