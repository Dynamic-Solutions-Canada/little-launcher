using System.Diagnostics;

namespace LittleLauncher.Classes;

/// <summary>
/// The user's way to reach the logs (LOG-8): the tray menu and the About page both call this.
/// </summary>
internal static class AppDiagnostics
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Open the folder that holds the log files in Explorer.
    /// </summary>
    /// <remarks>
    /// The logs are written beside settings.json in the app's data folder (LOG-1). A packaged
    /// install's %AppData% writes are redirected to the package's <c>LocalCache\Roaming</c>, so
    /// that is where Explorer is sent. Not <see cref="MainWindow.GetPhysicalAppDataDir"/>: for a
    /// packaged install that is <c>RoamingState</c>, which holds the companion but no logs (checked
    /// on an installed copy). This matches winui-app-kit's <c>AppEnvironment.PhysicalDataDirectory</c>,
    /// which replaces it with the kit adoption. A failure is logged and otherwise ignored: there is
    /// nothing more useful to show from a tray click.
    /// </remarks>
    public static void OpenLogsFolder()
    {
        try
        {
            string folder = LogsFolder();
            Directory.CreateDirectory(folder);
            // explorer.exe started directly, with the path as one argument, so spaces need no quoting.
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Could not open the logs folder");
        }
    }

    /// <summary>The real path of the folder the log files are in, for this install type.</summary>
    private static string LogsFolder()
    {
        if (MainWindow.IsPackaged)
        {
            return Path.Combine(global::Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "Roaming", "LittleLauncher");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LittleLauncher");
    }
}
