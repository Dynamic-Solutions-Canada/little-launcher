using LittleLauncher.Classes.Settings;
using LittleLauncher.Localization;
using NLog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Windows.ApplicationModel;
using global::Windows.Storage.Pickers;
using WinRT.Interop;

using WinUIAppKit.Startup;

namespace LittleLauncher.Pages;

public partial class SystemPage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private bool _startupStateInitialized;
    private bool _updatingStartupSwitch;

    private bool _updatingShortcutsSwitch;

    public SystemPage()
    {
        InitializeComponent();
        DataContext = SettingsManager.Current;

        // Shown in the positive, stored in the negative. The inversion lives here rather than in
        // the model because a bool defaulting to true cannot be turned off under
        // WhenWritingDefault — see user-settings.md.
        _updatingShortcutsSwitch = true;
        WebShortcutsToggle.IsOn = !SettingsManager.Current.DisableWebLauncherShortcuts;
        _updatingShortcutsSwitch = false;

        PopulateLanguages();

        _ = RefreshStartupStateAsync();
        _ = RefreshProfileCleanupAsync();
        RefreshExtensionList();
    }

    // ── Interface language ─────────────────────────────────────────

    private bool _updatingLanguage;

    /// <summary>
    /// Set once the language has been changed in this run, so the rebuilt page keeps saying that
    /// background web launchers switch after a restart.
    /// </summary>
    private static bool _languageChangedThisRun;

    /// <summary>
    /// Fill the language picker: Match device first, then each supported language in its own
    /// name, with the saved choice selected.
    /// </summary>
    /// <remarks>
    /// Match device names the language it currently resolves to, so the user can see what they
    /// would get without switching. The item's <c>Tag</c> is the saved value; empty is Match device.
    /// </remarks>
    private void PopulateLanguages()
    {
        _updatingLanguage = true;

        string deviceName = SupportedLanguages.NativeNames[UiText.DeviceLocale];
        LanguageCombo.Items.Add(new ComboBoxItem
        {
            Content = UiText.Format("system.language.matchDevice", ("language", deviceName)),
            Tag = "",
        });

        foreach (string locale in SupportedLanguages.Ordered)
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = SupportedLanguages.NativeNames[locale], Tag = locale });
        }

        string saved = SupportedLanguages.NormalizeOverride(SettingsManager.Current.LanguageOverride) ?? "";
        LanguageCombo.SelectedItem = LanguageCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => (string)item.Tag == saved);

        LanguageRestartNote.Visibility = _languageChangedThisRun ? Visibility.Visible : Visibility.Collapsed;

        _updatingLanguage = false;
    }

    /// <summary>
    /// Save a new language and rebuild Settings in it. Other windows pick it up as they open.
    /// </summary>
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string choice })
        {
            return;
        }

        if (choice == (SupportedLanguages.NormalizeOverride(SettingsManager.Current.LanguageOverride) ?? ""))
        {
            return;
        }

        SettingsManager.Current.LanguageOverride = choice;
        SettingsManager.SaveSettings();
        _languageChangedThisRun = true;
        UiText.NotifyChanged();

        // Deferred so this page's own event handler finishes before its window is replaced.
        DispatcherQueue.TryEnqueue(() => SettingsWindow.ReopenAfterLanguageChange(typeof(SystemPage)));
    }

    // ── Browser extensions ─────────────────────────────────────────

    /// <summary>
    /// Lists what is installed, with a way to remove each.
    /// </summary>
    /// <remarks>
    /// App-wide, because extensions belong to a WebView2 <em>profile</em> and most launchers share
    /// one — see <c>BrowserExtensionService</c>. Removing takes effect for a launcher the next time
    /// its browser starts; nothing here reaches into a running one.
    /// </remarks>
    private void RefreshExtensionList()
    {
        ExtensionList.Children.Clear();

        var folders = Services.BrowserExtensionService.InstalledFolders;
        if (folders.Count == 0)
        {
            ExtensionList.Children.Add(new TextBlock
            {
                Text = UiText.Get("system.extensions.none"),
                FontSize = 12,
                Opacity = 0.5,
            });
            return;
        }

        foreach (string folder in folders.ToList())
        {
            string captured = folder;
            bool missing = !Directory.Exists(captured);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = Services.BrowserExtensionService.ReadName(captured),
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            // A folder that has gone is listed rather than hidden: it is why an extension stopped
            // working, and silently dropping it would leave that unexplained.
            if (missing)
            {
                label.Children.Add(new TextBlock
                {
                    Text = UiText.Get("system.extensions.folderMissing"),
                    FontSize = 11,
                    Opacity = 0.6,
                });
            }

            var remove = new Button { Content = UiText.Get("common.remove") };
            remove.Click += (_, _) =>
            {
                Services.BrowserExtensionService.Uninstall(captured);
                RefreshExtensionList();
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(remove, 1);
            row.Children.Add(label);
            row.Children.Add(remove);
            ExtensionList.Children.Add(row);
        }
    }

    private void AddExtensionButton_Click(object sender, RoutedEventArgs e) => _ = AddExtensionAsync();

    /// <summary>
    /// Adds an unpacked extension folder, or an archive of one.
    /// </summary>
    /// <remarks>
    /// A picker for both, because both are what a user actually has: uBlock Origin Lite ships a
    /// <c>.zip</c> on its releases page, and anything already unpacked is a folder. The service
    /// copies whichever it is into its own store, so the download can be tidied away afterwards.
    /// </remarks>
    private async Task AddExtensionAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        string? installed = await Services.BrowserExtensionService.InstallAsync(folder.Path);
        RefreshExtensionList();

        if (installed == null)
        {
            Logger.Warn("No manifest.json in {Path}; not an unpacked extension", folder.Path);
        }
    }

    // ── Unused browser profiles ────────────────────────────────────

    private List<Services.WebProfileCleanupService.Reclaimable> _reclaimableProfiles = [];

    private static string NewLine => System.Environment.NewLine;

    /// <summary>
    /// Counts what could be reclaimed and offers it, or says there is nothing to do.
    /// </summary>
    /// <remarks>
    /// Off the UI thread: sizing a profile folder walks a Chromium cache of tens of thousands of
    /// files, which is not something to do while the page is trying to appear.
    /// </remarks>
    private async Task RefreshProfileCleanupAsync()
    {
        var found = await Task.Run(Services.WebProfileCleanupService.Scan);
        _reclaimableProfiles = found;

        long bytes = found.Sum(f => f.Bytes);
        bool any = found.Count > 0;

        ProfileCleanupButton.IsEnabled = any;
        ProfileCleanupSubtitle.Text = any
            ? UiText.Plural("system.profiles.reclaimable", found.Count,
                ("size", Services.WebProfileCleanupService.FormatSize(bytes)))
            : UiText.Get("system.profiles.nothing");
    }

    /// <summary>
    /// Deletes the unused profiles, after saying exactly which sign-ins are going.
    /// </summary>
    /// <remarks>
    /// Named rather than counted in the confirmation: "3 profiles" is not something anyone can
    /// agree to, and the only question that matters is whether one of them is a launcher they still
    /// use — which the list answers at a glance.
    /// </remarks>
    private async void ProfileCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reclaimableProfiles.Count == 0)
        {
            return;
        }

        var lines = _reclaimableProfiles
            .Select(f => $"• {f.Description} ({Services.WebProfileCleanupService.FormatSize(f.Bytes)})");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = UiText.Get("system.profiles.deleteTitle"),
            Content = UiText.Format("system.profiles.deleteMessage", ("profiles", string.Join(NewLine, lines))),
            PrimaryButtonText = UiText.Get("common.delete"),
            CloseButtonText = UiText.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ProfileCleanupButton.IsEnabled = false;
        ProfileCleanupButton.Content = UiText.Get("system.profiles.deleting");

        var (deleted, bytes) = await Services.WebProfileCleanupService.DeleteAsync(_reclaimableProfiles);

        ProfileCleanupButton.Content = UiText.Get("common.delete");
        await RefreshProfileCleanupAsync();

        // Rescanned above, so anything a locked file kept back is still listed and still offered.
        if (deleted > 0)
        {
            ProfileCleanupSubtitle.Text = UiText.Format("system.profiles.freed",
                ("size", Services.WebProfileCleanupService.FormatSize(bytes)),
                ("status", ProfileCleanupSubtitle.Text));
        }
    }

    private void WebShortcutsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        // Setting IsOn from code raises Toggled exactly as a click does, so the initial sync above
        // would otherwise write the setting back over itself.
        if (_updatingShortcutsSwitch)
        {
            return;
        }

        SettingsManager.Current.DisableWebLauncherShortcuts = !WebShortcutsToggle.IsOn;
        SettingsManager.SaveSettings();
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingStartupSwitch || !_startupStateInitialized)
        {
            return;
        }

        await SetStartupAsync(StartupSwitch.IsOn);
    }

    private async Task RefreshStartupStateAsync()
    {
        try
        {
            bool isEnabled = await IsStartupEnabledAsync();
            ApplyStartupSwitchState(isEnabled);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to read startup state");
        }
        finally
        {
            _startupStateInitialized = true;
        }
    }

    private async Task SetStartupAsync(bool enable)
    {
        try
        {
            // The kit enables or disables the startup task (packaged) or the Run value, and reports
            // the state Windows actually ended up in, which a user or policy may have decided.
            var status = await App.Startup.SetEnabledAsync(enable);
            if (MainWindow.IsPackaged)
            {
                App.Startup.RemoveUnpackagedEntries();
            }

            ApplyStartupSwitchState(status.IsOn());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to set startup");
            ApplyStartupSwitchState(await IsStartupEnabledAsync());
        }
    }

    private void ApplyStartupSwitchState(bool isEnabled)
    {
        try
        {
            _updatingStartupSwitch = true;
            StartupSwitch.IsOn = isEnabled;
            SettingsManager.Current.Startup = isEnabled;
        }
        finally
        {
            _updatingStartupSwitch = false;
        }
    }

    private static async Task<bool> IsStartupEnabledAsync() => (await App.Startup.GetStatusAsync()).IsOn();

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExportButtonClickAsync();
    }

    private async Task ExportButtonClickAsync()
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(UiText.Get("system.backup.xmlFiles"), new List<string> { ".xml" });
        picker.SuggestedFileName = UiText.Format("system.backup.suggestedFileName",
            ("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)));
        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            try
            {
                SettingsManager.SaveSettings(file.Path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error exporting settings");
            }
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ImportButtonClickAsync();
    }

    private async Task ImportButtonClickAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".xml");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            try
            {
                SettingsManager.RestoreSettings(file.Path);
                SettingsManager.SaveSettings();

                // Restart to apply imported settings
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    System.Diagnostics.Process.Start(exePath);
                }

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error importing settings");
            }
        }
    }

    private static void InitializePicker(object picker)
    {
        var window = SettingsWindow.GetCurrent();
        if (window == null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}
