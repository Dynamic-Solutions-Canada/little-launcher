using LittleLauncher.Classes;
using LittleLauncher.Controls;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;


public partial class FlyoutWindow
{
    private const int EditToolbarGap = 8;
    private (int Width, int Height) EditToolbarSizeDips => _itemsView.ToolbarSize;
    internal static void ShowInEditMode(
        MainWindow owner,
        string launcherId,
        global::Windows.Graphics.RectInt32? setupBounds = null)
    {
        if (LauncherItemsEditorWindow.TryFocus(launcherId)) return;
        // A dismissed or still-warming flyout is visible off screen, not on it , Toggle brings
        // it in. `_isOpen` is what distinguishes the two; see FlyoutWindow.ParkOffScreen.
        bool alreadyVisible =
            _instances.TryGetValue(launcherId, out var existing) &&
            existing._hwnd != IntPtr.Zero &&
            IsWindow(existing._hwnd) &&
            existing._isOpen;

        // Toggle would *close* an already-open flyout.
        if (!alreadyVisible)
        {
            // A launcher hidden from the tray has no icon to anchor to, so the tray corner
            // would point at nothing , centre it instead.
            var launcher = SettingsManager.Current.Launchers.FirstOrDefault(l => l.Id == launcherId);
            bool hasTrayIcon = launcher is { NIconHide: false };

            (int x, int y) = hasTrayIcon ? GetTrayAnchorPoint() : GetScreenCentreAnchor();
            if (setupBounds is { } bounds)
                (x, y) = GetSetupAnchorPoint(bounds);
            Toggle(owner, x, y, launcherId, setupBounds);
        }

        if (!_instances.TryGetValue(launcherId, out var flyout)) return;

        if (alreadyVisible)
        {
            if (setupBounds.HasValue)
                flyout._setupEditBounds = setupBounds;

            flyout.DispatcherQueue.TryEnqueue(() =>
            {
                if (flyout._isOpen && !flyout._isHiding)
                {
                    flyout.EnterEditMode();
                    if (setupBounds.HasValue) flyout.ResizeForEditChrome();
                }
            });
            return;
        }

        // Wait out the open animation before entering edit mode.
        //
        // ShowAnimated drives the window's position on a rendering loop for
        // ShowAnimationDurationMs. Resizing partway through gets overwritten when the
        // animation's next frame writes the geometry it was aiming for , which is why the
        // flyout opened from settings ignored the height of the column headers.
        int openingVersion = flyout._animationVersion;
        var settle = flyout.DispatcherQueue.CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(
            AreAnimationsEnabled ? ShowAnimationDurationMs + 60 : 0);
        settle.IsRepeating = false;
        settle.Tick += (s, _) =>
        {
            s.Stop();
            if (flyout._isOpen && !flyout._isHiding && flyout._animationVersion == openingVersion)
                flyout.EnterEditMode();
        };
        settle.Start();
    }

    /// <summary>Approximate on-screen position of the notification area, for anchoring.</summary>
    private static (int X, int Y) GetTrayAnchorPoint()
    {
        try
        {
            GetCursorPos(out var cursor);
            IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            // Bottom-right of the work area: where the tray sits in the default layout.
            // CalculatePlacement re-detects the real taskbar edge and clamps from there.
            return (info.rcWork.Right - 8, info.rcWork.Bottom - 8);
        }
        catch
        {
            GetCursorPos(out var fallback);
            return (fallback.X, fallback.Y);
        }
    }

    /// <summary>
    /// Horizontally centred on the work area, but at the taskbar edge , so a launcher with no
    /// tray icon still opens like a normal flyout, just centred rather than off in the corner.
    /// </summary>
    private static (int X, int Y) GetScreenCentreAnchor()
    {
        try
        {
            GetCursorPos(out var cursor);
            IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            GetMonitorInfo(monitor, ref info);
            var work = info.rcWork;
            return (work.Left + ((work.Right - work.Left) / 2), work.Bottom - 8);
        }
        catch
        {
            GetCursorPos(out var fallback);
            return (fallback.X, fallback.Y);
        }
    }

    /// <summary>The floating bar above the flyout. Created lazily on first entry to edit mode.</summary>
    private EditToolbarWindow? _editToolbarBar;
    /// <summary>True while an owned editor window or picker is open, so focus loss must not dismiss.</summary>
    private bool _isModalOpen;

    /// <summary>
    /// The currently open owned editor/prompt window, so it can be closed if editing ends
    /// underneath it. Without this an "Add item" window outlives the edit session that
    /// opened it and would commit into a launcher the user has already navigated away from.
    /// </summary>
    private Window? _openModal;

    /// <summary>
    /// Tears down every window this flyout owns. Called when its launcher is deleted.
    /// </summary>
    /// <remarks>
    /// The floating toolbar and any open editor are separate top-level windows, so closing the
    /// flyout leaves them on screen , orphaned UI for a launcher that no longer exists.
    /// </remarks>
    internal void CloseEditChrome()
    {
        _setupEditBounds = null;
        _itemsView.SetEditing(false, notifyHost: false);
        CloseOpenModal();
        _itemsView.Dispose();
        var bar = _editToolbarBar;
        _editToolbarBar = null;
        try { bar?.Close(); } catch { /* already closing */ }
    }

    private void CloseOpenModal()
    {
        var modal = _openModal;
        _openModal = null;
        try { modal?.Close(); } catch { /* already closing */ }
        SetFlyoutTopmost(true);
    }

