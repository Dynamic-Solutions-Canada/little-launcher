> **Scope:** Use when building, verifying, packaging or installing the Windows app from WSL, or changing the .NET build floor.
> **Governs:** `windows-build.json`, `package.json`, `global.json`, `Directory.Build.props`, `Directory.Build.targets`.

# Windows desktop workflow

Windows builds run from WSL through the shared Windows build tools, which stage the working tree in a disposable Windows TEMP copy, build there and return logs and packages to `.artifacts/windows-build/`. Their commands, options, signing, local build VM and troubleshooting are documented once, in the [Windows build tools README](https://github.com/Dynamic-Solutions-Canada/windows-build-tools) (the link resolves once that repository is published). winui-app-kit's `scripts/windows-build` (in the `external/winui-app-kit` submodule) calls them, and `windows-build.json` configures them for this repo. This guide keeps only what is specific to Little Launcher.

## This repo's commands

| Command | What it does |
| --- | --- |
| `npm run verify:desktop` | Debug compile of the app and the companion helper |
| `npm run package:desktop:dev` | Signed dev package, not installed |
| `npm run install:desktop` | Signed dev package, updated in place over the installed copy and launched |
| `npm run package:desktop` | Unsigned Store package |

The build host's architecture is the default platform. `verify:desktop` compiles only the project named in `windows-build.json`, so the test harnesses under `tests/` are not compiled by it.

## What is specific to Little Launcher

- **Dev identity:** `windows-build.json` uses the Store identity as the dev identity, so a signed dev build updates the installed copy, keeping its settings and sign-ins. A Store install is a bundle, and a single dev `.msix` cannot update a bundle; installing over a Store copy needs a bundle built with the same version. Never uninstall to get past an update error (`0x80073CFB`): uninstalling deletes package settings, browser profiles and caches.
- **Companion helper:** Release packages publish `LauncherShortcut` with Native AOT, which needs the Visual Studio C++ build tools (with ARM64 tools) on the Windows build machine. Debug builds use the framework-dependent helper.
- **Packaging script:** `LittleLauncherMSIX/build-msix.ps1` (tiles, the notification activator and the helper are specific to this app); see [installer.md](installer.md#building-the-msix-build-msixps1).
- **Submodules:** a worktree needs `git submodule update --init` before a Windows build (`external/promo`).

## .NET build security floor

`global.json` requires stable SDK 10.0.403 or later within .NET 10 (`rollForward: latestFeature`, so a later 10.0 feature band also qualifies; PLAT-2). Both release workflows install that SDK from the same file. The shared Windows bridge copies these root build files into disposable staging, so local builds and the nested NativeAOT publish use the same policy. Install the required SDK on the build machine through its normal maintenance process if it is missing; the build does not install one.

`Directory.Build.props` sets `TargetLatestRuntimePatch=true`. With the selected SDK, this selects runtime 10.0.14 or later for self-contained output and raises the shared runtime requirement in framework-dependent output. `Directory.Build.targets` rejects explicit .NET 10 runtime versions below 10.0.14 and rejects disabling latest-patch selection without an explicit patched runtime. SDK feature-band upgrades should update `global.json` and recheck runtime and NativeAOT compiler-pack versions together.

This floor follows [Microsoft's CVE-2026-111497 advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9). The [v1.41.1 release build](https://github.com/Dynamic-Solutions-Canada/little-launcher/actions/runs/39680937971) used SDK 10.0.402/runtime 10.0.13 for both architectures. Its published x64 NativeAOT helper also contains the 10.0.13 runtime marker. No direct Brotli decompression or automatic HTTP decompression was found in the application source; decoder reachability through shipped binaries and transitive dependencies remains unconfirmed. A patched build floor is not evidence of an exploitable application path or a remediated installed copy.

Rebuild and inspect both x64 and ARM64 portable and Store outputs before distribution. Check shared runtime requirements, embedded runtime packs, and the NativeAOT helper independently. Updating a machine's shared runtime cannot replace code embedded in an existing helper or self-contained package. WebView2's Evergreen runtime is serviced separately from .NET.

Default login startup on both platforms is described in [installer.md](installer.md).
