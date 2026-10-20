using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Classes;

/// <summary>
/// Renders complete emoji sequences to transparent color bitmaps without a XAML tree.
/// Uses Windows DirectWrite shaping and Direct2D color glyphs on a software device, so it
/// also works on the background thread that prepares jump-list icons. No GPU is required.
/// </summary>
internal static unsafe class GlyphIconRenderer
{
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid WriteFactoryId = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");

    // Repeated tray, shortcut and toast writes ask for the same raster. Bound the cache to
    // 32 images (at most 8 MiB for the normal 256px icon) and return owned clones to callers.
    private static readonly Dictionary<(string Text, int Color, int Size, float FontSize), Bitmap> Cache = new();
    private static readonly object CacheLock = new();
    private const int CacheLimit = 32;

    /// <summary>
    /// Returns an owned premultiplied BGRA bitmap for the entire UTF-16 sequence, including
    /// surrogate pairs, variation selectors and ZWJ ligatures. Color glyphs retain the font's
    /// palette; <paramref name="foreground"/> only fills glyphs without intrinsic color.
    /// Native failures propagate to the existing icon-resolution error handler, never to GDI.
    /// </summary>
    internal static Bitmap RenderEmoji(string text, Color foreground, int size, float fontSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var key = (text, foreground.ToArgb(), size, fontSize);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return (Bitmap)cached.Clone();
            }

