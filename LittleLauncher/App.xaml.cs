using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LittleLauncher;

/// <summary>
/// Application entry point (WinUI 3).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main UI thread's DispatcherQueue, captured at launch.
    /// Use this to marshal calls back to the UI thread from background threads.
    /// </summary>
    public static DispatcherQueue MainDispatcherQueue { get; private set; } = null!;

    public App()
    {
        // Name the data folder for the kit's shared NLog config before anything logs (LOG-1).
        // Permanent: it is the folder settings.json and the logs have always been in (KIT-2).
        WinUIAppKit.Logging.AppLogging.UseDataFolder("LittleLauncher");
        InitializeComponent();
    }

    /// <summary>The single-instance mutex. Permanent (KIT-2): changing it lets two copies run.</summary>
    internal const string MutexName = "LittleLauncher";

    /// <summary>
    /// The hidden host window's title, which a second launch and the companion look for. Permanent;
    /// it must match MainWindow.xaml's Title and LauncherShortcut's FindWindow call.
    /// </summary>
    internal const string HostWindowTitle = "Little Launcher Host";

    /// <summary>
    /// Start with Windows (TRAY-10): the manifest's startup task when packaged, the "Little Launcher"
    /// Run value when not. Both names are permanent (KIT-2).
    /// </summary>
    internal static readonly WinUIAppKit.Startup.StartupRegistration Startup = new("LittleLauncherStartup", "Little Launcher");

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // One running copy, claimed before settings load so a second launch exits without touching
        // the settings file (winui-app-kit gotcha).
        if (!WinUIAppKit.SingleInstance.TryClaim(MutexName))
        {
            // Default: show settings (Start Menu, double-click exe, etc.). The companion exe signals
            // flyouts itself and never starts a second LittleLauncher.exe while one is running.
            // A --silent second launch (login startup) just exits.
            string[] commandLine = Environment.GetCommandLineArgs();
            bool isSilent = commandLine.Length > 1
                && commandLine[1].Equals("--silent", StringComparison.OrdinalIgnoreCase);
            if (!isSilent)
            {
                WinUIAppKit.SingleInstance.SignalRunningInstance(HostWindowTitle, "LittleLauncher_ShowSettings");
            }

            Environment.Exit(0);
        }

        MainDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Must happen on the UI thread, before any WebView2 exists. WinUI 3 only calls
        // CoInitializeEx, and OLE drag and drop needs OleInitialize on top of it — without it a
        // hosted browser silently supports no dragging: not within the page, not from outside.
        int oleResult = NativeMethods.OleInitialize(IntPtr.Zero);
        if (oleResult < 0)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn("OleInitialize failed (0x{0:X}); drag and drop inside web flyouts will not work", oleResult);
        }

        // Log unhandled exceptions before the process dies
        AppDomain.CurrentDomain.UnhandledException += (sender, a) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                a.ExceptionObject as Exception,
                "Unhandled exception occurred");
            NLog.LogManager.Flush();
        };

        UnhandledException += (sender, e) =>
        {
            NLog.LogManager.GetCurrentClassLogger().Error(
                e.Exception,
                "Unhandled UI exception");
            NLog.LogManager.Flush();
            e.Handled = true;
        };

        // Restore settings before any window constructor accesses SettingsManager.Current
        SettingsManager.RestoreSettings();

        // Interface language: winui-app-kit's UiText over the shared catalogs, starting from the
        // saved choice (empty follows Windows). Settings changes it with SetOverride.
        WinUIAppKit.Localization.UiText.Configure(
            typeof(App).Assembly,
            Localization.AppLanguages.ResourcePrefix,
            Localization.AppLanguages.Supported,
            SettingsManager.Current.LanguageOverride);

        // The "Our other apps" page translates its own text and follows the app's language (PROMO-7).
        TechnicallyReal.Promo.PromoLocalization.LocaleProvider = () => WinUIAppKit.Localization.UiText.Locale;
        WinUIAppKit.Localization.UiText.Changed += TechnicallyReal.Promo.PromoLocalization.Refresh;

        m_window = new MainWindow();
        m_window.Activate();
    }

    internal Window? m_window;
}
