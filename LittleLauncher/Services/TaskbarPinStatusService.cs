// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LittleLauncher.Services;

/// <summary>A completed Windows query, or no reliable result for this caller identity.</summary>
internal enum TaskbarPinState { Unknown, NotPinned, Pinned }

/// <summary>Reads one launcher's live pin state through the production isolated, unshown helper.</summary>
internal static class TaskbarPinStatusService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Runs only --check-only, which never submits a pin or changes shell resources. The helper
    /// validates its old caller identity before querying Windows. Missing registration, unsupported
    /// APIs and unexpected exits stay Unknown. Cancellation/deadline stops only this read-only child;
    /// failure to reap it throws so callers cannot start snapshot replacement while it remains alive.
    /// </summary>
    internal static async Task<TaskbarPinState> QueryAsync(
        string executable, string launcherId, string aumid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executable) || string.IsNullOrEmpty(aumid)) return TaskbarPinState.Unknown;

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--pin");
        start.ArgumentList.Add("--check-only");
        start.ArgumentList.Add("--launcher");
        start.ArgumentList.Add(launcherId);
        start.ArgumentList.Add("--previous-aumid");
        start.ArgumentList.Add(aumid);

        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            try { started = process.Start(); }
            catch (Win32Exception ex)
            {
                Logger.Debug(ex, "Could not start the read-only taskbar check for {LauncherId}", launcherId);
                return TaskbarPinState.Unknown;
            }
            if (!started) return TaskbarPinState.Unknown;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Logger.Debug("Read-only taskbar check timed out for {LauncherId}", launcherId);
                return TaskbarPinState.Unknown;
            }

            Logger.Debug("Read-only taskbar check for {LauncherId} exited {ExitCode}", launcherId, process.ExitCode);
            if (process.ExitCode == 4) return TaskbarPinState.Pinned;
            if (process.ExitCode == 5) return TaskbarPinState.NotPinned;
            return TaskbarPinState.Unknown;
        }
        finally
        {
            if (started) await StopReadOnlyChildAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>Reaps only the owned read-only query, independent of the closing window's UI context.</summary>
    private static async Task StopReadOnlyChildAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            Logger.Warn(ex, "The owned read-only taskbar query did not stop");
            throw new InvalidOperationException("The taskbar status check did not stop. No new pin has been started.", ex);
        }
    }
}
