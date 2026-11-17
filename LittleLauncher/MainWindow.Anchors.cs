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

/// <summary>Where a flyout opens from: the tray icon or the launcher's pinned taskbar button.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Resolves the center of this launcher's registered tray icon for tray and notification
    /// activation. Returns false and preserves the supplied fallback if the shell has no bounds.
    /// </summary>
    private bool TryResolveTrayAnchorPoint(string launcherId, int fallbackX, int fallbackY,
        out int anchorX, out int anchorY)
    {
        anchorX = fallbackX;
        anchorY = fallbackY;
        if (!_trayIcons.TryGetValue(launcherId, out var entry) || !entry.IsAdded)
        {
            return false;
        }

        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hwnd,
            uID = entry.Uid,

            // Empty unless Windows knows the icon by GUID; then it finds it by window and number.
            guidItem = entry.UsesGuid ? entry.Guid : Guid.Empty,
        };
        int result = Shell_NotifyIconGetRect(ref identifier, out var iconRect);
        if (result != 0 || iconRect.Right <= iconRect.Left || iconRect.Bottom <= iconRect.Top)
        {
            Logger.Debug("Tray anchor unavailable for launcher {Id}: HRESULT {Result}", launcherId, result);
            return false;
        }

        anchorX = iconRect.Left + (iconRect.Right - iconRect.Left) / 2;
        anchorY = iconRect.Top + (iconRect.Bottom - iconRect.Top) / 2;
        return true;
    }

    private bool TryResolveLauncherAnchorPoint(string launcherId, int fallbackX, int fallbackY, out int anchorX, out int anchorY)
    {
        anchorX = fallbackX;
        anchorY = fallbackY;

        var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
        if (launcher == null)
        {
            return false;
        }

        try
        {
            var element = FindTaskbarLauncherElement(launcher.Name);
            if (element == null)
            {
                return false;
            }

            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return false;
            }

            anchorX = (int)Math.Round(bounds.Left + (bounds.Width / 2.0));
            anchorY = (int)Math.Round(bounds.Top + (bounds.Height / 2.0));
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static AutomationElement? FindTaskbarLauncherElement(string launcherName)
    {
        string expectedName = $"Little Launcher - {launcherName}";

        var focusedMatch = MatchLauncherElementOrAncestor(AutomationElement.FocusedElement, expectedName, launcherName);
        if (focusedMatch != null)
        {
            return focusedMatch;
        }

        var root = AutomationElement.RootElement;
        if (root == null)
        {
            return null;
        }

        foreach (string taskbarClass in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var taskbar = root.FindFirst(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, taskbarClass));
            if (taskbar == null)
            {
                continue;
            }

            var exactMatch = taskbar.FindFirst(
                TreeScope.Descendants,
                BuildLauncherButtonCondition(expectedName));
            if (exactMatch != null)
            {
                return exactMatch;
            }

            var partialMatch = FindLauncherButtonBySubstring(taskbar, launcherName);
            if (partialMatch != null)
            {
                return partialMatch;
            }
        }

        return null;
    }

    private static AutomationElement? MatchLauncherElementOrAncestor(AutomationElement? element, string expectedName, string launcherName)
    {
        while (element != null)
        {
            if (IsLauncherButtonMatch(element, expectedName, launcherName))
            {
                return element;
            }

            try
            {
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }

    private static Condition BuildLauncherButtonCondition(string buttonName)
    {
        return new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, buttonName));
    }

    private static AutomationElement? FindLauncherButtonBySubstring(AutomationElement taskbar, string launcherName)
    {
        var buttons = taskbar.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            if (IsLauncherButtonMatch(button, null, launcherName))
            {
                return button;
            }
        }

        return null;
    }

    private static bool IsLauncherButtonMatch(AutomationElement element, string? expectedName, string launcherName)
    {
        string name;
        try
        {
            name = element.Current.Name ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expectedName) &&
            string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Contains(launcherName, StringComparison.OrdinalIgnoreCase) &&
               name.Contains("Little Launcher", StringComparison.OrdinalIgnoreCase);
    }
}
