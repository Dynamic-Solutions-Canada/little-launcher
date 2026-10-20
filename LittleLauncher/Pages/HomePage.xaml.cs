using LittleLauncher.Classes.Settings;
using LittleLauncher.Services;
using LittleLauncher.ViewModels;
using NLog;
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LittleLauncher.Pages;

public partial class HomePage : Page
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    internal UserSettings Settings => SettingsManager.Current;

    private UpdateService.UpdateCheckResult? _updateResult;

    public HomePage()
    {
        InitializeComponent();
        LoadAppIcon();
        VersionTextBlock.Text = SettingsManager.Current.LastKnownVersion;
        BuildTypeTextBlock.Text = GetBuildTypeLabel();
        WhatsNewInfoBar.IsOpen = SettingsManager.Current.ShowWebLauncherNotice;
        _ = CheckForUpdateAsync();
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
        if (!SettingsManager.Current.ShowWebLauncherNotice) return;

        SettingsManager.Current.ShowWebLauncherNotice = false;
        SettingsManager.SaveSettings();
    }

    private static string GetBuildTypeLabel()
    {
#if DEBUG
        return "(Debug)";
#else
        return MainWindow.IsPackaged ? "(MSIX)" : "(Portable)";
#endif
    }

    private void LoadAppIcon()
    {
        // Prefer the source PNG/image for crisp rendering — BitmapImage handles PNG
        // much better than ICO on high-DPI (WIC's ICO decoder may pick a low-res frame).
        string? source = ResolveAppIconSource();
        if (source == null) return;

        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = 256;
        bmp.DecodePixelHeight = 256;
        bmp.UriSource = new Uri(source);
        AppIcon.Source = bmp;
    }

    /// <summary>
    /// Returns the app icon source path — always the blue rocket PNG (app identity).
    /// </summary>
    private static string? ResolveAppIconSource()
    {
        string png = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", "Blue.png");
        return File.Exists(png) ? png : null;
    }

    private async Task CheckForUpdateAsync()
    {
        if (MainWindow.IsPackaged) return;

        try
        {
            // Use the cached startup result if available; otherwise check now.
            var result = UpdateService.LatestResult ?? await UpdateService.CheckForUpdateAsync();
            if (result is { UpdateAvailable: true })
            {
                _updateResult = result;
                UpdateInfoBar.Message =
                    $"A new version ({result.LatestVersion}) is available. You are running {result.CurrentVersion}.";
                UpdateInfoBar.IsOpen = true;

                UpdateActionButton.Content = "View Release";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed on HomePage");
        }
    }

    private void UpdateAction_Click(object sender, RoutedEventArgs e)
    {
        if (_updateResult?.ReleaseUrl is { Length: > 0 } url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void LauncherItems_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(LaunchersPage));
    }

    private void SyncSettings_Click(object sender, PointerRoutedEventArgs e)
    {
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(SyncPage));
    }

    private void DashboardCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        }
    }

    private void DashboardCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    private void TrayIconSwitch_Toggled(object sender, RoutedEventArgs e) { }
}
