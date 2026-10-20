using LittleLauncher.Classes.Settings;
using LittleLauncher.Controls;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Threading.Tasks;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow
{
    bool ILauncherItemsHost.IsRegularWindow => false;
    IntPtr ILauncherItemsHost.DialogOwnerHwnd => _hwnd;

    /// <summary>Native hosts resize around intrinsic view dimensions only while presented.</summary>
    void ILauncherItemsHost.ContentSizeChanged(bool keepRightEdge)
    {
        if (!_isOpen || _isHiding) return;
        UpdateEditToolbarColumns();
        ResizeWindowToCurrentContent(keepRightEdge);
        RepositionEditToolbarBar();
    }

    void ILauncherItemsHost.EditModeChanged()
    {
        SetEditModeBorder(_isEditMode);
        if (_isOpen) ResizeForEditChrome();
    }

    void ILauncherItemsHost.FinishEditing() => ExitEditMode();

    void ILauncherItemsHost.LaunchItem(LauncherItem item)
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        Launch(item);
    }

    void ILauncherItemsHost.OpenAppSettings()
    {
        HideFlyout();
        _lastDismissed = DateTime.UtcNow;
        if (_owner != null) SettingsWindow.ShowInstance(_owner);
    }

    Task<T> ILauncherItemsHost.RunModalAsync<T>(Func<Action<Window>, Task<T>> show) => RunModalAsync(show);

    void ILauncherItemsHost.BeginIconResize()
    {
        _animationVersion++;
        _isHiding = false;
        _isShowing = false;
    }

    double ILauncherItemsHost.GetResizePointerX(PointerRoutedEventArgs args)
    {
        // A left-edge resize moves the HWND. Screen coordinates preserve the original gesture
        // origin across those moves, exactly as before the shared view was extracted.
        GetCursorPos(out var point);
        return point.X;
    }

    private int GetFlyoutWidth() => _itemsView.ContentWidth;

    /// <summary>Only the popup host clamps the shared view's natural height to its monitor.</summary>
    private double MeasureContentHeight() => Math.Clamp(
        _itemsView.ContentHeight,
        _itemsView.MinimumContentHeight,
        GetWorkAreaHeightDips() - 16);

    private void RebuildItemsIfNeeded() => _itemsView.Refresh();

    /// <summary>Refreshes all shared views and the composite tray icon after model/icon changes.</summary>
    internal static void InvalidateItems(string? launcherId = null, bool force = true)
    {
        LauncherItemsView.InvalidateItems(launcherId, force);
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            if ((launcherId == null || launcher.Id == launcherId)
                && launcher.TrayIconMode == TrayIconModes.Composite)
                MainWindow.Current?.UpdateTrayIcon(launcher);
        }
    }

    internal static void InvalidateAllItems() => InvalidateItems();

    /// <summary>Ends native editing before the same launcher opens in a regular editor window.</summary>
    internal static void ParkForItemEditor(string launcherId)
    {
        if (!_instances.TryGetValue(launcherId, out var flyout)) return;
        flyout.HideFlyout();
        flyout.EndPreRender();
        flyout._animationVersion++;
        flyout._isShowing = false;
        flyout._isHiding = false;
        flyout.ParkOffScreen();
        flyout._lastDismissed = DateTime.MinValue;
    }
}
