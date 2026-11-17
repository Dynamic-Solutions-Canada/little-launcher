---
name: add-settings-page
description: Scaffold a new Settings page with XAML and code-behind, its NavigationViewItem and its translated text. Use when Settings needs a new top-level page.
---

# Add a settings page

Goal: a new page reachable from the Settings navigation, with its text in every language.

1. Create `LittleLauncher/Pages/{PageName}Page.xaml` following an existing page (for example `HomePage.xaml` or `SystemPage.xaml`) and [docs/xaml.md](../../../docs/xaml.md). Add `xmlns:l="using:LittleLauncher.Localization"` and use `{l:Str Key=...}` for every string; bind to `SettingsManager.Current` where needed.
2. Create `LittleLauncher/Pages/{PageName}Page.xaml.cs` in namespace `LittleLauncher.Pages`, inheriting `Page`, with a constructor that calls `InitializeComponent()`.
3. Add a `NavigationViewItem` in `LittleLauncher/SettingsWindow.xaml`: `TargetPageType` set to the new page, `Content` from `{l:Str Key=...}`, and a Segoe Fluent Icons glyph.
4. Add every new key to the shared catalogs as described in [docs/localization.md](../../../docs/localization.md), in `en-US` and all six other catalogs.
5. Update `docs/architecture.md` and `docs/components.md` for the new page.
6. Run `npm run verify:linux` for the catalog checks, then build with the `desktop` skill.

Report: the page, its navigation entry, the docs updated, and the build and check results.
