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

/// <summary>Start with Windows and silent launches.</summary>
public sealed partial class MainWindow
{
    /// <summary>Register the default once, preserving later app and OS opt-outs.</summary>
    private static async Task ApplyFirstRunStartupAsync()
    {
        if (!SettingsManager.IsFirstRun || !SettingsManager.Current.Startup)
        {
            return;
        }

        // Enables the startup task only from its initial Disabled state (never DisabledByUser or
        // policy); unpackaged, writes the Run value with --silent. Failures are logged by the kit.
        await App.Startup.RegisterOnFirstRunAsync();
    }

    /// <summary>
    /// Returns true when the app should start silently (tray only, no Settings window).
    /// Silent launch happens on Windows startup and when the companion exe cold-starts the app.
    /// </summary>
    private static bool IsSilentLaunch()
    {
        // Command-line flag used by unpackaged startup (registry Run key)
        // and companion exe cold-start.
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--silent", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // MSIX StartupTask activation. The manifest declares a StartupTask
        // with no way to pass args, so detect it via the AppLifecycle API.
        // In non-MSIX builds, GetActivatedEventArgs() throws — the catch
        // handles that gracefully.
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs?.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
            {
                return true;
            }
        }
        catch { /* Not available or not packaged — treat as normal launch */ }

        return false;
    }

    /// <summary>
    /// Keep the startup entry pointing at this copy. Packaged: remove Run values left by an earlier
    /// unpackaged build, which would start a second, stale copy at sign-in. Unpackaged: re-point an
    /// existing Run value at this executable with --silent, which also fixes entries from before v1.7
    /// that lacked the flag; it never creates one or overrides the user turning startup off.
    /// </summary>
    private static void MigrateStartupRegistryEntry()
    {
        if (IsPackaged)
        {
            App.Startup.RemoveUnpackagedEntries();
        }
        else
        {
            App.Startup.RepointRunEntry();
        }
    }

    /// <summary>Set once an orderly exit has started, so the host window may close.</summary>
    private bool _exiting;

    /// <summary>
    /// Exit the app in order: save settings, unregister notifications, remove the tray icons, flush
    /// the log, then let WinUI close every window and end the process through
    /// <see cref="Application.Exit"/>. A second call while exiting does nothing.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Environment.Exit(int)"/>: ending the process with windows still alive crashes
    /// Windows App SDK 2 on Windows 10, and an orderly exit lets each window run its own close
    /// handling. A failing step is logged and the rest still run.
    /// </remarks>
    internal void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Logger.Info("Exiting");
        RunExitStep("save settings", SettingsManager.SaveSettings);
        RunExitStep("unregister notifications", () => Microsoft.Windows.AppNotifications.AppNotificationManager.Default.Unregister());
        RunExitStep("remove tray icons", CleanupTrayIcons);
        NLog.LogManager.Flush();
        Application.Current.Exit();
    }

    /// <summary>Runs one exit step, logging a failure instead of stopping the exit.</summary>
    private static void RunExitStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Exit step failed: {Step}", name);
        }
    }
}
