# Shared code and data

`PinSnapshot.cs` is compiled into the main app and Native AOT companion. Keep it dependency-free,
use source-generated JSON serialization, and keep its files local rather than adding them to sync.
Read [icons.md](../docs/icons.md) and [components.md](../docs/components.md)
before changing its effective name, icon-byte or launch-command comparison.

`SupportedLanguages.cs`, `UiText.cs` and the `Localization/` catalogs are the interface-language layer shared by the main app, the Native AOT companion and (catalogs only) the Linux app. Keep `UiText` dependency-free and AOT-safe. Read [localization.md](../docs/localization.md) before changing them; the language list and matching rules must stay identical to Persistent's and to `linux/little_launcher/i18n.py`.
