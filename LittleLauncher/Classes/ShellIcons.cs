using System.Runtime.InteropServices;
using System.Threading;
using static LittleLauncher.Classes.NativeMethods;

namespace LittleLauncher.Classes;

/// <summary>
/// Extracts a shell item's icon (e.g. a <c>shell:AppsFolder\{AUMID}</c> app, or a
/// plain file path) as top-down 32bpp BGRA pixels, via IShellItemImageFactory.
/// Used by the unified add/edit-item app/PWA picker to stream list icons into memory
/// without touching disk. All interop lives in <see cref="NativeMethods"/>.
/// </summary>
internal static class ShellIcons
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // GetImage returns E_PENDING while the shell rasterizes the icon on a background
    // thread (common for PWA / Store tiles whose image isn't in the icon cache yet).
    private const int E_PENDING = unchecked((int)0x8000000A);

    public sealed record IconPixels(byte[] Bgra, int Width, int Height);

    public static IconPixels? Extract(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory == null)
        {
            Logger.Debug($"ShellIcons: SHCreateItemFromParsingName 0x{hr:X8} for {parsingName}");
            return null;
        }

        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            // Retry on E_PENDING: the first request kicks off async extraction, so an
            // uncached icon (e.g. a freshly-seen PWA) needs a few attempts to resolve.
            var sz = new SIZE { cx = size, cy = size };
            for (int attempt = 0; ; attempt++)
            {
                hr = factory.GetImage(sz, 0, out hBitmap);
                if (hr == 0 && hBitmap != IntPtr.Zero)
                {
                    break;
                }

                if (hr == E_PENDING && attempt < 15) { Thread.Sleep(40); continue; }
                Logger.Debug($"ShellIcons: GetImage 0x{hr:X8} (attempt {attempt}) for {parsingName}");
                return null;
            }

            GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            if (w <= 0 || h <= 0)
            {
                Logger.Debug($"ShellIcons: empty bitmap {w}x{h} for {parsingName}");
                return null;
            }

            // BitBlt the source HBITMAP into a top-down 32bpp DIB we control, to
            // normalize orientation/format, then copy out the BGRA bytes.
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // negative = top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                }
            };

            IntPtr hdcSrc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr hdcDst = CreateCompatibleDC(IntPtr.Zero);
            IntPtr hDib = CreateDIBSection(hdcDst, ref bmi, DIB_RGB_COLORS, out IntPtr dibBits, IntPtr.Zero, 0);
            try
            {
                if (hDib == IntPtr.Zero || dibBits == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr oldSrc = SelectObject(hdcSrc, hBitmap);
                IntPtr oldDst = SelectObject(hdcDst, hDib);
                BitBlt(hdcDst, 0, 0, w, h, hdcSrc, 0, 0, SRCCOPY);
                SelectObject(hdcSrc, oldSrc);
                SelectObject(hdcDst, oldDst);

                var bytes = new byte[w * h * 4];
                Marshal.Copy(dibBits, bytes, 0, bytes.Length);
                return new IconPixels(bytes, w, h);
            }
            finally
            {
                if (hDib != IntPtr.Zero)
                {
                    DeleteObject(hDib);
                }

                DeleteDC(hdcSrc);
                DeleteDC(hdcDst);
            }
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            Marshal.ReleaseComObject(factory);
        }
    }
}
