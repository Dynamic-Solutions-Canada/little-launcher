# LittleLauncherMSIX (MSIX / Store package)

This folder holds `Package.appxmanifest` and `Images/`. `Images/` is written by `npm run icons` from the masters in `assets/brand/` (see [icons.md](../docs/icons.md#key-files)); never edit it by hand. winui-app-kit's `build/build-msix.ps1`, configured by `windows-build.json` at the repo root, stamps the manifest (replacing `VERSION_PLACEHOLDER`) and builds the packaged app; the kit's script is never edited from this repo (KIT-8). The Store MSIX is the only Windows delivery channel. The separate Linux application uses only AppImage; see [Linux delivery](../linux/README.md#linux-appimage-delivery). MSIX has VFS redirection and no custom uninstall actions; see the packaging guide for the implications:

Read and follow [installer.md](../docs/installer.md) before changing the code described above.

The MSIX manifest version is derived from `Directory.Build.props` at build time. Release process and version sources:

Read and follow [versioning.md](../docs/versioning.md) before changing the code described above.
