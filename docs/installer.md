> **Scope:** Use when changing how Little Launcher is packaged, installed or updated. Covers Windows portable ZIP and Microsoft Store MSIX delivery, Linux AppImage delivery, desktop shortcut integration, and the update flow for each.
> **Governs:** `**/UpdateService.cs`, `**/build-msix.ps1`, `**/Package.appxmanifest`.

# Packaging and updates

The Windows app ships through **two channels, and neither of them is an installer of ours**:

| Channel | Artifact | Put in place by | Updates |
|---|---|---|---|
| **Portable** | `LittleLauncher-{x64,ARM64}-portable.zip`, attached to the GitHub release | the user, unzipping it wherever they like | in-app check against GitHub Releases; the app opens the release page and the user replaces the folder |
| **Microsoft Store** | MSIX, submitted by CI to Partner Center | the Store | About opens the Store updates page; the Store installs updates |

**There is no MSI.** A per-user WiX installer shipped up to v1.35.1 and was retired: it duplicated
what the Store already does properly — managed install, silent update, clean uninstall — while
carrying its own upgrade code, custom actions, uninstall script and signing step. Releases from
before the cut keep their `.msi` assets, so old download links still resolve, and an installed MSI
copy keeps working: its updater finds no `.msi` asset on a newer release and falls back to opening
the release page, which is what the portable build now does too.