            using var rendered = RenderCore(text, foreground, size, fontSize);
            if (Cache.Count >= CacheLimit)
            {
                var oldest = Cache.First();
                Cache.Remove(oldest.Key);
                oldest.Value.Dispose();
            }
            Cache.Add(key, (Bitmap)rendered.Clone());
            return (Bitmap)rendered.Clone();
        }
    }

    /// <summary>Creates a temporary software rendering chain and releases every COM reference in reverse order.</summary>
    private static Bitmap RenderCore(string text, Color foreground, int size, float fontSize)
    {
        using var resources = new ComResources();
        // D3D_DRIVER_TYPE_WARP = 5, BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7.
        Check(D3D11CreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0x20,
            IntPtr.Zero, 0, 7, out var d3dDevice, out _, IntPtr.Zero));
        resources.Add(d3dDevice);
        Guid dxgiId = DxgiDeviceId;
        Check(Marshal.QueryInterface(d3dDevice, in dxgiId, out var dxgiDevice));
        resources.Add(dxgiDevice);
        Check(D2D1CreateDevice(dxgiDevice, IntPtr.Zero, out var device));
        resources.Add(device);
        IntPtr context = IntPtr.Zero;
        // ID2D1Device::CreateDeviceContext, slot 4 (after IUnknown and ID2D1Resource).
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(device, 4))(device, 0, &context));
        resources.Add(context);

        var target = CreateBitmap(context, size, options: 1); // TARGET
        resources.Add(target);
        var readable = CreateBitmap(context, size, options: 6); // CPU_READ | CANNOT_DRAW
        resources.Add(readable);
        Check(DWriteCreateFactory(0, in WriteFactoryId, out var writeFactory));
        resources.Add(writeFactory);
        var format = CreateTextFormat(writeFactory, fontSize);
        resources.Add(format);
        var brush = CreateBrush(context, foreground);
        resources.Add(brush);

        var layout = CreateTextLayout(writeFactory, format, text, size);
        resources.Add(layout);
        DrawSequence(context, target, layout, brush, size);
        // ID2D1Bitmap::CopyFromBitmap, slot 8. Null point/rect copies the entire target.
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)Slot(readable, 8))(
            readable, IntPtr.Zero, target, IntPtr.Zero));
        return CopyPixels(readable, size);
    }

    /// <summary>Allocates a 96-DPI premultiplied BGRA bitmap with target or CPU-read usage.</summary>
    private static IntPtr CreateBitmap(IntPtr context, int size, uint options)
    {
        var dimensions = new D2D_SIZE_U { Width = (uint)size, Height = (uint)size };
        var properties = new D2D_BITMAP_PROPERTIES1
        {
            Format = 87, // DXGI_FORMAT_B8G8R8A8_UNORM
            AlphaMode = 1, // D2D1_ALPHA_MODE_PREMULTIPLIED
            DpiX = 96, DpiY = 96, Options = options
        };
        IntPtr bitmap = IntPtr.Zero;
        // ID2D1DeviceContext::CreateBitmap, slot 57 (the Bitmap1 overload).
        Check(((delegate* unmanaged[Stdcall]<IntPtr, D2D_SIZE_U, IntPtr, uint, D2D_BITMAP_PROPERTIES1*, IntPtr*, int>)Slot(context, 57))(
            context, dimensions, IntPtr.Zero, 0, &properties, &bitmap));
        return bitmap;
    }

    /// <summary>Creates a centered, unwrapped emoji text format; DirectWrite shapes the full sequence.</summary>
    private static IntPtr CreateTextFormat(IntPtr factory, float fontSize)
    {
        IntPtr format = IntPtr.Zero;
        fixed (char* family = "Segoe UI Emoji", locale = "en-us")
        {
            // IDWriteFactory::CreateTextFormat, slot 15; regular weight/style/stretch.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, uint, uint, uint, float, char*, IntPtr*, int>)Slot(factory, 15))(
                factory, family, IntPtr.Zero, 400, 0, 5, fontSize, locale, &format));
        }
        try
        {
            // IDWriteTextFormat: text alignment center, paragraph alignment center, no wrapping.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(format, 3))(format, 2));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(format, 4))(format, 2));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(format, 5))(format, 1));
            return format;
        }
        catch
        {
            Marshal.Release(format);
            throw;
        }
    }

    /// <summary>Creates the fallback fill for monochrome characters in a color font.</summary>
    private static IntPtr CreateBrush(IntPtr context, Color foreground)
    {
        var color = new D2D_COLOR_F
        {
            R = foreground.R / 255f, G = foreground.G / 255f,
            B = foreground.B / 255f, A = foreground.A / 255f
        };
        IntPtr brush = IntPtr.Zero;
        // ID2D1RenderTarget::CreateSolidColorBrush, slot 8.
        Check(((delegate* unmanaged[Stdcall]<IntPtr, D2D_COLOR_F*, IntPtr, IntPtr*, int>)Slot(context, 8))(
            context, &color, IntPtr.Zero, &brush));
        return brush;
    }

    /// <summary>Shapes the entire sequence into an unwrapped layout, retaining visible ink outside its line box.</summary>
    private static IntPtr CreateTextLayout(IntPtr factory, IntPtr format, string text, int size)
    {
        IntPtr layout = IntPtr.Zero;
        fixed (char* characters = text)
        {
            // IDWriteFactory::CreateTextLayout, slot 18; string length is UTF-16 units.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, IntPtr, float, float, IntPtr*, int>)Slot(factory, 18))(
                factory, characters, (uint)text.Length, format, size, size, &layout));
        }
        return layout;
    }

    /// <summary>
    /// Fits the actual visible ink, including font overhang, inside the raster. Font size alone
    /// is not a bound: Segoe UI Emoji can draw below its centered layout box and get cut off.
    /// </summary>
    private static D2D_MATRIX_3X2_F GetInkTransform(IntPtr layout, int size)
    {
        DWRITE_OVERHANG_METRICS overhang = default;
        // IDWriteTextLayout::GetOverhangMetrics, slot 61. Negative values are inner whitespace.
        Check(((delegate* unmanaged[Stdcall]<IntPtr, DWRITE_OVERHANG_METRICS*, int>)Slot(layout, 61))(
            layout, &overhang));
        float left = -overhang.Left;
        float top = -overhang.Top;
        float width = size + overhang.Left + overhang.Right;
        float height = size + overhang.Top + overhang.Bottom;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The emoji text layout has no visible ink.");
        }

        // Reserve antialiasing/hinting space because DirectWrite metrics are in DIPs,
        // while the final visible pixel bounds can differ slightly after rasterization.
        float inset = Math.Max(1, size / 64f);
        float available = Math.Max(1, size - 2 * inset);
        float scale = Math.Min(1, Math.Min(available / width, available / height));
        return new D2D_MATRIX_3X2_F
        {
            M11 = scale,
            M22 = scale,
            Dx = (size - width * scale) / 2 - left * scale,
            Dy = (size - height * scale) / 2 - top * scale
        };
    }

    /// <summary>Draws fitted color glyphs onto transparent pixels and completes the batch before readback.</summary>
    private static void DrawSequence(IntPtr context, IntPtr target, IntPtr layout, IntPtr brush, int size)
    {
        var transform = GetInkTransform(layout, size);
        // SDK ABI slots: ID2D1DeviceContext::SetTarget, then ID2D1RenderTarget methods.
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(context, 74))(context, target);
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D_MATRIX_3X2_F*, void>)Slot(context, 30))(context, &transform);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Slot(context, 34))(context, 2); // grayscale antialiasing
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(context, 48))(context); // BeginDraw
        var clear = new D2D_COLOR_F();
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D_COLOR_F*, void>)Slot(context, 47))(context, &clear);
        // DrawTextLayout, slot 28: ENABLE_COLOR_FONT | NO_SNAP. Fit uses visible ink
        // metrics, so baseline snapping must not move it past the reserved inset.
        ((delegate* unmanaged[Stdcall]<IntPtr, D2D_POINT_F, IntPtr, IntPtr, uint, void>)Slot(context, 28))(
            context, default, layout, brush, 5);
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(context, 49))(
            context, IntPtr.Zero, IntPtr.Zero)); // EndDraw reports deferred draw errors.
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Slot(context, 74))(context, IntPtr.Zero);
    }

    /// <summary>Copies mapped rows respecting both native pitch and managed stride; preserves premultiplied alpha.</summary>
    private static Bitmap CopyPixels(IntPtr readable, int size)
    {
        D2D_MAPPED_RECT mapped = default;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, D2D_MAPPED_RECT*, int>)Slot(readable, 14))(
            readable, 1, &mapped)); // ID2D1Bitmap1::Map(READ)
        try
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    for (int row = 0; row < size; row++)
                    {
                        Buffer.MemoryCopy((byte*)mapped.Bits + row * mapped.Pitch,
                            (byte*)pixels.Scan0 + row * pixels.Stride, size * 4, size * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(pixels);
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        finally
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(readable, 15))(readable)); // Unmap
        }
    }

    /// <summary>
    /// Gets a COM ABI slot. Indices and signatures match Microsoft's WinSDK d2d1.h,
    /// d2d1_1.h and dwrite.h; the base IUnknown/resource methods count toward every index.
    /// Keeping calls explicit avoids an incomplete managed COM inheritance declaration.
    /// </summary>
    private static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    /// <summary>Keeps temporary COM ownership local to one render, including partial initialization failures.</summary>
    private sealed class ComResources : IDisposable
    {
        private readonly List<IntPtr> _references = new();
        internal void Add(IntPtr reference) => _references.Add(reference);
        public void Dispose()
        {
            for (int index = _references.Count - 1; index >= 0; index--)
            {
                Marshal.Release(_references[index]);
            }
        }
    }
}
