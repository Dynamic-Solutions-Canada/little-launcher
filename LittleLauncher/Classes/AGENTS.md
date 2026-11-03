# LittleLauncher/Classes

P/Invoke and native interop conventions apply here — `NativeMethods.cs` is the single home for all `[LibraryImport]`/`[DllImport]` declarations.

Read and follow [pinvoke.md](../../docs/pinvoke.md) before changing the code described above.

`IconGallery.cs`, `IconCatalogView.cs`, `SelectionReveal.cs`, `AutomaticPageIconChoice.cs`, `PageIconPreview.cs` and `GlyphIconRenderer.cs` also live in this folder. When editing these, follow the icon system conventions: [docs/icons.md](../../docs/icons.md).

`Settings/SettingsManager.cs` also follows [user-settings.md](../../docs/user-settings.md): preserve old omitted-false startup settings, explicit false serialization and the transient first-run distinction. Only genuinely absent settings qualify for default OS startup registration.
