using System.Diagnostics;

namespace LittleLauncher.Classes;

/// <summary>
/// Restarts the app, for changes that only take effect on a fresh start (importing settings).
/// </summary>
/// <remarks>
/// <para>Packaged, Windows App SDK's <c>AppInstance.Restart</c> ends this process and starts the
/// new one itself, so the new copy never meets this one's single-instance mutex.</para>
/// <para>Unpackaged, or when that call fails, the app starts its own exe with
/// <see cref="AfterExitOfArgument"/> and this process's id, then exits in order. The new copy waits
/// for this process to end (<see cref="WaitForPreviousInstance"/>) before it claims the mutex, so it
/// does not find this copy still running and hand over to it.</para>
/// <para>If a second app ever needs this, it moves to winui-app-kit.</para>
/// </remarks>
internal static class AppRestart
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>The argument naming the process a restarted copy waits for, followed by its id.</summary>
    internal const string AfterExitOfArgument = "--after-exit-of";

    /// <summary>The longest a restarted copy waits for the previous one to end.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Restart the app. Returns only if neither way could start; the app then keeps running.
    /// </summary>
    public static void Restart()
    {
        if (MainWindow.IsPackaged)
        {
            // Ends this process when it succeeds; a returned reason means it did not.
            var reason = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
            Logger.Warn("AppInstance.Restart failed ({Reason}); restarting by hand", reason);
        }

        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            Logger.Error("Cannot restart: the executable path is unknown");
            return;
        }

        try
        {
            var start = new ProcessStartInfo(exePath) { UseShellExecute = false };
            start.ArgumentList.Add(AfterExitOfArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(start);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cannot restart: the new copy did not start");
            return;
        }

        MainWindow.Current?.ExitApp();
    }

    /// <summary>
    /// When this copy was started by <see cref="Restart"/>, wait for the copy that started it to
    /// end, at most <see cref="MaxWait"/>. Call first in <c>OnLaunched</c>, before the single-instance
    /// check. Does nothing for an ordinary launch.
    /// </summary>
    /// <param name="args">The command line, as <see cref="Environment.GetCommandLineArgs"/> returns it.</param>
    public static void WaitForPreviousInstance(string[] args)
    {
        int index = Array.IndexOf(args, AfterExitOfArgument);
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(processId);
            if (!previous.WaitForExit(MaxWait))
            {
                Logger.Warn("The previous copy (process {Id}) was still running after {Seconds} s", processId, MaxWait.TotalSeconds);
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not wait for the previous copy");
        }
    }
}
