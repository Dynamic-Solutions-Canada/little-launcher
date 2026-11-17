using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using System.Linq;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>
/// SettingsWindow — the main user-facing settings UI (WinUI 3).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? _instance;
    private readonly MainWindow? _owner;
    private IntPtr _hIconSmall;
    private IntPtr _hIconBig;


    public SettingsWindow(MainWindow owner)
    {
        if (_instance != null)
        {
            SetForegroundWindow(WindowNative.GetWindowHandle(_instance));
            Close();
            return;
        }

        _owner = owner;
        InitializeComponent();
        _instance = this;
        ShowResetNoticeIfPending();
        // Only clear our own registration: after a language change the replacement registers
        // itself before this window's Closed event necessarily arrives.
        Closed += (s, e) =>
        {
            if (_instance == this)
            {
                _instance = null;
            }
        };

        RootNavigation.Loaded += (_, _) =>
        {
            LabelSettingsItem();
            RootNavigation.OpenPaneLength = NavigationPaneLength();
        };

        // Mica backdrop
        SystemBackdrop = new MicaBackdrop();

        // Configure title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set the window icon (titlebar, taskbar, Alt-Tab)
        var hwnd = WindowNative.GetWindowHandle(this);

        // Give this window its own AppUserModelID so the taskbar treats it
        // independently from the main (invisible) window. Without this, the
        // taskbar always uses the exe's embedded icon (WindowsAppSDK#2730).
        SetWindowAppUserModelId(hwnd, "LittleLauncher.Settings");

        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        string settingsIcon = Path.Combine(
            MainWindow.GetPhysicalAppDataDir(), "settings-icon.ico");
        if (!File.Exists(settingsIcon))
        {
            MainWindow.SaveSettingsIconToAppData();
        }

        string iconPath = File.Exists(settingsIcon)
            ? settingsIcon
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "LittleLauncher.ico");
        ApplyWindowIcon(hwnd, iconPath);
        SetAppWindowIcon(appWindow, iconPath);
        LoadTitleBarIcon(iconPath);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi / 96.0;
        var settings = SettingsManager.Current;

        if (IsUsableSavedGeometry(settings))
        {
            appWindow.Resize(new global::Windows.Graphics.SizeInt32(
                settings.SettingsWindowWidth, settings.SettingsWindowHeight));
            appWindow.Move(ClampToWorkArea(
                settings.SettingsWindowX, settings.SettingsWindowY,
                settings.SettingsWindowWidth, settings.SettingsWindowHeight));
        }
        else
        {
            // Default: 900x700 centered on cursor monitor
            int width = (int)(900 * scale);
            int height = (int)(700 * scale);
            appWindow.Resize(new global::Windows.Graphics.SizeInt32(width, height));

            GetCursorPos(out var cursorPt);
            var monitor = MonitorFromPoint(cursorPt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref mi))
            {
                int cx = mi.rcWork.Left + (mi.rcWork.Right - mi.rcWork.Left - width) / 2;
                int cy = mi.rcWork.Top + (mi.rcWork.Bottom - mi.rcWork.Top - height) / 2;
                appWindow.Move(new global::Windows.Graphics.PointInt32(cx, cy));
            }
        }

        // Navigate to home
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));

        // Apply saved theme to this window
        Classes.ThemeManager.ApplySavedTheme(this);

        // Restore maximized state
        if (settings.SettingsWindowMaximized)
        {
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        // Re-apply icon after WinUI finishes initializing (it can override WM_SETICON)
        Activated += (s, e) =>
        {
            if (_hIconBig != IntPtr.Zero)
            {
                var h = WindowNative.GetWindowHandle(this);
                SendMessage(h, WM_SETICON, ICON_SMALL, _hIconSmall);
                SendMessage(h, WM_SETICON, ICON_BIG, _hIconBig);
            }
        };

        Closed += SettingsWindow_Closed;
    }

    /// <summary>
    /// Shows winui-app-kit's "settings were reset" bar above the pages when this launch replaced
    /// an unreadable settings file (CONF-2, UI-15). Shown once: the defaults are already saved.
    /// </summary>
    /// <remarks>
    /// The kit labels the bar from its own shared catalogs. Until this app moves to the kit's
    /// language classes those read in English whatever the interface language.
    /// </remarks>
    private void ShowResetNoticeIfPending()
    {
        if (!SettingsManager.TakeResetNotice(out string? backupPath))
        {
            return;
        }

        var notice = WinUIAppKit.Settings.SettingsResetNotice.Create(backupPath);
        notice.Margin = new Thickness(16, 0, 16, 8);
        NoticeHost.Children.Add(notice);
    }

    /// <summary>
    /// Show the singleton settings window (create if needed, activate if exists).
    /// </summary>
    public static void ShowInstance(MainWindow owner)
    {
        if (_instance == null)
        {
            new SettingsWindow(owner).Activate();
        }
        else
        {
            SetForegroundWindow(WindowNative.GetWindowHandle(_instance));
        }
    }

    /// <summary>
    /// Replace the open Settings window with a new one showing <paramref name="pageType"/>, so
    /// every label is read again in the newly chosen language.
    /// </summary>
    /// <remarks>
    /// XAML text is resolved when an element is created, and the window's own navigation and
    /// title are part of it, so a fresh window is the only complete relabel. Closing saves the
    /// window's size and position first, so the replacement opens where this one was.
    /// </remarks>
    internal static void ReopenAfterLanguageChange(Type pageType)
    {
        if (_instance?._owner is not MainWindow owner)
        {
            return;
        }

        var current = _instance;
        _instance = null;
        current.Close();

        var replacement = new SettingsWindow(owner);
        replacement.Activate();
        replacement.NavigateTo(pageType);
    }

    /// <summary>
    /// Navigate to a specific page type (used from HomePage dashboard cards).
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        ContentFrame.Navigate(pageType);

        // Settings page uses built-in settings button
        if (pageType == typeof(SystemPage))
        {
            RootNavigation.SelectedItem = RootNavigation.SettingsItem;
            return;
        }

        // Update selected nav item
        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && GetPageTypeFromTag(tag) == pageType)
            {
                RootNavigation.SelectedItem = item;
                return;
            }
        }
        foreach (var item in RootNavigation.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && GetPageTypeFromTag(tag) == pageType)
            {
                RootNavigation.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>
    /// Labels NavigationView's built-in Settings entry in the chosen interface language.
    /// </summary>
    /// <remarks>
    /// WinUI words that entry from its own resources, which follow the OS and package languages
    /// rather than the language chosen on the System page. The entry exists only once the
    /// control's template has been applied, hence the call from <c>Loaded</c>.
    /// </remarks>
    private void LabelSettingsItem()
    {
        if (RootNavigation.SettingsItem is NavigationViewItem settingsItem)
        {
            string label = UiText.Get("common.settings");
            settingsItem.Content = label;

            // NavigationView sets its own accessible name on this item in the Windows language,
            // which would otherwise stay English for screen readers after an in-app change.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, label);
        }
    }

    /// <summary>
    /// The open navigation pane width that fits the longest label in the current language.
    /// </summary>
    /// <remarks>
    /// The pane is fixed-width and its labels do not wrap, so a width chosen for English cuts
    /// off longer languages ("Sincronización en la nube"). Each label is measured at the item's
    /// font, plus the icon column and padding. Never narrower than the original 180, nor so wide
    /// that it crowds the page.
    /// </remarks>
    /// <returns>The pane width in effective pixels.</returns>
    private double NavigationPaneLength()
    {
        const double minimum = 180;
        const double maximum = 320;
        const double iconAndPadding = 88;

        var labels = RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .Append(RootNavigation.SettingsItem as NavigationViewItem)
            .Select(item => item?.Content as string)
            .Where(label => !string.IsNullOrEmpty(label));

        var probe = new TextBlock { FontSize = 14 };
        double widest = 0;
        foreach (string? label in labels)
        {
            probe.Text = label;
            probe.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, probe.DesiredSize.Width);
        }

        return Math.Clamp(widest + iconAndPadding, minimum, maximum);
    }

    internal MainWindow? GetOwner() => _owner;

    internal static SettingsWindow? GetCurrent() => _instance;

    /// <summary>The currently displayed page, if any.</summary>
    internal object? CurrentPage => ContentFrame?.Content;

    /// <summary>
    /// Re-reads the settings icon (app icon + gear overlay) and applies it to this window.
    /// Called when the tray icon mode or OS theme changes.
    /// </summary>
    internal void RefreshIcon()
    {
        string settingsIcon = Path.Combine(
            MainWindow.GetPhysicalAppDataDir(), "settings-icon.ico");
        if (!File.Exists(settingsIcon))
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        ApplyWindowIcon(hwnd, settingsIcon);
        SetAppWindowIcon(appWindow, settingsIcon);
        LoadTitleBarIcon(settingsIcon);
    }

    /// <summary>
    /// Sets the AppWindow icon via the HICON → IconId interop path.
    /// This is the documented way to update titlebar + taskbar + Alt-Tab.
    /// </summary>
    private static void SetAppWindowIcon(AppWindow appWindow, string icoPath)
    {
        // Load at native size (0,0) so the OS picks the best resolution
        var hIcon = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);
        if (hIcon == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var iconId = Microsoft.UI.Win32Interop.GetIconIdFromIcon(hIcon);
            appWindow.SetIcon(iconId);
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Sets both ICON_SMALL and ICON_BIG on the HWND via WM_SETICON.
    /// Keeps HICON handles alive as instance fields so the taskbar retains them.
    /// </summary>
    private void ApplyWindowIcon(IntPtr hwnd, string icoPath)
    {
        // Clean up previous handles
        if (_hIconSmall != IntPtr.Zero) { DestroyIcon(_hIconSmall); _hIconSmall = IntPtr.Zero; }
        if (_hIconBig != IntPtr.Zero) { DestroyIcon(_hIconBig); _hIconBig = IntPtr.Zero; }

        _hIconSmall = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        _hIconBig = LoadImage(IntPtr.Zero, icoPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        if (_hIconSmall != IntPtr.Zero)
        {
            SendMessage(hwnd, WM_SETICON, ICON_SMALL, _hIconSmall);
        }

        if (_hIconBig != IntPtr.Zero)
        {
            SendMessage(hwnd, WM_SETICON, ICON_BIG, _hIconBig);
        }
    }

    /// <summary>
    /// Loads the icon into the custom titlebar Image element.
    /// </summary>
    private void LoadTitleBarIcon(string icoPath)
    {
        try
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(icoPath));
        }
        catch { /* fallback: leave empty */ }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type? pageType = null;
        if (args.IsSettingsSelected)
        {
            pageType = typeof(SystemPage);
        }
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            pageType = GetPageTypeFromTag(tag);
        }

        if (pageType == null)
        {
            return;
        }

        // Don't re-navigate if we're already on this page type
        if (ContentFrame.Content?.GetType() == pageType)
        {
            return;
        }

        ContentFrame.Navigate(pageType);
    }

    private static Type? GetPageTypeFromTag(string tag) => tag switch
    {
        "HomePage" => typeof(HomePage),
        "LaunchersPage" => typeof(LaunchersPage),
        "SyncPage" => typeof(SyncPage),
        "SystemPage" => typeof(SystemPage),
        "PromotedAppsPage" => typeof(TechnicallyReal.Promo.PromotedAppsPage),
        "AboutPage" => typeof(AboutPage),
        _ => null
    };

    /// <summary>
    /// Navigate to the Launchers page and auto-open the settings dialog for the given launcher.
    /// </summary>
    public void NavigateToLauncherSettings(Launcher launcher)
    {

        // If LaunchersPage is already displayed, call the dialog directly
        if (ContentFrame.Content is LaunchersPage existingPage)
        {
            _ = existingPage.ShowLauncherSettingsDialogPublic(launcher);
            return;
        }

        // Set pending BEFORE selecting the nav item, because SelectedItem
        // triggers SelectionChanged → Navigate synchronously, which creates
        // the LaunchersPage instance that checks PendingSettingsLauncher.
        LaunchersPage.PendingSettingsLauncher = launcher;

        foreach (var item in RootNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == "LaunchersPage")
            {
                RootNavigation.SelectedItem = item;
                break;
            }
        }

        // If SelectionChanged didn't navigate (e.g. already selected), force it
        if (ContentFrame.Content is not LaunchersPage)
        {
            ContentFrame.Navigate(typeof(LaunchersPage));
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs e)
    {
        // Save window state (size, position, maximized)
        var hwnd = WindowNative.GetWindowHandle(this);
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(wndId);
        var settings = SettingsManager.Current;

        bool isMaximized = appWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Maximized;
        settings.SettingsWindowMaximized = isMaximized;

        // A minimized window reports the placeholder rect Windows parks it in - about 237x39 at
        // (-32000, -32000) - and saving that means the next open restores a window a few pixels
        // tall, off every screen. The maximized case above is excluded for the same reason: neither
        // is the size the user chose. `IsIconic` rather than the presenter's state, because the
        // window can be minimized by the shell (a taskbar click, Win+D) without the presenter being
        // asked.
        bool isMinimized = IsIconic(hwnd);

        if (!isMaximized && !isMinimized)
        {
            settings.SettingsWindowX = appWindow.Position.X;
            settings.SettingsWindowY = appWindow.Position.Y;
            settings.SettingsWindowWidth = appWindow.Size.Width;
            settings.SettingsWindowHeight = appWindow.Size.Height;
        }

        SettingsManager.SaveSettings();
        // Refresh tray icons in case launchers were added, removed, or renamed
        MainWindow.Current?.RefreshTrayIcons();
        // Flush any pending sync and upload immediately so the server
        // has the latest settings even if the app is killed shortly after.
        AutoSyncService.FlushPendingUpload();
    }

    /// <summary>The smallest stored size worth restoring, in physical pixels.</summary>
    /// <remarks>
    /// Well under any size a person would drag this window to, and well over the placeholder rect a
    /// minimized window reports. It is a corruption check, not a minimum size.
    /// </remarks>
    private const int MinRestorableWidth = 400;
    private const int MinRestorableHeight = 300;

    /// <summary>Whether the stored geometry is worth restoring at all.</summary>
    /// <remarks>
    /// Settings files already carry a bad size on any machine that closed this window while it was
    /// minimized, so rejecting one is what repairs them: the default path runs instead, and the
    /// next ordinary close writes a real size over it.
    /// </remarks>
    private static bool IsUsableSavedGeometry(ViewModels.UserSettings settings) =>
        settings.SettingsWindowWidth >= MinRestorableWidth &&
        settings.SettingsWindowHeight >= MinRestorableHeight;

    /// <summary>
    /// Nudges a stored position back onto a screen, so a window saved on a monitor that has since
    /// been unplugged still comes back somewhere the user can see it.
    /// </summary>
    private static global::Windows.Graphics.PointInt32 ClampToWorkArea(int x, int y, int width, int height)
    {
        try
        {
            var origin = new POINT { X = x, Y = y };
            IntPtr monitor = MonitorFromPoint(origin, MONITOR_DEFAULTTONEAREST);

            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return new global::Windows.Graphics.PointInt32(x, y);
            }

            var work = info.rcWork;
            int left = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
            int top = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

            return new global::Windows.Graphics.PointInt32(left, top);
        }
        catch
        {
            return new global::Windows.Graphics.PointInt32(x, y);
        }
    }
}
