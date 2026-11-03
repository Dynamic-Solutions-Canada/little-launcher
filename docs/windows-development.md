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

Use the repository [desktop skill](../.agents/skills/desktop/SKILL.md) for build, verification and installation requests. Build-only requests do not authorize installation. Use `--skip-build` only when explicitly asked to reuse the last package. Report the resulting version, output location, build result and available runtime checks; distinguish checks needing human interaction. Follow [the release workflow](versioning.md) for publishing, leave changes uncommitted unless requested, and never change tracked release versions or package identity for a local build.

## Existing Fedora build VM

The host now has an existing libvirt qemu:///system domain named windows-dev, with reserved address 192.168.122.77 and key-only SSH alias windows-dev (user ryan). Its administrator key file contains this host's public key, retains restrictive SYSTEM/Administrators ACLs and its original read-only attribute. SSH and its automatic service startup are configured; no password access or new public forwarding was enabled. The VM remains stopped between build jobs to release its 8 GiB RAM. Start it with virsh when needed.

The current native VM transport uses SSH/SCP for snapshots and outputs and the QEMU guest agent for the shared build-staged.ps1 process under SYSTEM. Key-authenticated SSH logons cannot load this PFX into the user key store, so running signing directly in that SSH context failed. Build source remains authoritative here and is copied into a unique C:\Windows\SystemTemp\WindowsAppBuilds directory; no build runs from a mounted Linux checkout. Include Shared/PinSnapshot.cs, both projects, package resources and the promo dependency. Staging/version allocation/signing still use the existing shared build driver and preserved signer. Private-key/OAuth transfer into this VM was explicitly authorized for this session; preserve that boundary rather than silently copying credentials to a different host.

Docker's FORWARD drop policy blocked libvirt NAT. Two narrowly scoped DOCKER-USER nft rules currently permit only 192.168.122.77 outbound TCP 80/443 through the host uplink and established replies. They open no inbound services; inspect them if Docker or the host restarts rather than changing the global forwarding policy. Visual C++ ARM64 cross-tools were added to the existing C:\BuildTools instance, allowing native-AOT companion cross-publishing.

Both full Release architectures now compile and package successfully as 1.41.1.1. A signed bundle contains both architectures and retains the Store name/publisher. Native Windows trust and independent signature/timestamp/package-digest checks pass. Public-only certificate, bundle and individual MSIXs were copied to the host Downloads folder. Every task-owned source/signing/OAuth stage was removed; neither laptop nor VM app installation was changed. Actual laptop Store upgrade, runtime sign-ins and native visual acceptance remain user testing. Evidence: .artifacts/windows-build/laptop-delivery.json. No source commit, CI upload, Store submission or release was performed.

The current local Windows VM interactive credential is stored privately at `/home/rewen/.config/windows-build-tools/windows-dev.json` (directory0700/file0600), updated by Ryan on2026-11-03. Read that record when interactive login is needed; do not use older transcript credentials or duplicate the password into project documentation. SSH continues using its existing key. Native UI checks require a signed-in Windows desktop, not a Little Launcher/OneDrive sign-in.

The later icon-chooser follow-up compiled x64 Debug and built/installed the current signed x64 Release1.41.1.1 in this VM for native comparison. Developer signature, installed executable location, search width and chooser-only Browse/Cancel were verified through native UI Automation. This supersedes the earlier no-VM-install status; the laptop and Downloads delivery files were not changed. Task-owned source/signing and public install/UI stages were removed. Keep interactive install stage names short: the current shared helper’s schtasks /tr path has a261-character limit. The VM remains available to Ryan after this comparison. Evidence: `.artifacts/linux-native/icon-chooser-parity/vm-native-verification.json`.


## Default login startup on both platforms

Fresh Windows settings default `UserSettings.Startup` to true and always serialize its Boolean value, including false. Existing JSON files whose older false value was omitted retain false during restore. `SettingsManager.IsFirstRun` is transient and true only when both JSON and legacy XML settings are absent; unreadable existing files do not qualify. `MainWindow.ApplyFirstRunStartupAsync` registers the unpackaged per-user Run command once with `--silent`. The MSIX StartupTask manifest defaults enabled; OS DisabledByUser/policy states are never overridden. Subsequent launches do not recreate a user's removed startup entry. The System page continues displaying actual OS state. Linux already defaults `StartAtLogin` to true and retains an explicit false, using the stable AppImage path; temporary previews still disable startup integration. No auto-sync preference is enabled by this change.

Production model JSON checks prove default true and false reload persistence. Full x64 Release1.41.1.2 compiles and signs. The isolated Linux native test also confirms the default-on setting. Windows fresh portable OS registration remains native acceptance beyond the model/build checks.

The sync/startup follow-up installs x64 Release1.41.1.2 in the same Developer family. Native UI Automation verifies five initially collapsed destinations and the manifest’s enabled startup default while the existing OS-off state survives the update. SYSTEM build transport must query the interactive user’s installed version and seed only the disposable source before the shared version allocator runs; tracked versions stay unchanged. Evidence is in `.artifacts/linux-native/sync-startup-followup/`.
