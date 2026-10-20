// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes.Settings;
using LittleLauncher.Services;
using LittleLauncher.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LittleLauncher.Windows;

/// <summary>Current Windows pin state and effective-detail comparisons for the settings row.</summary>
public sealed partial class LauncherSettingsWindow
{
    private enum PinDetailsState { Unknown, Unchanged, Changed }
    private TextBlock? _pinStatus;
    private Button? _pinButton;
    private bool _pinStatusQueued;
    private bool _pinStatusClosed;
    private bool _pinStatusWasDeactivated;
    private bool _pinStateChecking;
    private string _pinStateAumid = "";
    private TaskbarPinState _pinState;
    private int _pinStateGeneration;
    private Task _pinStateQuery = Task.CompletedTask;
    private CancellationTokenSource? _pinStateCancellation;
    private Exception? _pinStateQueryFailure;

    /// <summary>Checks once on open; edits only recompute local details against that live result.</summary>
    private void InitializePinStatus()
    {
        SettingsManager.Saved += QueuePinStatusRefresh;
        MainWindow.LauncherIconSaved += PinIconSaved;
        Activated += PinStatusActivated;
        if (_nameBox != null) _nameBox.TextChanged += PinNameEdited;
        MainWindow.EnsureLauncherIconSaved(_launcher);
        QueuePinStatusRefresh();
    }

    private void PinIconSaved(string launcherId)
    {
        if (launcherId == _launcher.Id) QueuePinStatusRefresh();
    }

    private void PinNameEdited(object sender, TextChangedEventArgs args) => QueuePinStatusRefresh();

