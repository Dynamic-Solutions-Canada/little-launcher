using System.Diagnostics;
using System.ComponentModel;

namespace LauncherShortcut;

/// <summary>
/// Queries an old identity in a separate, read-only process. The pinning process keeps its new
/// startup identity and never relies on releasing a process-scoped WinRT object's identity cache.
/// </summary>
internal static class PreviousPinProcess
{
    /// <summary>
    /// Runs the production --check-only phase with a ten-second outer bound. Only known exit codes
    /// establish a live state. A timed-out child is terminated and reaped before snapshot replacement;
    /// failure to stop that owned read-only child aborts the attempt without starting a new pin.
    /// </summary>
    internal static ExistingPinState Query(string launcherId, string aumid)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--pin");
        start.ArgumentList.Add("--check-only");
        start.ArgumentList.Add("--launcher");
        start.ArgumentList.Add(launcherId);
        start.ArgumentList.Add("--previous-aumid");
        start.ArgumentList.Add(aumid);

        Process? child;
        try { child = Process.Start(start); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            TaskbarPinning.Log($"previous-pin-process: could not start; state unknown; {ex.Message}");
            return ExistingPinState.Unknown;
        }
        if (child == null) return ExistingPinState.Unknown;

        using (child)
        {
            TaskbarPinning.Log($"previous-pin-process: pid={child.Id}; aumid={aumid}");
            if (!child.WaitForExit(10_000))
            {
                TaskbarPinning.Log($"previous-pin-process: pid={child.Id}; read-only query deadline; state unknown");
                try { child.Kill(); }
                catch (InvalidOperationException) when (child.HasExited) { }
                if (!child.WaitForExit(2_000))
                {
                    throw new InvalidOperationException("The old pin-state query did not stop. No new pin has been started.");
                }
                return ExistingPinState.Unknown;
            }
            TaskbarPinning.Log($"previous-pin-process: pid={child.Id}; exit={child.ExitCode}");
            if (child.ExitCode == 4) return ExistingPinState.Pinned;
            if (child.ExitCode == 5) return ExistingPinState.NotPinned;
            return ExistingPinState.Unknown;
        }
    }
}
