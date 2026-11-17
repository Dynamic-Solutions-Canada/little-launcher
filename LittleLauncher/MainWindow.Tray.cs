using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Models;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>One notification-area icon per launcher: creation, removal, menu, visibility and tooltip.</summary>
public sealed partial class MainWindow
{
    /// <summary>Holds the Win32 HICON state for one launcher's tray icon.</summary>
    private sealed class TrayIconEntry
    {
        public uint Uid;
        public Guid Guid;
        public System.Drawing.Icon? Icon;
        public bool IsAdded; // false when NIconHide=true (NIM_DELETE called)

        /// <summary>
        /// Whether Windows knows this icon by its GUID. False after Windows refused the GUID, as
        /// Windows 10 was seen to after an in-place package update (the GUID is tied to the exe path,
        /// and every update installs to a new folder); the icon is then known by window and number
        /// for the rest of the session. Kept as insurance on Windows 11, which this app requires.
        /// </summary>
        public bool UsesGuid = true;
    }

    /// <summary>
    /// Tray data that identifies <paramref name="entry"/> the way Windows knows it: by GUID, or by
    /// window and number once the GUID was refused. Every Shell_NotifyIcon call builds on this.
    /// </summary>
    /// <param name="entry">The launcher's tray icon.</param>
    /// <param name="flags">The NIF_ flags for the fields the caller sets; NIF_GUID is added here.</param>
    private NOTIFYICONDATA IconData(TrayIconEntry entry, uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = entry.Uid,
        uFlags = entry.UsesGuid ? flags | NIF_GUID : flags,
        guidItem = entry.UsesGuid ? entry.Guid : Guid.Empty,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private void SetupTrayIcons()
    {
        _wmTrayCallback = RegisterWindowMessage("LittleLauncher_TrayCallback");
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            CreateTrayIconForLauncher(launcher);
        }

        SaveSettingsIconToAppData();
    }

