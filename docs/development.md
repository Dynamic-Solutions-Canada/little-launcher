# Developing Little Launcher

Little Launcher has separate native Windows and Linux hosts. The Windows workflow is documented below; Linux uses Qt/PySide6 with a sandboxed Electron web engine. Start with [the Linux development guide](../linux/README.md) for source runs, verification and x64 AppImage packaging.

## Setup

Clone with submodules (`--recurse-submodules`, or `git submodule update --init` afterwards), run `npm ci --prefix linux/browser` (the Linux browser's packages, which the Node tests load), then run `lefthook install` once so the git hooks in `lefthook.yml` block signing material, credentials, settings files and large files from being committed. Install [lefthook](https://lefthook.dev) first if it is missing.

## Windows prerequisites

- Windows 10/11 (build 22000+)
- [.NET SDK 10.0.403 or a later .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), selected by `global.json` (stable SDKs only).

Builds select the SDK's latest .NET runtime patch, at least 10.0.14, including the NativeAOT shortcut helper and self-contained Store output. Portable builds require that patched shared runtime or later. Explicit runtime overrides below 10.0.14 fail before runtime-pack resolution. See [the build security floor](windows-development.md#net-build-security-floor).

## Windows build

```bash
npm run verify:desktop
```

The commands build for the build host's architecture by default. [windows-development.md](windows-development.md) lists them and what is specific to this repo; the shared Windows build tools document the rest.

## Checks

`npm run validate` is the Linux gate, run before finishing any change together with `npm run verify:desktop`. In order, it runs:

- `npm run format:check`: every C# project outside `external/` matches the root `.editorconfig`, which is winui-app-kit's file copied unchanged; `npm run format` (winui-app-kit's `scripts/format`) rewrites the files, repeating until they settle.
- `npm run check:gitignore`: `.gitignore` ends with winui-app-kit's shared block, unchanged.
- `npm run check:l10n`: the catalog checks in [localization.md](localization.md#checks).
- `npm run check:listing`: winui-app-kit's Store listing check: `store-assets/microsoft/listings.json` and the current version's `release-notes/` files fit the Store's limits, and the README tagline is the first sentence of the `en-US` short description.
- `npm test`: every portable .NET harness under `tests/`, found by winui-app-kit's `scripts/run-tests` (each has a README saying what it covers and does not prove), then `npm run test:linux`.

Formatting and the .NET harnesses run through winui-app-kit's `scripts/dotnet`, which uses the SDK pinned in `global.json` when installed and otherwise its Docker image, capped at 3 GB. The native suite in `tests-native/` moves the pointer on a dedicated test desktop and never runs from these commands; its [README](../tests-native/LittleLauncher.NativeDrag/README.md) explains how to run it deliberately.

## Windows run

```bash
npm run install:desktop
```

Or open `LittleLauncher.slnx` in Visual Studio / Rider and press F5.

## Windows architecture

| Layer | Description |
|---|---|
| `MainWindow` | Invisible host window. Owns the system-tray icon (`H.NotifyIcon`). Enforces single-instance via Mutex. Cross-process IPC via registered window messages. |
| `FlyoutWindow` | A popup window that displays launcher items in list view, icon grid view, or a compact tray-sized small-icon grid, positioned above the taskbar. Dismissed on focus loss or Escape. Supports direct drag-and-drop reordering, item right-click move/edit/remove actions, and icon-grid edge resizing. |
| `SettingsWindow` | winui-app-kit's `SettingsShell` (Mica, title bar, navigation with a fixed footer): pages for Home, Launchers, Cloud Sync, Settings, Our other apps and the kit's About page. |
| `SftpSyncService` | Static async methods for upload/download/test-connection using SSH.NET (`Renci.SshNet`). Also handles per-launcher shared sync (file or SFTP). Supports private-key and password auth. |
| `AutoSyncService` | Manages automatic sync: startup download, debounced upload, periodic download, and shared launcher sync. |
| `SettingsManager` | Fully static. Serialises `UserSettings` to `%AppData%\LittleLauncher\settings.json` via `System.Text.Json`. Migrates from legacy `settings.xml` on first load. |
| `ThemeManager` | Sets `RequestedTheme` on root `FrameworkElement` of each window. Detects system dark/light mode via cached `UISettings`. |

See [docs/architecture.md](architecture.md) for detailed flows: launch modes, settings persistence, the launcher-item icon pipeline, SFTP sync, theming, the companion exe, and MSIX packaging.

## Windows tech stack

| Package | Version | Purpose |
|---|---|---|
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) | 1.8.260921001 (1.8.12, from winui-app-kit; the manifest requires runtime 8000.994.2142.0 or later) | WinUI 3 controls, Mica, NavigationView |
| [H.NotifyIcon.WinUI](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | System tray icon |
| [WinUI App Kit](https://github.com/Dynamic-Solutions-Canada/winui-app-kit) | submodule `external/winui-app-kit` | Shared build configuration, package versions and logging config |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.0 | Source-gen `[ObservableProperty]`, `RelayCommand` |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | 2026.0.0 | SFTP sync |
| [NLog](https://nlog-project.org/) | 6.2.2 | Logging, with winui-app-kit's shared config (one file per launch beside `settings.json`) |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) | 10.0.13 | Firefox bookmark import from `places.sqlite` |

- `Microsoft.WindowsDesktop.App` is a framework reference, for `System.Windows.Automation` taskbar button detection.
- The WebView2 managed API arrives with the Windows App SDK (no explicit package reference). Web launchers need the Evergreen WebView2 Runtime, which ships with Windows 11; `WebFlyoutWindow` offers to install it if `CoreWebView2Environment` cannot start.
- Linux dependencies are listed in [the Linux guide](../linux/README.md) and [docs/linux.md](linux.md).

**Target:** .NET 10, `net10.0-windows10.0.22000.0`, unpackaged (`WindowsPackageType=None`), platforms `x64` and `ARM64`.

## Project structure

```
LittleLauncher/              # WinUI 3 application project
├── App.xaml / App.xaml.cs     # Bootstrap, exception handling, settings restore
├── MainWindow.xaml/.cs        # Invisible host + tray icon + singleton IPC
├── SettingsWindow.xaml/.cs    # WinUI 3 settings UI with Mica backdrop
├── Classes/
│   ├── NativeMethods.cs       # P/Invoke declarations (user32, dwmapi, shcore, comctl32, shlwapi)
│   ├── ThemeManager.cs        # Theme orchestration (ElementTheme)
│   └── Settings/
│       └── SettingsManager.cs # JSON serialisation (fully static)
├── Models/
│   ├── LauncherItem.cs
│   ├── Launcher.cs            # Multi-launcher model with sharing properties
│   └── SshConnectionProfile.cs
├── Pages/
│   ├── HomePage.xaml/.cs
│   ├── LaunchersPage.xaml/.cs  # Launcher card management + sharing UI
│   ├── LauncherBulkOps.cs       # Item export/import + bookmark import
│   ├── SyncPage.xaml/.cs
│   ├── SystemPage.xaml/.cs
├── Services/
│   ├── AutoSyncService.cs     # Automatic sync triggers
│   ├── FaviconService.cs      # Website favicon & title fetching
│   └── SftpSyncService.cs     # SSH/SFTP upload/download/test + shared sync
├── ViewModels/
│   └── UserSettings.cs        # Observable settings (CommunityToolkit.Mvvm)
├── Windows/
│   └── FlyoutWindow.xaml/.cs   # Launcher flyout popup
├── Localization/Str.cs         # {l:Str Key=...} XAML markup extension
└── Resources/
    └── LittleLauncher.ico

Shared/Localization/           # Translation catalogs shared with Linux (see docs/localization.md)

LauncherShortcut/              # Windows companion exe for pin-to-taskbar helper
linux/                         # Native Linux Qt host, isolated Electron browser and AppImage packaging
```

## Where data lives

The app keeps settings, icons and browser profiles under `%AppData%\LittleLauncher\` and `%LocalAppData%\LittleLauncher\` on Windows (redirected into the package folder for a Store install, so uninstalling removes them) and under `~/.config/little-launcher/` on Linux, plus startup entries, Start Menu shortcuts and taskbar pins. [installer.md](installer.md#uninstall-cleanup) lists every location and what is left after uninstalling. Sync and sharing write only to the destinations the user sets up. The app collects no telemetry or crash reports.

## Build secret

The Google Drive desktop OAuth client secret is a build input: `local.secrets.props` (git-ignored) supplies the `GoogleClientSecret` property locally, and the Store workflow passes the `GOOGLE_CLIENT_SECRET` repository secret. A desktop client secret compiled into a shipped app is not confidential, because Google's installed-app model assumes it can be extracted; keeping it out of the repo only keeps it out of the public source. Without it, builds compile and Google Drive sign-in may fail. See [sync.md](sync.md).

## Releasing

`<Version>` in `Directory.Build.props` is the only version source. A `v*` tag runs `build-msix.yml` (the GitHub Release) and `store-publish.yml` (builds the unsigned x64 and ARM64 packages and submits them to Partner Center). The [packaging guide](installer.md) and [versioning guide](versioning.md) describe both, and the `release` skill is the procedure.

Linux delivery uses only the x64 AppImage, targeting Debian 12 / Ubuntu 24.04 LTS or newer, built by the reusable `linux-appimage.yml` workflow. See [Linux delivery](../linux/README.md#linux-appimage-delivery).
