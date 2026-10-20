using System.Runtime.InteropServices;
using static LauncherShortcut.NativeMethods;

namespace LauncherShortcut;

/// <summary>
/// A real foreground owner for Windows confirmation, with launcher identity and one short reminder.
/// It has no action buttons. Read-only identity queries use the same bounded message pump unshown.
/// </summary>
internal sealed partial class PinWindow
{
    private const string ClassName = "LittleLauncher.PinProgress";
    // Keep frame styles for DWM's shadow/rounding, then remove the classic nonclient caption.
    private const uint WindowStyle = 0x82C40000; // WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_CLIPCHILDREN
    private static readonly WindowProc Procedure = WindowCallback;
    private static bool _registered;
    private readonly Action<nint, Action<int>> _ready;
    private readonly string? _iconPath;
    private readonly bool _visible;
    private readonly string _name;
    private nint _hwnd;
    private nint _icon;
    private bool _completed;
    private int _result;
    private GCHandle _handle;

    private PinWindow(string name, Action<nint, Action<int>> ready, string? iconPath, bool visible)
    {
        _name = name;
        _ready = ready;
        _iconPath = iconPath;
        _visible = visible;
    }

    /// <summary>Runs one top-level STA message pump; callback completion closes it before another identity is used.</summary>
    internal static int Run(string name, string? iconPath, bool visible, Action<nint, Action<int>> ready)
    {
        if (visible) TaskbarPinning.Log("pin-window: creating foreground card");
        EnsureClass();
        var window = new PinWindow(name, ready, iconPath, visible);
        window._handle = GCHandle.Alloc(window);
        try
        {
            window._hwnd = CreateWindowExW(0, ClassName, name, WindowStyle,
                0, 0, 320, 120, 0, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(window._handle));
            if (window._hwnd == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (visible)
            {
                TaskbarPinning.Log("pin-window: HWND created; applying appearance");
                window.RefreshAppearance();
                TaskbarPinning.Log("pin-window: appearance applied; laying out card");
                window.Layout(center: true);
                TaskbarPinning.Log("pin-window: layout ready; showing card");
                ShowWindow(window._hwnd, 1);
                SetForegroundWindow(window._hwnd);
            }
            bool queued = PostMessageW(window._hwnd, 0x8001, 0, 0);
            if (visible) TaskbarPinning.Log($"pin-window: ready queued={queued}; foreground={GetForegroundWindow() == window._hwnd}");
            if (!queued)
            {
                // No ready callback means no deadline timer. Exit through normal cleanup instead
                // of leaving a visible window waiting forever; preserve the attempted resources.
                return 3;
            }
            while (!window._completed && GetMessageW(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
            if (!window._completed) window._result = 3;
            return window._result;
        }
        finally
        {
            if (window._hwnd != 0) DestroyWindow(window._hwnd);
            if (window._icon != 0) DestroyIcon(window._icon);
            window.DisposeAppearance();
            window._handle.Free();
        }
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(Procedure),
            Instance = GetModuleHandleW(null),
            Cursor = LoadCursorW(0, 32512),
            Name = Marshal.StringToHGlobalUni(ClassName),
        };
        try
        {
            if (RegisterClassExW(ref definition) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        finally { Marshal.FreeHGlobal(definition.Name); }
    }

    private void Complete(int result)
    {
        _result = result;
        _completed = true;
        KillTimer(_hwnd, 1);
        DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private static nint WindowCallback(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == 0x0081 /* WM_NCCREATE */)
            {
                nint handle = Marshal.ReadIntPtr(lParam);
                SetWindowLongPtrW(hwnd, -21 /* GWLP_USERDATA */, handle);
            }
            nint data = GetWindowLongPtrW(hwnd, -21);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is PinWindow window)
            {
                if (message == 0x0083 /* WM_NCCALCSIZE */) return 0;
                if (message == 0x0084 /* WM_NCHITTEST */) return 2; // HTCAPTION: draggable, never resizable.
                if (message == 0x00A3 /* WM_NCLBUTTONDBLCLK */) return 0;
                if (message == 0x0112 /* WM_SYSCOMMAND */ &&
                    ((wParam & 0xFFF0) == 0xF000 || (wParam & 0xFFF0) == 0xF030)) return 0;
                if (message == 0x0010 /* WM_CLOSE */)
                {
                    // Keep the request timer alive. Its bounded cancellation path owns completion.
                    return 0;
                }
                if (message == 0x0002 /* WM_DESTROY */)
                {
                    if (!window._completed)
                    {
                        window._completed = true;
                        window._result = 3;
                    }
                    window._hwnd = 0;
                }
                if (message == 0x8001)
                {
                    if (window._visible) TaskbarPinning.Log("pin-window: ready callback entered");
                    window._ready(hwnd, window.Complete);
                    return 0;
                }
                if (window._visible && window.HandleAppearanceMessage(message, wParam, lParam, out nint result))
                {
                    return result;
                }
                if (message == 0x02E0 /* WM_DPICHANGED */)
                {
                    var bounds = Marshal.PtrToStructure<Rect>(lParam);
                    SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x14);
                    window.Layout(center: false);
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            // Native callbacks cannot propagate exceptions through user32. Complete without success.
            TaskbarPinning.Log($"pin-window: callback failed message=0x{message:X4}; hr=0x{ex.HResult:X8}; {ex.Message}");
            nint data = GetWindowLongPtrW(hwnd, -21);
            if (data != 0 && GCHandle.FromIntPtr(data).Target is PinWindow window) window.Complete(3);
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
