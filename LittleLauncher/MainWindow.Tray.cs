using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using WinUIAppKit.Tray;
using LittleLauncher.Windows;
using System.Linq;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>
/// One notification-area icon per launcher, on the kit's <see cref="TrayIconHost"/> (TRAY-1, TRAY-3):
/// creation, removal, menu, visibility, tooltip and re-rendering. The host re-adds every icon when
/// Explorer restarts and falls back from a refused GUID; this file owns only the launcher policy.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>One launcher's tray icon and the image it shows.</summary>
    private sealed class TrayIconEntry(TrayIcon icon)
    {
        public TrayIcon Icon { get; } = icon;

        /// <summary>
        /// The PNG last given to <see cref="TrayIcon.Update"/>, kept because the kit has no
        /// tooltip-only update: a rename sends the same image with the new name.
        /// </summary>
        public byte[]? Png { get; set; }
    }

    /// <summary>The host every launcher's icon lives on; created once the host window exists.</summary>
    private TrayIconHost? _trayHost;

    /// <summary>Per-launcher tray icons (key = Launcher.Id).</summary>
    private readonly Dictionary<string, TrayIconEntry> _trayIcons = new();

    private void SetupTrayIcons()
    {
        // The callback message name is the one the app has always registered, so nothing about the
        // icons' identity changes with the move to the kit.
        _trayHost = new TrayIconHost(_hwnd, "LittleLauncher_TrayCallback");

        // Scaling, taskbar theme or display layout changed: redraw the tray images only. This fires
        // on every WM_SETTINGCHANGE, so it must not write icon files (the theme listener does that).
        _trayHost.RenderNeeded += RenderAllTrayImages;

        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            CreateTrayIconForLauncher(launcher);
        }

        SaveSettingsIconToAppData();
    }

    /// <summary>
    /// Registers a launcher's window messages and adds its tray icon. Does nothing for a launcher
    /// that already has one, since the host refuses a second icon with the same GUID.
    /// </summary>
    private void CreateTrayIconForLauncher(Launcher launcher)
    {
        if (_trayHost is null || _trayIcons.ContainsKey(launcher.Id))
        {
            return;
        }

        // Register per-launcher window message for flyout signaling (from companion exe)
        int wmFlyout = RegisterWindowMessage($"LittleLauncher_ShowFlyout_{launcher.Id}");
        _wmShowFlyoutPerLauncher[launcher.Id] = wmFlyout;

        // And the two a jump list task on this launcher's pinned button sends.
        int wmLaunchItem = RegisterWindowMessage($"LittleLauncher_LaunchItem_{launcher.Id}");
        _wmLaunchItemPerLauncher[launcher.Id] = wmLaunchItem;

        int wmLauncherAction = RegisterWindowMessage($"LittleLauncher_LauncherAction_{launcher.Id}");
        _wmLauncherActionPerLauncher[launcher.Id] = wmLauncherAction;

        var entry = new TrayIconEntry(_trayHost.Add(GetTrayIconGuid(launcher.Id)));
        _trayIcons[launcher.Id] = entry;
        WireTrayIconEvents(entry.Icon, launcher.Id);

        // Visibility first: a new icon appears on its first Update unless it is hidden.
        entry.Icon.IsVisible = IsTrayIconShown(launcher);
        RenderTrayImage(launcher, entry);
        SaveResolvedIconToAppData(launcher);

        // Subscribe to launcher property changes to live-update the icon
        launcher.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Launcher.TrayIconMode):
                case nameof(Launcher.CustomTrayIconPath):
                case nameof(Launcher.CustomTrayIconData):
                case nameof(Launcher.TrayIconPreviewData):
                case nameof(Launcher.TrayIconPreviewMode):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIcon(launcher));
                    break;
                case nameof(Launcher.Platform):
                case nameof(Launcher.Kind):
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!launcher.IsSupportedOnThisOperatingSystem)
                        {
                            FlyoutWindow.DisposeLauncher(launcher.Id);
                        }

                        UpdateTrayIconVisibility(launcher);
                    });
                    break;
                case nameof(Launcher.NIconHide):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIconVisibility(launcher));
                    break;
                case nameof(Launcher.Name):
                    DispatcherQueue.TryEnqueue(() => UpdateTrayIconTooltip(launcher));
                    break;
            }
        };
    }

    /// <summary>
    /// Routes an icon's clicks to its launcher. Handlers capture the id, not the launcher object,
    /// because sync can replace launcher objects while the icon lives on.
    /// </summary>
    private void WireTrayIconEvents(TrayIcon icon, string launcherId)
    {
        // A double-click toggles a second time, as each click did before the kit: the kit reports
        // the first click as Clicked and the second as DoubleClicked, so two quick clicks still
        // open and then close the panel.
        icon.Clicked += anchor => LauncherPanels.Toggle(this, anchor.X, anchor.Y, launcherId);
        icon.DoubleClicked += anchor => LauncherPanels.Toggle(this, anchor.X, anchor.Y, launcherId);
        icon.ContextMenuRequested += (x, y) => ShowContextMenu(x, y, launcherId);
    }

    /// <summary>Whether a launcher's icon belongs in the tray: enabled, not hidden, and for this OS.</summary>
    private static bool IsTrayIconShown(Launcher launcher) =>
        SettingsManager.Current.IsLauncherEnabled(launcher.Id) && !launcher.NIconHide && !IsHiddenOnThisOperatingSystem(launcher);

    /// <summary>
    /// Draws a launcher's icon at the tray's current pixel size and gives it to the shell with the
    /// launcher's name. Works while the icon is hidden too, so it shows current content when shown.
    /// </summary>
    private static void RenderTrayImage(Launcher launcher, TrayIconEntry entry)
    {
        var png = RenderTrayIconPng(launcher, TrayIcon.IconPixelSize);
        if (png is null)
        {
            return;
        }

        entry.Png = png;
        entry.Icon.Update(png, launcher.Name ?? "");
    }

    /// <summary>Redraws every launcher's tray image, for a scaling, taskbar or display change.</summary>
    private void RenderAllTrayImages()
    {
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            if (_trayIcons.TryGetValue(launcher.Id, out var entry))
            {
                RenderTrayImage(launcher, entry);
            }
        }
    }

    /// <summary>Removes one launcher's icon from the tray and forgets it.</summary>
    private void RemoveTrayIcon(string launcherId)
    {
        if (_trayIcons.Remove(launcherId, out var entry))
        {
            entry.Icon.Dispose();
        }
    }

    /// <summary>Removes every tray icon and stops listening for tray messages (an exit step).</summary>
    private void CleanupTrayIcons()
    {
        _trayHost?.Dispose();
        _trayHost = null;
        _trayIcons.Clear();
    }

    /// <summary>
    /// Shows the tray menu at a screen point. Called from the icon's dispatched event, so the
    /// blocking native menu never runs inside the window procedure.
    /// </summary>
    /// <param name="launcherId">The launcher whose icon was right-clicked, or null for app actions only.</param>
    private void ShowContextMenu(int x, int y, string? launcherId = null)
    {
        var menu = new TrayMenu();

        var launcher = launcherId is null
            ? null
            : SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher != null)
        {
            // Opens directly rather than routing through the settings window.
            menu.Add(UiText.Get("tray.menu.editLauncherSettings"), () => _ = Windows.LauncherSettingsWindow.ShowAsync(launcher));
            menu.AddSeparator();
        }

        // TRAY-5 order: app actions, Settings, Open logs folder, separator, Exit.
        menu.Add(UiText.Get("tray.menu.settings"), () => SettingsWindow.ShowInstance(this));
        menu.Add(UiText.Get("tray.menu.openLogsFolder"), WinUIAppKit.Logging.AppLogging.OpenLogsFolder);
        menu.AddSeparator();
        menu.Add(UiText.Get("tray.menu.exit"), ExitApp);
        menu.Show(_hwnd, x, y);
    }

    /// <summary>
    /// Rebuilds tray icons for all launchers. Removes icons for deleted launchers,
    /// re-creates icons for new launchers, and re-subscribes PropertyChanged handlers
    /// in case launcher objects were replaced (e.g. by SFTP sync).
    /// </summary>
    public void RefreshTrayIcons()
    {
        var currentIds = SettingsManager.Current.Launchers.Select(l => l.Id).ToHashSet();

        // Remove icons for deleted launchers
        foreach (var id in _trayIcons.Keys.ToList())
        {
            if (!currentIds.Contains(id))
            {
                RemoveTrayIcon(id);
                _wmShowFlyoutPerLauncher.Remove(id);
                _wmLaunchItemPerLauncher.Remove(id);
                _wmLauncherActionPerLauncher.Remove(id);
            }
        }

        // Create icons for new launchers, update existing ones.
        // Use RefreshLauncherIcon (not UpdateTrayIcon) to avoid per-launcher
        // SaveSettingsIconToAppData/CleanUpStaleIconFiles; we do those once below.
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            if (!_trayIcons.ContainsKey(launcher.Id))
            {
                CreateTrayIconForLauncher(launcher);
            }
            else
            {
                RefreshLauncherIcon(launcher);
            }
        }

        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();

        // A launcher that has been switched between kinds still owns the panel it used to use.
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            LauncherPanels.SyncKind(launcher);
        }

        LauncherPanels.WarmUp(this, SettingsManager.Current.Launchers);

        // This is the one place that already runs on every launcher add, remove, rename, icon
        // change and sync-driven replacement, which is exactly the set of events a Start Menu
        // shortcut has to follow. It prunes as well as writes, so a deleted launcher's shortcut
        // goes with it.
        Services.StartMenuShortcutService.Sync(SettingsManager.Current.Launchers);
    }

    /// <summary>Hide shortcut launchers belonging to another operating system.</summary>
    private static bool IsHiddenOnThisOperatingSystem(Launcher launcher) =>
        !launcher.IsSupportedOnThisOperatingSystem;

    internal void UpdateTrayIconVisibility(Launcher launcher)
    {
        if (_trayIcons.TryGetValue(launcher.Id, out var entry))
        {
            entry.Icon.IsVisible = IsTrayIconShown(launcher);
        }
    }

    /// <summary>
    /// Gives the icon the launcher's new name. The kit sets image and tooltip together, so the
    /// last image is sent again; a hidden icon keeps the name for when it is shown.
    /// </summary>
    private void UpdateTrayIconTooltip(Launcher launcher)
    {
        if (_trayIcons.TryGetValue(launcher.Id, out var entry) && entry.Png is not null)
        {
            entry.Icon.Update(entry.Png, launcher.Name ?? "");
        }
    }

    /// <summary>
    /// The launcher's permanent tray GUID: its id when that is a GUID, otherwise a name hash.
    /// Windows remembers the user's show-or-hide choice by it, so this must never change.
    /// </summary>
    internal static Guid GetTrayIconGuid(string launcherId)
    {
        if (Guid.TryParse(launcherId, out var parsedGuid))
        {
            return parsedGuid;
        }

        // The kit's name hash is the same MD5 version-3 derivation the app used before the move.
        return TrayIconHost.GuidFromName("LittleLauncher.Tray." + launcherId);
    }
}
