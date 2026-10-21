using System;

namespace LittleLauncher.Controls;

/// <summary>Dimension-only group hit policy, shared by drag feedback and the final drop.</summary>
internal static class LauncherDropGeometry
{
    internal enum GroupZone { Outside, Before, Inside, After }

    /// <summary>
    /// Reserves up to eight DIPs at the outer top/bottom for sibling insertion. The middle
    /// remains a group target even in short groups; invalid or out-of-bounds geometry is ignored.
    /// Coordinates and dimensions are relative to the full realised group container.
    /// </summary>
    internal static GroupZone GetGroupZone(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0 || x < 0 || x >= width || y < 0 || y >= height)
            return GroupZone.Outside;

        double edge = Math.Min(8, height / 4);
        if (y < edge)
            return GroupZone.Before;
        if (y >= height - edge)
            return GroupZone.After;
        return GroupZone.Inside;
    }

    /// <summary>
    /// Places an end marker inside the last child's lower area. Insets keep it distinct
    /// from the full-width sibling boundary, including tiny heading or narrow icon cells.
    /// Invalid/unrealised dimensions do not produce a marker.
    /// </summary>
    internal static (double Left, double Top, double Width, double Height)? GetEndMarker(
        double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return null;

        double sideInset = Math.Min(8, width / 4);
        double bottomInset = Math.Min(8, height / 3);
        double thickness = Math.Min(2, height / 4);
        return (sideInset, height - bottomInset - thickness, width - sideInset * 2, thickness);
    }
}
