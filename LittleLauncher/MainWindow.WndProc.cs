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

/// <summary>Cross-process window messages from the companion, a second launch and the tray.</summary>
public sealed partial class MainWindow
{
    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        // Handle tray icon left/right click callbacks from Shell_NotifyIcon
        if ((int)msg == _wmTrayCallback && _wmTrayCallback != 0)
        {
            uint uid = (uint)(wParam.ToInt64() & 0xFFFFFFFF);
            int notification = (int)(lParam.ToInt64() & 0xFFFF);

            string? targetLauncherId = null;
            foreach (var (lid, entry) in _trayIcons)
            {
                if (entry.Uid == uid) { targetLauncherId = lid; break; }
            }

            if (targetLauncherId != null)
            {
                if (notification == WM_LBUTTONUP)
                {
                    GetCursorPos(out var pt);
                    string lid = targetLauncherId;
                    TryResolveTrayAnchorPoint(lid, pt.X, pt.Y, out int anchorX, out int anchorY);

                    DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, anchorX, anchorY, lid));
                }
                else if (notification == WM_RBUTTONUP || notification == WM_CONTEXTMENU)
                {
                    GetCursorPos(out var pt);
                    int cx = pt.X, cy = pt.Y;
                    string lid = targetLauncherId;
                    DispatcherQueue.TryEnqueue(() => ShowContextMenu(cx, cy, lid));
                }
            }
            return IntPtr.Zero;
        }

        // Iterate per-launcher window messages (from companion exe / LauncherShortcut)
        foreach (var (launcherId, wmShowFlyout) in _wmShowFlyoutPerLauncher)
        {
            if ((int)msg == wmShowFlyout && wmShowFlyout != 0)
            {
                // Resolve which launcher to show: use named launcherId, fall back to first launcher
                var targetId = launcherId;
                if (string.IsNullOrEmpty(targetId))
                {
                    targetId = SettingsManager.Current.Launchers.FirstOrDefault()?.Id ?? string.Empty;
                }

                int anchorX = (int)wParam;
                int anchorY = (int)lParam;
                TryResolveLauncherAnchorPoint(targetId, anchorX, anchorY, out anchorX, out anchorY);
                DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, anchorX, anchorY, targetId));
                return IntPtr.Zero;
            }
        }

        // A jump list task on a pinned taskbar button. wParam is the entry's position in the
        // published list and lParam the token identifying what was published there; both are
        // needed because the list is a snapshot that may predate an edit.
        foreach (var (launcherId, wmLaunchItem) in _wmLaunchItemPerLauncher)
        {
            if ((int)msg == wmLaunchItem && wmLaunchItem != 0)
            {
                int index = (int)wParam;
                int token = (int)lParam;
                var targetId = launcherId;

                // The cursor is over the jump list, which is over the taskbar button - close
                // enough to anchor a panel that has to open anyway (a web launcher's bookmark
                // needs its window), and the same resolution the pinned click uses.
                GetCursorPos(out var cursor);
                TryResolveLauncherAnchorPoint(targetId, cursor.X, cursor.Y, out int anchorX, out int anchorY);

                DispatcherQueue.TryEnqueue(() =>
                    LauncherPanels.LaunchFromJumpList(this, targetId, index, token, anchorX, anchorY));
                return IntPtr.Zero;
            }
        }

        // One of the launcher's own commands, from below the separator on its jump list.
        foreach (var (launcherId, wmLauncherAction) in _wmLauncherActionPerLauncher)
        {
            if ((int)msg == wmLauncherAction && wmLauncherAction != 0)
            {
                int action = (int)wParam;
                var targetId = launcherId;
                DispatcherQueue.TryEnqueue(() => LauncherPanels.RunLauncherAction(this, targetId, action));
                return IntPtr.Zero;
            }
        }

        if ((int)msg == _wmShowSettings && _wmShowSettings != 0)
        {
            DispatcherQueue.TryEnqueue(() => SettingsWindow.ShowInstance(this));
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
