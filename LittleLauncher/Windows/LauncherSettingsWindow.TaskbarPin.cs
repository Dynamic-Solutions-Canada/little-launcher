using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading.Tasks;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

/// <summary>Pin snapshots, their edit warning, and the companion request lifecycle.</summary>
public sealed partial class LauncherSettingsWindow
{
    /// <remarks>
    /// A pinned icon is baked once: Windows never re-reads it, so anything not resolved by the
    /// time this runs stays wrong until the user unpins and pins again. The two deferred fields
    /// are therefore committed first, and a site-icon fetch they start is waited for. A web
    /// launcher pinned the moment its address was typed used to pin the generic app icon.
    /// </remarks>
    private async void PinToTaskbar_Click(object sender, RoutedEventArgs e)
    {
        if (_pinning || sender is not Button button || button.Tag is not Launcher launcher) return;
        _pinning = true;
        button.IsEnabled = false;

        bool confirmedLivePin = false;
        try
        {
            await StopPinStateQueryAsync();
            if (_pinStatusClosed) return;
            CommitName();
            CommitWebUrl();
            await WaitForIconAdoptionAsync(button);
            if (_pinStatusClosed) return;
            button.IsEnabled = false;

            string flyoutExe = MainWindow.GetFlyoutCompanionPath();
            if (!File.Exists(flyoutExe))
            {
                await ReportPinErrorAsync("The companion flyout exe was not found. Build the project first.");
                return;
            }

            MainWindow.EnsureLauncherIconSaved(launcher);
            string appDataDir = MainWindow.GetPhysicalAppDataDir();
            string baseIco = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");
            string stamp = Guid.NewGuid().ToString("N");
            string pinnedIcon = Path.Combine(appDataDir, $"app-icon-{launcher.Id}-pin{stamp}.ico");
            if (File.Exists(baseIco)) File.Copy(baseIco, pinnedIcon);

            // Both identity and icon path must be new: Windows caches names and icons separately.
            // Keep the saved identity until Windows confirms, or the user confirms a manual pin.
            string aumid = $"LittleLauncher.Launcher.{launcher.Id}.{stamp}";
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = flyoutExe,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--pin");
            start.ArgumentList.Add("--launcher");
            start.ArgumentList.Add(launcher.Id);
            start.ArgumentList.Add("--name");
            start.ArgumentList.Add(launcher.Name);
            start.ArgumentList.Add("--aumid");
            start.ArgumentList.Add(aumid);
            if (!string.IsNullOrEmpty(launcher.PinAumid))
            {
                start.ArgumentList.Add("--previous-aumid");
                start.ArgumentList.Add(launcher.PinAumid);
            }
            if (File.Exists(pinnedIcon))
            {
                start.ArgumentList.Add("--icon");
                start.ArgumentList.Add(pinnedIcon);
            }
            if (!string.IsNullOrEmpty(launcher.PinAumid))
            {
                start.ArgumentList.Add("--repin");
            }
            // An earlier unknown outcome preserves resources, but does not force a removal dialog
            // or manual fallback. The helper checks the saved identity and can request Windows again.

            int result;
            try
            {
                // Give the companion foreground ownership for Windows confirmation or manual pinning.
                NativeMethods.ShowWindow(_hwnd, 6 /* SW_MINIMIZE */);
                using var process = System.Diagnostics.Process.Start(start);
                if (process == null) throw new InvalidOperationException("The pin helper did not start.");
                await process.WaitForExitAsync();
                result = process.ExitCode;
            }
            finally
            {
                // The independent helper can finish after settings has closed. Do not restore or
                // focus a destroyed HWND; confirmed pin state below must still be saved.
                if (!_pinStatusClosed)
                {
                    NativeMethods.ShowWindow(_hwnd, 9 /* SW_RESTORE */);
                    NativeMethods.SetForegroundWindow(_hwnd);
                }
            }

            if (result == 1 || result == 2)
            {
                launcher.PinAumid = aumid;
                // Persist confirmed identity with local settings. Existing-launcher sync merges
                // preserve this machine's value; newly imported launchers can carry a source value.
                SettingsManager.SaveSettings();
                if (result == 1)
                {
                    CacheConfirmedPinState();
                    confirmedLivePin = true;
                }
            }
            else if (result == 4)
            {
                CacheConfirmedPinState();
                confirmedLivePin = true;
            }
            else if (result == 3)
            {
                await ReportPinErrorAsync((string)Application.Current.Resources["TaskbarPinUnconfirmed"]);
            }
            else if (result != 0)
            {
                await ReportPinErrorAsync("The pin helper could not complete the request. See taskbar-pin.log for details.");
            }
        }
        catch (Exception ex)
        {
            await ReportPinErrorAsync("Could not pin this launcher. " + ex.Message);
        }
        finally
        {
            _pinning = false;
            if (!_pinStatusClosed)
            {
                button.IsEnabled = true;
                RequestPinStateRefresh(force: !confirmedLivePin);
                QueuePinStatusRefresh();
            }
        }
    }

    /// <summary>Logs pin errors even after close; a missing dialog host must not fault this async event.</summary>
    private async Task ReportPinErrorAsync(string message)
    {
        var logger = NLog.LogManager.GetCurrentClassLogger();
        logger.Warn("Taskbar pin request: {Message}", message);
        if (_pinStatusClosed) return;
        try
        {
            await ShowErrorAsync(message);
        }
        catch (Exception ex)
        {
            // Close can also happen while ShowAsync is awaiting its result. Reporting failure
            // belongs in the log, never in another dialog against the same unavailable XamlRoot.
            logger.Warn(ex, "Could not show the taskbar pin error");
        }
    }

}
