using Microsoft.Win32;
using System.Runtime.InteropServices;
using static LauncherShortcut.NativeMethods;

namespace LauncherShortcut;

/// <summary>Theme, DPI layout and alpha-icon drawing for the transient native pin card.</summary>
internal sealed partial class PinWindow
{
    private const string PromptReminder = "Look for the Windows pinning prompt.";
    private nint _background;
    private nint _title;
    private nint _description;
    private nint _titleFont;
    private nint _bodyFont;
    private uint _titleColor;
    private uint _bodyColor;
    private int _iconLeft;
    private int _iconTop;
    private int _iconSize;

    /// <summary>Uses the system high-contrast palette before considering the normal app theme.</summary>
    private void RefreshAppearance()
    {
        var contrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        // SPI_GETHIGHCONTRAST returns a borrowed DefaultScheme pointer. WinForms explicitly
        // corrects Learn's ownership note; freeing this system pointer corrupts the process heap.
        // https://github.com/dotnet/winforms/blob/189b209933b2e244748a97e66b30be2f8d11b9e4/src/System.Private.Windows.Core/src/Windows/Win32/PInvokeCore.SystemParametersInfo.cs
        bool highContrast = SystemParametersInfoW(0x0042 /* SPI_GETHIGHCONTRAST */, contrast.Size,
            ref contrast, 0) && (contrast.Flags & 1) != 0;

        bool dark = false;
        uint background;
        int border;
        if (highContrast)
        {
            background = GetSysColor(5 /* COLOR_WINDOW */);
            _titleColor = GetSysColor(8 /* COLOR_WINDOWTEXT */);
            _bodyColor = _titleColor;
            border = unchecked((int)GetSysColor(6 /* COLOR_WINDOWFRAME */));
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            background = dark ? 0x00252525u : 0x00FAFAFAu;
            _titleColor = dark ? 0x00F5F5F5u : 0x001A1A1Au;
            _bodyColor = dark ? 0x00C4C4C4u : 0x005F5F5Fu;
            border = dark ? 0x00474747 : 0x00D6D6D6;
        }
        nint brush = CreateSolidBrush(background);
        if (brush == 0) throw new InvalidOperationException("Windows could not create the pin card background.");
        if (_background != 0) DeleteObject(_background);
        _background = brush;

        // Match the existing flyout's DWM-rounded frame and shadow without a classic title strip.
        int darkMode = dark ? 1 : 0;
        int corners = highContrast ? 1 /* DWMWCP_DONOTROUND */ : 2 /* DWMWCP_ROUND */;
        DwmSetWindowAttribute(_hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corners, sizeof(int));
        DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        InvalidateCard();
    }

    /// <summary>
    /// Fits one launcher title and a short prompt reminder beside its icon. Native STATIC text
    /// keeps both strings accessible; the caption remains available to the shell without duplication.
    /// </summary>
    private void Layout(bool center)
    {
        if (!_visible) return;
        uint dpi = GetDpiForWindow(_hwnd);
        int Scale(int dips) => (int)Math.Round(dips * dpi / 96d);
        int width = Scale(420);
        int height = Scale(112);
        int left = 0;
        int top = 0;
        uint flags = 0x0034; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
        if (center)
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfoW(MonitorFromWindow(GetForegroundWindow(), 2), ref monitor);
            left = monitor.Work.Left + (monitor.Work.Right - monitor.Work.Left - width) / 2;
            top = monitor.Work.Top + (monitor.Work.Bottom - monitor.Work.Top - height) / 2;
        }
        else
        {
            flags |= 0x0002; // Keep the suggested WM_DPICHANGED position.
        }
        SetWindowPos(_hwnd, 0, left, top, width, height, flags);
        if (GetDpiForWindow(_hwnd) != dpi)
        {
            // Initial centering can cross monitors and synchronously deliver WM_DPICHANGED.
            // Do not overwrite that new layout with dimensions from the creation monitor.
            Layout(center);
            return;
        }

