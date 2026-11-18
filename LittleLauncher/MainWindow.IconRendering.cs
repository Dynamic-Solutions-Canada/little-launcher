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

/// <summary>Drawing launcher icons: presets, glyphs, composites and .ico conversion.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Renders a launcher's tray image as a PNG at exactly <paramref name="size"/> pixels square
    /// (the tray's current size), falling back to the app icon if the launcher's own fails.
    /// Returns null only when even the app icon cannot be read.
    /// </summary>
    private static byte[]? RenderTrayIconPng(Launcher launcher, int size)
    {
        try
        {
            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap != null)
            {
                return ResizeToPng(bitmap, size);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve tray icon for launcher {Name}, falling back to the app icon", launcher.Name);
        }

        // Last-resort fallback: the app's own icon
        string fallbackIco = Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
        if (!File.Exists(fallbackIco))
        {
            return null;
        }

        using var ico = new System.Drawing.Icon(fallbackIco, 256, 256);
        using var fallback = ico.ToBitmap();
        return ResizeToPng(fallback, size);
    }

    /// <summary>
    /// Preset icon PNG filenames (in Resources/AppIcons/), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, string> PresetIcons = new()
    {
        { TrayIconModes.Blue, "Blue" },
        { TrayIconModes.Green, "Green" },
        { TrayIconModes.Teal, "Teal" },
        { TrayIconModes.Red, "Red" },
        { TrayIconModes.Orange, "Orange" },
        { TrayIconModes.Purple, "Purple" },
    };

    /// <summary>
    /// Glyph preset icon characters (Segoe Fluent Icons), keyed by TrayIconMode string.
    /// </summary>
    private static readonly Dictionary<string, (char Glyph, string Label)> GlyphPresets = new()
    {
        { TrayIconModes.Pin, ('\uE840', "Pin") },
        { TrayIconModes.Star, ('\uE734', "Star") },
        { TrayIconModes.Heart, ('\uEB51', "Heart") },
        { TrayIconModes.Lightning, ('\uE945', "Lightning") },
        { TrayIconModes.Search, ('\uE721', "Search") },
        { TrayIconModes.Globe, ('\uE774', "Globe") },
    };

    /// <summary>
    /// Single source of truth for rendering a launcher's icon as a 256×256 bitmap.
    /// All icon surfaces (tray, shortcuts, settings window) derive from this.
    /// </summary>
    internal static byte[]? ExportLauncherGlyphPng(Launcher launcher)
    {
        if (!TrayIconModes.IsGlyphMode(launcher.TrayIconMode))
        {
            return null;
        }

        using var bitmap = ResolveBaseIconBitmap(launcher);
        if (bitmap is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static System.Drawing.Bitmap? ResolveBaseIconBitmap(Launcher launcher)
    {
        string mode = launcher.TrayIconMode;

        try
        {
            // Composite mode — 2×2 grid of first 4 item icons
            if (mode == TrayIconModes.Composite)
            {
                return RenderCompositeIconBitmap(launcher);
            }

            // Custom user image
            if (mode == TrayIconModes.Custom)
            {
                string path = launcher.CustomTrayIconPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using var ico = new System.Drawing.Icon(path, 256, 256);
                        return new System.Drawing.Bitmap(ico.ToBitmap(), 256, 256);
                    }
                    using var original = new System.Drawing.Bitmap(path);
                    return TrimAndResizeTo256(original);
                }
            }

            if (TrayIconModes.IsGlyphMode(mode) && launcher.TrayIconPreviewMode == mode && !string.IsNullOrEmpty(launcher.TrayIconPreviewData))
            {
                try
                {
                    using var stream = new MemoryStream(PortableLauncherIcon.DecodePreview(launcher.TrayIconPreviewData));
                    using var preview = new System.Drawing.Bitmap(stream);
                    return TrimAndResizeTo256(preview);
                }
                catch { /* Preserve the source choice and use native rendering if the preview is unreadable. */ }
            }

            // Glyph presets
            if (GlyphPresets.TryGetValue(mode, out var preset))
            {
                bool dark = ThemeManager.IsDarkTheme();
                var fg = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;
                return RenderGlyphBitmap(preset.Glyph, fg);
            }

            // Gallery-chosen glyph/emoji (stored as "Glyph:X" or "Glyph:#RRGGBB:X" in TrayIconMode)
            if (TrayIconModes.IsGlyphMode(mode))
            {
                string? glyphStr = TrayIconModes.GetGlyphCharacter(mode);
                if (!string.IsNullOrEmpty(glyphStr) && glyphStr.Length > 0)
                {
                    bool dark = ThemeManager.IsDarkTheme();
                    string? colorHex = TrayIconModes.GetGlyphColor(mode);
                    var fg = !string.IsNullOrEmpty(colorHex)
                        ? ParseHexColor(colorHex, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                        : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                    string fontName = Classes.IconGallery.IsFluentGlyph(glyphStr)
                        ? "Segoe Fluent Icons"
                        : "Segoe UI Emoji";
                    return RenderGlyphBitmap(glyphStr, fg, 256, 240f, fontName);
                }
            }

            // Color presets (fallback to Blue if unknown)
            if (!PresetIcons.TryGetValue(mode, out var name))
            {
                name = PresetIcons[TrayIconModes.Blue];
            }

            string pngPath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", $"{name}.png");
            if (File.Exists(pngPath))
            {
                using var original = new System.Drawing.Bitmap(pngPath);
                return TrimAndResizeTo256(original);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to resolve base icon bitmap for mode {Mode}", mode);
        }
        return null;
    }

    /// <summary>
    /// Renders a Segoe Fluent Icons glyph as a 256×256 bitmap.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(char glyph, System.Drawing.Color fg)
    {
        return RenderGlyphBitmap(glyph.ToString(), fg, 256, 240f);
    }

    /// <summary>Parses "#RRGGBB" to a System.Drawing.Color, returning fallback on failure.</summary>
    private static System.Drawing.Color ParseHexColor(string hex, System.Drawing.Color fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                int r = Convert.ToByte(hex[..2], 16);
                int g = Convert.ToByte(hex[2..4], 16);
                int b = Convert.ToByte(hex[4..6], 16);
                return System.Drawing.Color.FromArgb(255, r, g, b);
            }
        }
        catch { /* fall through */ }
        return fallback;
    }

    /// <summary>
    /// Renders a complete glyph sequence. Fluent icons keep their existing tint renderer;
    /// emoji use DirectWrite shaping and Direct2D color glyphs rather than monochrome GDI.
    /// </summary>
    private static System.Drawing.Bitmap RenderGlyphBitmap(string glyph, System.Drawing.Color fg, int size, float fontPx, string fontName = "Segoe Fluent Icons")
    {
        if (!Classes.IconGallery.IsFluentGlyph(glyph))
        {
            return GlyphIconRenderer.RenderEmoji(glyph, fg, size, fontPx);
        }

        var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new System.Drawing.Font(fontName, fontPx, System.Drawing.GraphicsUnit.Pixel);
            using var brush = new System.Drawing.SolidBrush(fg);
            using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
            fmt.Alignment = System.Drawing.StringAlignment.Center;
            fmt.LineAlignment = System.Drawing.StringAlignment.Center;
            g.DrawString(glyph.ToString(), font, brush, new System.Drawing.RectangleF(0, 0, size, size), fmt);
        }
        return bitmap;
    }

    /// <summary>
    /// Rasterises one launcher item's icon at the requested size: its cached image if it has one,
    /// otherwise its glyph rendered in its colour. Returns null when the item has neither.
    /// </summary>
    /// <remarks>
    /// <para>Shared by the composite tray icon and the taskbar jump list, which need the same
    /// answer at different sizes. It deliberately takes the item's icon fields rather than the
    /// item: the jump list resolves icons on a background thread from a snapshot, and must not
    /// touch the observable objects the UI thread is free to be editing.</para>
    /// <para><paramref name="dark"/> is passed in rather than read here for the same reason -
    /// <c>ThemeManager</c> is the UI thread's to ask.</para>
    /// </remarks>
    internal static System.Drawing.Bitmap? ResolveItemIconBitmap(
        string iconPath, string iconGlyph, string iconColor, int size, bool dark)
    {
        try
        {
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using var ico = new System.Drawing.Icon(iconPath, size, size);
                    using var icoBitmap = ico.ToBitmap();
                    return new System.Drawing.Bitmap(icoBitmap, size, size);
                }

                using var orig = new System.Drawing.Bitmap(iconPath);
                return new System.Drawing.Bitmap(orig, size, size);
            }

            if (!string.IsNullOrEmpty(iconGlyph))
            {
                var fg = !string.IsNullOrEmpty(iconColor)
                    ? ParseHexColor(iconColor, dark ? System.Drawing.Color.White : System.Drawing.Color.Black)
                    : (dark ? System.Drawing.Color.White : System.Drawing.Color.Black);
                string fontName = Classes.IconGallery.IsFluentGlyph(iconGlyph)
                    ? "Segoe Fluent Icons"
                    : "Segoe UI Emoji";
                return RenderGlyphBitmap(iconGlyph, fg, size, size * 0.8f, fontName);
            }
        }
        catch { /* best-effort: an unreadable icon is not a reason to fail the whole surface */ }

        return null;
    }

    /// <summary>
    /// Renders a 2×2 composite icon from the first 4 launchable items, similar to
    /// how Windows 11 Start menu shows folder/group previews.
    /// </summary>
    private static System.Drawing.Bitmap? RenderCompositeIconBitmap(Launcher launcher)
    {
        const int canvasSize = 256;
        const int cellSize = 124;  // each sub-icon cell
        const int gap = 4;         // gap between cells

        // Collect first 4 launchable items (flatten groups)
        var items = new List<LauncherItem>();
        CollectLaunchableItems(launcher.Items, items, 4);
        if (items.Count == 0)
        {
            return null;
        }

        // 2×2 grid positions, centered on canvas
        int gridWidth = cellSize * 2 + gap;
        int offsetX = (canvasSize - gridWidth) / 2;
        int offsetY = (canvasSize - gridWidth) / 2;
        var positions = new (int X, int Y)[]
        {
            (offsetX, offsetY),
            (offsetX + cellSize + gap, offsetY),
            (offsetX, offsetY + cellSize + gap),
            (offsetX + cellSize + gap, offsetY + cellSize + gap),
        };

        bool dark = ThemeManager.IsDarkTheme();
        var bitmap = new System.Drawing.Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.Clear(System.Drawing.Color.Transparent);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        for (int i = 0; i < Math.Min(items.Count, 4); i++)
        {
            var item = items[i];
            var pos = positions[i];

            var subIcon = ResolveItemIconBitmap(item.IconPath, item.IconGlyph, item.IconColor, cellSize, dark);

            if (subIcon == null)
            {
                continue;
            }

            using (subIcon)
            {
                g.DrawImage(subIcon,
                    new System.Drawing.Rectangle(pos.X, pos.Y, cellSize, cellSize),
                    new System.Drawing.Rectangle(0, 0, subIcon.Width, subIcon.Height),
                    System.Drawing.GraphicsUnit.Pixel);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// Collects the first N launchable (non-group, non-heading, non-column-break) items,
    /// flattening groups.
    /// </summary>
    internal static void CollectLaunchableItems(IEnumerable<LauncherItem> source, List<LauncherItem> result, int max)
    {
        foreach (var item in source)
        {
            if (result.Count >= max)
            {
                return;
            }

            if (item.IsColumnBreak)
            {
                continue;
            }

            if (item.IsGroup)
            {
                CollectLaunchableItems(item.Children, result, max);
                continue;
            }
            // Headings have no Path and aren't launchable
            if (string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            result.Add(item);
        }
    }

    /// <summary>Creates a GraphicsPath for a rounded rectangle.</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedRectPath(int x, int y, int w, int h, int r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Trims transparent padding and centers the content on a 256×256 canvas,
    /// preserving the original aspect ratio.
    /// </summary>
    private static System.Drawing.Bitmap TrimAndResizeTo256(System.Drawing.Bitmap original)
    {
        var bounds = GetOpaqueContentBounds(original);
        int contentSize = Math.Max(bounds.Width, bounds.Height);
        if (contentSize <= 0)
        {
            contentSize = original.Width;
        }

        // Fill 100% of the canvas so icons appear full-size in tray/taskbar
        float scale = 256f / contentSize;
        int drawW = (int)(bounds.Width * scale);
        int drawH = (int)(bounds.Height * scale);
        int offsetX = (256 - drawW) / 2;
        int offsetY = (256 - drawH) / 2;

        const int iconSize = 256;
        var resized = new System.Drawing.Bitmap(iconSize, iconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            // Draw only the trimmed content region, preserving aspect ratio
            var dest = new System.Drawing.Rectangle(offsetX, offsetY, drawW, drawH);
            g.DrawImage(original, dest, bounds, System.Drawing.GraphicsUnit.Pixel);
        }
        return resized;
    }

    /// <summary>
    /// Returns the bounding rectangle of non-transparent pixels in a bitmap.
    /// </summary>
    private static System.Drawing.Rectangle GetOpaqueContentBounds(System.Drawing.Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).A > 0)
                {
                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }
        }
        if (maxX < minX)
        {
            return new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        }

        return new System.Drawing.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Renders a Bitmap as a multi-resolution ICO byte array (16–256 px, PNG-compressed).
    /// Used for both in-memory Icon creation and direct file writes.
    /// Writing these bytes directly to disk avoids System.Drawing.Icon.Save() which
    /// is known to lose multi-resolution data on .NET.
    /// </summary>
    internal static byte[] BitmapToIcoBytes(System.Drawing.Bitmap bitmap)
    {
        int[] sizes = [16, 24, 32, 48, 64, 256];
        byte[][] pngEntries = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            pngEntries[i] = ResizeToPng(bitmap, sizes[i]);
        }

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICO header
            bw.Write((short)0);                     // reserved
            bw.Write((short)1);                     // type: icon
            bw.Write((short)sizes.Length);           // image count

            // Directory entries — offset starts after header + all directory entries
            int dataOffset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // width
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));  // height
                bw.Write((byte)0);            // color palette
                bw.Write((byte)0);            // reserved
                bw.Write((short)1);           // color planes
                bw.Write((short)32);          // bits per pixel
                bw.Write(pngEntries[i].Length); // image data size
                bw.Write(dataOffset);         // offset to image data

                dataOffset += pngEntries[i].Length;
            }

            // PNG payloads
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(pngEntries[i]);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Scales a bitmap to <paramref name="size"/> pixels square with high-quality bicubic
    /// filtering on a transparent canvas, and returns it as PNG bytes. Shared by the tray image
    /// and every entry of a written .ico, so both surfaces scale the same way.
    /// </summary>
    private static byte[] ResizeToPng(System.Drawing.Bitmap bitmap, int size)
    {
        using var resized = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(resized))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(bitmap, 0, 0, size, size);
        }

        using var pngStream = new MemoryStream();
        resized.Save(pngStream, ImageFormat.Png);
        return pngStream.ToArray();
    }
}
