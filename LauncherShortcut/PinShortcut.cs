using System.Runtime.InteropServices;
using LittleLauncher.Shared;
using static LauncherShortcut.NativeMethods;
using static LauncherShortcut.ShellInterop;

namespace LauncherShortcut;

/// <summary>
/// One explicit pin snapshot per launcher. Cancel restores the previous Start entry byte for byte.
/// Known old taskbar pins require manual removal; unknown old state permits a fresh attempt.
/// </summary>
internal sealed unsafe class PinShortcut
{
    private readonly string _directory;
    private readonly Dictionary<string, byte[]> _previous;
    private readonly string _aumid;
    private readonly string _record;
    private readonly string _pending;
    private readonly byte[]? _previousPending;
    private readonly PinSnapshot _snapshot;
    private readonly string _launcherId;

    internal PinShortcut(string aumid, string exe, string launcherId, string displayName, string icon)
    {
        _aumid = aumid;
        _launcherId = launcherId;
        _snapshot = PinSnapshot.Capture(aumid, displayName, exe, $"--launcher {launcherId}", File.Exists(icon) ? icon : exe);
        _record = Path.Combine(AppContext.BaseDirectory, $"pin-shortcut-{launcherId}.txt");
        _pending = Path.Combine(AppContext.BaseDirectory, $"pin-pending-{launcherId}.txt");
        _previousPending = File.Exists(_pending) ? File.ReadAllBytes(_pending) : null;
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Little Launcher Pins", launcherId);
        Directory.CreateDirectory(_directory);
        _previous = Directory.GetFiles(_directory, "*.lnk").ToDictionary(path => path, File.ReadAllBytes);

        try
        {
            // Invalidate any old confirmation record before changing its shortcut, including if
            // the helper exits unexpectedly between replacement and the Windows request.
            MarkPending();

            // Only known old pins require removal acknowledgement. Unknown old state permits this
            // replacement; keep a backup through cancellation and preserve every immutable icon.
            foreach (string path in _previous.Keys)
            {
                File.Delete(path);
            }

            string safeName = new string(displayName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
            if (safeName.Length == 0) safeName = "Little Launcher";
            Write(Path.Combine(_directory, safeName + ".lnk"), exe, launcherId, displayName, icon, aumid);
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, 0, 0);
        }
        catch
        {
            Restore();
            RestorePending();
            throw;
        }
    }

    /// <summary>Retains the new entry only after native success or explicit manual-pin confirmation.</summary>
    internal void Complete(bool pinned, bool unconfirmed, Action<Exception>? metadataError = null)
    {
        if (unconfirmed)
        {
            MarkPending();
            return;
        }
        if (pinned)
        {
            try { _snapshot.Save(AppContext.BaseDirectory, _launcherId); }
            catch (Exception ex)
            {
                // Optional comparison metadata must not invalidate a pin Windows has confirmed.
                // A missing/old baseline remains unknown, rather than falsely classifying an edit.
                metadataError?.Invoke(ex);
            }
            File.WriteAllText(_record, _aumid);
        }
        else
        {
            Restore();
            RestorePending();
            return;
        }
        File.Delete(_pending);
    }

    /// <summary>Records the unresolved local identity so its resources and late pin remain accounted for.</summary>
    internal void MarkPending() => File.WriteAllText(_pending, _aumid);

    /// <summary>Cancellation restores any unresolved marker that predates the current attempt.</summary>
    private void RestorePending()
    {
        if (_previousPending == null) File.Delete(_pending);
        else File.WriteAllBytes(_pending, _previousPending);
    }

    private void Restore()
    {
        foreach (string path in Directory.GetFiles(_directory, "*.lnk"))
        {
            File.Delete(path);
        }
        foreach (var entry in _previous)
        {
            File.WriteAllBytes(entry.Key, entry.Value);
        }
        SHChangeNotify(0x08000000, 0, 0, 0);
    }

    /// <summary>Confirms Windows can resolve this old identity from its intact physical shortcut.</summary>
    internal static bool HasMatchingIdentity(string launcherId, string aumid)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Little Launcher Pins", launcherId);
        if (!Directory.Exists(directory)) return false;
        foreach (string path in Directory.GetFiles(directory, "*.lnk"))
        {
            nint link = CreateLink();
            try
            {
                nint file = Query(link, new Guid("0000010B-0000-0000-C000-000000000046"));
                try
                {
                    fixed (char* text = path)
                    {
                        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Slot(file, 5))(file, text, 0));
                    }
                }
                finally { Release(file); }
                nint store = Query(link, new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"));
                string? id;
                try { id = GetAppId(store); }
                finally { Release(store); }
                if (id != aumid) continue;
                string target = ReadLinkText(link, path: true);
                string arguments = ReadLinkText(link, path: false);
                if (string.Equals(Path.GetFullPath(target), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                    && arguments == $"--launcher {launcherId}") return true;
            }
            finally { Release(link); }
        }
        return false;
    }

    private static string ReadLinkText(nint link, bool path)
    {
        char[] buffer = new char[32768];
        fixed (char* text = buffer)
        {
            int hr = path
                ? ((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)Slot(link, 3))(link, text, buffer.Length, 0, 4)
                : ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, 10))(link, text, buffer.Length);
            Marshal.ThrowExceptionForHR(hr);
        }
        int length = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static nint CreateLink()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(
            new Guid("00021401-0000-0000-C000-000000000046"), 0, 1,
            new Guid("000214F9-0000-0000-C000-000000000046"), out nint link));
        return link;
    }

    /// <summary>Creates the real Start menu link required by TaskbarManager's desktop contract.</summary>
    private static void Write(string path, string exe, string launcherId, string name, string icon, string aumid)
    {
        nint link = CreateLink();
        try
        {
            SetText(link, 20, exe); // IShellLinkW.SetPath
            SetText(link, 11, $"--launcher {launcherId}");
            SetText(link, 9, Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory);
            SetText(link, 7, name);
            string iconFile = File.Exists(icon) ? icon : exe;
            fixed (char* text = iconFile)
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, 17))(link, text, 0));
            }

            nint store = Query(link, new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"));
            try { SetAppId(store, aumid); }
            finally { Release(store); }

            nint file = Query(link, new Guid("0000010B-0000-0000-C000-000000000046"));
            try
            {
                fixed (char* text = path)
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(file, 6))(file, text, 1));
                }
            }
            finally { Release(file); }
        }
        finally { Release(link); }
    }

    private static void SetText(nint link, int slot, string value)
    {
        fixed (char* text = value)
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, slot))(link, text));
        }
    }
}
