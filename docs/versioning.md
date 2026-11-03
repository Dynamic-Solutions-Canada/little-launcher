> **Scope:** Use when bumping version numbers, creating releases, or modifying version-related files. Documents every file that contains a version string and the release workflow.
> **Governs:** `**/Directory.Build.props`, `**/MainWindow.xaml.cs`, `**/Package.appxmanifest`, `**/AppxManifest.xml`, `**/build-msix.ps1`, `**/.github/workflows/build-msix.yml`.

# Versioning & Releases

Little Launcher uses **semantic versioning** (`vMAJOR.MINOR.PATCH`).

## Single source of truth

The version is defined **once** in `Directory.Build.props`:

```xml
<Version>1.2.0</Version>
```

All other consumers derive from this automatically:

| Consumer | How it gets the version |
|---|---|
| **App (in-code display)** | `MainWindow.xaml.cs` reads `Assembly.GetName().Version` at startup — set by MSBuild from `<Version>` |
| **MSIX manifest** | `LittleLauncherMSIX/build-msix.ps1` replaces `VERSION_PLACEHOLDER` in `Package.appxmanifest` with the version from `Directory.Build.props` at build time |
| **Portable zip** | Built from the same `dotnet build` output, so it carries the assembly version |
| **Linux AppImage** | `linux/packaging/build.py` reads `<Version>` and stages VERSION; asset name is `LittleLauncher-{version}-linux-x64.AppImage` |
| **Git tag** | Created manually to match: `git tag -a v1.1.0 ...` |

**`Directory.Build.props` is the only file to edit.** There are no fallback version strings left to keep in sync — the WiX `Package.wxs` define was the last one, and it went with the MSI.

## Release workflow

Pushing a tag matching `v*` triggers `.github/workflows/build-msix.yml` which:

1. Reads the version from `Directory.Build.props`
2. Stamps the MSIX manifest with the four-part version
3. Builds for **x64** and **ARM64** (`dotnet build -c Release`) and Authenticode-signs the exes
4. Creates a **GitHub Release** with user-facing notes from `docs/releases/vX.Y.Z.md` when present, or a commit-summary fallback, followed by the required disclosure
5. Attaches Windows `LittleLauncher-{x64,ARM64}-portable.zip` and the Linux x64 AppImage/checksum from reusable `linux-appimage.yml`. Linux CI builds in Debian12, then extracts and checks bundled imports. No DEB/RPM assets are offered.

The separate `store-publish.yml` builds both unsigned Store packages and submits them on the
same tag using CLI v0.4.3 and the verified US $0.99 `Tier1012`. Manual runs default to draft
review. See [installer.md](installer.md) for the regional price migration.

Release-note filtering must succeed even when only version-bump commits remain, so the maintenance
fallback can run. The workflow uses `awk` rather than `grep`, whose no-match exit status aborts Bash
under `-e`. If publication fails after successful builds, the matching CI build artifacts can be
packaged as portable ZIPs and published against the existing tag without rewriting it.

Release v1.42.0 publishes Windows portable and Linux AppImage downloads without a Store submission. Its tag is explicitly excluded from `store-publish.yml`; manual Store publication remains available when requested. Other version tags retain their existing Store behavior.

For a clean Electron 45 install, download the engine explicitly with `node linux/browser/node_modules/electron/install.js`; `npm ci` alone installs the lazy-download package. The Linux workflow does this before packaging. To rebuild an existing immutable source tag with repaired CI, manually dispatch `linux-appimage.yml` from the repaired workflow and set `source_ref` to the tag. Verify that source revision and combine its AppImage/checksum with Windows portable outputs built from the same tag before publishing; do not move the release tag.

## How to release

1. Edit `Directory.Build.props` — change `<Version>X.Y.Z</Version>`
2. Commit: `git commit -am "Bump version to vX.Y.Z"`
3. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <brief summary>"`
4. Push both: `git push origin main vX.Y.Z`
5. Verify both workflows: the GitHub release and the Store submission (or resolve a pricing block in Partner Center)

## Version bump guidance

Before preparing a Store update, account for versions already handed off for upload as well as
published Git tags. Store submissions can precede GitHub releases. Changed package contents need
a newer release version; reusing the same version and filename makes replacement builds ambiguous
and can conflict with packages already submitted to Partner Center.

- **Patch** (`v1.0.1`): Bug fixes, minor tweaks, no new features
- **Minor** (`v1.1.0`): New features, non-breaking changes
- **Major** (`v2.0.0`): Breaking changes to settings format, major redesigns

## Local WSL build and install

Use `npm run build:desktop`, `npm run verify:desktop`, `npm run install:desktop`,
and `npm run package:desktop` from WSL. See [the common desktop workflow](windows-development.md).
Direct PowerShell packaging commands above describe release internals, not the
local development entry point. Never uninstall to bypass a same-version error.