    private void CreateTrayIconForLauncher(Launcher launcher)
    {
        // Register per-launcher window message for flyout signaling (from companion exe)
        int wmFlyout = RegisterWindowMessage($"LittleLauncher_ShowFlyout_{launcher.Id}");
        _wmShowFlyoutPerLauncher[launcher.Id] = wmFlyout;

        // And the two a jump list task on this launcher's pinned button sends.
        int wmLaunchItem = RegisterWindowMessage($"LittleLauncher_LaunchItem_{launcher.Id}");
        _wmLaunchItemPerLauncher[launcher.Id] = wmLaunchItem;

        int wmLauncherAction = RegisterWindowMessage($"LittleLauncher_LauncherAction_{launcher.Id}");
        _wmLauncherActionPerLauncher[launcher.Id] = wmLauncherAction;

        uint uid = _nextIconId++;
        Guid trayGuid = GetTrayIconGuid(launcher.Id);
        var icon = ResolveTrayIcon(launcher);
        SaveResolvedIconToAppData(launcher);

        var entry = new TrayIconEntry { Uid = uid, Guid = trayGuid, Icon = icon };
        _trayIcons[launcher.Id] = entry;

        if (SettingsManager.Current.IsLauncherEnabled(launcher.Id) && !launcher.NIconHide && !IsHiddenOnThisOperatingSystem(launcher))
        {
            AddNativeIcon(entry, launcher.Name);
        }

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

    private void AddNativeIcon(TrayIconEntry entry, string? tooltip)
    {
        var tip = (tooltip ?? "");
        if (tip.Length > 127)
        {
            tip = tip[..127];
        }

        var data = IconData(entry, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.uCallbackMessage = (uint)_wmTrayCallback;
        data.hIcon = entry.Icon?.Handle ?? IntPtr.Zero;
        data.szTip = tip;
        if (!Shell_NotifyIcon(NIM_ADD, ref data) && entry.UsesGuid)
        {
            // Windows can refuse a GUID it recorded for the previous install's exe path (seen on
            // Windows 10). Add the icon by window and number instead, so the launcher keeps its tray
            // icon after an update.
            Logger.Warn("Tray icon GUID refused for {Tip}; using window and number this session", tip);
            entry.UsesGuid = false;
            data = IconData(entry, NIF_MESSAGE | NIF_ICON | NIF_TIP);
            data.uCallbackMessage = (uint)_wmTrayCallback;
            data.hIcon = entry.Icon?.Handle ?? IntPtr.Zero;
            data.szTip = tip;
            if (!Shell_NotifyIcon(NIM_ADD, ref data))
            {
                Logger.Warn("Tray icon could not be added for {Tip}", tip);
            }
        }

        entry.IsAdded = true;
    }

    private void RemoveNativeIcon(TrayIconEntry entry)
    {
        var data = IconData(entry, 0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        entry.IsAdded = false;
    }

    private void CleanupTrayIcons()
    {
        foreach (var entry in _trayIcons.Values)
        {
            if (entry.IsAdded)
            {
                RemoveNativeIcon(entry);
            }

            entry.Icon?.Dispose();
        }
        _trayIcons.Clear();
    }

    private void ShowContextMenu(int x, int y, string? launcherId = null)
    {
        var popup = new H.NotifyIcon.Core.PopupMenu();

        if (launcherId != null)
        {
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            if (launcher != null)
            {
                var editSettings = new H.NotifyIcon.Core.PopupMenuItem { Text = UiText.Get("tray.menu.editLauncherSettings") };
                editSettings.Click += (s, e) =>
                {
                    // Opens directly rather than routing through the settings window.
                    DispatcherQueue.TryEnqueue(() => _ = Windows.LauncherSettingsWindow.ShowAsync(launcher));
                };
                popup.Items.Add(editSettings);

                popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
            }
        }

        // TRAY-5 order: app actions, Settings, Open logs folder, separator, Exit.
        var settingsItem = new H.NotifyIcon.Core.PopupMenuItem { Text = UiText.Get("tray.menu.settings") };
        settingsItem.Click += (s, e) => SettingsWindow.ShowInstance(this);
        popup.Items.Add(settingsItem);
        var logsItem = new H.NotifyIcon.Core.PopupMenuItem { Text = UiText.Get("tray.menu.openLogsFolder") };
        logsItem.Click += (s, e) => WinUIAppKit.Logging.AppLogging.OpenLogsFolder();
        popup.Items.Add(logsItem);
        popup.Items.Add(new H.NotifyIcon.Core.PopupMenuSeparator());
        var exitItem = new H.NotifyIcon.Core.PopupMenuItem { Text = UiText.Get("tray.menu.exit") };
        exitItem.Click += (s, e) => ExitApp();
        popup.Items.Add(exitItem);
        popup.Show(_hwnd, x, y);
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
                var entry = _trayIcons[id];
                if (entry.IsAdded)
                {
                    RemoveNativeIcon(entry);
                }

                entry.Icon?.Dispose();
                _trayIcons.Remove(id);
                _wmShowFlyoutPerLauncher.Remove(id);
                _wmLaunchItemPerLauncher.Remove(id);
                _wmLauncherActionPerLauncher.Remove(id);
            }
        }

        // Create icons for new launchers, update existing ones.
        // Use RefreshLauncherIcon (not UpdateTrayIcon) to avoid per-launcher
        // SaveSettingsIconToAppData/CleanUpStaleIconFiles — we do those once below.
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
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry))
        {
            return;
        }

        bool hidden = !SettingsManager.Current.IsLauncherEnabled(launcher.Id) || launcher.NIconHide || IsHiddenOnThisOperatingSystem(launcher);
        if (hidden && entry.IsAdded)
        {
            RemoveNativeIcon(entry);
        }
        else if (!hidden && !entry.IsAdded)
        {
            AddNativeIcon(entry, launcher.Name);
        }
    }

    private void UpdateTrayIconTooltip(Launcher launcher)
    {
        if (!_trayIcons.TryGetValue(launcher.Id, out var entry) || !entry.IsAdded)
        {
            return;
        }

        var tip = (launcher.Name ?? "");
        if (tip.Length > 127)
        {
            tip = tip[..127];
        }

        var data = IconData(entry, NIF_TIP);
        data.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private static Guid GetTrayIconGuid(string launcherId)
    {
        if (Guid.TryParse(launcherId, out var parsedGuid))
        {
            return parsedGuid;
        }

        byte[] hash = MD5.HashData(System.Text.Encoding.UTF8.GetBytes("LittleLauncher.Tray." + launcherId));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }
}
