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

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
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

        // Interface language. The OS list is the user's full ordered preference, not the
        // package-filtered ApplicationLanguages list, which only ever contains the manifest's
        // declared languages.
        Localization.UiText.Configure(
            () => SettingsManager.Current.LanguageOverride,
            () => global::Windows.System.UserProfile.GlobalizationPreferences.Languages);

        // The "Our other apps" page translates its own text and follows the app's language (PROMO-7).
        TechnicallyReal.Promo.PromoLocalization.LocaleProvider = () => Localization.UiText.Locale;
        Localization.UiText.Changed += TechnicallyReal.Promo.PromoLocalization.Refresh;

        m_window = new MainWindow();
        m_window.Activate();
    }

    internal Window? m_window;
}
