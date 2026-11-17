# Little Launcher: Agent Instructions

Little Launcher puts tray flyouts for shortcuts and live web pages on Windows and Linux, with launchers synced between devices. It is two native applications in one repo: Windows uses .NET 10 WinUI 3 and WebView2 and ships only through the Microsoft Store as a paid MSIX (GitHub Releases attach no Windows build); Linux uses Qt/PySide6 with a sandboxed Electron web engine and ships only as an x64 AppImage, with KDE as the primary target. Portable launcher data and selected icons sync between the two. The "Our other apps" page comes from the apps-promo submodule at `external/promo`.

## Standards

This repo follows the shared standards at https://github.com/Dynamic-Solutions-Canada/app-standards. Read the index for this stack, https://github.com/Dynamic-Solutions-Canada/app-standards/blob/main/stacks/windows.md, then the topic file for the area you are changing. If `external/standards/` exists, it is the same content pinned for offline use. Where this file differs from the standards, this file wins and says why.

## Architecture

- **Behaviour and boundaries** are in [docs/architecture.md](docs/architecture.md); **module ownership** is in [docs/components.md](docs/components.md). Read the relevant sections before changing an area.
- **Windows app** (`LittleLauncher/`): `MainWindow` is the invisible host that owns the tray icons and single instance; `SettingsWindow` hosts the Settings pages; `FlyoutWindow` (shortcuts) and `WebFlyoutWindow` (web pages) are the tray flyouts. `LauncherShortcut/` is the Native AOT companion helper used for taskbar pins.
- **Linux app** (`linux/`): a separate Qt host with an isolated Electron browser process. It shares the launcher document format and the translation catalogs with Windows, not code.
- **Shared** (`Shared/`): the translation catalogs and `UiText`, compiled into both Windows projects and read by Linux.
- Nested `AGENTS.md` files hold area rules. List them with `git ls-files '*AGENTS.md'` and read the ones for the folders you touch.

## Behaviour

