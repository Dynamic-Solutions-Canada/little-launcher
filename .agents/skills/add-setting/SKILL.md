---
name: add-setting
description: Add an observable setting to UserSettings with its serialization, change handler and optional Settings UI. Use when a new user preference or persisted option is needed.
---

# Add a setting

Goal: a new setting that saves, restores and, if it has UI, shows translated text in every language.

1. In `LittleLauncher/ViewModels/UserSettings.cs`, add an `[ObservableProperty]` field in the matching category section. If changing it needs a side effect, implement `partial void On{PropertyName}Changed` guarded by `_initializing`. Follow [docs/user-settings.md](../../../docs/user-settings.md) for defaults and serialization.
2. If it needs UI, add the control to the relevant settings page and bind it to `SettingsManager.Current.{PropertyName}` with `Mode=TwoWay`.
3. Add its text to the shared catalogs as described in [docs/localization.md](../../../docs/localization.md): the key in `Shared/Localization/en-US/<area>.json` and all six other catalogs, used from XAML with `{l:Str Key=...}`.
4. If Linux has the same setting, mirror it there (see [docs/linux.md](../../../docs/linux.md)).
5. Run `npm run verify:linux` for the catalog checks, then build with the `desktop` skill.

Report: the setting's name and default, where its UI is, and the build and check results.
