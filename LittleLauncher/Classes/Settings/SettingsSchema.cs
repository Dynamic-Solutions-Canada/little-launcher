using System.Text.Json.Nodes;

namespace LittleLauncher.Classes.Settings;

/// <summary>
/// The settings file's schema steps (CONF-3), for winui-app-kit's <c>JsonSettingsStore</c>. The step
/// at index <c>n</c> upgrades a file from schema version <c>n</c> to <c>n + 1</c>; a file with no
/// <c>SchemaVersion</c> is version 0, so the current version is the number of steps.
/// </summary>
/// <remarks>
/// <para>Steps are never removed, reordered or edited once shipped: an installed copy may be any
/// number of versions behind. Each step works on the JSON as it was written then, so it names
/// properties by their stored spelling, not with <c>nameof</c>, which would follow a later rename.</para>
/// <para>Dependency-free (no WinUI, no app services) so the settings harness under <c>tests/</c>
/// can compile it in and run every step against real version 0 files.</para>
/// </remarks>
internal static class SettingsSchema
{
    /// <summary>Every step, in order. The current schema version is <c>Migrations.Count</c>.</summary>
    public static IReadOnlyList<Action<JsonObject>> Migrations { get; } =
    [
        WrittenDefaults,
    ];

    /// <summary>
    /// Version 0 to 1: the switch from omitted defaults to written defaults.
    /// </summary>
    /// <remarks>
    /// <para>Version 0 files were written with <c>WhenWritingDefault</c>, which left out every
    /// property holding its CLR default (<c>false</c>, <c>0</c>, null). Read back, a missing key
    /// takes the constructor's value, and the model was arranged so that matched what had been
    /// meant, with one exception handled here.</para>
    /// <para><c>Startup</c> defaults to <c>true</c>, but files written before it was always
    /// written stored "off" by leaving it out. A missing <c>Startup</c> therefore means off, the
    /// rule the old loader applied on every read. Every other missing key already loads as it
    /// always has, so nothing else changes.</para>
    /// </remarks>
    private static void WrittenDefaults(JsonObject root)
    {
        if (!root.ContainsKey("Startup"))
        {
            root["Startup"] = false;
        }
    }
}
