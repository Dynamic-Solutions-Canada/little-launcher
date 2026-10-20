using LittleLauncher.Classes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

/// <summary>
/// The edit-mode toolbar, floating in its own bar just above the flyout.
/// </summary>
/// <remarks>
/// <para>A separate window rather than a row inside the flyout. Keeping it out of
/// <c>ContentStack</c> means edit mode no longer changes the flyout's height at all, so the
/// toolbar cannot crowd the launcher's content or perturb the height arithmetic — the source
/// of several sizing bugs when it lived inside.</para>
/// <para>It is borderless, always-on-top and <c>WS_EX_NOACTIVATE</c>, so clicking a button
/// does not steal focus from the flyout, and <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt-Tab.
/// It must be repositioned whenever the flyout moves or resizes.</para>
/// </remarks>
internal sealed class EditToolbarWindow : Window
{
    /// <summary>Gap between the bar and the flyout's top edge, in DIPs.</summary>
    private const int GapDips = 6;

    private readonly IntPtr _hwnd;
    private readonly FrameworkElement _content;
    private bool _hasSessionOwner;
    private IntPtr _previousOwner;
    private bool _previousTopmost;

    public EditToolbarWindow(FrameworkElement content, IntPtr sessionOwner = default)
    {
        _content = content;
        _hwnd = WindowNative.GetWindowHandle(this);

        try
        {
            var presenter = OverlappedPresenter.CreateForContextMenu();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            GetAppWindow().SetPresenter(presenter);
            if (sessionOwner != IntPtr.Zero)
                BeginOwnedSession(sessionOwner);

            SystemBackdrop = new DesktopAcrylicBackdrop();
            Content = content;
            ThemeManager.ApplySavedTheme(this);

            // No focus theft, no Alt-Tab entry. Applied *before* Activate so the first show does
            // not pull foreground away from the flyout.
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            // A WinUI 3 window does not create its content island until it is activated — without
            // this the HWND is shown but renders nothing at all.
            Activate();
            ShowWindow(_hwnd, SW_HIDE);
        }
        catch when (sessionOwner != IntPtr.Zero)
        {
            // A failed constructor never reaches the flyout's toolbar field. Release its
            // temporary owner and close the partial window here instead of leaking it.
            try { EndOwnedSession(); }
            finally { Close(); }
            throw;
        }
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd));

    /// <summary>
    /// Sizes the bar above <paramref name="ownerHwnd"/> with their right edges aligned,
    /// clamped into the work area so it can never sit off-screen.
    /// </summary>
    public void PositionAbove(IntPtr ownerHwnd, int contentWidthDips, int contentHeightDips)
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !IsWindow(ownerHwnd))
            return;

        // The bar may still be on a different monitor before this move. Size it for the
        // flyout it follows, so the first placement is correct on a mixed-DPI desktop.
        double scale = GetDpiForWindow(ownerHwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)Math.Ceiling(contentWidthDips * scale);
        int height = (int)Math.Ceiling(contentHeightDips * scale);

        GetWindowRect(ownerHwnd, out var owner);
        int left = owner.Right - width;
        int top = owner.Top - height - (int)(GapDips * scale);

        var centre = new POINT { X = owner.Left + ((owner.Right - owner.Left) / 2), Y = owner.Top };
        IntPtr monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(monitor, ref info);
        var work = info.rcWork;

        if (left < work.Left) left = work.Left;
        if (left + width > work.Right) left = work.Right - width;

        // If there is no room above (flyout anchored to the top of the screen), sit below it.
        if (top < work.Top)
            top = owner.Bottom + (int)(GapDips * scale);

        // A Settings-owned bar follows its owner's stacking order, including app switches.
        // Repositioning it must not raise the session above whichever app is now foreground.
        IntPtr insertAfter = _hasSessionOwner ? IntPtr.Zero : HWND_TOPMOST;
        uint flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
        if (_hasSessionOwner) flags |= SWP_NOZORDER;
        SetWindowPos(_hwnd, insertAfter, left, top, width, height, flags);
    }

    /// <summary>Temporarily follows the editor as an owned, non-topmost Settings session bar.</summary>
    internal void BeginOwnedSession(IntPtr ownerHwnd)
    {
        if (_hasSessionOwner) return;
        var presenter = GetAppWindow().Presenter as OverlappedPresenter;
        bool previousTopmost = presenter?.IsAlwaysOnTop ?? false;
        _previousOwner = SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, ownerHwnd);
        _previousTopmost = previousTopmost;
        _hasSessionOwner = true;
        try
        {
            if (presenter != null) presenter.IsAlwaysOnTop = false;
        }
        catch
        {
            EndOwnedSession();
            throw;
        }
    }

    /// <summary>Restores the hidden toolbar's previous owner and normal topmost policy.</summary>
    internal void EndOwnedSession()
    {
        if (!_hasSessionOwner) return;
        _hasSessionOwner = false;
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, _previousOwner);
        if (GetAppWindow().Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _previousTopmost;
    }

    public void HideBar()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            ShowWindow(_hwnd, SW_HIDE);
    }
}
