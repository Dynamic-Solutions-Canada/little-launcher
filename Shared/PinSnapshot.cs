using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleLauncher.Shared;

/// <summary>Local immutable details of the last confirmed pin, shared with the AOT companion.</summary>
internal sealed class PinSnapshot
{
    public string Aumid { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string IconHash { get; init; } = "";

    /// <summary>Captures effective icon bytes rather than settings that may render the same icon.</summary>
    internal static PinSnapshot Capture(string aumid, string displayName, string executable, string arguments, string icon)
    {
        return new PinSnapshot
        {
            Aumid = aumid,
            DisplayName = displayName,
            Executable = Path.GetFullPath(executable),
            Arguments = arguments,
            IconHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(icon))),
        };
    }

    /// <summary>Compares visible/launch details; each new attempt's fresh identity does not count as an edit.</summary>
    internal bool HasSameDetails(PinSnapshot other) =>
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
        && string.Equals(Executable, other.Executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Arguments, other.Arguments, StringComparison.Ordinal)
        && string.Equals(IconHash, other.IconHash, StringComparison.Ordinal);

    /// <summary>Missing, old or unreadable metadata is an unknown baseline, never evidence of an edit.</summary>
    internal static PinSnapshot? Read(string directory, string launcherId)
    {
        try
        {
            string path = MetadataPath(directory, launcherId);
            if (!File.Exists(path)) return null;
            var snapshot = JsonSerializer.Deserialize(File.ReadAllText(path), PinSnapshotJsonContext.Default.PinSnapshot);
            return snapshot?.IsValid == true ? snapshot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Rejects partial or malformed baselines instead of classifying them as changed/unchanged.</summary>
    private bool IsValid =>
        !string.IsNullOrWhiteSpace(Aumid)
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(Executable)
        && Path.IsPathFullyQualified(Executable)
        && !string.IsNullOrWhiteSpace(Arguments)
        && IconHash is { Length: 64 }
        && IconHash.All(Uri.IsHexDigit);

    /// <summary>Publishes metadata only after pin confirmation, using an atomic same-directory replacement.</summary>
    internal void Save(string directory, string launcherId)
    {
        string path = MetadataPath(directory, launcherId);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, PinSnapshotJsonContext.Default.PinSnapshot));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string MetadataPath(string directory, string launcherId) =>
        Path.Combine(directory, $"pin-snapshot-{launcherId}.json");
}

[JsonSerializable(typeof(PinSnapshot))]
internal partial class PinSnapshotJsonContext : JsonSerializerContext
{
}
