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
    }

    /// <summary>Checks GitHub releases for a portable build, or opens Store updates for MSIX.</summary>
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.IsPackaged)
        {
            UpdateStatusText.Text = UpdateService.OpenStoreUpdates()
                ? GetText("StoreUpdatesOpened")
                : GetText("StoreUpdatesOpenFailed");
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking...";
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var result = await UpdateService.CheckForUpdateAsync();
            if (result == null)
            {
                UpdateStatusText.Text = "Unable to check for updates. Check your internet connection.";
            }
            else if (result.UpdateAvailable)
            {
                _updateResult = result;
                UpdateStatusText.Text = $"Version {result.LatestVersion} is available (you have {result.CurrentVersion})";
                CheckUpdateButton.Content = "View Release";
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Click -= CheckForUpdates_Click;
                CheckUpdateButton.Click += ViewRelease_Click;
                return;
            }
            else
            {
                UpdateStatusText.Text = $"You're up to date ({result.CurrentVersion})";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Manual update check failed");
            UpdateStatusText.Text = "Update check failed. Try again later.";
        }

        CheckUpdateButton.Content = "Check for Updates";
        CheckUpdateButton.IsEnabled = true;
    }

    private void ViewRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult?.ReleaseUrl is { Length: > 0 } url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private static string GetText(string key)
    {
        return Application.Current.Resources.TryGetValue(key, out object value) && value is string text
            ? text
            : key;
    }

}