The native Linux app uses only x64 AppImage, targeting Debian 12 / Ubuntu 24.04 LTS or newer. AppImage builds include update checks and explicit download, install and restart. See [Native Linux AppImage delivery](#native-linux-appimage-delivery) for its runtime, desktop integration and update safeguards.

## Install layout

| What | Portable | MSIX |
|---|---|---|
| App files | wherever the user unzipped it | `%ProgramFiles%\WindowsApps\{PFN}\`, managed by Windows |
| Settings/data | `%AppData%\LittleLauncher\` | the same path, VFS-redirected into `%LocalAppData%\Packages\{PFN}\` |
| Start Menu shortcut | `%AppData%\...\Start Menu\Programs\Little Launcher.lnk`, written by the app | the package's own manifest entry |

Anything an *external* process opens by path — shell `.lnk` files, the companion exe — has to go
through `MainWindow.GetPhysicalAppDataDir()` rather than raw `%AppData%`, which MSIX redirects.

## Start Menu shortcut lifecycle

1. **On first launch** `EnsureStartMenuShortcuts()` in `MainWindow.xaml.cs` writes
   `Programs\Little Launcher.lnk` pointing at the running exe. Nothing creates it beforehand: a
   portable build has no installer to do it, and a packaged one already has its own entry, so
   `EnsureStartMenuShortcuts()` stops early when `IsPackaged`.
2. **On icon change** `UpdateShortcutIcons()` re-stamps the shortcut with the new icon.
3. **On uninstall** — MSIX takes its entry with the package; a portable copy leaves the `.lnk`
   behind until `cleanup-uninstall.ps1` is run (see below).

**Critical:** the shortcut sits directly in `ProgramMenuFolder`, not a subfolder, so the path the
app writes and the path it later updates are the same one. `RemoveLegacyMsiSubfolderShortcut()`
still deletes the stale `Little Launcher\` subfolder shortcut the MSI used to create — keep it,
that is the only thing tidying up after a machine that once had the MSI.

## Portable update flow

`CheckForGitHubUpdateAsync()` reads the latest release tag and compares it against the running
assembly version. **That is all it does — nothing is downloaded and nothing is installed.** Home
and About show the new version behind a **View Release** button that opens the release page, and
unpackaged builds additionally raise the startup toast (packaged ones do not — Store updates
arrive on their own, so there is nothing to interrupt anyone about).

Deliberate: a portable copy is a directory the user chose, may have put somewhere unwritable, and
is executing out of at that moment. Self-replacing it means surviving locked WebView2 and
companion-exe handles and stripping Mark-of-the-Web from every extracted file, to save a drag and
drop. If it is ever reconsidered, that is the work involved.

## MSIX / Store update flow

Packaged installs leave update discovery and installation in the Microsoft Store. The About
page's **Check for Updates** button launches
`ms-windows-store://downloadsandupdates`, the Store's Updates and downloads page.
The Store checks the signed-in account's apps, so the page may include updates for other apps.
The URI opens the page; the app does not claim that a fresh scan or installation completed.

Little Launcher does not query StoreContext on startup or show a Store update banner on Home.
Its portable build still checks GitHub Releases and links to the release page.

The Store handles any update and restart prompt after the page opens.

## Uninstall cleanup

**Portable:** deleting the folder removes the app but not what it wrote outside it, and there is
no uninstaller left to tidy that up. `cleanup-uninstall.ps1` ships in the build output for the
user to run by hand, with the app closed — it used to be an MSI custom action and is now just a
script:

| What | Where |
|---|---|
| App data folder | `%AppData%\LittleLauncher\` (settings, companion exe, icons, web profiles) |
| Start Menu shortcuts | `Programs\Little Launcher.lnk`, the legacy `Little Launcher Flyout*.lnk`, and the `Programs\Little Launcher\` folder of per-web-launcher shortcuts |
| Startup registry entry | `HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Little Launcher` |
| Pinned taskbar shortcuts | Any `.lnk` in `User Pinned\TaskBar\` targeting `LittleLauncherFlyout.exe` |

Run it *before* deleting the folder — it lives in that folder, and it does not remove the folder
itself.

**MSIX limitation:** MSIX has no custom uninstall actions. When an MSIX package is removed, Windows deletes the package files, its own Start Menu entry, **and all VFS-redirected data** (settings, cached icons, companion exe) because the entire `%LocalAppData%\Packages\{PFN}\` tree is removed. Pinned taskbar shortcuts survive as dead `.lnk` files — Windows 11 eventually detects and offers to remove stale pins. Settings **do** survive MSIX upgrades — Windows preserves package data during version updates, including updates initiated in the Store.

## Building the MSIX (`build-msix.ps1`)

`LittleLauncherMSIX/build-msix.ps1 -Platform {x64|ARM64}` publishes the app (self-contained) + the AOT companion, assembles the layout, runs `makepri`/`makeappx`, and signs. Toolchain requirements (see also the local-build memory):

- **Windows SDK** packaging tools (`makeappx`/`makepri`/`signtool`). The script auto-detects the newest installed `C:\Program Files (x86)\Windows Kits\10\bin\10.*` that has them — don't hardcode a version.
- **VS C++ build tools** (incl. `VC.Tools.ARM64`) for the Native AOT companion. The script prepends the **VS Installer dir to `PATH`** when `vswhere.exe` isn't resolvable, because the ILCompiler targets shell out to `vswhere`; without it the native link fails with **exit code 123**.
- **`-NoSign`** is Store mode: skips signing and leaves the Store `Identity`/`Publisher` intact for the Store to re-sign on ingestion. Without it, the manifest publisher is rewritten to the dev cert subject so `signtool` can sign locally.
- An explicit TrustedPfxPath uses only that signer; absence of the legacy default PFX must not generate an unrelated self-signed certificate. Signed output exports that selected signer as public-only signing.cer and directs trust to Local Computer / Trusted People. The suggested Add-AppxPackage command never forces a downgrade; unsigned Store output is explicitly identified as unsuitable for sideloading.

### Sideloading over the installed package from WSL

Run `npm run install:desktop`. The shared bridge signs with the preserved
Store-identity development certificate, matching the existing package family.
It assigns a higher dev revision only in disposable C: staging, updates in place,
checks the installed version, and launches the app. Never uninstall to bypass
`0x80073CFB`: package settings, profiles and caches are removed on uninstall.

`npm run build:desktop` creates the signed package without installation;
`npm run install:desktop -- --skip-build` reuses the last successful dev package.
`npm run package:desktop` creates the unsigned Store package. Use
`-- --platform x64` for x64. Output packages include their version in the filename
and return to `.artifacts/windows-build/` in WSL.

All four apps select the same local signer in `windows-build.json`. The single
private key lives beside `~/development/windows-build-tools`, outside the app
repositories. Do not recreate it or switch publisher to fix an install failure.
Read [the shared desktop workflow](windows-development.md) for the signer,
certificate trust, cleanup and container transport policy.

`SignatureKind = Developer` identifies a sideloaded build; `Store` identifies
the Store-signed copy. The shared helper stops on a conflicting development
publisher and never removes packages or forces a downgrade.

## Toast notifications in MSIX

Packaged builds register for notifications like unpackaged ones. This needs two manifest
extensions on the `<Application>`, which are easy to miss because their absence fails at
*runtime*, not at packaging time:

- `<com:Extension Category="windows.comServer">` with an `ExeServer` whose `Arguments` are
  `----AppNotificationActivated:` and a `<com:Class Id="…">`
- `<uap:Extension Category="windows.toastNotificationActivation">` with the same
  `ToastActivatorCLSID`

Plus `xmlns:com` and `com` in `IgnorableNamespaces`.

Without them a clicked toast has no activator and `AppNotificationManager.Register()` can throw,
which is why registration used to be skipped for packaged builds entirely — a workaround that
read like a platform limitation and was not one.

**The CLSID must stay stable.** Changing it orphans the activator for any toast already sitting
in the Action Center.

## CI Store publishing

`build-msix.yml` publishes portable ZIPs and the GitHub Release on `v*` tags.
`store-publish.yml` builds unsigned x64 and ARM64 MSIX packages on one runner,
combines them with MakeAppx into one architecture-aware `.msixbundle` stamped with
the app version, wraps it in
`LittleLauncher-{version}.msixupload`, and submits it to Partner Center for
product `9P3ZZBDQ6PJF`. No Store package is uploaded as a public Actions artifact.
MakeAppx defaults to a date-based bundle version unless `/bv` is supplied; that
could outrank the next app version and block normal Store updates.
Do not replace the bundle with a ZIP of two loose MSIX files: Partner Center
ingested that shape as an x64-only update for the other Store apps.

The workflow pins [Microsoft Store CLI v0.4.3](https://github.com/microsoft/msstore-cli/releases/tag/v0.4.3).
The published submission reports `PriceId: "Base"`, which the CLI cannot round-trip.
Tag runs now supply `Tier1012`, the verified US $0.99 tier, and may change converted
prices in other markets. The user accepted the initial 36-market comparison below.
Manual runs default to a draft; a different `price_id` can only be tested in a draft.
The workflow checks for a pending submission before invoking the CLI, because the
CLI would otherwise delete an existing draft. See [Microsoft PR #175](https://github.com/microsoft/msstore-cli/pull/175).

The first live test, [v1.40.2 on September 22, 2026](https://github.com/RyanEwen/LittleLauncher/actions/runs/35759458540),
built both packages and authenticated successfully. The CLI created a draft, retrieved it,
and stopped because the API returned `Base`. No package update was committed and pricing
was left unchanged. Tier1012 was subsequently verified and accepted, with the
regional price changes recorded below. The CLI attempts to delete its temporary draft
on this pricing failure. Do not assume that failed run left a draft available.

The initial Tier2 test hit the listing's 20-feature limit; a corrected draft then
passed upload validation but failed API commit ingestion with `Price Tier is not
supported`. That failed draft was deleted and replaced by a Tier1012 submission.
The workflow checks for pending submissions before invoking the CLI, so a retry
cannot silently overwrite an existing draft or in-flight submission.

Manual dispatch defaults `no_commit` to true, uploading a draft without submitting it.
Use that first to verify credentials, both architectures and pricing in Partner Center.
Tag pushes and manual runs with `no_commit` disabled commit the submission; certification
and the submission's publishing settings determine when it becomes available.

All four required repository secrets were present when checked on September 22, 2026:
`AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`, `AZURE_AD_APPLICATION_SECRET`,
and `SELLER_ID`. Presence does not verify expiry or the app registration's Manager role.

For manual fallback, build both architectures with `build-msix.ps1 -Platform x64 -NoSign`
and `-Platform ARM64 -NoSign`, then upload the individual `.msix` files in Partner Center.
The workflow-generated `.msixupload` is also valid because it contains a real MSIX bundle.

## Runbook: creating the Store publishing credentials

The secrets come from a Microsoft Entra **app registration** that Partner Center has been told to
trust. CI uses these credentials; renew the client secret before its configured expiry.

**Step 0 — do you have a tenant?** Partner Center → gear icon → **Account settings** →
**Tenants**. Store dev accounts opened with a personal Microsoft account often have **none**,
and nothing else works without one.

- No tenant → **Create a new Microsoft Entra ID tenant** right there (free, and the button is on
  that same page). This becomes the tenant that owns the app registration.
- Tenant already listed → note its domain and continue.

**Step 1 — register the app.** [entra.microsoft.com](https://entra.microsoft.com) →
**Entra ID** → **App registrations** → **New registration**.

- Name: anything (e.g. `LittleLauncher Store Publisher`).
- Supported account types: **Single tenant**.
- **Redirect URI: leave blank.** This is a daemon/service credential — there is no interactive
  sign-in, so a redirect URI is not used.
- **Do not add any API permissions.** This is the step people over-do: publishing rights do *not*
  come from Graph scopes, they come from the Partner Center role in step 3. An app with Graph
  permissions and no Partner Center role still gets denied.

From the app's **Overview**, copy **Application (client) ID** → `AZURE_AD_APPLICATION_CLIENT_ID`,
and **Directory (tenant) ID** → `AZURE_AD_TENANT_ID`.

**Step 2 — client secret.** In that app → **Certificates & secrets** → **New client secret**.
Copy the **Value** column (not "Secret ID") **immediately** — it is never shown again. Set a
calendar reminder for the expiry date; an expired secret fails the publish step with an auth
error and nothing else explains why. → `AZURE_AD_APPLICATION_SECRET`.

**Step 3 — authorize it in Partner Center.** This is the step that actually grants publishing
rights, and the one most often missed. Partner Center → **Account settings** →
**User management** → **Microsoft Entra applications** → add the app registration from step 1 and
assign it the **Manager** role. Without this, authentication succeeds and submission is refused.

Note there are three distinct "roles" here and they are easy to conflate — the one that matters
is the middle row:

| Role | Where | Governs |
|---|---|---|
| Your user's Partner Center role | Account settings → User management → **Users** | your own dashboard access |
| **The app registration's role** | Account settings → User management → **Microsoft Entra applications** | **API publishing rights — must be Manager** |
| Your directory role | entra.microsoft.com | whether you can create app registrations at all |

**Step 4 — seller ID.** Partner Center → **Account settings** → **Identifiers** (or Developer
settings). Copy **Seller ID** / **Publisher ID** → `SELLER_ID`.

**Step 5 — load the secrets** (values go over stdin, never into shell history):

```powershell
.\LittleLauncherMSIX\set-store-secrets.ps1
```

**Step 6: validate a draft.** Manually dispatch `store-publish.yml` with `no_commit` enabled,
then inspect the draft's packages and pricing in Partner Center before committing it.
The workflow supplies Tier1012 by default; review the resulting regional prices.

### Repairing the existing pricing test draft

The manual Store workflow accepts an existing_draft ID with no_commit enabled and an explicit
price_id. The selected draft must match the current pending API submission and be PendingCommit.
The helper in .github/scripts/update-store-test-draft.ps1 combines the approved Web Launcher
feature bullets to meet the API's 20-feature limit, sets the requested tier, replaces packages,
and uploads the bundle. It never creates, deletes, or commits submissions. Review the resulting
regional prices and packages in Partner Center before any separate submission decision.


The September 22 Tier2 draft retry (Actions run 35766179842) uploaded both 1.40.2
packages and reduced the English features to 20. It failed at API commit ingestion
and has since been replaced. Draft prices shown before ingestion did not establish
effective regional pricing.


### Committing with publication held

After approval, dispatch store-review.yml with the existing submission_id. Its helper
commit-store-review.ps1 changes only targetPublishMode to Manual, reads it back before
committing, and checks ingestion status. It does not release the app. Re-running after
commit reads status without committing again. Inspect ingested prices and packages before
separately authorizing publication.


The earlier Tier2 held commit was attempted in Actions run 35767396785. Microsoft confirmed
targetPublishMode Manual and accepted the commit request, then returned CommitFailed with
InvalidOperation: Price Tier is not supported. Tier2 is therefore NOT a verified usable
price for this app; accepting the initial PUT did not establish ingestion compatibility.
No publication occurred from that attempt.


Use the inspect_only option of store-review.yml to read the submission's pricing
fields without changing it. Microsoft documents Tier1012-Tier1424 for advanced
pricing and Tier2-Tier96 for the original model, but the msstore-cli maintainers
measured the isAdvancedPricingModel flag flipping after a PUT and both ranges being
accepted on the same product. Do not select a tier based on this flag. The Partner
Center price-tier table or a Microsoft maintainer's mapping is needed to identify a
US price; regional prices still require review in Partner Center.


Read-only API inspections (Actions runs 35783439136 and 35783542100) showed that
the published submission has priceId Base with isAdvancedPricingModel true, whereas
the failed Tier2 draft reports isAdvancedPricingModel false. The differing flags
do not establish a valid tier range for this app. The current Partner Center
'view conversion table' link failed to load for both live and draft submissions.

On September 23, 2026, a Microsoft maintainer clarified in
[PR #175](https://github.com/microsoft/msstore-cli/pull/175#issuecomment-5791491206)
that Tier1012 is US $0.99. The reply lists the US price ranges and increments for
the current Tier1012-Tier1424 sequence. The
[replacement draft run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35877319754)
used Tier1012, 20 English features, and both 1.40.2 packages. The
[held commit run](https://github.com/RyanEwen/LittleLauncher/actions/runs/35878083671)
passed price-tier ingestion and entered `PreProcessing` with `targetPublishMode:
Manual`. After the user accepted the regional price changes, Partner Center's
publishing option was changed to publish as soon as certification passes.
Submission 42 is in certification. The U.S. price remains $0.99,
but **36 of 240 market prices changed** compared with published Submission 41.
See the [complete regional comparison](store-pricing-comparison-2026-09-23.md).
The user accepted those regional price changes on September 23, 2026.
Submission 42 will publish automatically if it passes certification.


## Local WSL build and install

Use `npm run build:desktop`, `npm run verify:desktop`, `npm run install:desktop`,
and `npm run package:desktop` from WSL. See [the common desktop workflow](windows-development.md).
Direct PowerShell packaging commands above describe release internals, not the
local development entry point. Never uninstall to bypass a same-version error.


## Native Linux AppImage delivery

Release discovery and exact asset validation use the canonical `Dynamic-Solutions-Canada/little-launcher` repository after its GitHub transfer. API redirects from the old owner must not leave asset validation expecting the old path. Existing desktop identities, profiles and installed data paths remain stable.

AppImage is the only offered Linux format. `linux/packaging/build.py` builds x64 on Debian 12 (glibc 2.36); the minimum target is Debian 12 / Ubuntu 24.04 LTS or newer. The image bundles private Python 3.11, PySide6-Essentials/Qt 6.11.2, dbus-next, Electron 45, Secret Service and Qt D-Bus client tools, and redistribution notices. Host graphics drivers, glibc and C++ runtime remain external. Source development continues to use distro Qt. A desktop Secret Service wallet is required for OneDrive; tokens never enter the image or settings.

```bash
npm ci --prefix linux/browser
node linux/browser/node_modules/electron/install.js
npm run package:linux -- --engine-dir linux/browser/node_modules/electron/dist --output .artifacts/linux-appimage
chmod +x LittleLauncher-<version>-linux-x64.AppImage
./LittleLauncher-<version>-linux-x64.AppImage
```

The container receives source and engine read-only and assembles without network. The official type-2 runtime is size/hash locked in `runtime-lock.json`; changes require a reviewed lock update. Zstd payload extraction and relocated imports are checked. The private AppDir is a qualification intermediate, not another delivery format. No RPM/DEB builder is offered. ARM64 remains a future build target.

First production launch registers app-owned user menu entries/icon and enables login startup by default. System can turn startup off; that choice survives updates. Entries point at the stable AppImage file, never its temporary mount. Keep the image in its intended writable location. Preview setup stays disabled and never enables real startup. KWin helper installation remains an explicit System action.

About offers Check for Updates, Download, and Install and Restart. Automatic checks default on (after startup, then every six hours); downloads and installation require explicit actions. Only newer stable releases from the fixed GitHub repository, matching the exact platform asset name, declared size and SHA-256 digest, are accepted. Downloads are bounded and staged beside a writable, owned, nonsymlink AppImage. Installation waits for normal sync/browser shutdown, releases the instance lock, revalidates the payload and destination, retains `.AppImage.previous`, atomically replaces the image, then restarts with the same data directory. A declined protected-page quit cancels restart intent. Ordinary exit discards a staged download. Immediate execution failure restores the previous image; later startup crashes are not automatically detected. Source and read-only launches can check but cannot self-update. Check preference, startup, browser sessions and positions remain device-local.

The reusable `linux-appimage.yml` builds/extracts the image and checks bundled imports; `build-msix.yml` attaches AppImage and checksum alongside Windows assets. No workflow or release was published during local development. A private GPU-enabled Fedora KDE session verifies the real type-2 extract-and-run path, native Wayland, browser seccomp, default startup, stable menu entries, actual file replacement/restart, retained cookie/settings and startup-off choice. Replacement uses an identical payload with synthetic release metadata; it is not a live GitHub upgrade. FUSE mount mode and native Ubuntu desktop policy acceptance remain separate qualification work. Bundled Qt currently uses a fallback widget style rather than loading ABI-incompatible host KDE plugins; full packaged KDE theme fidelity remains a release gate.

### KDE appearance in AppImage launches

`AppRun` now prefers a complete distro Python/Qt/KDE runtime when the KDE desktop has Python3.11+, PySide6 QtWidgets/QtNetwork/QtDBus with Qt6.8.2+ (below7), dbus-next and the native KDE platform plugin. `host_bootstrap.py --probe` checks those prerequisites without creating a window. Host Qt loads only its own plugins; the bundled Qt stack remains isolated and is used when prerequisites are absent. No host packages are installed or required for the bundled fallback. An internal `LITTLE_LAUNCHER_BUNDLED_RUNTIME=1` override qualifies that fallback. Fedora native probing selects Breeze and the user's dark palette; this fixes the observed installed AppImage regression on this machine, not native-style parity for all fallback-only desktops.

### Extension runtime packaging

The AppImage builder compiles the attributed extension compatibility TypeScript before container staging and includes generated scripts, Apache notices and pinned production Node dependencies. TypeScript and tests are not runtime dependencies. Managed extension packages and vaults remain in the existing user data directory and are never bundled into the AppImage. Notification permission decisions now persist there too. Extensions use generic local ZIP/folder import with bounded, traversal-safe extraction; no vendor-specific installer, Store downloader or automatic extension update is shipped. Existing user extension packages and catalog entries are preserved during local AppImage updates.


## Default login startup on both platforms

Fresh Windows settings default `UserSettings.Startup` to true and always serialize its Boolean value, including false. Existing JSON files whose older false value was omitted retain false during restore. `SettingsManager.IsFirstRun` is transient and true only when both JSON and legacy XML settings are absent; unreadable existing files do not qualify. `MainWindow.ApplyFirstRunStartupAsync` registers the unpackaged per-user Run command once with `--silent`. The MSIX StartupTask manifest defaults enabled; OS DisabledByUser/policy states are never overridden. Subsequent launches do not recreate a user's removed startup entry. The System page continues displaying actual OS state. Linux already defaults `StartAtLogin` to true and retains an explicit false, using the stable AppImage path; temporary previews still disable startup integration. No auto-sync preference is enabled by this change.

Production model JSON checks prove default true and false reload persistence. Full x64 Release1.41.1.2 compiles and signs. The isolated Linux native test also confirms the default-on setting. Windows fresh portable OS registration remains native acceptance beyond the model/build checks.
