using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static LauncherShortcut.NativeMethods;
using static LauncherShortcut.ShellInterop;

namespace LauncherShortcut;

/// <summary>
/// Runs desktop TaskbarManager on the helper's STA with a guarded native-window message pump.
/// Read-only old-identity queries stay unshown; pin requests use a real foreground progress owner.
/// </summary>
internal static unsafe class TaskbarPinning
{
    private const string LafKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\LimitedAccessFeatures\com.microsoft.windows.taskbar.pin";
    private const string LafSeed = "4096B239A7295B635C090E647E867B5707DA6AB6CB78340B01FE4E0C8F4953D4";
    private const string ManualMessage = "Right-click this launcher's taskbar icon and choose \"Pin to taskbar\".\n\nClick OK only after pinning it, or Cancel to leave the saved pin unchanged.";
    private static readonly TimerProc TimerCallback = OnTimer;
    private static nint _manager;
    private static nint _operation;
    private static nint _info;
    private static nint _window;
    private static bool _initialized;
    private static bool _checking;
    private static bool _requesting;
    private static int _attempts;
    private static long _operationStarted;
    private static long _cancelRequested;
    private static bool _pinRequestStarted;
    private static long _lastStatusLog;
    private static int _lastStatus = -1;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(5);
    private static bool _checkOnly;
    private static bool _manualFallback;
    private static bool _firstTimerCallback;
    private static Action<int>? _complete;
    private static PinShortcut? _shortcut;
    private static string? _pendingPath;
    private static string _reason = "Native pinning is unavailable.";

    internal static string InitialMessage => _reason + "\n\n" + ManualMessage;

    /// <summary>Runs a compact foreground card pointing to Windows confirmation, with no action buttons.</summary>
    internal static int RunNativeWindow(string name, string icon, Action<nint> stampIdentity)
    {
        if (_manager == 0) return 11; // Manual fallback, no progress window is needed.
        int result = PinWindow.Run(name, icon, visible: true, (hwnd, complete) =>
        {
            stampIdentity(hwnd);
            _complete = complete;
            OnActivated(hwnd);
        });
        _complete = null;
        return result;
    }

