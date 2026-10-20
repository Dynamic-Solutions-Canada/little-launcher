using System;
using System.Runtime.InteropServices;
using global::Windows.Graphics;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Windows;

public partial class FlyoutWindow
{
    /// <summary>
    /// Physical bounds of the launcher settings window that opened this first edit session.
    /// Session-only: normal invocation, Done, hiding and disposal all discard this anchor.
    /// </summary>
    private RectInt32? _setupEditBounds;

    /// <summary>Space kept above a setup editor for its toolbar and the gap below the bar.</summary>
    private double SetupToolbarClearanceDips => EditToolbarSizeDips.Height + EditToolbarGap;

    /// <summary>Returns the setup window's centre in physical screen coordinates.</summary>
    private static (int X, int Y) GetSetupAnchorPoint(RectInt32 bounds) =>
        (bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));

    /// <summary>
    /// Centres the editor where setup was, using that monitor's DPI and work area even if
    /// the pointer has since moved. Reused after every content resize for this session.
    /// </summary>
    private FlyoutPlacement CalculateSetupPlacement(RectInt32 bounds)
    {
        var (x, y) = GetSetupAnchorPoint(bounds);
        var point = new POINT { X = x, Y = y };
        IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpi, out _);
        double scale = dpi / 96.0;
        if (scale <= 0) scale = 1.0;

        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(monitor, ref info);
        var workArea = info.rcWork;

        // Toolbar wrapping can change with the number of columns or the icon view mode.
        // Reserve its current arithmetic height, without measuring the parked window.
        UpdateEditToolbarColumns();
        int toolbarClearance = (int)Math.Ceiling(SetupToolbarClearanceDips * scale);
        workArea.Top += toolbarClearance;

        int width = (int)Math.Ceiling(GetFlyoutWidth() * scale);
        int height = (int)Math.Ceiling(MeasureContentHeight() * scale);
        var (left, top) = ClampFlyoutPosition(x - (width / 2), y - (height / 2), width, height, workArea);
        int slideDistance = Math.Max(18, (int)Math.Round(SlideDistanceDip * scale));

        return new FlyoutPlacement(left, top, top + slideDistance, width, height, FlyoutEntranceEdge.Bottom);
    }

    /// <summary>
    /// Keeps a flyout inside a monitor's work area without changing content dimensions.
    /// Oversized content keeps its start visible instead of pushing it past the work area's start.
    /// </summary>
    private static (int Left, int Top) ClampFlyoutPosition(int left, int top, int width, int height, RECT workArea) =>
        (
            Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)),
            Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height))
        );
}
