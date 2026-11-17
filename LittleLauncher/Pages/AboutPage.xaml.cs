using LittleLauncher.Classes;
using LittleLauncher.Localization;
using LittleLauncher.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LittleLauncher.Pages;

public partial class AboutPage : Page
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private UpdateService.UpdateCheckResult? _updateResult;

    public AboutPage()
    {
        InitializeComponent();

        // Built from the one definition of the repository (BRAND-5), not retyped in XAML.
        SourceCodeLink.NavigateUri = AboutContent.SourceCode;
    }

    /// <summary>Checks GitHub releases for a portable build, or opens Store updates for MSIX.</summary>
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.IsPackaged)
        {
            UpdateStatusText.Text = UpdateService.OpenStoreUpdates()
                ? UiText.Get("about.update.storeOpened")
                : UiText.Get("about.update.storeOpenFailed");
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = UiText.Get("about.update.checkingButton");
        UpdateStatusText.Text = UiText.Get("about.update.checkingStatus");

        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result == null)
            {
                UpdateStatusText.Text = UiText.Get("about.update.unableToCheck");
            }
            else if (result.UpdateAvailable)
            {
                _updateResult = result;
                UpdateStatusText.Text = UiText.Format("about.update.available",
                    ("latest", result.LatestVersion),
                    ("current", result.CurrentVersion));
                CheckUpdateButton.Content = UiText.Get("about.update.viewRelease");
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Click -= CheckForUpdates_Click;
                CheckUpdateButton.Click += ViewRelease_Click;
                return;
            }
            else
            {
                UpdateStatusText.Text = UiText.Format("about.update.upToDate", ("current", result.CurrentVersion));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Manual update check failed");
            UpdateStatusText.Text = UiText.Get("about.update.failed");
        }

        CheckUpdateButton.Content = UiText.Get("about.update.checkButton");
        CheckUpdateButton.IsEnabled = true;
    }

    private void ViewRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult?.ReleaseUrl is { Length: > 0 } url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

}
