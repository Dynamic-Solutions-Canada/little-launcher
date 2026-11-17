> **Scope:** Use when bumping version numbers, creating releases, or modifying version-related files. Documents every file that contains a version string and the release workflow.
> **Governs:** `**/Directory.Build.props`, `**/MainWindow.xaml.cs`, `**/Package.appxmanifest`, `**/AppxManifest.xml`, `**/build-msix.ps1`, `**/.github/workflows/build-msix.yml`, `**/.github/workflows/store-publish.yml`, `**/.github/workflows/linux-appimage.yml`, `release-notes/**`.

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
| **App (in-code display)** | `MainWindow.xaml.cs` reads `Assembly.GetName().Version` at startup: set by MSBuild from `<Version>` |
| **MSIX manifest** | `LittleLauncherMSIX/build-msix.ps1` replaces `VERSION_PLACEHOLDER` in `Package.appxmanifest` with the version from `Directory.Build.props` at build time |
| **Linux AppImage** | `linux/packaging/build.py` reads `<Version>` and stages VERSION; asset name is `LittleLauncher-{version}-linux-x64.AppImage` |
| **Git tag** | Created manually to match: `git tag -a v1.1.0 ...` |

**`Directory.Build.props` is the only file to edit.** There are no fallback version strings left to keep in sync: the WiX `Package.wxs` define was the last one, and it went with the MSI.

## Release notes

Each release has a committed notes file, `release-notes/<version>.md` (REL-6), written and approved before the version is bumped and committed together with the bump (REL-2). The body is user-facing bullets only: no heading and no disclosure line, which CI adds. It must fit the Microsoft Store's 1,500-character "What's new" limit. From the next version on, the interface is localized, so add one sibling per other wording catalog (`<version>.es-ES.md`, `.fr-FR.md`, `.de-DE.md`, `.zh-CN.md`, `.pt-BR.md`, `.ja-JP.md`); the Canadian identities reuse `en-US` and `fr-FR`. Files for versions up to 1.43.0 are kept as published, with their old headings.

## Release workflow

A `v*` tag runs two workflows. Neither falls back to commit subjects when notes are missing; both fail instead.

- **`build-msix.yml` (Build and Package):** runs `npm run validate` and checks that the notes exist (CI-4), checks that the x64 and ARM64 MSIX packages build and sign (with a throwaway certificate; nothing is published), builds the Linux AppImage through the reusable `linux-appimage.yml`, then creates the GitHub Release titled `Little Launcher X.Y.Z` from the notes, a "Get it" section and the AI disclosure. The Release attaches only `LittleLauncher-X.Y.Z-linux-x64.AppImage` and `SHA256SUMS`: as a paid Store app in a public repo, no Windows build is attached (DIST-5, D-020). The in-app Linux updater fetches the AppImage by that exact name, so it never changes.
- **`store-publish.yml` (Publish to Microsoft Store):** calls winui-app-kit's reusable Store workflow. It runs the gate, builds both unsigned packages with the Google client secret supplied at build time, uploads them as a **draft** with the notes for every listing locale, and stops (REL-7). Submitting the draft for certification is a second, deliberate step: run the workflow manually on the same tag with `submit_draft` set to the draft ID from the tag run's summary. The verified US $0.99 price tier is `Tier1012`; see [installer.md](installer.md) for pricing history.

For a clean Electron 45 install, download the engine explicitly with `node linux/browser/node_modules/electron/install.js`; `npm ci` alone installs the lazy-download package. The Linux workflow does this before packaging. To rebuild an existing tag's AppImage with repaired CI, manually dispatch `linux-appimage.yml` from the repaired workflow and set `source_ref` to the tag; do not move the release tag.

## How to release

Use the `release` skill; it holds the full procedure (REL-8, REL-12). In short: write and approve the notes, bump `<Version>` and commit it with the notes as `Bump version to vX.Y.Z`, create the annotated tag `vX.Y.Z`, push the branch and the tag together, verify the Release and the Store draft, submit the draft, then do the post-release steps.

## Version bump guidance

Before preparing a Store update, account for versions already handed off for upload as well as
published Git tags. Store submissions can precede GitHub releases. Changed package contents need
a newer release version; reusing the same version and filename makes replacement builds ambiguous
and can conflict with packages already submitted to Partner Center.

- **Patch** (`v1.0.1`): Bug fixes, minor tweaks, no new features
- **Minor** (`v1.1.0`): New features, non-breaking changes
- **Major** (`v2.0.0`): Breaking changes to settings format, major redesigns

## Local WSL build and install

Use `npm run package:desktop:dev`, `npm run verify:desktop`, `npm run install:desktop`,
and `npm run package:desktop` from WSL. See [the common desktop workflow](windows-development.md).
Direct PowerShell packaging commands above describe release internals, not the
local development entry point. Never uninstall to bypass a same-version error.
