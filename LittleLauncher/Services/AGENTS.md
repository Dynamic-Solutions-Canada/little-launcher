# LittleLauncher/Services

`UpdateService.cs` checks GitHub releases for portable builds and opens the Microsoft Store updates page for packaged builds. When changing update, install, or packaging behavior, follow the packaging conventions:

Read and follow [installer.md](../../.codex/docs/installer.md) before changing the code described above.

Global launcher sync (`LauncherSyncService`, `SftpSyncService`, `FolderSyncService`, `CloudFolderService`, `LauncherPayload`, `AutoSyncService`) is transport-pluggable. Before changing any of it — including adding a trigger — follow the sync conventions:

Read and follow [sync.md](../../.codex/docs/sync.md) before changing the code described above.

Other services in this folder (`FaviconService`, `AppCatalog`, `BookmarkImport`, …) are described in the root [AGENTS.md](../../AGENTS.md) architecture section.

`FaviconService.cs` and `FaviconService.IconPreview.cs` share website icon discovery. Read
[icons.md](../../.codex/docs/icons.md) before changing them. Preview fetches return bytes only,
with cancellation, bounded fresh requests and no cache writes or Google fallback. Ordinary
item favicon callers keep their existing default behavior.

`TaskbarPinStatusService.cs` wraps only the companion's isolated `--pin --check-only` mode for
launcher settings. Follow [icons.md](../../.codex/docs/icons.md). Keep the child unshown, read-only,
bounded and cancelable; only known exits establish state, and cleanup must finish before a caller
may replace a pin snapshot. Never query the packaged Settings process identity instead.
