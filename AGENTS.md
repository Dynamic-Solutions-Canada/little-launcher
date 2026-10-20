# Little Launcher — Codex Instructions

## Project overview

Little Launcher is a .NET 10 WinUI 3 desktop application (unpackaged) that provides a system-tray launcher with a flyout popup for shortcuts. It also syncs settings to a remote server via SSH/SFTP.

## Architecture

Read the relevant component and namespace sections in [.codex/docs/project-architecture.md](.codex/docs/project-architecture.md) before changing that area. Keep that reference current when architecture or namespaces change.

## Topic-specific guidance (scoped instructions)

These guides hold detailed conventions for specific areas. Codex auto-loads the nearest `AGENTS.md` when you work inside a subdirectory, and those nested files explicitly direct you to read the relevant guide(s) below. When working in a given area, read the matching guide:

| Guide | Read when working on | Files it governs |
|---|---|---|
| [.codex/docs/pinvoke.md](.codex/docs/pinvoke.md) | P/Invoke, Win32 interop, native signatures | `Classes/NativeMethods.cs` |
| [.codex/docs/user-settings.md](.codex/docs/user-settings.md) | Observable settings, Launcher model, serialization | `ViewModels/UserSettings*.cs`, `Models/Launcher.cs` |
| [.codex/docs/drag-drop.md](.codex/docs/drag-drop.md) | Shared launcher drag-and-drop, edit mode and hosts | `Controls/LauncherItemsView*`, `Controls/ILauncherItemsHost.cs`, `Windows/FlyoutWindow*.cs`, `Windows/LauncherItemsEditorWindow.cs`, `SettingsWindow.ItemEditing.cs` |
| [.codex/docs/web-launchers.md](.codex/docs/web-launchers.md) | Web launchers, the WebView2 flyout, its resource policy | `Windows/WebFlyoutWindow.cs`, `Windows/LauncherPanels.cs` |
| [.codex/docs/sync.md](.codex/docs/sync.md) | Global sync, sync providers, adding a transport | `Services/*SyncService.cs`, `Services/LauncherPayload.cs`, `Services/CloudFolderService.cs`, `Pages/SyncPage.xaml*` |
| [.codex/docs/xaml.md](.codex/docs/xaml.md) | WinUI 3 XAML, controls, localization, backdrops | `**/*.xaml`, `SettingsWindow.ItemEditing.cs` |
| [.codex/docs/icons.md](.codex/docs/icons.md) | App/tray/shortcut/window icons | `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `LaunchersPage.xaml*`, `HomePage.xaml.cs`, `FlyoutConverters.cs`, `FlyoutWindow.xaml*`, `IconGallery.cs`, `LauncherShortcut/**` |
| [.codex/docs/installer.md](.codex/docs/installer.md) | Packaging (portable zip + Store MSIX), install/upgrade, update flows | `UpdateService.cs`, `build-msix.ps1`, `Package.appxmanifest` |
| [.codex/docs/versioning.md](.codex/docs/versioning.md) | Version bumps and releases | `Directory.Build.props`, `MainWindow.xaml.cs`, `Package.appxmanifest`, `build-msix.ps1`, `.github/workflows/build-msix.yml` |

## Conventions

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for all bindable settings properties.
- Partial `On<Property>Changed` methods in `UserSettings` handle side-effects (theme changes, taskbar updates).
- An `_initializing` flag in `UserSettings` suppresses change handlers during deserialization.
- P/Invoke declarations live in `NativeMethods.cs`. Always use `using static LittleLauncher.Classes.NativeMethods;` imports.
- Use `[LibraryImport]` for new P/Invoke declarations; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects navigated via `NavigationView`. No MVVM framework routing — just `TargetPageType` in XAML.
- String resources live in `Resources/Localization/Dictionary-en-US.xaml`. In code: `Application.Current.Resources.TryGetValue("KeyName", out object value)`.
- Use `CommunityToolkit.Mvvm.Input.RelayCommand` for ICommand implementations.
- **MSIX VFS rule:** Any file path referenced by external processes (shell `.lnk` files, companion exe) must use `MainWindow.GetPhysicalAppDataDir()`, not raw `Environment.GetFolderPath(ApplicationData)`. The latter is VFS-redirected inside MSIX. See [.codex/docs/icons.md](.codex/docs/icons.md) for details.

## Build

```bash
npm run verify:desktop
```

`Directory.Build.props` auto-detects the platform from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). For the shared npm commands, pass `-- --platform x64` or `-- --platform ARM64`.

Target: `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

Release builds AOT-publish the companion exe (`LauncherShortcut`) automatically. Debug builds copy the framework-dependent output for faster iteration.

## Dependencies

- Microsoft.WindowsDesktop.App (framework reference for `System.Windows.Automation` taskbar button detection)
- Microsoft.WindowsAppSDK 1.8.260209005 (WinUI 3)
- H.NotifyIcon.WinUI 2.4.1 (system tray)
- CommunityToolkit.Mvvm 8.4.0
- SSH.NET 2026.0.0
- NLog 6.1.1
- Microsoft.Data.Sqlite 10.0.13 (Firefox bookmark import from places.sqlite)
- Microsoft Edge WebView2 — the managed API arrives transitively with the Windows App SDK (no explicit `PackageReference`), and web launchers need the **Evergreen WebView2 Runtime** on the machine. It ships with Windows 11; `WebFlyoutWindow` surfaces an install prompt if `CoreWebView2Environment` cannot start.

## Common tasks

- **Add a new settings page:** Create `Pages/FooPage.xaml` + `.cs`, add a `NavigationViewItem` in `SettingsWindow.xaml`, add any new string keys to `Dictionary-en-US.xaml`.
- **Add a new launcher feature:** Extend `LauncherItem`, update the shared `LauncherItemsView` renderer/edit behavior, and update `ItemEditorWindow` for its form. Preserve both native and regular hosts.
- **Add a new setting:** Add an `[ObservableProperty]` to `UserSettings.cs`. It will auto-serialize to JSON.
- **Add/extend icon gallery tabs:** Edit `Classes/IconGallery.cs`; add entries to `FluentIconCategories` or `EmojiCategories` arrays. The gallery supports Segoe Fluent Icons (PUA glyphs), emojis, and app color icons. Icon selection uses a pending-selection model with Confirm/Cancel buttons. Selected icons get an accent border. Color swatches override `ButtonBackgroundPointerOver`/`ButtonBackgroundPressed` to keep their color on hover. When opened, the gallery pre-selects the current icon (correct tab, color swatch, and icon button) so the user can change just the color without re-finding the icon.

## Launcher kinds

| Kind | `Launcher.Kind` | Tray click opens | Editing |
|---|---|---|---|
| Shortcuts | `LauncherKinds.Items` (0, default) | `FlyoutWindow` with the launcher's items | Shared item view in the tray flyout or regular Settings-origin editor |
| Web page | `LauncherKinds.Web` (1) | `WebFlyoutWindow` on `Launcher.WebAddress`; the first of its `WebBookmarks`; with the rest as a bar along the bottom | Launcher settings grouped into General, Appearance, Size and position, Behavior, and Privacy and sign-ins, with matching quick controls and section links in the flyout menu. Advanced holds troubleshooting actions; the bookmark bar supports direct editing |

A web launcher has no items, so item editing, sharing and bulk operations are hidden for it. See [.codex/docs/web-launchers.md](.codex/docs/web-launchers.md).

## Launcher item types

| Type | `IsWebsite` | `IsPwa` | `Path` | `Arguments` | Launch method |
|---|---|---|---|---|---|
| Website | `true` | `false` | URL | — | `UseShellExecute` or app-window mode |
| Application | `false` | `false` | exe path | optional args | `Process.Start(Path, Arguments)` |
| Progressive Web App | `false` | `true` | AUMID (e.g. `domain-HEX_hash!App`) | — | `explorer shell:AppsFolder\{Path}` |
| Heading | — | — | — | — | Not launchable (visual divider, renamed from Category) |
| Group | — | — | — | — | Not launchable (collapsible parent containing child items/headings via `Children` collection) |
| Column Break | — | — | — | — | Not launchable (splits the flyout into a new side-by-side column; `IsColumnBreak = true`) |

Groups have a `Children` (`ObservableCollection<LauncherItem>`) collection for child items/headings. The shared `LauncherItemsView` templates render their child lists in both native and regular hosts. `LauncherItem.IsExpanded` remains transient (`[JsonIgnore]`, defaults `true`); extracting a view must not change that model state or serialization.

Column breaks (`IsColumnBreak = true`) are invisible sentinels in the flat `Items` list. `LauncherItemsView.BuildColumnLists()` derives side-by-side column collections; `SyncColumnsToFlatList()` flushes reorder operations back to the flat list. Structural edits instead mutate `Launcher.Items` and rebuild derived columns. Column widths come from shared layout arithmetic and string measurement, preserving identical item geometry in the native popup and the regular scrolling editor. Add/remove columns through the shared edit commands. Create a sentinel with `LauncherItem.CreateColumnBreak()`.

While editing, item/group context menus offer move, edit/rename and remove actions; the shared hover pencil is an overlay that does not affect layout. Move to lists groups and other launchers for cross-launcher moves. Read-only shared subscribers cannot edit. Per-launcher bulk export/import stays on the separate ellipsis action beside Settings Items.

PWAs are auto-detected by enumerating `shell:AppsFolder` for Chromium-registered app entries (AUMIDs matching `{domain}-{HEX}_{hash}!App`). Icons are fetched from the PWA domain via `FaviconService.FetchAndCacheAsync()`.
- **Release a new version:** Edit `<Version>` in `Directory.Build.props`, commit, tag `vX.Y.Z`, push — that file is the only version string to touch. The MSIX manifest version is auto-stamped by `LittleLauncherMSIX/build-msix.ps1`. See [.codex/docs/versioning.md](.codex/docs/versioning.md) for the full checklist.
- **Update the app icon:** Replace `Resources/AppIcons/Blue.png`, then regenerate `Resources/LittleLauncher.ico` from it (multi-resolution ICO: 16–256px). The `.ico` is committed — it's not auto-generated by the build. See [.codex/docs/icons.md](.codex/docs/icons.md).

## Documentation maintenance

**Documentation updates are part of the task — a task is not done until docs are updated.**

After completing any feature, bug fix, or structural change, review and update the affected documentation before considering the task done. This includes:

| What changed | Update these |
|---|---|
| New/removed service or class | `.codex/docs/project-architecture.md` (Key namespaces table), repo memory |
| New/changed settings property | [.codex/docs/user-settings.md](.codex/docs/user-settings.md), repo memory |
| New/changed P/Invoke | [.codex/docs/pinvoke.md](.codex/docs/pinvoke.md) |
| Icon system changes | [.codex/docs/icons.md](.codex/docs/icons.md) (surfaces table, TrayIconMode table, gotchas) |
| Packaging or update-flow changes | [.codex/docs/installer.md](.codex/docs/installer.md) |
| Version bump | [.codex/docs/versioning.md](.codex/docs/versioning.md) if the process changed; `Directory.Build.props` is the only version string |
| New/changed XAML patterns | [.codex/docs/xaml.md](.codex/docs/xaml.md) |
| Drag-and-drop changes | [.codex/docs/drag-drop.md](.codex/docs/drag-drop.md) |
| New page or navigation change | `.codex/docs/project-architecture.md` (Architecture section) |
| New dependency added/removed | `AGENTS.md` (Dependencies list) |
| Any structural change | `ARCHITECTURE.md`, `README.md` if affected |

**Rule:** When you create new files for an area, make sure the matching topic guide in `.codex/docs/` still describes them, and that the nested `AGENTS.md` in that directory links to the right guide(s). A guide that isn't referenced from the directory you're editing won't be loaded.

**Rule:** Read the relevant topic guide before deciding whether it needs updating — don't skip this based on assumptions.

## Codex documentation and workflows

Topic guides live in `.codex/docs/`; reusable workflows live in `.agents/skills/`.
Read the applicable nested `AGENTS.md` and its linked guides before editing a directory.
Markdown links are explicit reading instructions, not automatic imports. Maintain these Codex files as the canonical project guidance.
See [.codex/README.md](.codex/README.md) for the workflow index.

## Local build and launch preference

Use the **sideloaded Release MSIX** for local verification and launch unless the user explicitly
requests a debug or unpackaged build. Follow the
[rebuild skill](.agents/skills/source-command-rebuild/SKILL.md) and the
[packaging guide](.codex/docs/installer.md). Update the existing package in place so its settings
and sign-ins survive. Confirm `SignatureKind = Developer` and that the running executable is
inside the installed package's `InstallLocation`; stop any debug copy before launching.
A path under WindowsApps alone does not distinguish a Store install from a sideloaded one.

## Windows desktop workflow

Source is authoritative in WSL. Read `.codex/docs/windows-development.md` and use
`npm run build:desktop`, `npm run verify:desktop`, `npm run install:desktop`, and
`npm run package:desktop` from the repository root. The common bridge builds in
a disposable C: Windows TEMP directory and cleans it after use. Never use a D:
checkout or build directly against WSL source with Windows tools. Dev install
versions change only in the temporary copy; never uninstall to bypass an update
error. Store releases keep their existing project-specific release workflow.
