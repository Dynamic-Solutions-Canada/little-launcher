# LittleLauncher/Controls

Reusable XAML views, panels and attached behaviors live here.

Read [xaml.md](../../.codex/docs/xaml.md) before changing these controls or their template wiring.
Read [project-architecture.md](../../.codex/docs/project-architecture.md) for component and namespace ownership.

`ConditionalNameToolTip` owns conditional full-name tooltips for launcher item templates and trimmed group/column captions. Template roots target the nearest list/grid item; attaching Text directly to a TextBlock uses that label as both trim source and tooltip target, keeping group tooltips away from child items. Keep state local, detach label subscriptions on unload, and read actual trimming without measuring or changing layout.

`LauncherItemsView.xaml` and its focused partials own the shared launcher renderer, editing, toolbar and custom drag/drop. Read [drag-drop.md](../../.codex/docs/drag-drop.md) for geometry, source-of-truth and async session rules, and [icons.md](../../.codex/docs/icons.md) for item/icon invalidation. `ILauncherItemsHost` separates window placement, modal HWND ownership and completion policy. Keep the same implementation in both native popup and regular editor hosts; no live visual-tree transfers, duplicate templates, reflow or new persisted geometry.

`WrappingPanel` follows the sibling custom-Panel measure/arrange convention for natural-width control rows. It keeps child/tab order, wraps complete buttons when space runs out, and is used only for the regular editor toolbar. Do not reuse the fixed-cell launcher item panel or change item geometry to lay out window actions.


`LauncherItemsView.Hover.cs` owns passive leading/trailing or corner grip-and-pencil cues for both editor hosts, with opaque dynamic themed backing. Group cues stay on the header; list/header text uses temporary native ellipsis within unchanged dimensions, restoring the exact local Margin on exit/recycle/unload/rebuild/close. Keep cues clipped, non-hit-testable and dimension-neutral; the whole item stays clickable/draggable. Preserve conditional name tooltips, read-only/synthetic/column-break exclusions, child-over-group priority and unhandled exits.
