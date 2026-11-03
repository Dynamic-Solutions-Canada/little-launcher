# LittleLauncher/Models

`Launcher.cs` (and `LauncherItem`) model conventions — observable properties, the `TrayIconMode` string scheme, sharing properties, and the legacy-settings migration path.

Read and follow [user-settings.md](../../docs/user-settings.md) before changing the code described above.

`WebLauncherPresets.cs` defines creation-only web launcher templates. Read
[web-launchers.md](../../docs/web-launchers.md) before changing their defaults or flow.


`Launcher.Platform` owns whole shortcut-launcher OS eligibility. Web launchers remain shared. Legacy `LauncherItem.SupportedOperatingSystems` metadata is retained without filtering or editor controls. Read [user-settings.md](../../docs/user-settings.md) for ownership and portable glyph-preview semantics.