    /// <summary>
    /// Queries the OLD caller identity while its source shortcut is still intact. A missing or
    /// mismatched link cannot prove absence; unsupported, failed and bounded-out queries are Unknown.
    /// The hidden query pump never submits a pin request or changes foreground focus. Call only
    /// from the dedicated --check-only process, never before a different identity in this process.
    /// </summary>
    internal static ExistingPinState CheckExistingPin(string launcherId, string aumid)
    {
        _checkOnly = true;
        try
        {
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(aumid));
            if (!InitializeNative(aumid, needManager: true)) return ExistingPinState.Unknown;
            if (!PinShortcut.HasMatchingIdentity(launcherId, aumid))
            {
                Log("previous-pin: unknown; matching Start shortcut unavailable");
                return ExistingPinState.Unknown;
            }
            int result = PinWindow.Run("Check launcher pin", null, visible: false, (hwnd, complete) =>
            {
                _complete = complete;
                OnActivated(hwnd);
            });
            if (result == 6) return ExistingPinState.Pinned;
            if (result == 7) return ExistingPinState.NotPinned;
            return ExistingPinState.Unknown;
        }
        catch (Exception ex)
        {
            Log($"previous-pin: unknown hr=0x{ex.HResult:X8}; {ex.Message}");
            return ExistingPinState.Unknown;
        }
        finally
        {
            CloseOperation();
            Release(_manager);
            _manager = 0;
            if (_initialized) RoUninitialize();
            _initialized = false;
            _checkOnly = false;
            _checking = false;
            _requesting = false;
            _manualFallback = false;
            _pinRequestStarted = false;
            _attempts = 0;
            _window = 0;
            _complete = null;
            _cancelRequested = 0;
            _lastStatus = -1;
        }
    }

    /// <summary>Verifies the actual process identity and desktop contract before creating a shell snapshot.</summary>
    internal static bool Prepare(string aumid, string exe, string launcherId, string name, string icon, bool manualOnly)
    {
        _pendingPath = Path.Combine(AppContext.BaseDirectory, $"pin-pending-{launcherId}.txt");
        try
        {
            if (!InitializeNative(aumid, needManager: !manualOnly)) return false;
            if (manualOnly)
            {
                _shortcut = new PinShortcut(aumid, exe, launcherId, name, icon);
                _reason = "Pin this launcher manually.";
                Log("fallback: explicit manual request; no native request");
                return true;
            }

            _shortcut = new PinShortcut(aumid, exe, launcherId, name, icon);
            Log("eligible: unpackaged STA; desktop marker; Start shortcut created");
            return true;
        }
        catch (Exception ex)
        {
            _reason = "Windows could not prepare native pinning. You can pin this launcher manually.";
            Log($"fallback: prepare hr=0x{ex.HResult:X8}; {ex.Message}");
            Release(_manager);
            _manager = 0;
            return false;
        }
    }

    /// <summary>Creates a fresh manager only after the caller has set the intended process AUMID.</summary>
    private static bool InitializeNative(string aumid, bool needManager)
    {
        uint length = 0;
        int packageStatus = GetCurrentPackageFullName(ref length, 0);
        Log($"identity packageStatus={packageStatus}; aumid={aumid}");
        if (packageStatus != 15700 /* APPMODEL_ERROR_NO_PACKAGE */)
        {
            _reason = "Windows started this helper with package identity, so native pinning cannot target this individual launcher.";
            Log("fallback: inherited-package-identity");
            return false;
        }

        Marshal.ThrowExceptionForHR(RoInitialize(0 /* RO_INIT_SINGLETHREADED */));
        _initialized = true;
        if (!needManager) return true;

        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(LafKey);
        if (key?.GetValue(LafSeed) is int required && required != 0)
        {
            _reason = "This Windows version requires restricted access for native pinning. You can pin this launcher manually.";
            Log("fallback: laf-token-required");
            return false;
        }

        string className = "Windows.UI.Shell.TaskbarManager";
        Marshal.ThrowExceptionForHR(WindowsCreateString(className, (uint)className.Length, out nint classId));
        try
        {
            int hr = RoGetActivationFactory(classId, new Guid("cdfefd63-e879-4134-b9a7-8283f05f9480"), out nint marker);
            if (hr < 0)
            {
                _reason = "This Windows version does not support native pinning from desktop apps.";
                Log($"fallback: desktop-marker hr=0x{hr:X8}");
                return false;
            }
            Release(marker);

            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, new Guid("db32ab74-de52-4fe6-b7b6-95ff9f8395df"), out nint factory));
            try
            {
                nint manager = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 6))(factory, &manager));
                _manager = manager;
            }
            finally { Release(factory); }
        }
        finally { WindowsDeleteString(classId); }

        return true;
    }

    /// <summary>Schedules one guarded timer while the native window's top-level message pump runs.</summary>
    internal static void OnActivated(nint hwnd)
    {
        if (_manager == 0 || _manualFallback) return;
        _window = hwnd;
        _operationStarted = Stopwatch.GetTimestamp();
        _firstTimerCallback = true;
        if (SetTimer(hwnd, 1, 100, TimerCallback) == 0)
        {
            ShowFallback("Windows could not start native pinning.");
        }
        else if (!_checkOnly) Log("native-window: operation timer armed");
    }

    /// <summary>Each callback performs at most one API transition. A guard covers COM reentrancy.</summary>
    private static void OnTimer(nint hwnd, uint message, nuint timer, uint time)
    {
        if (_checking) return;
        _checking = true;
        try
        {
            if (_firstTimerCallback)
            {
                _firstTimerCallback = false;
                if (!_checkOnly) Log("native-window: first operation timer callback");
            }
            if (_operation != 0)
            {
                int status = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(_info, 7))(_info, &status));
                if (status != _lastStatus || Stopwatch.GetElapsedTime(_lastStatusLog) >= TimeSpan.FromSeconds(15))
                {
                    Log($"operation: {(_pinRequestStarted ? "pin" : "check")} status={status}; elapsedSeconds={Stopwatch.GetElapsedTime(_operationStarted).TotalSeconds:F1}");
                    _lastStatus = status;
                    _lastStatusLog = Stopwatch.GetTimestamp();
                }
                if (status == 0)
                {
                    CheckDeadline();
                    return; // Started, leave the STA free to dispatch messages.
                }
                if (status != 1 /* Completed */)
                {
                    // Cancellation is advisory and a terminal error does not prove no pin appeared.
                    // Keep resources unless Windows actually returns a completed false result.
                    Log($"operation: terminal status={status}; outcome unconfirmed");
                    CloseOperation(terminal: true);
                    if (_checkOnly) Complete(5);
                    else if (_pinRequestStarted) FinishUnconfirmed();
                    else ShowFallback("Windows could not check whether this launcher is pinned.");
                    return;
                }

                byte pinned = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(_operation, 8))(_operation, &pinned));
                CloseOperation(terminal: true);
                if (_checkOnly)
                {
                    Log(pinned != 0 ? "previous-pin: pinned" : "previous-pin: not-pinned");
                    Complete(pinned != 0 ? 6 : 7);
                    return;
                }
                if (pinned != 0)
                {
                    Log("result: pinned");
                    KillTimer(hwnd, timer);
                    Complete(6);
                    return;
                }
                if (_requesting)
                {
                    // Windows returns false for both user decline and denial without a prompt.
                    // Never turn that into a second manual prompt, or replace the previous identity.
                    Log("result: not-pinned (Windows returned false: declined or denied)");
                    KillTimer(hwnd, timer);
                    Complete(2);
                    return;
                }
                if (_cancelRequested != 0)
                {
                    ShowFallback("Windows took too long to check this launcher. You can pin it manually.");
                    return;
                }
                _requesting = true;
            }

            if (!_checkOnly)
            {
                if (GetForegroundWindow() != hwnd)
                {
                    if (++_attempts >= 30) ShowFallback("Bring this launcher to the foreground to pin it manually.");
                    return;
                }

                byte allowed = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(_manager, 7))(_manager, &allowed));
                if (allowed == 0)
                {
                    // Give the shell a bounded chance to index the newly written Start entry.
                    if (++_attempts >= 30) ShowFallback("Windows is not allowing a native pin request right now.");
                    return;
                }

            }

            nint operation = 0;
            int slot = _requesting ? 10 : 8;
            if (_requesting)
            {
                // Preserve the attempted identity before the request, including unexpected exits.
                // A late pin may still use these resources; the marker does not prohibit a retry.
                _shortcut?.MarkPending();
                _pinRequestStarted = true;
            }
            _cancelRequested = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_manager, slot))(_manager, &operation));
            _operation = operation;
            _lastStatus = -1;
            _info = Query(operation, new Guid("00000036-0000-0000-C000-000000000046"));
            Log(_requesting ? "request: Windows confirmation started" : "check: current app pinned");
        }
        catch (Exception ex)
        {
            Log($"native-error hr=0x{ex.HResult:X8}; {ex.Message}");
            if (_checkOnly)
            {
                Complete(5);
            }
            else if (_pinRequestStarted)
            {
                FinishUnconfirmed();
            }
            else
            {
                ShowFallback("Windows could not offer native pinning. You can pin this launcher manually.");
            }
        }
        finally { _checking = false; }
    }

    /// <summary>
    /// Cancels after a bounded wait. Cancel only requests cancellation; an unconfirmed result must
    /// keep its shortcut and icon because Windows may still complete the pin independently.
    /// </summary>
    private static void CheckDeadline()
    {
        if (_cancelRequested == 0)
        {
            TimeSpan deadline = _checkOnly ? TimeSpan.FromSeconds(5) : RequestDeadline;
            if (Stopwatch.GetElapsedTime(_operationStarted) < deadline) return;
            _cancelRequested = Stopwatch.GetTimestamp();
            int hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_info, 9))(_info);
            Log($"cancel: deadline reached; advisory Cancel hr=0x{hr:X8}");
            // The foreground card closes after the grace period without offering a second pin action.
            return;
        }

        TimeSpan grace = _checkOnly ? TimeSpan.FromSeconds(1) : CancellationGrace;
        if (Stopwatch.GetElapsedTime(_cancelRequested) >= grace)
        {
            if (_checkOnly) Complete(5);
            else if (_pinRequestStarted) FinishUnconfirmed();
            else ShowFallback("Windows could not check whether this launcher is pinned.");
        }
    }

    /// <summary>Closes without claiming success or rolling back resources a late pin may reference.</summary>
    private static void FinishUnconfirmed()
    {
        Log("result: unconfirmed; preserving snapshot and pending marker");
        KillTimer(_window, 1);
        Complete(3);
    }

    private static void ShowFallback(string reason)
    {
        _reason = reason;
        _manualFallback = true;
        Log("fallback: " + reason);
        Complete(_checkOnly ? 5 : 11);
    }

    private static void Complete(int result)
    {
        KillTimer(_window, 1);
        _complete?.Invoke(result);
    }

    private static void CloseOperation(bool terminal = false)
    {
        if (_info != 0 && terminal)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_info, 10))(_info);
            if (hr < 0) Log($"operation: Close hr=0x{hr:X8}");
        }
        Release(_info);
        Release(_operation);
        _info = 0;
        _operation = 0;
    }

    /// <summary>Restores a cancelled snapshot and releases apartment-bound native references on their STA.</summary>
    internal static void Finish(int dialogResult)
    {
        bool pinned = dialogResult == 6 || dialogResult == 1;
        bool unconfirmed = dialogResult == 3;
        if (_window != 0) KillTimer(_window, 1);
        if (pinned) Log("dialog: pin confirmed");
        else if (unconfirmed) Log("dialog: unconfirmed; keeping previous identity and pending resources");
        else Log("dialog: cancelled; keeping previous identity");
        try
        {
            _shortcut?.Complete(pinned, unconfirmed,
                ex => Log($"snapshot-metadata-error: {ex.Message}"));
            if (pinned && _shortcut == null && _pendingPath != null) File.Delete(_pendingPath);
        }
        catch (Exception ex) { Log($"snapshot-completion-error: {ex.Message}"); }
        CloseOperation();
        Release(_manager);
        _manager = 0;
        if (_initialized) RoUninitialize();
    }

    /// <summary>Pin-only diagnostics stay beside the stable helper, away from the latency-critical launch path.</summary>
    internal static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "taskbar-pin.log"), $"{DateTimeOffset.UtcNow:O} {message}\n"); }
        catch { /* Diagnostics must not prevent a pin or its manual fallback. */ }
    }
}

/// <summary>A supported live result is distinct from a caller identity Windows could not resolve.</summary>
internal enum ExistingPinState { Unknown, Pinned, NotPinned }
