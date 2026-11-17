# Project documentation

Project documentation lives in this directory. [The root README](../README.md) introduces the app and installation. [AGENTS.md](../AGENTS.md) and the source directories' nested instructions route coding work to the relevant guide; reusable agent workflows live under `.agents/skills/`. The agent reaches the skills through the tracked `.claude/skills` link.

## Start here

| Document | Purpose |
| --- | --- |
| [Features](features.md) | Everything the app can do, in more detail than the README |
| [Development](development.md) | Windows build prerequisites, project layout and release entry points |
| [Linux development and use](../linux/README.md) | Linux installation, source runs, isolated verification and AppImage builds |
| [Architecture](architecture.md) | Application behavior, lifecycle and cross-platform boundaries |
| [Component map](components.md) | Owning modules, namespaces and responsibility boundaries |
| [Glossary](glossary.md) | Shared product terminology |
| [Launcher model](launcher-model.md) | Launcher kinds, item types, groups and column breaks |

## Implementation guides

| Guide | Area |
| --- | --- |
| [Linux](linux.md) | Native desktop UI, browser isolation, integration and qualification |
| [Windows development](windows-development.md) | This repo's Windows build commands, specifics and .NET build floor |
| [User settings](user-settings.md) | Observable models, serialization and device-local preferences |
| [Drag and drop](drag-drop.md) | Shared launcher item views, editing and drop policy |
| [Web launchers](web-launchers.md) | WebView2 windows, tabs, profiles, permissions and lifecycle |
| [Icons](icons.md) | Launcher, tray, window and item icon selection and portable assets |
| [Sync](sync.md) | Portable payloads, sync providers, revision guards and conflicts |
| [XAML](xaml.md) | WinUI controls, localization and native backdrops |
| [Localization](localization.md) | Supported languages, shared translation catalogs and adding UI text |
| [Native interop](pinvoke.md) | Win32 signatures, shell integration and native API conventions |
| [Packaging and updates](installer.md) | Windows Store/portable and Linux AppImage delivery |
| [Versioning](versioning.md) | Version source, release artifacts and publication workflow |

## Release notes

Each version's notes are in [release-notes/](../release-notes/), one file per version; [1.42.0](../release-notes/1.42.0.md) introduced the native Linux app.

## Designs, decisions and verification

Dated investigations, audits and prototypes live in [notes/](notes/); they record what was true when written, not current policy.

- [Linux design](linux-design.md) records agreed requirements and subsequent decisions. Historical requirements may be superseded by the linked decisions.
- [Architecture decisions](adr/) record deliberate choices, including [native Linux controls and an isolated web engine](adr/0003-native-linux-controls-and-isolated-web-engine.md) and [whole-launcher OS ownership](adr/0004-whole-launcher-platform-ownership.md).
- [Linux UI parity review](linux-ui-parity.md) and [parity audit](notes/linux-parity-audit.md) distinguish implemented behavior, qualification and outstanding work.
- [The original KDE capability probe](notes/prototypes/linux-kde-poc/README.md) retains reproducible source and observed results. Raw test evidence remains local and ignored by Git.
- [Store pricing comparison](notes/store-pricing-comparison-2026-09-23.md) is a dated investigation, not current release policy. Use the packaging guide for the release workflow.

## Reusable contributor workflows

- [Build, verify, package or install the Windows app](../.agents/skills/desktop/SKILL.md)
- [Add a launcher feature](../.agents/skills/add-launcher-feature/SKILL.md)
- [Add a setting](../.agents/skills/add-setting/SKILL.md)
- [Add a settings page](../.agents/skills/add-settings-page/SKILL.md)
- [Audit documentation](../.agents/skills/audit-docs/SKILL.md)
- [Commit requested changes](../.agents/skills/commit/SKILL.md)
- [Publish a requested release](../.agents/skills/release/SKILL.md)

## Maintenance

Keep each fact in its owning guide and link to it from overview documents. Update the component map when module ownership changes and the architecture guide when behavior or boundaries change. Update instruction and workflow links whenever a guide moves. Store generated logs, screenshots, package files and verification reports in ignored `.artifacts/` or temporary directories, rather than adding them as project documentation.
