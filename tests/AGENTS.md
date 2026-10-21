# Launcher editing regressions

Read [drag-drop.md](../.codex/docs/drag-drop.md) before changing drag policy or these suites.

`LauncherItemReorder.Tests` links the production observable model, window-free move/flatten policy and numeric group-boundary hit policy. Use synthetic items and reference assertions; do not replace the real model with test stand-ins or require user settings. Test exact zone thresholds and distinct child-versus-sibling persistence, while leaving actual nested routing and tint/caret review to the native suite.

`native-drag` owns actual installed-app pointer checks, synthetic fixtures, target geometry assertions and the consoleless Windows executor. Read its [README](native-drag/README.md) before running it. Keep the explicit expected-computer and desktop-interaction guards. UIA can set up the editor, but tested moves must go through native pointer input. Preserve original settings before replacing fixtures and retain the backup on failed cleanup. Screenshots support visual review and do not imply automated pixel acceptance.

`native-drag/interaction-cases.ps1` is dot-sourced only by that guarded suite. It owns actual group-pencil taps, changing drag destinations, pointer/hit witnesses and foreign-window/Done/dismissal checks. Its Notepad cases require no existing Notepad process and close only the isolated synthetic document's process. Preserve the original eight default cases; follow-up interactions are explicitly selected with `--cases`. A regular editor must close on Done, while a native flyout resumes click-away dismissal.

Group-boundary cases must keep their exact receiver/last-child/following-group geometry assertions and prove saved items remain unchanged before Drop. Inside/after/inside screenshots support human review of the faded last child and internal marker versus an unchanged child and outside marker. Held-pointer checks compare geometry twice before release; group-end-leave tests cue cleanup before held-Escape cancellation. These checks do not claim automated pixel or opacity acceptance.

`PageIconChoiceRegression` links the production automatic-icon choice and exact preview writer. Read [icons.md](../.codex/docs/icons.md) and its [README](PageIconChoiceRegression/README.md) before changes. Keep synthetic byte fixtures and cancellation/source-identity assertions; its UI adapters do not establish native decoding or icon-surface acceptance.