    /// <summary>
    /// Tints the flyout's window border while editing, so the whole surface reads as "in edit
    /// mode" rather than only the floating toolbar.
    /// </summary>
    /// <remarks>
    /// Uses the DWM border colour rather than a XAML border: it costs no layout at all, so it
    /// cannot change the flyout's width or inset its content , which a <c>BorderThickness</c>
    /// on <c>RootGrid</c> would.
    /// </remarks>
    private void SetEditModeBorder(bool editing)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;

        try
        {
            int value;
            if (editing)
            {
                var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush;
                var c = accent?.Color ?? global::Windows.UI.Color.FromArgb(255, 0x60, 0xA0, 0xE0);
                // COLORREF is 0x00BBGGRR, the reverse of the usual RGB order.
                value = (c.B << 16) | (c.G << 8) | c.R;
            }
            else
            {
                value = unchecked((int)DWMWA_COLOR_DEFAULT);
            }

            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref value, sizeof(int));
        }
        catch { /* border tint is cosmetic */ }
    }

    /// <summary>
    /// Toggles the flyout's always-on-top flag.
    /// </summary>
    /// <remarks>
    /// The flyout is created always-on-top so it renders above the tray overflow popup, but
    /// that also puts it above our own owned editor windows , owner relationship alone does
    /// not win against a topmost owner. Dropping the flag while a modal is open is what
    /// actually lets the editor sit in front.
    /// </remarks>
    private void SetFlyoutTopmost(bool topmost)
    {
        try
        {
            if (GetAppWindow().Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = topmost;
        }
        catch { /* presenter unavailable during teardown */ }
    }

    /// <summary>Runs an owned modal window, keeping the flyout pinned open and out of its way.</summary>
    private async Task<T> RunModalAsync<T>(Func<Action<Window>, Task<T>> show)
    {
        _isModalOpen = true;
        SetFlyoutTopmost(false);
        // The bar is topmost in its own right, so dropping only the flyout still leaves it
        // floating over the editor.
        _editToolbarBar?.HideBar();
        try
        {
            return await show(w => _openModal = w);
        }
        finally
        {
            _isModalOpen = false;
            _openModal = null;
            SetFlyoutTopmost(true);
            RepositionEditToolbarBar();
            RestoreFlyoutActivation();
        }
    }

    /// <summary>
    /// Returns foreground to the flyout after an owned window closes.
    /// </summary>
    /// <remarks>
    /// Dismissal is driven by the <c>Deactivated</c> event, which can only fire for a window
    /// that currently holds activation. Opening a modal takes activation away, and closing it
    /// does not necessarily give it back , leaving the flyout visible but unable to ever
    /// deactivate, so it stayed on screen until the user re-triggered it.
    /// </remarks>
    private void RestoreFlyoutActivation()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        if (!_isOpen) return;

        try { SetForegroundWindow(_hwnd); } catch { /* foreground can be refused */ }
    }

    /// <summary>Edit mode pins the flyout open; so does any modal it owns.</summary>
    private bool SuppressDismiss => _isEditMode || _isModalOpen || _setupEditBounds.HasValue;

    /// <summary>
    /// True when any launcher is being edited in its flyout. Used by the sync service, which must
    /// not download over a user mid-edit.
    /// </summary>
    internal static bool IsAnyInEditMode() => _instances.Values.Any(f => f.SuppressDismiss) || LauncherItemsEditorWindow.IsAnyOpen;

    /// <summary>Enters the shared editing behavior while retaining native popup chrome.</summary>
    internal void EnterEditMode() => _itemsView.EnterEditMode();

    private void ShowEditToolbarBar()
    {
        if (_itemsView.Toolbar == null || !_isEditMode || _isModalOpen || !_isOpen || _isHiding) return;

        _editToolbarBar ??= new EditToolbarWindow(_itemsView.Toolbar!);

        var (width, height) = EditToolbarSizeDips;
        if (width <= 0 || height <= 0) return;

        _editToolbarBar.PositionAbove(_hwnd, width, height);
    }

    /// <summary>
    /// Keeps the bar parked over the flyout after a move or resize.
    /// </summary>
    /// <remarks>
    /// Deferred to the dispatcher because <c>ResizeWindowToCurrentContent</c> moves the flyout
    /// with <c>SWP_ASYNCWINDOWPOS</c>: reading its rect immediately afterwards returns the
    /// <i>old</i> bounds, so the bar would follow the flyout's previous right edge and
    /// end up visibly off to one side.
    /// </remarks>
    private void RepositionEditToolbarBar()
    {
        if (!_isEditMode) return;
        DispatcherQueue.TryEnqueue(ShowEditToolbarBar);
    }

    internal void ExitEditMode()
    {
        if (!_isEditMode) return;
        _setupEditBounds = null;
        _itemsView.SetEditing(false, notifyHost: false);
        CloseOpenModal();
        _editToolbarBar?.HideBar();
        SetEditModeBorder(false);
        ResizeForEditChrome();
        // The floating Done button does not activate windows. If edit mode survived an
        // app switch, restore activation so the next outside click can deactivate us.
        RestoreFlyoutActivation();
    }

    private void ResizeForEditChrome()
    {
        UpdateEditToolbarColumns();
        ResizeWindowToCurrentContent(keepRightEdge: false);
        RepositionEditToolbarBar();
    }

    private void UpdateEditToolbarColumns() => _itemsView.UpdateToolbarWidth(GetFlyoutWidth() - 16);
}
