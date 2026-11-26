using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using WinUIAppKit.Pages;
using WinUIAppKit.Windowing;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>
/// The settings window: winui-app-kit's <see cref="SettingsShell"/> with Little Launcher's own pages
/// (Home, Launchers, Cloud Sync) above the shell's fixed footer (Our other apps, About, Settings).
/// One instance at a time.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? _instance;
    private readonly MainWindow? _owner;

    /// <summary>Menu tags of the app's own pages. The shell's footer pages use its own tags.</summary>
    private const string HomeTag = "HomePage";
    private const string LaunchersTag = "LaunchersPage";
    private const string SyncTag = "SyncPage";

    public SettingsWindow(MainWindow owner)
    {
        if (_instance != null)
        {
            WindowChrome.BringToFront(_instance);
            Close();
            return;
        }

        _owner = owner;
        InitializeComponent();
        _instance = this;

        // Only clear our own registration: after a language change the replacement registers
        // itself before this window's Closed event necessarily arrives.
        Closed += (s, e) =>
        {
            if (_instance == this)
            {
                _instance = null;
            }
        };

        Shell.Caption = "Little Launcher";
        Shell.IconPngPath = App.IconPngPath;
        AddMenuItem("home.title", HomeTag, "NavHome", new SymbolIcon(Symbol.Home));
        AddMenuItem("launchers.title", LaunchersTag, "NavLaunchers", new FontIcon { Glyph = "\uF0E2" });
        AddMenuItem("sync.title", SyncTag, "NavSync", new SymbolIcon(Symbol.Sync));

        // Its own AppUserModelID, so the taskbar groups and draws this window apart from the
        // invisible host window; without it the taskbar uses the exe's embedded icon
        // (WindowsAppSDK#2730).
        SetWindowAppUserModelId(WindowNative.GetWindowHandle(this), "LittleLauncher.Settings");

        // Mica, the custom title bar, sharp icons, a minimum size, the saved placement (or the
        // default size, centred on the cursor's monitor) and the theme, from the kit's setup.
        Classes.ThemeManager.SyncFromSettings();
        var settings = SettingsManager.Current;
        WindowChrome.SetUpSettingsWindow(this, Shell.TitleBar, SettingsIconPath(), SavedPlacement(settings),
            defaultWidthDip: 900, defaultHeightDip: 700);

        // The kit restores the normal placement only; maximized is this app's own setting.
        if (settings.SettingsWindowMaximized && WindowChrome.AppWindowOf(this).Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        Shell.PageSelected += tag =>
        {
            // The kit's About page takes Little Launcher's AboutInfo; every other page needs none.
            object? parameter = tag == SettingsShell.AboutTag ? Classes.AboutContent.Create() : null;
            if (PageFor(tag) is Type page && Shell.ContentFrame.Content?.GetType() != page)
            {
                Shell.ContentFrame.Navigate(page, parameter);
            }
        };
        Shell.Select(HomeTag);

        Closed += SettingsWindow_Closed;
    }

    /// <summary>
    /// Adds one of the app's own pages to the navigation, labelled in the current language. The
    /// automation ID lets UI automation (the Store screenshot capture) find the item in every
    /// interface language, since a code-built item has no <c>x:Name</c> to supply one.
    /// </summary>
    private void AddMenuItem(string labelKey, string tag, string automationId, IconElement icon)
    {
        var item = new NavigationViewItem { Content = UiText.Get(labelKey), Tag = tag, Icon = icon };
        AutomationProperties.SetAutomationId(item, automationId);
        Shell.MenuItems.Add(item);
    }

    /// <summary>
    /// The window icon: the app icon with a gear, drawn for the current tray style and Windows
    /// theme by <see cref="MainWindow.SaveSettingsIconToAppData"/>, else the plain app icon.
    /// </summary>
    private static string SettingsIconPath()
    {
        string settingsIcon = Path.Combine(MainWindow.GetPhysicalAppDataDir(), "settings-icon.ico");
        if (!File.Exists(settingsIcon))
        {
            MainWindow.SaveSettingsIconToAppData();
        }

        return File.Exists(settingsIcon)
            ? settingsIcon
            : Path.Combine(AppContext.BaseDirectory, "Resources", "LittleLauncher.ico");
    }

    /// <summary>
    /// The saved normal placement, from the settings' flat fields, which keep their names so existing
    /// settings files still restore the window. The kit ignores a size too small to be real (the
    /// placeholder a minimized window reports) and a position on a monitor that is gone.
    /// </summary>
    private static WindowPlacement? SavedPlacement(ViewModels.UserSettings settings) =>
        settings.SettingsWindowWidth <= 0 || settings.SettingsWindowHeight <= 0
            ? null
            : new WindowPlacement
            {
                X = settings.SettingsWindowX,
                Y = settings.SettingsWindowY,
                Width = settings.SettingsWindowWidth,
                Height = settings.SettingsWindowHeight,
            };

    /// <summary>
    /// Show the singleton settings window (create if needed, bring it forward if open).
    /// </summary>
    public static void ShowInstance(MainWindow owner)
    {
        if (_instance == null)
        {
            new SettingsWindow(owner).Activate();
        }
        else
        {
            WindowChrome.BringToFront(_instance);
        }
    }

    /// <summary>
    /// Replace the open Settings window with a new one showing <paramref name="pageType"/>, so
    /// every label is read again in the newly chosen language.
    /// </summary>
    /// <remarks>
    /// The app's XAML text is resolved when an element is created, so a fresh window is the only
    /// complete relabel. Closing saves the window's size and position first, so the replacement
    /// opens where this one was.
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
    /// Navigate to a page by type and select its menu item (used by the Home page's cards).
    /// </summary>
    public void NavigateTo(Type pageType)
    {
        if (TagFor(pageType) is string tag)
        {
            Shell.Select(tag);
        }
    }

    internal MainWindow? GetOwner() => _owner;

    internal static SettingsWindow? GetCurrent() => _instance;

    /// <summary>The currently displayed page, if any.</summary>
    internal object? CurrentPage => Shell.ContentFrame.Content;

    /// <summary>
    /// Re-reads the settings icon (app icon + gear overlay) and applies it to this window.
    /// Called when the tray icon style or the Windows theme changes.
    /// </summary>
    internal void RefreshIcon()
    {
        string settingsIcon = Path.Combine(MainWindow.GetPhysicalAppDataDir(), "settings-icon.ico");
        if (File.Exists(settingsIcon))
        {
            WindowIcon.Apply(this, settingsIcon);
        }
    }

    /// <summary>The page for a menu tag: the app's own pages, then the shell's Settings, About and Our other apps.</summary>
    private static Type? PageFor(string tag) => tag switch
    {
        HomeTag => typeof(HomePage),
        LaunchersTag => typeof(LaunchersPage),
        SyncTag => typeof(SyncPage),
        SettingsShell.SettingsTag => typeof(SystemPage),
        SettingsShell.AboutTag => typeof(WinUIAppKit.Pages.AboutPage),
        SettingsShell.OtherAppsTag => typeof(TechnicallyReal.Promo.PromotedAppsPage),
        _ => null,
    };

    /// <summary>The menu tag that opens <paramref name="pageType"/>, or null for a page with no menu item.</summary>
    private static string? TagFor(Type pageType)
    {
        foreach (string tag in new[] { HomeTag, LaunchersTag, SyncTag, SettingsShell.SettingsTag, SettingsShell.AboutTag, SettingsShell.OtherAppsTag })
        {
            if (PageFor(tag) == pageType)
            {
                return tag;
            }
        }

        return null;
    }

    /// <summary>
    /// Navigate to the Launchers page and auto-open the settings dialog for the given launcher.
    /// </summary>
    public void NavigateToLauncherSettings(Launcher launcher)
    {
        // If LaunchersPage is already displayed, call the dialog directly
        if (Shell.ContentFrame.Content is LaunchersPage existingPage)
        {
            _ = existingPage.ShowLauncherSettingsDialogPublic(launcher);
            return;
        }

        // Set pending BEFORE selecting the menu item: selecting navigates synchronously, and the
        // new LaunchersPage instance reads PendingSettingsLauncher as it loads.
        LaunchersPage.PendingSettingsLauncher = launcher;
        Shell.Select(LaunchersTag);

        // Select does nothing when the item is already selected; navigate in that case.
        if (Shell.ContentFrame.Content is not LaunchersPage)
        {
            Shell.ContentFrame.Navigate(typeof(LaunchersPage));
        }
    }

    private void SettingsWindow_Closed(object sender, WindowEventArgs e)
    {
        // Save the window state. The kit's capture is null while minimized or maximized, so neither
        // overwrites the normal size the user chose; maximized is remembered on its own.
        var settings = SettingsManager.Current;
        settings.SettingsWindowMaximized =
            WindowChrome.AppWindowOf(this).Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (WindowChrome.CapturePlacement(this) is { } placement)
        {
            settings.SettingsWindowX = placement.X;
            settings.SettingsWindowY = placement.Y;
            settings.SettingsWindowWidth = placement.Width;
            settings.SettingsWindowHeight = placement.Height;
        }

        SettingsManager.SaveSettings();

        // Refresh tray icons in case launchers were added, removed, or renamed
        MainWindow.Current?.RefreshTrayIcons();

        // Flush any pending sync and upload immediately so the server
        // has the latest settings even if the app is killed shortly after.
        AutoSyncService.FlushPendingUpload();
    }
}
