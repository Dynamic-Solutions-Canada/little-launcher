> **Read when:** writing or changing Windows C# or XAML in `LittleLauncher/` or `LauncherShortcut/`: a setting, a page, a P/Invoke declaration, UI text, a path another process reads, or a launcher item feature.
> **Governs:** `LittleLauncher/**/*.cs`, `LittleLauncher/**/*.xaml`, `LauncherShortcut/**`

# Code conventions

The Windows app's coding rules and the gotchas it has hit. Each topic's detail is in the doc linked from the bullet; Linux rules are in [docs/linux.md](../../docs/linux.md).

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for every bindable setting. Partial `On<Property>Changed` methods in `UserSettings` handle side effects; the `_initializing` flag suppresses them during deserialization.
- P/Invoke declarations live in `NativeMethods.cs`, imported with `using static LittleLauncher.Classes.NativeMethods;`. New declarations use `[LibraryImport]`; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects in winui-app-kit's `SettingsShell`, which `SettingsWindow` maps from menu tags to page types; About is the kit's shared page. There is no MVVM routing framework. Commands use `CommunityToolkit.Mvvm.Input.RelayCommand`.
- All UI text lives in the shared catalogs under `Shared/Localization/`, for both Windows and Linux: XAML uses `{l:Str Key=area.name}`, C# uses `UiText`, Python uses `i18n.t`. Every new key goes into all seven catalogs; see [docs/localization.md](../../docs/localization.md).
- **MSIX VFS rule:** any path read by another process (shell `.lnk` files, the companion helper) uses `MainWindow.GetPhysicalAppDataDir()`, never `Environment.GetFolderPath(ApplicationData)`, which is redirected inside the package. See [docs/icons.md](../../docs/icons.md).
- A new launcher item feature extends `LauncherItem` and the shared `LauncherItemsView` renderer, so the tray flyout and the regular editor both get it; use the `add-launcher-feature` skill.
- Do not apply Windows MSIX install or launch commands to the Linux app.