    /// <summary>Returning from Explorer may follow a manual unpin, so recheck instead of trusting history.</summary>
    private void PinStatusActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _pinStatusWasDeactivated = true;
            return;
        }
        RequestPinStateRefresh(force: _pinStatusWasDeactivated);
        _pinStatusWasDeactivated = false;
        QueuePinStatusRefresh();
    }

    /// <summary>Coalesces saves/name/icon events; a cached identity does not launch another process.</summary>
    private void QueuePinStatusRefresh()
    {
        if (_pinStatusClosed || _pinStatusQueued) return;
        _pinStatusQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _pinStatusQueued = false;
            if (_pinStatusClosed) return;
            RequestPinStateRefresh(force: false);
            RefreshPinStatus();
        }))
        {
            _pinStatusQueued = false;
        }
    }

    /// <summary>
    /// Serializes isolated read-only children, refreshing only on open, return to the window,
    /// identity changes or an unconfirmed pin outcome. Generations reject superseded results.
    /// </summary>
    private void RequestPinStateRefresh(bool force)
    {
        if (_pinStatusClosed || _pinning || _pinStateQueryFailure != null) return;
        string aumid = _launcher.PinAumid;
        if (string.IsNullOrEmpty(aumid))
        {
            if (_pinStateAumid.Length != 0)
            {
                _pinStateGeneration++;
                _pinStateCancellation?.Cancel();
            }
            _pinStateAumid = "";
            _pinState = TaskbarPinState.Unknown;
            _pinStateChecking = false;
            return;
        }
        if (!force && _pinStateAumid == aumid) return;

        Task previousQuery = _pinStateQuery;
        _pinStateCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _pinStateCancellation = cancellation;
        int generation = ++_pinStateGeneration;
        _pinStateAumid = aumid;
        _pinState = TaskbarPinState.Unknown;
        _pinStateChecking = true;
        _pinStateQuery = RefreshPinStateAsync(previousQuery, aumid, generation, cancellation);
    }

    /// <summary>Waits for the old child to exit before starting another, then updates only its current generation.</summary>
    private async Task RefreshPinStateAsync(
        Task previousQuery, string aumid, int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await previousQuery;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_pinStateQueryFailure != null) return;
            var state = await TaskbarPinStatusService.QueryAsync(
                MainWindow.GetFlyoutCompanionPath(), _launcher.Id, aumid, cancellation.Token);
            if (_pinStatusClosed || generation != _pinStateGeneration || _launcher.PinAumid != aumid) return;
            _pinState = state;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The service reaps its read-only child before cancellation reaches this window.
        }
        catch (Exception ex)
        {
            _pinStateQueryFailure = ex;
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Could not finish the read-only taskbar status check");
        }
        finally
        {
            if (ReferenceEquals(_pinStateCancellation, cancellation)) _pinStateCancellation = null;
            cancellation.Dispose();
            if (!_pinStatusClosed && generation == _pinStateGeneration)
            {
                _pinStateChecking = false;
                RefreshPinStatus();
            }
        }
    }

    /// <summary>Pin replacement must wait until any background reader has released the old snapshot.</summary>
    private async Task StopPinStateQueryAsync()
    {
        _pinStateGeneration++;
        _pinStateCancellation?.Cancel();
        await _pinStateQuery;
        _pinStateChecking = false;
        if (_pinStateQueryFailure != null)
            throw new InvalidOperationException("The taskbar status check could not finish. Close these settings and try again.", _pinStateQueryFailure);
    }

    /// <summary>Native success/already-pinned is itself a fresh live result; no duplicate query is needed.</summary>
    private void CacheConfirmedPinState()
    {
        _pinStateAumid = _launcher.PinAumid;
        _pinState = TaskbarPinState.Pinned;
        _pinStateChecking = false;
    }

    private void DisposePinStatus()
    {
        _pinStatusClosed = true;
        _pinStateGeneration++;
        _pinStateCancellation?.Cancel();
        SettingsManager.Saved -= QueuePinStatusRefresh;
        MainWindow.LauncherIconSaved -= PinIconSaved;
        Activated -= PinStatusActivated;
        if (_nameBox != null) _nameBox.TextChanged -= PinNameEdited;
    }

    /// <summary>Uses the standard row/action unless Windows confirms a current pin exists.</summary>
    private void RefreshPinStatus()
    {
        if (_pinStatus == null || _pinButton == null) return;
        string key = "TaskbarPinDescription";
        string action = "TaskbarPinAction";
        bool alreadyPinned = false;
        if (_pinStateChecking)
        {
            key = "TaskbarPinChecking";
        }
        else if (_pinStateAumid == _launcher.PinAumid && _pinState == TaskbarPinState.Pinned)
        {
            PinSnapshot? baseline = PinSnapshot.Read(MainWindow.GetPhysicalAppDataDir(), _launcher.Id);
            PinDetailsState details = ComparePinDetails(baseline);
            action = "TaskbarPinAgainAction";
            if (details == PinDetailsState.Changed) key = "TaskbarPinChangedHint";
            else if (details == PinDetailsState.Unchanged)
            {
                key = "TaskbarAlreadyPinned";
                action = "TaskbarPinnedAction";
                alreadyPinned = true;
            }
            else key = "TaskbarPinDetailsUnknown";
        }
        // Unknown is not proof of absence; product policy simply permits a fresh pin without an
        // old-pin warning. Keep the diagnostic state intact and let the user clean up duplicates.
        _pinStatus.Text = (string)Application.Current.Resources[key];
        _pinStatus.Visibility = Visibility.Visible;
        _pinButton.Content = (string)Application.Current.Resources[action];
        _pinButton.IsEnabled = !_pinning && !alreadyPinned;
    }

    /// <summary>Compares effective ICO bytes and the uncommitted typed name; missing metadata stays unknown.</summary>
    private PinDetailsState ComparePinDetails(PinSnapshot? baseline)
    {
        if (baseline == null || baseline.Aumid != _launcher.PinAumid) return PinDetailsState.Unknown;
        try
        {
            string icon = Path.Combine(MainWindow.GetPhysicalAppDataDir(), $"app-icon-{_launcher.Id}.ico");
            string name = _nameBox?.Text.Trim() ?? _launcher.Name;
            if (name.Length == 0) name = _launcher.Name;
            var current = PinSnapshot.Capture(_launcher.PinAumid, $"Little Launcher - {name}",
                MainWindow.GetFlyoutCompanionPath(), $"--launcher {_launcher.Id}", icon);
            return baseline.HasSameDetails(current) ? PinDetailsState.Unchanged : PinDetailsState.Changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PinDetailsState.Unknown;
        }
    }
}
