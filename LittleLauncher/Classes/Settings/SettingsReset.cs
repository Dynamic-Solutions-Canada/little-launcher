using System.Text.Json;
using System.Text.Json.Nodes;
using LittleLauncher.ViewModels;
using WinUIAppKit.Settings;

namespace LittleLauncher.Classes.Settings;

/// <summary>
/// What "Reset settings" (CONF-4) puts back to its default and what it keeps. Reset means the app's
/// preferences, never the user's content: launchers, their items and bookmarks, sync setup and
/// installed browser extensions all carry over.
/// </summary>
/// <remarks>
/// <para><b>Every saved setting is in exactly one of the two lists below.</b> A new setting goes in
/// <see cref="Kept"/> when it is the user's content or setup (anything they would have to rebuild
/// by hand), and in <see cref="Reset"/> when it is a preference. The Sync harness fails when a
/// saved property is in neither list or in both, so a new setting cannot be wiped by default.</para>
/// <para>Dependency-free apart from the model and the kit's store options, so the harness can
/// compile it in. Names are the stored property names.</para>
/// </remarks>
internal static class SettingsReset
{
    /// <summary>The settings a reset carries over unchanged.</summary>
    public static IReadOnlyList<string> Kept { get; } =
    [
        // The user's launchers, with their items, bookmarks and sharing setup, and the stamp sync
        // compares to decide which copy is newer.
        nameof(UserSettings.Launchers),
        nameof(UserSettings.LaunchersModifiedUtc),

        // Launchers switched off on this device: part of the launcher setup, not a preference.
        nameof(UserSettings.DisabledLauncherIds),

        // Sync destinations and how they connect. Credentials are in ProtectedStore and untouched.
        nameof(UserSettings.SyncProvider),
        nameof(UserSettings.EnabledSyncProviders),
        nameof(UserSettings.SyncFolderPath),
        nameof(UserSettings.WebDavUrl),
        nameof(UserSettings.WebDavUsername),
        nameof(UserSettings.SftpHost),
        nameof(UserSettings.SftpPort),
        nameof(UserSettings.SftpUsername),
        nameof(UserSettings.SftpPrivateKeyPath),
        nameof(UserSettings.SftpRemotePath),
        nameof(UserSettings.SftpAutoSync),
        nameof(UserSettings.SftpAutoSyncInterval),

        // Browser extensions installed onto the web launchers' profiles, and those on the header.
        nameof(UserSettings.BrowserExtensions),
        nameof(UserSettings.BrowserExtensionFolders),
        nameof(UserSettings.PinnedBrowserExtensions),

        // Windows owns Start with Windows; the setting mirrors it and must not disagree with it.
        nameof(UserSettings.Startup),

        // Version stamps, so a reset never looks like an upgrade.
        nameof(UserSettings.LastRunVersion),
        nameof(UserSettings.LastKnownVersion),
    ];

    /// <summary>The preferences a reset returns to their defaults.</summary>
    public static IReadOnlyList<string> Reset { get; } =
    [
        nameof(UserSettings.AppTheme),
        nameof(UserSettings.LanguageOverride),
        nameof(UserSettings.FlyoutAnimationsEnabled),
        nameof(UserSettings.DisableWebLauncherShortcuts),
        nameof(UserSettings.ProfilesWithoutPasswordManager),
        nameof(UserSettings.ShowWebLauncherNotice),
        nameof(UserSettings.TaskbarWidgetEnabled),
        nameof(UserSettings.TaskbarWidgetSelectedMonitor),
        nameof(UserSettings.TaskbarWidgetPosition),
        nameof(UserSettings.TaskbarWidgetPadding),
        nameof(UserSettings.TaskbarWidgetManualPadding),
        nameof(UserSettings.SettingsWindowX),
        nameof(UserSettings.SettingsWindowY),
        nameof(UserSettings.SettingsWindowWidth),
        nameof(UserSettings.SettingsWindowHeight),
        nameof(UserSettings.SettingsWindowMaximized),

        // Legacy fields, read only when a file has no launchers yet; never needed after a reset.
        nameof(UserSettings.NIconHide),
        nameof(UserSettings.TrayIconMode),
        nameof(UserSettings.CustomTrayIconPath),
    ];

    /// <summary>
    /// The settings after a reset: <paramref name="defaults"/> with every <see cref="Kept"/> value
    /// copied from <paramref name="current"/>. Neither argument is changed.
    /// </summary>
    /// <param name="current">The settings in use before the reset.</param>
    /// <param name="defaults">Freshly created defaults, as the store writes them.</param>
    /// <returns>A new settings object; the caller still runs the post-load steps on it.</returns>
    public static UserSettings CarryOver(UserSettings current, UserSettings defaults)
    {
        var options = JsonSettingsStore<UserSettings>.DefaultOptions;
        var source = JsonSerializer.SerializeToNode(current, options)!.AsObject();
        var result = JsonSerializer.SerializeToNode(defaults, options)!.AsObject();

        foreach (string name in Kept)
        {
            if (source.TryGetPropertyValue(name, out var value))
            {
                result[name] = value?.DeepClone();
            }
            else
            {
                // Nulls are never written, so a kept setting that is null has no node: drop the
                // default's node too, leaving the property at its (null) initial value.
                result.Remove(name);
            }
        }

        return result.Deserialize<UserSettings>(options)
            ?? throw new JsonException("Reset settings deserialized to null.");
    }
}
