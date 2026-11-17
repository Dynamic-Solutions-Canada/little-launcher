using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Classes;

/// <summary>
/// Shared chrome for the app's small owned windows (item editor, text prompt): the app icon
/// on the HWND/taskbar/Alt-Tab, plus a Mica-friendly custom title bar.
/// </summary>
/// <remarks>
/// These windows extend content into the title bar for the same reason
/// <c>SettingsWindow</c> does: a default WinUI title bar does not follow the app's
/// <c>RequestedTheme</c>, so it renders light chrome above dark content. Drawing our own
/// keeps the caption on-theme and lets Mica run the full height of the window.
/// </remarks>
internal static class WindowChrome
{
    private const double TitleBarHeight = 40;

    /// <summary>
    /// Resolves the icon these windows wear: the application's own.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not <c>app-icon.ico</c>.</b> That file reads like the app's icon and is not:
    /// <c>SaveResolvedIconToAppData</c> keeps it mirroring the <em>first launcher's</em> icon,
    /// because shortcuts and the companion exe's pin dialog want a launcher's face. A dialog of the
    /// app does not. Reading it meant the item editor, the text prompt, launcher settings and the
    /// extension popup all wore whichever launcher happened to sort first, which looks like a bug
    /// because it is one. A window that wants a specific launcher's icon passes the path in.
    /// </remarks>
    internal static string? ResolveAppIconPath()
    {
        string appIcon = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        return File.Exists(appIcon) ? appIcon : null;
    }

    /// <summary>
    /// Sets the window icon for the title bar, taskbar and Alt-Tab: the app's own icon unless
    /// <paramref name="icoPath"/> names another.
    /// </summary>
    /// <remarks>
    /// winui-app-kit's <c>WindowIcon.Apply</c> does the work, loading the icon at the window's own DPI
    /// so it stays sharp at any scaling, and keeping the handles alive for the window's lifetime.
    /// A missing icon file is cosmetic only and leaves the window as it is.
    /// </remarks>
    internal static void ApplyIcon(Window window, string? icoPath = null)
    {
        icoPath ??= ResolveAppIconPath();
        if (icoPath == null)
        {
            return;
        }

        WinUIAppKit.Windowing.WindowIcon.Apply(window, icoPath);
    }

    /// <summary>
    /// Builds a draggable title bar row (icon + caption). Pass the result to
    /// <c>Window.SetTitleBar</c> and place it in row 0 of the window's root grid.
    /// </summary>
    internal static Grid BuildTitleBar(string title, string? icoPath = null)
    {
        icoPath ??= ResolveAppIconPath();

        var bar = new Grid
        {
            Height = TitleBarHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(12, 0, 0, 0),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        if (icoPath != null)
        {
            try { icon.Source = new BitmapImage(new Uri(icoPath)); }
            catch { /* leave blank */ }
        }
        Grid.SetColumn(icon, 0);

        var caption = new TextBlock
        {
            Text = title,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(caption, 1);

        bar.Children.Add(icon);
        bar.Children.Add(caption);
        return bar;
    }
}