- A launcher is either a **shortcuts** launcher (items, groups, headings, column breaks) or a **web** launcher (one web page with its own bookmark bar). A web launcher has no items, so item editing, sharing and bulk operations are hidden for it. Kinds and item types are in [docs/launcher-model.md](docs/launcher-model.md).
- A shortcut launcher belongs to the OS that created it; on the other OS it stays in Settings as disabled and has no tray icon. Web launchers are shared. See [docs/adr/0004-whole-launcher-platform-ownership.md](docs/adr/0004-whole-launcher-platform-ownership.md).
- Settings, sign-ins and browser profiles of an installed copy must survive every update. Permanent identifiers never change: the Store package identity, the tray GUIDs, the mutex name, the startup task ID, the data folder name and the Linux application ID (see [docs/linux.md](docs/linux.md#permanent-application-id)).

## Conventions and gotchas

- Use `[ObservableProperty]` from CommunityToolkit.Mvvm for every bindable setting. Partial `On<Property>Changed` methods in `UserSettings` handle side effects; the `_initializing` flag suppresses them during deserialization.
- P/Invoke declarations live in `NativeMethods.cs`, imported with `using static LittleLauncher.Classes.NativeMethods;`. New declarations use `[LibraryImport]`; existing ones use `[DllImport]`.
- Pages are WinUI 3 `Page` objects navigated through `NavigationView` with `TargetPageType` in XAML; there is no MVVM routing framework. Commands use `CommunityToolkit.Mvvm.Input.RelayCommand`.
- All UI text lives in the shared catalogs under `Shared/Localization/`, for both Windows and Linux: XAML uses `{l:Str Key=area.name}`, C# uses `UiText`, Python uses `i18n.t`. Every new key goes into all seven catalogs; see [docs/localization.md](docs/localization.md).
- **MSIX VFS rule:** any path read by another process (shell `.lnk` files, the companion helper) uses `MainWindow.GetPhysicalAppDataDir()`, never `Environment.GetFolderPath(ApplicationData)`, which is redirected inside the package. See [docs/icons.md](docs/icons.md).
- A new launcher item feature extends `LauncherItem` and the shared `LauncherItemsView` renderer, so the tray flyout and the regular editor both get it; use the `add-launcher-feature` skill.
- Do not apply Windows MSIX install or launch commands to the Linux app.

## Commands

Source is authoritative in WSL, with submodules initialised (`git submodule update --init`); the desktop commands, test runner and checks come from winui-app-kit at `external/winui-app-kit`. Windows builds go through the shared Windows build tools; [docs/windows-development.md](docs/windows-development.md) has this repo's specifics.

| Command | Proves | Does not prove |
| --- | --- | --- |
| `npm run verify:desktop` | The Windows app and helper compile (Debug), XAML included | That it packages or runs; the `tests/` harnesses are not compiled |
| `npm run package:desktop:dev` | A signed dev package builds | That it installs or runs |
| `npm run install:desktop` | The dev package updates the installed copy in place and launches | Behaviour; check it in the running app |
| `npm run package:desktop` | An unsigned Store package builds | Signing; that it runs |
| `npm test` | Every .NET harness under `tests/` (found by winui-app-kit's `run-tests`) and `test:linux` pass, on Linux | What needs a window, the installed package or the shell (see each harness README) |
| `npm run test:linux` | The Linux portable checks and Node suites pass | Native Qt behaviour, which is skipped without PySide6 |
| `npm run check:l10n` | Every catalog has the English keys and placeholders, and the language lists agree | Translation quality |
| `npm run validate` | The Linux gate: `format:check`, `check:gitignore`, `check:l10n`, then `npm test` | A Windows compile; also run `verify:desktop` |
| `npm run format` | Rewrites C# outside `external/` to the root `.editorconfig` | Behaviour |

Before finishing a change, run `npm run validate` and `npm run verify:desktop`. The native suite under `tests-native/` drives a real desktop and never runs from these commands; see [tests-native/AGENTS.md](tests-native/AGENTS.md).

Local verification uses the sideloaded Release MSIX through the `desktop` skill, updating the installed package in place so its settings and sign-ins survive. Use a debug or unpackaged build only when asked for one.

## Release

`<Version>` in `Directory.Build.props` is the only version string. A `v*` tag runs `build-msix.yml` (the GitHub Release, with the Linux AppImage) and `store-publish.yml` (a Store draft; submitting it is a separate manual run). Use the `release` skill; [docs/versioning.md](docs/versioning.md) and [docs/installer.md](docs/installer.md) have the detail.

## Guidance map

| Guide, doc, or skill | Read or use when | Governs |
| --- | --- | --- |
| [docs/README.md](docs/README.md) | Looking for any project document | `docs/**` |
| [docs/development.md](docs/development.md) | Setting up, checking dependencies, or finding a file | Prerequisites, tech stack, project layout |
| [docs/windows-development.md](docs/windows-development.md) | Building, verifying or installing on Windows from WSL | `windows-build.json`, `scripts/windows-build`, `global.json`, `Directory.Build.*` |
| [docs/launcher-model.md](docs/launcher-model.md) | Adding a launcher kind or item type | `Models/Launcher.cs`, `Models/LauncherItem.cs`, `Controls/LauncherItemsView*` |
| [docs/linux.md](docs/linux.md) and [linux/README.md](linux/README.md) | Native Linux UI, the browser boundary, desktop integration, packages, Linux dependencies, and `start:linux` / `package:linux` | `linux/**` |
| [docs/pinvoke.md](docs/pinvoke.md) | P/Invoke, Win32 interop, native signatures | `Classes/NativeMethods.cs` |
| [docs/user-settings.md](docs/user-settings.md) | Observable settings, the Launcher model, serialization | `ViewModels/UserSettings*.cs`, `Models/Launcher.cs` |
| [docs/drag-drop.md](docs/drag-drop.md) | Shared launcher drag-and-drop, edit mode and hosts | `Controls/LauncherItemsView*`, `Controls/ILauncherItemsHost.cs`, `Windows/FlyoutWindow*.cs`, `Windows/LauncherItemsEditorWindow.cs`, `SettingsWindow.ItemEditing.cs` |
| [docs/web-launchers.md](docs/web-launchers.md) | Web launchers, the WebView2 flyout, its resource policy | `Windows/WebFlyoutWindow.cs`, `Windows/LauncherPanels.cs` |
| [docs/sync.md](docs/sync.md) | Global sync, sync providers, shared launchers, adding a transport | `Services/*SyncService.cs`, `Services/*SharedStore.cs`, `Services/LauncherPayload.cs`, `Services/CloudFolderService.cs`, `Pages/SyncPage.xaml*` |
| [docs/xaml.md](docs/xaml.md) | WinUI 3 XAML, controls, backdrops | `**/*.xaml`, `SettingsWindow.ItemEditing.cs` |
| [docs/localization.md](docs/localization.md) | Interface language, catalogs, adding UI text | `Shared/Localization/**`, `Shared/UiText.cs`, `Shared/SupportedLanguages.cs`, `LittleLauncher/Localization/**`, `linux/little_launcher/i18n.py` |
| [docs/icons.md](docs/icons.md) | App, tray, shortcut and window icons, the icon gallery, website icons | `MainWindow.xaml.cs`, `SettingsWindow.xaml.cs`, `LaunchersPage.xaml*`, `HomePage.xaml.cs`, `FlyoutConverters.cs`, `FlyoutWindow.xaml*`, `IconGallery.cs`, `FaviconService*.cs`, `LauncherShortcut/**` |
| [docs/installer.md](docs/installer.md) | Packaging, install and upgrade, update flows, uninstall cleanup | `UpdateService.cs`, `build-msix.ps1`, `Package.appxmanifest`, `cleanup-uninstall.ps1` |
| [docs/versioning.md](docs/versioning.md) | Version bumps and releases | `Directory.Build.props`, `release-notes/**`, `Package.appxmanifest`, `build-msix.ps1`, `.github/workflows/**` |
| [tests/AGENTS.md](tests/AGENTS.md), [tests-native/AGENTS.md](tests-native/AGENTS.md) | Changing a harness or the native suite | `tests/**`, `tests-native/**` |
| `desktop` skill | Building, verifying, packaging or installing the Windows app locally | Local Windows builds |
| `add-setting`, `add-settings-page`, `add-launcher-feature` skills | Adding a setting, a Settings page or a launcher item feature | The files each names |
| `audit-docs` skill | Before a release, or after a batch of changes | Every guide, nested `AGENTS.md` and skill |
| `commit` skill | Committing requested changes | Commits |
| `release` skill | Cutting a release | `Directory.Build.props`, release notes, tags |

## Keeping docs current

A task is not done until its docs are updated. Read a guide before deciding it needs no change.

| What changed | Update |
| --- | --- |
| A service, class or module added, moved or removed | `docs/components.md` |
| Behaviour, boundaries or a new page or navigation | `docs/architecture.md` and `docs/components.md` |
| A settings or Launcher model property | `docs/user-settings.md` |
| A launcher kind or item type | `docs/launcher-model.md` |
| A P/Invoke declaration | `docs/pinvoke.md` |
| Icons | `docs/icons.md` (surfaces, TrayIconMode, gotchas) |
| Packaging, update flows or uninstall cleanup | `docs/installer.md` |
| The version process | `docs/versioning.md`; `Directory.Build.props` stays the only version string |
| A XAML pattern | `docs/xaml.md` |
| Drag and drop | `docs/drag-drop.md` |
| A dependency added, removed or updated | The tech stack in `docs/development.md` (Windows) or `linux/README.md` (Linux) |
| An npm command | The Commands table above, `docs/windows-development.md`, the `desktop` skill, `paseo.json` |
| A guide or skill added, moved or removed | The guidance map above, `docs/README.md`, the nested `AGENTS.md` that should link it, and every skill that names it |
| Any structural change | `docs/architecture.md`, and `README.md` if users would notice |
