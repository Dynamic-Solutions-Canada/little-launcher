using LittleLauncher.Models;
using System.IO;
using System.Security.Cryptography;

namespace LittleLauncher.Services;

/// <summary>Custom launcher image bytes travel with sync; filesystem paths remain local.</summary>
internal static class PortableLauncherIcon
{
    internal const int MaxBytes = 1024 * 1024;

    internal static byte[] Decode(string data)
    {
        if (data.Length > 4 * ((MaxBytes + 2) / 3))
            throw new InvalidDataException("A synced launcher icon exceeds 1 MiB.");
        byte[] bytes = Convert.FromBase64String(data);
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
            throw new InvalidDataException("A synced launcher icon is empty or too large.");
        bool supported = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 })
            || bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 })
            || bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8)
            || bytes.AsSpan().StartsWith("BM"u8);
        if (!supported) throw new InvalidDataException("A synced launcher icon is not a supported image.");
        return bytes;
    }

    internal static byte[] DecodePreview(string data)
    {
        byte[] bytes = Decode(data);
        if (bytes.Length < 24 || !bytes.AsSpan().StartsWith(new byte[] { 137,80,78,71,13,10,26,10 })
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4)) is 0 or > 1024
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4)) is 0 or > 1024)
            throw new InvalidDataException("A portable glyph preview must be a bounded PNG image.");
        return bytes;
    }

    internal static void Capture(Launcher launcher)
    {
        if (TrayIconModes.IsGlyphMode(launcher.TrayIconMode))
        {
            byte[]? preview = launcher.TrayIconPreviewMode == launcher.TrayIconMode && !string.IsNullOrEmpty(launcher.TrayIconPreviewData)
                ? DecodePreview(launcher.TrayIconPreviewData) : MainWindow.ExportLauncherGlyphPng(launcher);
            if (preview is not null && preview.Length <= MaxBytes)
            {
                launcher.TrayIconPreviewData = Convert.ToBase64String(preview);
                launcher.TrayIconPreviewMode = launcher.TrayIconMode;
            }
        }
        else { launcher.TrayIconPreviewData = ""; launcher.TrayIconPreviewMode = ""; }
        if (launcher.TrayIconMode != TrayIconModes.Custom)
        {
            launcher.CustomTrayIconData = "";
            return;
        }
        if (File.Exists(launcher.CustomTrayIconPath))
        {
            if (new FileInfo(launcher.CustomTrayIconPath).Length > MaxBytes)
                throw new InvalidDataException("Choose a custom launcher icon smaller than 1 MiB before uploading.");
            string data = Convert.ToBase64String(File.ReadAllBytes(launcher.CustomTrayIconPath));
            Decode(data);
            launcher.CustomTrayIconData = data;
        }
        else if (!string.IsNullOrEmpty(launcher.CustomTrayIconData))
            Decode(launcher.CustomTrayIconData);
    }

    internal static void Materialize(Launcher launcher)
    {
        if (!string.IsNullOrEmpty(launcher.TrayIconPreviewData)) DecodePreview(launcher.TrayIconPreviewData);
        if (launcher.TrayIconMode != TrayIconModes.Custom || string.IsNullOrEmpty(launcher.CustomTrayIconData)) return;
        byte[] bytes = Decode(launcher.CustomTrayIconData);
        string suffix = bytes.AsSpan().StartsWith(new byte[] { 0, 0, 1, 0 }) ? ".ico" : ".img";
        string path = Path.Combine(MainWindow.GetPhysicalAppDataDir(), "icons", "sync-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + suffix);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            LauncherPayload.WriteAtomic(path, bytes);
        string data = launcher.CustomTrayIconData;
        launcher.CustomTrayIconPath = path;
        launcher.CustomTrayIconData = data; // Path changes clear an obsolete embedded choice.
    }
}