        if (_title == 0)
        {
            const string prefix = "Little Launcher - ";
            string title = _name.StartsWith(prefix, StringComparison.Ordinal) ? _name[prefix.Length..] : _name;
            _title = CreateWindowExW(0, "STATIC", title, 0x50004080 /* SS_ENDELLIPSIS | SS_NOPREFIX */,
                0, 0, 1, 1, _hwnd, 0, 0, 0);
            _description = CreateWindowExW(0, "STATIC", PromptReminder, 0x50000080 /* SS_NOPREFIX */,
                0, 0, 1, 1, _hwnd, 0, 0, 0);
            if (_title == 0 || _description == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        SetWindowPos(_title, 0, Scale(80), Scale(27), Scale(316), Scale(26), 0x14);
        SetWindowPos(_description, 0, Scale(80), Scale(57), Scale(316), Scale(36), 0x14);
        ReplaceFonts(Scale(18), Scale(14));
        _iconLeft = Scale(24);
        _iconSize = Scale(40);
        _iconTop = (height - _iconSize) / 2;
        if (_iconPath != null && File.Exists(_iconPath))
        {
            nint icon = LoadImageW(0, _iconPath, 1 /* IMAGE_ICON */, _iconSize, _iconSize, 0x10 /* LR_LOADFROMFILE */);
            SendMessageW(_hwnd, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, icon);
            SendMessageW(_hwnd, 0x0080, 0 /* ICON_SMALL */, icon);
            if (_icon != 0) DestroyIcon(_icon);
            _icon = icon;
        }
        InvalidateCard();
    }

    /// <summary>Replaces DPI-scaled Segoe UI fonts only after both new handles are ready.</summary>
    private void ReplaceFonts(int titleHeight, int bodyHeight)
    {
        nint title = CreateFontW(-titleHeight, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        nint body = CreateFontW(-bodyHeight, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        if (title == 0 || body == 0)
        {
            if (title != 0) DeleteObject(title);
            if (body != 0) DeleteObject(body);
            throw new InvalidOperationException("Windows could not create the pin card fonts.");
        }
        SendMessageW(_title, 0x0030 /* WM_SETFONT */, title, 1);
        SendMessageW(_description, 0x0030, body, 1);
        if (_titleFont != 0) DeleteObject(_titleFont);
        if (_bodyFont != 0) DeleteObject(_bodyFont);
        _titleFont = title;
        _bodyFont = body;
    }

    /// <summary>Composites original icon alpha onto the card; no static-icon background or file edits.</summary>
    private void Paint()
    {
        nint dc = BeginPaint(_hwnd, out var paint);
        try
        {
            FillBackground(dc);
            if (_icon != 0)
                DrawIconEx(dc, _iconLeft, _iconTop, _icon, _iconSize, _iconSize, 0, 0, 3 /* DI_NORMAL */);
        }
        finally { EndPaint(_hwnd, ref paint); }
    }

    private void FillBackground(nint dc)
    {
        if (_background != 0 && GetClientRect(_hwnd, out var bounds)) FillRect(dc, ref bounds, _background);
    }

    /// <summary>Handles native drawing and appearance changes without touching the pin-request timer.</summary>
    private bool HandleAppearanceMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (message)
        {
            case 0x000F: // WM_PAINT
                Paint();
                return true;
            case 0x0014: // WM_ERASEBKGND
                FillBackground(wParam);
                result = 1;
                return true;
            case 0x0138: // WM_CTLCOLORSTATIC
                SetBkMode(wParam, 1 /* TRANSPARENT */);
                SetTextColor(wParam, lParam == _title ? _titleColor : _bodyColor);
                result = _background;
                return true;
            case 0x0015: // WM_SYSCOLORCHANGE
            case 0x001A: // WM_SETTINGCHANGE
            case 0x031A: // WM_THEMECHANGED
                RefreshAppearance();
                return true;
            default:
                return false;
        }
    }

    private void InvalidateCard()
    {
        InvalidateRect(_hwnd, 0, true);
        if (_title != 0) InvalidateRect(_title, 0, true);
        if (_description != 0) InvalidateRect(_description, 0, true);
    }

    /// <summary>Called only after HWND teardown, when child controls no longer reference the fonts.</summary>
    private void DisposeAppearance()
    {
        if (_titleFont != 0) DeleteObject(_titleFont);
        if (_bodyFont != 0) DeleteObject(_bodyFont);
        if (_background != 0) DeleteObject(_background);
    }
}
