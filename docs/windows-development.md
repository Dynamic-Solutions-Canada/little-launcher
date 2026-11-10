# Windows desktop workflow

From the WSL repository root, use the same commands across all four apps:

| Command | Result |
| --- | --- |
| `npm run build:desktop` | Signed ARM64 Release dev package |
| `npm run verify:desktop` | Full Windows Debug compile |
| `npm run install:desktop` | Build, update in place, launch |
| `npm run install:desktop -- --skip-build` | Reinstall the latest successful dev package |
| `npm run install:desktop -- --skip-build --dry-run` | Check the install without changing Windows |
| `npm run package:desktop` | Unsigned Store package |

Pass `-- --platform x64` for x64 or `-- --no-launch` when installing without
launching. The working tree is staged in a unique C: Windows TEMP directory,
cleaned after use. Logs and packages return to `.artifacts/windows-build/` in WSL.
Tracked release versions stay unchanged; dev revisions change only in staging.
Never uninstall to bypass an update error or silently replace a publisher.

The policy and implementation are shared, not copied per project. Read the
[shared build/install guide](../../windows-build-tools/README.md), or open
`$WINDOWS_BUILD_TOOLS/README.md` when using a custom tools location. The launcher is
`scripts/windows-build` and the per-project manifest is `windows-build.json`.

When this PC has no PowerShell 7 or .NET SDK, start the shared bridge's local Windows build VM; the same npm commands then build inside it automatically. It supports only `verify:desktop` and unsigned `package:desktop`, not `build:desktop` or `install:desktop`. Stop it afterwards, and never run it alongside the Android emulator VM. A worktree created without its submodules needs `git submodule update --init` before a Windows build. Setup and commands are in the [shared guide](../../windows-build-tools/README.md#local-windows-dev-vm-when-this-pc-has-no-windows-build-tools).

Use the repository [desktop skill](../.agents/skills/desktop/SKILL.md) for build, verification and installation requests. Build-only requests do not authorize installation. Use `--skip-build` only when explicitly asked to reuse the last package. Report the resulting version, output location, build result and available runtime checks; distinguish checks needing human interaction. Follow [the release workflow](versioning.md) for publishing, leave changes uncommitted unless requested, and never change tracked release versions or package identity for a local build.

## .NET build security floor

`global.json` requires stable SDK 10.0.403 or a later patch in the 10.0.4xx feature band. Both release workflows install that SDK from the same file. The shared Windows bridge copies these root build files into disposable staging, so local builds and the nested NativeAOT publish use the same policy. Install the required SDK on the build machine through its normal maintenance process if it is missing; the build does not install one.

`Directory.Build.props` sets `TargetLatestRuntimePatch=true`. With the selected SDK, this selects runtime 10.0.14 or later for self-contained output and raises the shared runtime requirement in framework-dependent output. `Directory.Build.targets` rejects explicit .NET 10 runtime versions below 10.0.14 and rejects disabling latest-patch selection without an explicit patched runtime. SDK feature-band upgrades should update `global.json` and recheck runtime and NativeAOT compiler-pack versions together.

This floor follows [Microsoft's CVE-2026-111497 advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9). The [v1.41.1 release build](https://github.com/Dynamic-Solutions-Canada/little-launcher/actions/runs/39680937971) used SDK 10.0.402/runtime 10.0.13 for both architectures. Its published x64 NativeAOT helper also contains the 10.0.13 runtime marker. No direct Brotli decompression or automatic HTTP decompression was found in the application source; decoder reachability through shipped binaries and transitive dependencies remains unconfirmed. A patched build floor is not evidence of an exploitable application path or a remediated installed copy.

Rebuild and inspect both x64 and ARM64 portable and Store outputs before distribution. Check shared runtime requirements, embedded runtime packs, and the NativeAOT helper independently. Updating a machine's shared runtime cannot replace code embedded in an existing helper or self-contained package. WebView2's Evergreen runtime is serviced separately from .NET.

## Default login startup on both platforms

Fresh Windows settings default `UserSettings.Startup` to true and always serialize its Boolean value, including false. Existing JSON files whose older false value was omitted retain false during restore. `SettingsManager.IsFirstRun` is transient and true only when both JSON and legacy XML settings are absent; unreadable existing files do not qualify. `MainWindow.ApplyFirstRunStartupAsync` registers the unpackaged per-user Run command once with `--silent`. The MSIX StartupTask manifest defaults enabled; OS DisabledByUser/policy states are never overridden. Subsequent launches do not recreate a user's removed startup entry. The System page continues displaying actual OS state. Linux already defaults `StartAtLogin` to true and retains an explicit false, using the stable AppImage path; temporary previews still disable startup integration. No auto-sync preference is enabled by this change.

Production model JSON checks prove default true and false reload persistence. Full x64 Release1.41.1.2 compiles and signs. The isolated Linux native test also confirms the default-on setting. Windows fresh portable OS registration remains native acceptance beyond the model/build checks.

The sync/startup follow-up installs x64 Release1.41.1.2 in the same Developer family. Native UI Automation verifies five initially collapsed destinations and the manifest’s enabled startup default while the existing OS-off state survives the update. Tracked versions stay unchanged. Evidence is in `.artifacts/linux-native/sync-startup-followup/`.
