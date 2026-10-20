using System.Runtime.InteropServices;

namespace LauncherShortcut;

/// <summary>Native entry points for the companion's optional desktop pin request.</summary>
internal static partial class NativeMethods
{
    internal delegate void TimerProc(nint hwnd, uint message, nuint timer, uint time);

    [LibraryImport("combase.dll")]
    internal static partial int RoInitialize(uint type);

    [LibraryImport("combase.dll")]
    internal static partial void RoUninitialize();

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string source, uint length, out nint value);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(nint value);

    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(nint name, in Guid iid, out nint factory);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [LibraryImport("kernel32.dll")]
    internal static partial int GetCurrentPackageFullName(ref uint length, nint name);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nuint SetTimer(nint hwnd, nuint id, uint interval, TimerProc callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndDialog(nint hwnd, nint result);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDlgItem(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial nint GetSystemMenu(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [LibraryImport("user32.dll")]
    internal static partial uint EnableMenuItem(nint menu, uint item, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [LibraryImport("user32.dll", EntryPoint = "SetDlgItemTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDlgItemText(nint hwnd, int id, string text);

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(uint change, uint flags, nint item1, nint item2);
    internal delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background, MenuName, Name, SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins { internal int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HighContrast { internal uint Size, Flags; internal nint DefaultScheme; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PaintData
    {
        internal nint DeviceContext;
        internal int Erase;
        internal Rect Bounds;
        internal int Restore, IncrementalUpdate;
        internal fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo { internal int Size; internal Rect Bounds, Work; internal uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? module);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(ref WindowClass definition);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowExW(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowLongPtrW(nint hwnd, int index);
    [LibraryImport("user32.dll")]
    internal static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out Message message, nint hwnd, uint first, uint last);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref Message message);
    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(ref Message message);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")]
    internal static partial nint LoadCursorW(nint instance, nint name);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
    [LibraryImport("user32.dll")]
    internal static partial nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint value);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeout, uint characterSet, uint outputPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint hwnd, out PaintData paint);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint hwnd, ref PaintData paint);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hwnd, out Rect bounds);
    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint dc, ref Rect bounds, nint brush);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height,
        uint animationFrame, nint background, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(nint hwnd, nint bounds, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll")]
    internal static partial uint GetSysColor(int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(uint action, uint parameter, ref HighContrast value, uint flags);
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string aumid);
    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(nint variant);
}
