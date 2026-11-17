# Localization

Little Launcher's Windows and Linux apps share one set of translation catalogs and the same language rules as Persistent.

## Languages

`Shared/AppLanguages.cs` is the canonical list, as winui-app-kit's `SupportedLanguages`, in picker order: `en-US`, `en-CA`, `es-ES`, `fr-FR`, `fr-CA`, `de-DE`, `zh-CN`, `pt-BR`, `ja-JP`. Canadian English reads the `en-US` catalog and Canadian French reads `fr-FR`; their identities stay distinct so a later regional wording needs no migration. `linux/little_launcher/i18n.py` mirrors the list, and a Linux test fails if the two drift.

Match device walks the user's ordered OS display languages, trying an exact identity and then a compatible language/script family. Unsupported languages fall back to English. Traditional Chinese never selects Simplified Chinese. These rules are winui-app-kit's `SupportedLanguages.Resolve`, shared with Persistent and the other apps; the Linux copy in `i18n.py` must stay identical.

The choice is per device. Windows stores it as `UserSettings.LanguageOverride` (empty means Match device); Linux stores `Language` in the device-local `linux.json`. Neither travels through launcher sync. An unknown saved value means Match device.

Changing the language takes effect for each surface the next time it is built. On Windows, Settings rebuilds itself immediately; the tray menu is built each time it opens; jump lists, Start menu shortcut names and the companion's `language.txt` are rewritten straight away. Web launcher panels stay loaded in the background, so their toolbars keep the old language until the app restarts. On Linux, the long-lived Settings window and tray menus keep their text until restart, while web launcher menus and chrome pick it up the next time a launcher opens. Both pickers therefore show a short restart note after a change. Regional formatting of numbers and dates follows the OS, not the interface language.

## Catalogs

```
Shared/Localization/<catalog>/<area>.json
```

Area files prefixed `linux` (for example `linuxUpdates.json`) hold Linux-only concepts; everything else is shared by both platforms, and Linux reuses the Windows key wherever the meaning matches.

`<catalog>` is one of `en-US`, `es-ES`, `fr-FR`, `de-DE`, `zh-CN`, `pt-BR`, `ja-JP`. Each area file is a flat JSON object of key to text. All area files of a catalog merge into one key space, so a key must appear in exactly one area file. `en-US` is the source; every other catalog must have exactly the same keys and the same placeholders.

**Keys** are `area.camelCaseName`, optionally with one more dotted level for grouping (`sync.sftp.hostLabel`). The first segment matches the file name. Keys describe meaning, not English wording, so a later wording change does not rename the key. `common.json` holds the generic words (OK, Cancel, Remove, Browse…); reuse those rather than adding a near-duplicate.

**Placeholders** are `{{name}}` (double braces, the winui-app-kit form), replaced once from named values; a value is never itself expanded, so user content containing braces is shown literally. Use a placeholder rather than concatenating fragments, because word order differs between languages. Never split one sentence across keys.

**Plurals** use `key.one` and `key.other`, with `{{count}}` supplied automatically. French and Portuguese use "one" for 0 and 1; Chinese and Japanese give both forms the same text.

**Not translated:** product and service names (Little Launcher, OneDrive, Google Drive, WebDAV, SFTP, SSH, Nextcloud, Windows, KDE, Microsoft Store, GitHub), launcher and item names, URLs, file paths, anything the user typed, text returned by a server or the OS, log messages, exception messages that only reach the log, settings and JSON property names, automation IDs and keyboard accelerator keys. Pass user and provider content as placeholder values, never as keys.

### Review status (LOC-12)

| Wording catalog | Interface | Store listing and release notes | Native-speaker review |
| --- | --- | --- | --- |
| `en-US` | Source | Source | Not applicable |
| `es-ES` | AI-drafted | AI-drafted | None yet |
| `fr-FR` | AI-drafted | AI-drafted | None yet |
| `de-DE` | AI-drafted | AI-drafted | None yet |
| `zh-CN` | AI-drafted | AI-drafted | None yet |
| `pt-BR` | AI-drafted | AI-drafted | None yet |
| `ja-JP` | AI-drafted | AI-drafted | None yet |

Update the row when a review happens. The Store listing is `store-assets/microsoft/listings.json`; see [its companion](../store-assets/microsoft/listings.md).

### Product terms

Listing text and interface text use the same word for each of these, taken from the catalogs: launcher (`es-ES` lanzador, `fr-FR` lanceur, `de-DE` Launcher, `zh-CN` 启动器, `pt-BR` inicializador, `ja-JP` ランチャー), flyout (`es-ES` ventana emergente, `fr-FR` panneau, `de-DE` Flyout, `zh-CN` 浮出窗口, `pt-BR` painel suspenso, `ja-JP` フライアウト), and the view modes, shortcuts and Cloud Sync labels as the `launcherSettings` and `sync` areas name them. Product and service names stay in English.

## Windows

winui-app-kit's `UiText` reads the catalogs, which `LittleLauncher.csproj` embeds as `LittleLauncher.Localization.<catalog>/<area>.json`; the kit merges a catalog's area files at first read. `App` configures it at startup with the saved choice, and the System page calls `UiText.SetOverride` when the user picks another language (empty follows Windows). When packaged, the kit takes the device's languages from Windows' list for this app, which Windows matches against the languages the package declares.

- XAML: add `xmlns:l="using:LittleLauncher.Localization"` to the root element, then `Text="{l:Str Key=sync.title}"`. Works for any string or object property (`Text`, `Content`, `Header`, `PlaceholderText`, `ToolTipService.ToolTip`, `AutomationProperties.Name`).
- C#: `UiText.Get("sync.title")`, `UiText.Format("sync.uploaded", ("provider", name))`, `UiText.Plural("launchers.count", n)`. Add `using WinUIAppKit.Localization;`. Keys starting with `kit.` are the kit's own shared labels; never define them in these catalogs.
- Text built in code for a window that stays open (a status line, a menu rebuilt on open) reads `UiText` when it is built, so it is in the current language whenever it is rebuilt.

The Native AOT taskbar-pin companion (`LauncherShortcut`) links the kit's dependency-free text core (`SupportedLanguages.cs` and `TextCatalogs.cs`, used through `CompanionLanguage.Text`), embeds only `pinning` and `common`, and reads the language the main app last wrote to `language.txt` beside it; `MainWindow` rewrites that file whenever the language changes.

The old `Resources/Localization/Dictionary-en-US.xaml` and `Application.Current.Resources` string lookups are retired; do not add string resources there.

## Linux

`linux/little_launcher/i18n.py` reads the same catalogs: from `Shared/Localization` in a source checkout, or the copy `packaging/build.py` bundles into the AppImage. Call `t('sync.title')`, `t('sync.uploaded', provider=name)` and `plural('launchers.count', n)`. Electron's trusted chrome receives its strings from the main process; see [linux.md](linux.md).

## Checks

`linux/tests/test_localization.py` checks that every catalog has exactly the English keys, matching placeholders and no empty values, that each key lives in the area file its prefix names, and that the Python language list matches `AppLanguages.cs`. `tests/LittleLauncher.Sync.Tests` covers the resolver, fallback, placeholder filling and plurals through the kit's `TextLookup`. The harnesses share `tests/HarnessLocalization/`, which links the kit's text core and a `UiText` stand-in over it, because the kit's `UiText` needs Windows. These checks do not establish translation quality.
