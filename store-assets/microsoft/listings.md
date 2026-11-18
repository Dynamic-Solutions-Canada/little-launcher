# Microsoft Store listing text

`listings.json` is the listing source in the shared `schemaVersion: 1` / `locales` shape (LIST-3), for the app's nine locale identities: `en-US`, `en-CA`, `es-ES`, `fr-FR`, `fr-CA`, `de-DE`, `zh-CN`, `pt-BR` and `ja-JP` (the same set the package manifest declares). Seven records hold wording; `en-CA` is `{ "copyFrom": "en-US" }` and `fr-CA` is `{ "copyFrom": "fr-FR" }`, matching the app's own catalogs. Fields are semantic names, not Partner Center CSV field IDs: when importing, merge into a real Partner Center export and keep its opaque `Field`/`ID` cells. No workflow publishes this file yet; Partner Center is updated from it by hand at the next submission (LIST-1, LIST-9).

The `en-US` record is the live listing as published with 1.43.0 (submission 1152921505702429888), with these changes, approved for the next submission:

- The short description, empty live, is now the README tagline plus "Available for Windows and Linux." (LIST-12).
- The eleventh feature lost its last two characters, a stray "C" left by an earlier truncation.
- Three features were out of date and are corrected as facts, not reworded: feature 3 (view modes) adds the compact small-icon grid; feature 6 (taskbar icons) describes pinning by what it does instead of naming the internal helper; feature 7 (shared launchers) lists every transport (file or folder, SFTP, OneDrive, WebDAV) and read-only or two-way sharing. The live listing was English only; the six translated records are new and go live with the next submission.

Release notes are not in this file. Each version's "What's new" text is `release-notes/<version>.md` (`en-US`) plus one `release-notes/<version>.<identity>.md` per other wording catalog, the same text the GitHub Release uses (REL-6, LIST-10).

Translated wording uses the same terms as the in-app catalogs (`Shared/Localization/`), so the listing matches what people see. Every translation, in the listing and in the release notes, was drafted by AI without native-speaker review (LOC-12); the per-catalog record is in [docs/localization.md](../../docs/localization.md).

`npm run check:listing` (part of `npm run validate`) checks this file and the current version's notes: the locales and aliases, every field, the limits below, matching caption counts, no em dashes, and that the README tagline is the first sentence of the `en-US` short description.

## Limits

| Text | Limit | Longest now |
| --- | --- | --- |
| Full description | 4,000 characters | 3,058 (fr-FR) |
| Short description | 270 characters | 146 (fr-FR) |
| Features | Up to 20, each up to 200 characters | 20 per locale, longest 197 (en-US) |
| Keywords | Up to 7, each up to 40 characters, 21 words combined | 7 per locale, at most 19 words (es-ES) |
| Screenshot captions | One per screenshot, up to 200 characters | 8 per locale, longest 63 (de-DE, fr-FR) |
| Release notes (`release-notes/`) | 1,500 characters | 582 (1.43.0, de-DE) |

## Claims to keep true

- Website shortcut icons are fetched only from the site itself; no third-party favicon service is contacted (from the release after 1.43.0).
- Cloud sync destinations (OneDrive, Google Drive) get a private app folder only; Little Launcher never sees the rest of the drive. WebDAV, shared folders and SSH/SFTP are also supported, several at once, with the newest copy winning.
- Sharing: a OneDrive link, a WebDAV server, SFTP or a shared folder. The owner chooses whether others can edit or only receive, and the choice travels with the share. From the release after 1.43.0, new one-way OneDrive links are view-only.
- A closed web launcher is suspended and then unloaded, so a hidden page uses no CPU, network or memory. Launchers set to keep running are the exception.
- Each web launcher keeps its own browser profile, so sign-ins survive restarts.
- No account, ads, analytics, telemetry or crash reporting.
- The interface is in English, Spanish, French, German, Simplified Chinese, Brazilian Portuguese and Japanese.
- Requires Windows 11 (build 22000, the manifest's minimum) and the Evergreen WebView2 Runtime, which Windows 11 includes.

## Console-only fields

| Field | Value |
| --- | --- |
| Store ID | 9P3ZZBDQ6PJF (https://apps.microsoft.com/detail/9p3zzbdq6pjf) |
| Package identity | `27766TechnicallyReal.LittleLauncher` (permanent; KIT-2) |
| Category | Productivity |
| Pricing | Paid, US $0.99 (price tier Tier1012); see [installer.md](../../docs/installer.md) for the pricing history |
| Privacy policy URL | https://www.dynamic-solutions.ca/privacy/little-launcher. The live listing record's own privacy, website and support fields are empty |
| Developer name | The live record shows "Ryan Ewen"; the copyright holder and publisher display name are Dynamic Solutions (BRAND-3) |

## Screenshots

The Store set is the eight screenshots in `store-assets/manifest.json`, captured from the installed app on the Windows test VM with synthetic launchers (LIST-8), in dark mode; [the store-assets README](../README.md) has how they are made. `screenshotCaptions` holds one caption per screenshot, in the same order. They replace the live listing's nine with the next release's submission. The README's screenshots in `store-assets/web/screenshots/` are a separate set and are never uploaded to the Store (LIST-8's synthetic-data rule applies to listings).
