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

/// <summary>
/// MainWindow — the invisible host window for the Little Launcher (WinUI 3).
///
/// Architecture notes:
///   - This window is never actually displayed.
///   - Its sole purpose is to own the tray icon.
///   - App.OnLaunched claims the "LittleLauncher" single-instance mutex (winui-app-kit) first.
///   - A registered window message lets a second instance or LauncherShortcut
///     signal the first to show the flyout via PostMessage.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The running MainWindow instance. Set in the constructor; null before construction.</summary>
    public static new MainWindow? Current { get; private set; }

    /// <summary>
    /// True when running as an MSIX-packaged app (has package identity).
    /// Cached once at startup — never changes during the process lifetime.
    /// </summary>
    internal static bool IsPackaged { get; } = DetectPackagedContext();

    private static bool DetectPackagedContext()
    {
        try
        {
            // Accessing Package.Current throws InvalidOperationException in unpackaged apps.
            _ = global::Windows.ApplicationModel.Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the Start Menu Programs folder path.
    /// In MSIX this is VFS-redirected, but the companion exe's relaunch
    /// properties handle taskbar pinning identity — the shortcuts here are
    /// just for the shell's AUMID index (best-effort).
    /// </summary>
    internal static string GetStartMenuProgramsDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs");
    }

    /// <summary>
    /// The Start Menu Programs folder as it exists on disk, which is what the shell indexes.
    /// </summary>
    /// <remarks>
    /// <see cref="GetStartMenuProgramsDir"/> goes through <c>SpecialFolder.StartMenu</c>, which
    /// MSIX VFS-redirects — anything written through it inside a packaged build lands where only
    /// that package can see it, so the shell never indexes it. Any shortcut that has to be
    /// *found* by Windows must be written here instead. Same rule as
    /// <see cref="GetPhysicalAppDataDir"/>, for the same reason.
    /// </remarks>
    internal static string GetPhysicalStartMenuProgramsDir() =>
        IsPackaged ? GetLegacySharedStartMenuProgramsDir() : GetStartMenuProgramsDir();

    internal static string GetLegacySharedStartMenuProgramsDir()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
    }

    /// <summary>
    /// Returns the physical (non-VFS) path for the app's data directory.
    /// In MSIX, writes to %AppData%\LittleLauncher are VFS-redirected to a
    /// package-specific folder that external processes (shell, companion exe)
    /// cannot see. This method returns the real redirect target path so that
    /// external references (.lnk IconLocation, etc.) point to existing files.
    /// </summary>
    internal static string GetPhysicalAppDataDir()
    {
        if (IsPackaged)
        {
            return Path.Combine(
                global::Windows.Storage.ApplicationData.Current.RoamingFolder.Path,
                "LittleLauncher");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LittleLauncher");
    }

    /// <summary>Per-launcher registered window message IDs (key = Launcher.Id).</summary>
    private readonly Dictionary<string, int> _wmShowFlyoutPerLauncher = new();

    /// <summary>
    /// Per-launcher "launch one of my entries" message IDs (key = Launcher.Id), sent by the
    /// companion exe when a taskbar jump list task is clicked.
    /// </summary>
    /// <remarks>
    /// A separate message rather than a parameter on the show-flyout one, because the two carry
    /// entirely different payloads: that one carries the anchor point the flyout opens at, and
    /// this one carries which entry to launch. A message has only two words to say it in.
    /// </remarks>
    private readonly Dictionary<string, int> _wmLaunchItemPerLauncher = new();

    /// <summary>
    /// Per-launcher "run one of my commands" message IDs (key = Launcher.Id), sent by the companion
    /// exe for the entries below the separator on a taskbar jump list.
    /// </summary>
    private readonly Dictionary<string, int> _wmLauncherActionPerLauncher = new();
    private static int _wmShowSettings;
    private int _wmTrayCallback;
    private IntPtr _hwnd;
    private SUBCLASSPROC? _wndProcDelegate;

    /// <summary>Per-launcher native tray icons (key = Launcher.Id).</summary>
    private readonly Dictionary<string, TrayIconEntry> _trayIcons = new();
    private uint _nextIconId = 1;
    private readonly global::Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _lastDarkTheme;

    /// <summary>Daily timer that refreshes stale auto-fetched item icons while the app runs.</summary>
    private System.Threading.Timer? _iconRefreshTimer;

    public MainWindow()
    {
        // The single instance is claimed in App.OnLaunched, before settings load.
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Current = this;

        Logger.Info("Starting Little Launcher MainWindow");

        // Register the window message for cross-process flyout signaling.
        // Per-launcher show-flyout messages are registered in CreateTrayIconForLauncher().
        _wmShowSettings = RegisterWindowMessage("LittleLauncher_ShowSettings");

        // Also register the legacy (no-launcher) message for backward compat with old pinned shortcuts,
        // and store it under an empty key so WndProc can route it to the first launcher.
        int legacyMsg = RegisterWindowMessage("LittleLauncher_ShowFlyout");
        _wmShowFlyoutPerLauncher[string.Empty] = legacyMsg;

        // Hide from Alt-Tab and make invisible
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        // Move off-screen
        var appWindow = GetAppWindow();
        appWindow.Move(new global::Windows.Graphics.PointInt32(-9999, -9999));
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(1, 1));

        // Keep the host window open when other windows close, which would otherwise end the app.
        // It closes only during an orderly exit (ExitApp).
        appWindow.Closing += (s, e) => e.Cancel = !_exiting;

        // Hook WndProc for cross-process PostMessage IPC
        _wndProcDelegate = WndProc;
        SetWindowSubclass(_hwnd, _wndProcDelegate, 0, 0);

        // Deferred init: apply theme, tray icon
        ApplyDeferredInit();
        EnsureStartMenuShortcuts();
        EnsureFlyoutShortcut();

        // After EnsureFlyoutShortcut, which is what deploys the companion exe these shortcuts
        // target — on a first run the other order writes shortcuts pointing at nothing.
        Services.StartMenuShortcutService.Sync(SettingsManager.Current.Launchers);

        CleanUpStaleIconFiles();
        LauncherPanels.WarmUp(this, SettingsManager.Current.Launchers);

        // After EnsureFlyoutShortcut too: every jump list task runs the companion exe, so there
        // is nothing to publish until it is on disk.
        Services.JumpListService.Initialize(DispatcherQueue);
        _ = StartAutoSyncAsync();
        _ = FetchMissingIconsOnStartupAsync();

        // The app stays resident in the tray for weeks, so a launch-only refresh
        // isn't enough. Check daily; downloads only happen once a cached icon
        // is older than FaviconService.IconMaxAge. Marshal to the UI thread so
        // IconPath change notifications are safe for bindings.
        _iconRefreshTimer = new System.Threading.Timer(
            _ => App.MainDispatcherQueue.TryEnqueue(() => _ = RefreshStaleIconsPeriodicAsync()),
            null,
            TimeSpan.FromHours(24),
            TimeSpan.FromHours(24));

        RegisterForNotifications();
        ShowUpgradeNoticeToast();
        _ = CheckForUpdateOnStartupAsync();

        // Tell Windows to include --silent when auto-restarting the app
        // (e.g. "Restart apps" after sign-in). Without this, Windows
        // relaunches the exe with no args, which opens the Settings window.
        RegisterApplicationRestart("--silent", 0);

        // Listen for OS theme changes to refresh icons
        _lastDarkTheme = Classes.ThemeManager.IsDarkTheme();
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;

        // A launch that had to reset unreadable settings opens Settings even when silent, so the
        // notice is seen: the defaults are now saved, and the next launch could not tell.
        if (!IsSilentLaunch() || SettingsManager.HasPendingResetNotice)
        {
            SettingsWindow.ShowInstance(this);
        }
    }

    private AppWindow GetAppWindow()
    {
        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// Opens a launcher's panel from a context with no pointer position — a clicked toast.
    /// </summary>
    /// <remarks>
    /// Prefer the launcher's tray icon, then its taskbar button if the icon is unavailable.
    /// The cursor over a toast is only a last resort. Explicit placement settings still apply
    /// inside the panel's placement calculation.
    /// </remarks>
    internal void OpenLauncherPanel(string launcherId)
    {
        GetCursorPos(out var pt);
        if (!TryResolveTrayAnchorPoint(launcherId, pt.X, pt.Y, out int anchorX, out int anchorY))
        {
            TryResolveLauncherAnchorPoint(launcherId, pt.X, pt.Y, out anchorX, out anchorY);
        }

        DispatcherQueue.TryEnqueue(() => LauncherPanels.Toggle(this, anchorX, anchorY, launcherId));
    }

    /// <summary>
    /// Finish initialization that needs the window to be set up.
    /// </summary>
    private void ApplyDeferredInit()
    {
        // Apply theme
        ThemeManager.ApplySavedTheme(this);

        // Clear stale paths left by a different install type (e.g. MSIX → portable).
        // MSIX VFS-redirects %AppData% into the package's RoamingState folder; those
        // paths become dead after uninstalling the MSIX and switching to the portable build.
        MigrateStaleIconPaths();

        // ── Tray icons (one per launcher) ──────────────────────────
        SetupTrayIcons();

        var asm = typeof(MainWindow).Assembly.GetName();
        SettingsManager.Current.LastKnownVersion = $"v{asm.Version!.Major}.{asm.Version.Minor}.{asm.Version.Build}";

        // Ensure existing Windows startup registry entry includes --silent
        MigrateStartupRegistryEntry();
        _ = ApplyFirstRunStartupAsync();
    }

    private void OnSystemThemeChanged(global::Windows.UI.ViewManagement.UISettings sender, object args)
    {
        bool dark = Classes.ThemeManager.IsDarkTheme();
        if (dark == _lastDarkTheme)
        {
            return;
        }

        _lastDarkTheme = dark;

        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                RefreshLauncherIcon(launcher);
            }

            SaveSettingsIconToAppData();
            SettingsWindow.GetCurrent()?.RefreshIcon();
        });
    }

    private async Task StartAutoSyncAsync()
    {
        await AutoSyncService.SyncOnStartupAsync();
        AutoSyncService.Start();
    }
}
