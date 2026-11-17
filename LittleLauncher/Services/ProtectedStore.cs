using System.Text;

namespace LittleLauncher.Services;

/// <summary>
/// Small objects persisted outside settings.json, encrypted to the current Windows user. A thin app
/// adapter over winui-app-kit's <see cref="WinUIAppKit.Settings.ProtectedStore"/>, which does the
/// encryption and the atomic writes.
/// </summary>
/// <remarks>
/// <para><b>Everything sync-related that is a credential belongs here, not in settings.json.</b>
/// That file is exported, imported, backed up and, the deciding reason, <i>uploaded by the sync
/// feature itself</i>. A refresh token or a WebDAV password in it would be copied to every machine
/// and into whatever server or folder is configured, which is the one place it must never go.</para>
/// <para>The folder, the file-name pattern and the entropy are permanent: they are what installed
/// copies already wrote, and changing any of them would sign every user out of every sync
/// destination.</para>
/// <para>This stays a static class with the old name so callers, and the OAuth harness's in-memory
/// stand-in, are unchanged.</para>
/// </remarks>
internal static class ProtectedStore
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// The kit store. The folder is the physical data folder, resolved on each call, because the
    /// files predate the kit there and a packaged install's %AppData% is redirected elsewhere.
    /// </summary>
    private static readonly WinUIAppKit.Settings.ProtectedStore Store = new(
        MainWindow.GetPhysicalAppDataDir,
        "cloud-{0}.dat",
        Encoding.UTF8.GetBytes("LittleLauncher.CloudTokens.v1"),
        logError: (message, ex) => Logger.Warn(ex, message));

    /// <summary>Load a stored object, or null when absent or unreadable.</summary>
    /// <remarks>
    /// Unlike the kit, an unreadable file is deleted. Only this process reads these files, and a
    /// blob that will not decrypt (a restored profile, another Windows user, corruption) never
    /// will, so deleting it turns it into the ordinary signed-out state the Sync page already
    /// explains, instead of retrying the decrypt on every sync.
    /// </remarks>
    public static T? Load<T>(string name) where T : class
    {
        T? value = Store.Load<T>(name);
        if (value == null && Store.Exists(name))
        {
            Store.Clear(name);
        }

        return value;
    }

    /// <summary>Persist an object, encrypted to the current user. Failures are logged, not thrown.</summary>
    public static void Save<T>(string name, T value) => Store.Save(name, value);

    /// <summary>Forget a stored object.</summary>
    public static void Clear(string name) => Store.Clear(name);
}
