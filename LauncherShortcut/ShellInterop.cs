using System.Runtime.InteropServices;

namespace LauncherShortcut;

/// <summary>Small raw COM helpers shared by pinning and shell links, compatible with Native AOT.</summary>
internal static unsafe class ShellInterop
{
    internal static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    internal static nint Query(nint instance, Guid iid)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, &iid, &result));
        return result;
    }

    internal static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
        }
    }

    /// <summary>Writes one LPWSTR property and commits it; the temporary PROPVARIANT owns its text.</summary>
    internal static void SetAppId(nint store, string aumid)
    {
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        byte* variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 31;
        nint text = Marshal.StringToCoTaskMemUni(aumid);
        *(nint*)(variant + 8) = text;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, byte*, int>)Slot(store, 6))(store, &key, variant));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Slot(store, 7))(store));
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    /// <summary>Reads an explicitly stamped shortcut AUMID; missing/unexpected values are not an identity.</summary>
    internal static string? GetAppId(nint store)
    {
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
        byte* variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, byte*, int>)Slot(store, 5))(store, &key, variant));
            return *(ushort*)variant == 31 ? Marshal.PtrToStringUni(*(nint*)(variant + 8)) : null;
        }
        finally { NativeMethods.PropVariantClear((nint)variant); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        internal Guid Format;
        internal uint Id;
    }
}
