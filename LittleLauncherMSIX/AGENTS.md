# LittleLauncherMSIX (MSIX / Store package)

`build-msix.ps1` stamps `Package.appxmanifest` (replacing `VERSION_PLACEHOLDER`) and builds the packaged app. The Store MSIX and the portable ZIP are the two Windows delivery channels. The separate Linux application uses only AppImage; see [Linux delivery](../linux/README.md#linux-appimage-delivery). MSIX has VFS redirection and no custom uninstall actions — see the packaging guide for the implications:

Read and follow [installer.md](../docs/installer.md) before changing the code described above.

The MSIX manifest version is derived from `Directory.Build.props` at build time. Release process and version sources:

Read and follow [versioning.md](../docs/versioning.md) before changing the code described above.
