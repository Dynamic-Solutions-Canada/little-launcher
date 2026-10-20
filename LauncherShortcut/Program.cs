using System.Diagnostics;
using LittleLauncher.Shared;
using System.Runtime.InteropServices;

namespace LauncherShortcut;

/// <summary>
/// Tiny companion exe intended to be pinned to the Windows taskbar.
///
/// Default mode:  Signals the main Little Launcher app to show its flyout via
///                a registered window message, passing the cursor position, then exits.
///                If Little Launcher is not running, launches it first, waits for its
///                window to appear, then signals the flyout.
/// --item mode:   Signals the main app to launch one entry of the launcher - an item, or a
///                bookmark for a web launcher - rather than showing the flyout. This is what a
///                task on the pinned button's jump list runs; the app publishes those tasks and
///                bakes the position and token into each one. Same "start the app if it is not
///                running" path as the default mode.
/// --pin mode:    Requests Windows confirmation using a matching Start menu shortcut.
///                Unsupported systems retain the manual taskbar context-menu flow.
///                A shortcut and window relaunch properties are never used together.
///
/// AUMID format: LittleLauncher.Launcher.{guid}.{uniqueStamp} (GUID stamp from current app;
/// legacy direct-helper fallback uses TickCount64).
///   WARNING: Windows caches pin display names per AUMID in CloudStore. If you
///   ever need to change the pin display name format, you MUST change the AUMID
///   format to bust the cache. Simply changing RelaunchDisplayNameResource is
///   not sufficient — the cached name persists across unpin/repin cycles.
///
/// Arguments:
///   --launcher {guid}  Target launcher ID
///   --name {name}      Launcher display name (used for pin title: "Little Launcher - {name}")
///   --item {index}     Launch this entry of the launcher instead of showing the flyout
///   --token {n}        Identifies what was published at that position, so an edited launcher
///                      cannot have the wrong entry launched out from under a stale jump list
///   --action {n}       Run one of the launcher's own commands (settings, edit, open in browser).
///                      The number is meaningless here and is forwarded as-is; Models/
///                      LauncherActions.cs on the app side is the only place it means anything.
///   --pin              Request native pin confirmation, with manual fallback
///   --repin            Require old-pin removal only when Windows confirms it is still pinned
///   --manual           Explicitly use the manual pinning fallback
///   --layout {guid}    Legacy alias for --launcher
/// </summary>
static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, int Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hInstance, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
    private static extern bool SetWindowText(nint hWnd, string lpString);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // Shell imports for per-launcher AUMID and window property stores.
    // The window always carries the AUMID. A matching physical Start shortcut supplies
    // launch metadata for native pinning; relaunch properties supply the manual-only fallback.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, out nint ppv);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(nint pvar);

    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    // {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}
    private static readonly Guid AppUserModelGuid = new(0x9F4C2855, 0x9F79, 0x4B39, 0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3);
    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = AppUserModelGuid, pid = 5 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchCommand = new() { fmtid = AppUserModelGuid, pid = 2 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchIconResource = new() { fmtid = AppUserModelGuid, pid = 3 };
    private static readonly PROPERTYKEY PKEY_AppUserModel_RelaunchDisplayNameResource = new() { fmtid = AppUserModelGuid, pid = 4 };

    // CBT hook state for setting the MessageBox icon before it becomes visible
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    private static HookProc? _hookDelegate; // prevent GC while hook is active
    private static nint _cbtHook;
    private static nint _hIconBig;
    private static nint _hIconSmall;

    // Relaunch properties set on the MessageBox HWND via IPropertyStore.
    // These tell the taskbar exactly what command, icon, and display name to use
    // for the manual-only fallback. A shortcut-backed pin sets only the AUMID here,
    // avoiding competing launch metadata from two shell identity sources.
    // The display name format must be "Little Launcher - {name}" — changing this
    // requires a new AUMID format to bust Windows' per-AUMID CloudStore cache.
    private static bool _shortcutIdentity;
    private static string? _relaunchAppId;
    private static string? _relaunchCommand;
    private static string? _relaunchIcon;
    private static string? _relaunchDisplayName;

    private static nint CbtCallback(int code, nint wParam, nint lParam)
    {
        try
        {
            const int HCBT_CREATEWND = 3;
            const int HCBT_ACTIVATE = 5;
            if (code == HCBT_CREATEWND || code == HCBT_ACTIVATE)
            {
                // wParam is the HWND — set our icon immediately.
                // HCBT_CREATEWND fires before the window is visible, so the shell
                // picks up our icon for the taskbar without flashing the embedded one.
                // HCBT_ACTIVATE is a backup to re-set the icon once fully initialized.
                if (_hIconBig != 0)
                    SendMessage(wParam, 0x0080 /* WM_SETICON */, 1 /* ICON_BIG */, _hIconBig);
                if (_hIconSmall != 0)
                    SendMessage(wParam, 0x0080 /* WM_SETICON */, 0 /* ICON_SMALL */, _hIconSmall);
                // Unhook on activate — the dialog is fully initialized at this point
                if (code == HCBT_ACTIVATE && _cbtHook != 0)
                {
                    // Stamp the AUMID; add relaunch metadata only without a matching shortcut.
                    if (_relaunchAppId != null)
                        SetWindowRelaunchProperties(wParam);

                    // Override the MessageBox title to the launcher display name.
                    // The taskbar reads the window title for the button tooltip,
                    // so this must match the relaunch display name.
                    if (_relaunchDisplayName != null)
                        SetWindowText(wParam, _relaunchDisplayName);

                    TaskbarPinning.OnActivated(wParam);

                    UnhookWindowsHookEx(_cbtHook);
                    _cbtHook = 0;
                }
            }
        }
        catch
        {
            // Swallow: an unhandled managed exception here causes
            // STATUS_FATAL_USER_CALLBACK_EXCEPTION and kills the process.
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Ensure cursor coordinates are reported in physical pixels.
        // Without this, a pinned non-MSIX helper can be DPI-virtualized,
        // causing the flyout anchor to be offset on scaled displays.
        _ = SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        // Parse --launcher {guid} argument (may be in any position)
        string? launcherId = null;
        string? launcherName = null;
        string? explicitIconPath = null;
        string? explicitAumid = null;
        string? previousAumid = null;
        int itemIndex = -1;
        int itemToken = 0;
        int launcherAction = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--launcher" || args[i] == "--layout")
                launcherId ??= args[i + 1];
            else if (args[i] == "--name")
                launcherName ??= args[i + 1];
            else if (args[i] == "--icon")
                explicitIconPath ??= args[i + 1];
            else if (args[i] == "--aumid")
                explicitAumid ??= args[i + 1];
            else if (args[i] == "--previous-aumid")
                previousAumid ??= args[i + 1];
            else if (args[i] == "--item" && itemIndex < 0)
                _ = int.TryParse(args[i + 1], out itemIndex);
            else if (args[i] == "--token")
                _ = int.TryParse(args[i + 1], out itemToken);
            else if (args[i] == "--action" && launcherAction == 0)
                _ = int.TryParse(args[i + 1], out launcherAction);
        }

        if (args.Length > 0 && args[0] == "--pin")
        {
            if (args.Contains("--check-only"))
            {
                // A read-only process uses only the old identity and exits before any pin setup.
                ExistingPinState state = ExistingPinState.Unknown;
                if (!string.IsNullOrEmpty(launcherId) && !string.IsNullOrEmpty(previousAumid))
                {
                    state = TaskbarPinning.CheckExistingPin(launcherId, previousAumid);
                }
                if (state == ExistingPinState.Pinned) Environment.ExitCode = 4;
                else if (state == ExistingPinState.NotPinned) Environment.ExitCode = 5;
                else Environment.ExitCode = 3;
                return;
            }

            // Process identity is a startup contract. This process never adopts the old identity
            // or creates an old-identity WinRT object, even when replacing an existing pin.
            string aumid = !string.IsNullOrEmpty(explicitAumid)
                ? explicitAumid
                : $"LittleLauncher.Launcher.{launcherId}.{Environment.TickCount64}";
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(aumid));
            TaskbarPinning.Log($"pin-process: pid={Environment.ProcessId}; aumid={aumid}");

            // Resolve icon path:
            // Both LoadImage (WM_SETICON) and RelaunchIconResource use the same
            // --icon timestamped path. Windows caches icon bitmaps per file path
            // in its icon cache DB, so reusing a stable path across pin attempts
            // serves stale cached bitmaps. The timestamped copy busts this cache.
            // All pin snapshots are retained because an earlier taskbar pin may still use one.
            string loadIconPath = Path.Combine(AppContext.BaseDirectory, "app-icon.ico");

            if (!string.IsNullOrEmpty(explicitIconPath) && File.Exists(explicitIconPath))
                loadIconPath = explicitIconPath;
            else if (!string.IsNullOrEmpty(launcherId))
            {
                string perLauncherIcon = Path.Combine(AppContext.BaseDirectory, $"app-icon-{launcherId}.ico");
                if (File.Exists(perLauncherIcon))
                    loadIconPath = perLauncherIcon;
            }

            ExistingPinState previousState = ExistingPinState.Unknown;
            if (!string.IsNullOrEmpty(launcherId) && !string.IsNullOrEmpty(previousAumid) && !args.Contains("--manual"))
            {
                previousState = PreviousPinProcess.Query(launcherId, previousAumid);
            }
            string displayName = string.IsNullOrEmpty(launcherName) ? "Little Launcher" : $"Little Launcher - {launcherName}";
            string executable = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");
            PinSnapshot? baseline = string.IsNullOrEmpty(launcherId) ? null : PinSnapshot.Read(AppContext.BaseDirectory, launcherId);
            PinSnapshot? current = null;
            if (!string.IsNullOrEmpty(launcherId))
            {
                current = PinSnapshot.Capture(previousAumid ?? "", displayName, executable,
                    $"--launcher {launcherId}", File.Exists(loadIconPath) ? loadIconPath : executable);
            }
            bool knownDetails = baseline != null && baseline.Aumid == previousAumid && current != null;
            if (previousState == ExistingPinState.Pinned && knownDetails && baseline!.HasSameDetails(current!))
            {
                Environment.ExitCode = 4;
                return; // An unchanged existing pin needs neither replacement nor another native prompt.
            }
            // Unknown history does not block a fresh attempt. Only a live pinned result warrants
            // asking the user to remove an old pin; pending markers alone never force manual mode.
            TaskbarPinning.Log($"previous-pin-policy: state={previousState}; replacement-warning={args.Contains("--repin") && previousState == ExistingPinState.Pinned}");
            if (args.Contains("--repin") && previousState == ExistingPinState.Pinned)
            {
                string explanation = knownDetails
                    ? "This launcher is still pinned. Its name, icon or launch details have changed."
                    : "This launcher is still pinned. Its previous details could not be compared. Cancel to keep the existing pin.";
                int confirmed = MessageBoxW(0, explanation + "\n\n" +
                    "Remove its taskbar pin by right-clicking it and choosing \"Unpin from taskbar\". " +
                    "Click OK when none remain to create a fresh pin, or Cancel to stop.",
                    "Replace taskbar pin", 0x00010041);
                if (confirmed != 1) return;
            }

            if (!string.IsNullOrEmpty(launcherId))
            {
                // The main app supplied the new identity so it can record the pin after success.
                // It was set once at process startup, before the optional old-state child or any UI.
                string exePath = Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");
                _relaunchAppId = aumid;
                _relaunchCommand = $"\"{exePath}\" --launcher {launcherId}";
                // Must be an icon *resource* reference — "path,index" — not a bare path.
                // Windows cannot parse a path on its own, so the taskbar silently falls back to
                // the generic document icon: the pinned button came up blank while the window's
                // title bar (which is driven by WM_SETICON, not this) showed the right icon.
                // Same format the Start Menu shortcut's IconLocation already uses.
                _relaunchIcon = File.Exists(loadIconPath) ? $"{loadIconPath},0" : $"{exePath},0";
                _relaunchDisplayName = string.IsNullOrEmpty(launcherName)
                    ? "Little Launcher"
                    : $"Little Launcher - {launcherName}";

                _shortcutIdentity = TaskbarPinning.Prepare(
                    aumid, exePath, launcherId, _relaunchDisplayName, loadIconPath, args.Contains("--manual"));

            }

            if (File.Exists(loadIconPath))
            {
                // Use system metrics for DPI-correct icon sizes
                int bigSize = GetSystemMetrics(11 /* SM_CXICON */);    // 32 @100%, 48 @150%
                int smallSize = GetSystemMetrics(49 /* SM_CXSMICON */); // 16 @100%, 24 @150%

                _hIconBig = LoadImage(0, loadIconPath, 1 /* IMAGE_ICON */, bigSize, bigSize,
                    0x0010 /* LR_LOADFROMFILE */);
                _hIconSmall = LoadImage(0, loadIconPath, 1 /* IMAGE_ICON */, smallSize, smallSize,
                    0x0010 /* LR_LOADFROMFILE */);

            }

            int result = TaskbarPinning.RunNativeWindow(_relaunchDisplayName ?? displayName,
                loadIconPath, SetWindowRelaunchProperties);
            if (result == 11)
            {
                // Instructions belong only to the unsupported/manual fallback, after native work ends.
                _hookDelegate = CbtCallback;
                _cbtHook = SetWindowsHookEx(5 /* WH_CBT */, _hookDelegate, 0, GetCurrentThreadId());
                result = MessageBoxW(0, TaskbarPinning.InitialMessage,
                    _relaunchDisplayName ?? displayName, 0x00010041);
            }

            TaskbarPinning.Finish(result);
            // Native success is IDYES; OK explicitly confirms the user completed manual pinning.
            if (result == 6) Environment.ExitCode = 1;
            else if (result == 1) Environment.ExitCode = 2;
            else if (result == 3) Environment.ExitCode = 3;
            else Environment.ExitCode = 0;

            // Clean up in case the hook wasn't triggered
            if (_cbtHook != 0)
            {
                UnhookWindowsHookEx(_cbtHook);
                _cbtHook = 0;
            }
            return;
        }

        var target = FindWindow(null, "Little Launcher Host");

        if (target == 0)
        {
            // Main app isn't running — launch it, then signal the flyout.
            string myDir = AppContext.BaseDirectory;
            string mainExe = Path.Combine(myDir, "LittleLauncher.exe");

            // Fallback for MSIX: when the companion exe lives in AppData,
            // the main exe isn't in the same directory. Read the breadcrumb
            // file written by the main app during EnsureFlyoutShortcut().
            if (!File.Exists(mainExe))
            {
                string breadcrumb = Path.Combine(myDir, "main-exe-path.txt");
                if (File.Exists(breadcrumb))
                {
                    string candidate = File.ReadAllText(breadcrumb).Trim();
                    if (File.Exists(candidate))
                        mainExe = candidate;
                }
            }

            if (!File.Exists(mainExe))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                Arguments = "--silent",
                WorkingDirectory = Path.GetDirectoryName(mainExe) ?? myDir,
                UseShellExecute = false
            });

            // Poll for the host window to appear (up to 10 seconds).
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(100);
                target = FindWindow(null, "Little Launcher Host");
                if (target != 0)
                    break;
            }

            if (target == 0)
                return;
        }

        // App is running — signal it. A jump list task names an entry to launch; everything else
        // means "show the flyout", and the two are different messages because their two words of
        // payload mean different things.
        if (itemIndex >= 0 && !string.IsNullOrEmpty(launcherId))
        {
            var launchMsg = (int)RegisterWindowMessage($"LittleLauncher_LaunchItem_{launcherId}");
            PostMessage(target, launchMsg, itemIndex, itemToken);
            return;
        }

        if (launcherAction > 0 && !string.IsNullOrEmpty(launcherId))
        {
            var actionMsg = (int)RegisterWindowMessage($"LittleLauncher_LauncherAction_{launcherId}");
            PostMessage(target, actionMsg, launcherAction, 0);
            return;
        }

        GetCursorPos(out var pt);
        // Use a per-launcher message name if a launcher ID was specified,
        // otherwise use the legacy message for backward compat.
        string msgName = string.IsNullOrEmpty(launcherId)
            ? "LittleLauncher_ShowFlyout"
            : $"LittleLauncher_ShowFlyout_{launcherId}";
        var msg = (int)RegisterWindowMessage(msgName);
        PostMessage(target, msg, pt.X, pt.Y);
    }

    /// <summary>
    /// Sets PKEY_AppUserModel_ID, RelaunchCommand, RelaunchIconResource, and
    /// RelaunchDisplayNameResource on the given HWND via its IPropertyStore.
    /// Uses raw COM vtable calls for Native AOT compatibility.
    /// </summary>
    private static unsafe void SetWindowRelaunchProperties(nint hwnd)
    {
        var IID_IPropertyStore = new Guid(0x886D8EEB, 0x8CF2, 0x4446, 0x8D, 0x02, 0xCD, 0xBA, 0x1D, 0xBD, 0xCF, 0x99);
        int hr = SHGetPropertyStoreForWindow(hwnd, ref IID_IPropertyStore, out nint pStore);
        if (hr != 0 || pStore == 0) return;

        try
        {
            nint* vtbl = *(nint**)pStore;
            // IPropertyStore vtable: [0]QI [1]AddRef [2]Release [3]GetCount [4]GetAt [5]GetValue [6]SetValue [7]Commit
            var setValue = (delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, nint, int>)vtbl[6];
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[7];

            if (_relaunchAppId != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_ID, _relaunchAppId);
            if (!_shortcutIdentity && _relaunchCommand != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchCommand, _relaunchCommand);
            if (!_shortcutIdentity && _relaunchIcon != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchIconResource, _relaunchIcon);
            if (!_shortcutIdentity && _relaunchDisplayName != null)
                SetPropString(pStore, setValue, PKEY_AppUserModel_RelaunchDisplayNameResource, _relaunchDisplayName);

            Marshal.ThrowExceptionForHR(commit(pStore));
        }
        finally
        {
            nint* vtbl = *(nint**)pStore;
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
            release(pStore);
        }
    }

    private static unsafe void SetPropString(
        nint pStore,
        delegate* unmanaged[Stdcall]<nint, PROPERTYKEY*, nint, int> setValue,
        PROPERTYKEY key, string value)
    {
        const int PROPVARIANT_SIZE = 24;
        nint pv = Marshal.AllocCoTaskMem(PROPVARIANT_SIZE);
        try
        {
            // Zero-init then write VT_LPWSTR (31) + string pointer
            new Span<byte>((void*)pv, PROPVARIANT_SIZE).Clear();
            Marshal.WriteInt16(pv, 0, 31); // VT_LPWSTR
            Marshal.WriteIntPtr(pv, 8, Marshal.StringToCoTaskMemUni(value));

            Marshal.ThrowExceptionForHR(setValue(pStore, &key, pv));
        }
        finally
        {
            PropVariantClear(pv);
            Marshal.FreeCoTaskMem(pv);
        }
    }
}
