# LauncherShortcut (companion exe)

`LittleLauncherFlyout.exe` signals the host via `PostMessage`. Its `--pin` flow lives in
`TaskbarPinning.cs` (live old-identity check, native Windows confirmation and manual fallback),
`PinWindow.cs` and `PinWindow.Appearance.cs` (real foreground reminder card, theme/DPI/alpha drawing,
plus an unshown read-only query pump), `PinShortcut.cs`
(one physical Start snapshot per launcher with cancellation rollback), and `ShellInterop.cs`
(raw COM helpers). New pinning imports live in `NativeMethods.cs`.

Read and follow [icons.md](../.codex/docs/icons.md) and
[pinvoke.md](../.codex/docs/pinvoke.md) before changing identity, icons, shortcuts or native code.
Keep the companion Native AOT compatible and normal launches free of pin-only work.
Never set window relaunch metadata alongside a matching AUMID-stamped Start shortcut.

Native waits are bounded (45 seconds plus five seconds for advisory cancellation). Preserve a
shortcut and its local pending marker when the current pin outcome is unknown. An earlier marker
does not force `--manual` or old-pin removal. Retry may use native confirmation again; only live
Pinned old state requires a removal acknowledgement. Unknown old state proceeds without a warning,
while retaining its diagnostic Unknown result. Cancel restores the earlier shortcut and marker.

`Shared/PinSnapshot.cs` is linked into both builds. It stores only local confirmed effective details
using source-generated JSON, not settings/sync data. `--pin --check-only` reuses the live old-state
query without pin submission. Missing caller registration/metadata stays unknown, never inferred
from taskbar registry caches. `PreviousPinProcess` runs the OLD identity in a separate bounded
read-only process. The pinning process sets its NEW identity once before any UI/WinRT; never change
its process AUMID after an old query. The child must exit before snapshot replacement.
The native card shows the launcher name and "Look for the Windows pinning prompt." without a
classic caption, progress bar or action buttons. Keep its native text accessible and draw the
unchanged icon over the card background with alpha. Manual fallback retains pin instructions.

`SPI_GETHIGHCONTRAST` returns a borrowed `DefaultScheme` pointer. Never free it, despite the
conflicting Learn ownership note; follow the pinned Microsoft implementation in
[pinvoke.md](../.codex/docs/pinvoke.md). Keep card milestone diagnostics narrow and distinguish
preparation from an actual native request. A failed ready queue must not enter an unarmed wait.
