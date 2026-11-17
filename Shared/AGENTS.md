# Shared code and data

`PinSnapshot.cs` is compiled into the main app and Native AOT companion. Keep it dependency-free,
use source-generated JSON serialization, and keep its files local rather than adding them to sync.
Read [icons.md](../docs/icons.md) and [components.md](../docs/components.md)
before changing its effective name, icon-byte or launch-command comparison.

`AppLanguages.cs` and the `Localization/` catalogs are the interface-language data shared by the main app, the Native AOT companion, the test harnesses and (catalogs only) the Linux app; winui-app-kit's localization classes read them. Keep `AppLanguages.cs` dependency-free apart from the kit's `SupportedLanguages`, so the companion and harnesses can link it. Read [localization.md](../docs/localization.md) before changing them; the language list and matching rules must stay identical to Persistent's and to `linux/little_launcher/i18n.py`.
