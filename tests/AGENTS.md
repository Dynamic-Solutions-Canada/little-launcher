# Launcher editing regressions

Read [drag-drop.md](../docs/drag-drop.md) before changing drag policy or these suites.

`LittleLauncher.ItemReorder.Tests` links the production observable model, window-free move/flatten policy and numeric group-boundary hit policy. Use synthetic items and reference assertions; do not replace the real model with test stand-ins or require user settings. Test exact zone thresholds and distinct child-versus-sibling persistence, while leaving actual nested routing and tint/caret review to the native suite.

`LittleLauncher.PageIconChoice.Tests` links the production automatic-icon choice and exact preview writer. Read [icons.md](../docs/icons.md) and its [README](LittleLauncher.PageIconChoice.Tests/README.md) before changes. Keep synthetic byte fixtures and cancellation/source-identity assertions; its UI adapters do not establish native decoding or icon-surface acceptance.

`LittleLauncher.Sync.Tests` links production launcher models, global payload and portable custom-icon IO. Read [sync.md](../docs/sync.md) and [icons.md](../docs/icons.md); its [README](LittleLauncher.Sync.Tests/README.md) documents adapted native boundaries and qualification limits. Use only disposable files and fake dispatch/service adapters; never real credentials or launcher settings.

The guarded native suite that drives the installed app lives outside this folder, in [tests-native/](../tests-native/AGENTS.md), so `npm test` never runs it.
