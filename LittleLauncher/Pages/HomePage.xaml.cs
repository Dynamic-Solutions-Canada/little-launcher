using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using NLog;
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LittleLauncher.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal UserSettings Settings => SettingsManager.Current;

    private WinUIAppKit.Updates.UpdateCheckResult? _updateResult;

    public HomePage()
    {
        InitializeComponent();
        WhatsNewInfoBar.IsOpen = SettingsManager.Current.ShowWebLauncherNotice;
        ShowResetNoticeIfPending();
        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Shows winui-app-kit's "settings were reset" bar at the top of this page when this launch
    /// replaced an unreadable settings file (CONF-2, UI-15). Home is the page Settings opens on, and
    /// the launch that needs the notice opens Settings even when silent. Shown once: the defaults are
    /// already saved.
    /// </summary>
    private void ShowResetNoticeIfPending()
    {
        if (SettingsManager.TakeResetNotice(out string? backupPath))
        {
            PageContent.Children.Insert(0, WinUIAppKit.Settings.SettingsResetNotice.Create(backupPath));
        }
    }

    /// <summary>
    /// Sends the user to the Launchers page, where the new kind is actually created.
    /// </summary>
    /// <remarks>
    /// The notice is dismissed by acting on it, not just by closing it — someone who has been
    /// taken to the page does not need telling again.
    /// </remarks>
    private void WhatsNewAction_Click(object sender, RoutedEventArgs e)
    {
        DismissWhatsNew();
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LaunchersPage));
    }

    private void WhatsNewInfoBar_CloseButtonClick(InfoBar sender, object args) => DismissWhatsNew();

    /// <summary>Clears the flag for good. Saved immediately, so it survives a hard exit.</summary>
    private void DismissWhatsNew()
    {
        WhatsNewInfoBar.IsOpen = false;
        if (!SettingsManager.Current.ShowWebLauncherNotice)
        {
            return;
        }

        SettingsManager.Current.ShowWebLauncherNotice = false;
        SettingsManager.SaveSettings();
    }

    private async Task CheckForUpdateAsync()
    {
        if (MainWindow.IsPackaged)
        {
            return;
        }

        try
        {
            // Use the cached startup result if available; otherwise check now.
            var result = MainWindow.StartupUpdateResult ?? await Classes.AboutContent.Updates.CheckGitHubAsync();
            if (result.Outcome == WinUIAppKit.Updates.UpdateCheckOutcome.UpdateAvailable)
            {
                _updateResult = result;
                UpdateInfoBar.Message = UiText.Format("home.update.message",
                    ("latest", result.LatestVersion),
                    ("current", result.CurrentVersion));
                UpdateInfoBar.IsOpen = true;

                UpdateActionButton.Content = UiText.Get("about.update.viewRelease");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed on HomePage");
        }
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult is not null)
        {
            WinUIAppKit.Updates.UpdateService.OpenReleasePage(_updateResult);
        }
    }

    private void LauncherItems_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LaunchersPage));
    }

    private void SyncSettings_Click(object sender, RoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(SyncPage));
    }

}
