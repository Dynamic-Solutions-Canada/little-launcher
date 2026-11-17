using LittleLauncher.Models;
using LittleLauncher.ViewModels;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Serialization;
using WinUIAppKit.Settings;

namespace LittleLauncher.Classes.Settings;

/// <summary>
/// Manages the application settings, kept in settings.json in %AppData%\LittleLauncher through
/// winui-app-kit's versioned <see cref="JsonSettingsStore{T}"/>. On first load, migrates from the
/// legacy settings.xml (XmlSerializer).
/// </summary>
public static class SettingsManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LittleLauncher");

    private static string SettingsFilePath => Path.Combine(SettingsDir, "settings.json");

    /// <summary>Legacy XML settings path — used for one-time migration only.</summary>
    private static string LegacyXmlPath => Path.Combine(SettingsDir, "settings.xml");

    /// <summary>
    /// The settings file's serializer options: winui-app-kit's, which write every value, defaults
    /// included, and omit only nulls. Also used for the item export, so it reads the same way.
    /// </summary>
    internal static JsonSerializerOptions JsonOptions => JsonSettingsStore<UserSettings>.DefaultOptions;

    /// <summary>
    /// settings.json, versioned by <see cref="SettingsSchema"/> (CONF-3). The path is unchanged
    /// from before the kit: the redirected %AppData% folder in a packaged install.
    /// </summary>
    private static readonly JsonSettingsStore<UserSettings> Store = new(SettingsFilePath, SettingsSchema.Migrations);

    /// <summary>Set when this launch replaced an unreadable file; see <see cref="TakeResetNotice"/>.</summary>
    private static bool _pendingResetNotice;

    /// <summary>Where the unreadable file was kept, for the reset notice.</summary>
    private static string? _resetBackupPath;

    /// <summary>Raised after settings have been written to disk.</summary>
    /// <remarks>
    /// An event rather than a call into whatever needs to know, so the dependency points the way
    /// it should: this class knows nothing about the surfaces that have to be rebuilt when
    /// something changes, and they subscribe to it. First subscriber is
    /// <c>Services.JumpListService</c>, which republishes the taskbar jump lists.
    /// </remarks>
    internal static event Action? Saved;

    private static UserSettings _current = new();

    /// <summary>Only an absent settings file qualifies for first-run OS registration.</summary>
    internal static bool IsFirstRun { get; private set; }

    /// <summary>
    /// The current user settings stored in the app.
    /// </summary>
    public static UserSettings Current
    {
        get
        {
            if (_current == null)
            {
                _current = new UserSettings();
            }
            return _current;
        }
        set => _current = value;
    }

    /// <summary>
    /// Flat enumeration of all <see cref="LauncherItem"/> objects across every launcher's Items
    /// collection (including group children). Use when a search must span all layouts.
    /// </summary>
    public static IEnumerable<LauncherItem> AllItems =>
        _current?.Launchers
            .SelectMany(l => l.Items.SelectMany(i => i.IsGroup ? new[] { i }.Concat(i.Children) : [i]))
        ?? Enumerable.Empty<LauncherItem>();

    /// <summary>Version of the running assembly, or null if it cannot be determined.</summary>
    private static Version? AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>The version that introduced web launchers, and so the one-time notice about them.</summary>
    private static readonly Version WebLauncherVersion = new(1, 25, 0);

    /// <summary>
    /// Raises any one-time notices this upgrade has earned. Call **before**
    /// <see cref="StampVersion"/>, which overwrites the evidence.
    /// </summary>
    /// <remarks>
    /// Only ever called from the two load paths that found an existing settings file. A fresh
    /// install has never seen the old behaviour, so announcing a change to it is pure noise —
    /// which is why the defaults path deliberately does not call this.
    /// </remarks>
    private static void RaiseUpgradeNotices(string? previousVersion)
    {
        if (!IsOlderThan(previousVersion, WebLauncherVersion))
        {
            return;
        }

        _current.ShowWebLauncherNotice = true;
        _current.UpgradeNoticesJustRaised = true;
    }

    /// <summary>
    /// True when a recorded version predates <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// A missing or unparseable value counts as older: settings files written before
    /// <see cref="UserSettings.LastRunVersion"/> existed carry nothing, and those are upgraders
    /// too — arguably the oldest ones.
    /// </remarks>
    private static bool IsOlderThan(string? version, Version threshold)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return true;
        }

        return !Version.TryParse(version, out var parsed) || parsed < threshold;
    }

    /// <summary>
    /// Records the running version in the settings file, so an upgrade can be detected later.
    /// </summary>
    /// <remarks>
    /// A settings file with no recorded version predates this field, which is what identifies
    /// an install upgrading from before it existed. Any future one-time upgrade notice should
    /// compare <c>LastRunVersion</c> here and only fire when a settings file already existed —
    /// a fresh install has never seen whatever is being announced.
    /// </remarks>
    private static void StampVersion()
    {
        var current = AppVersion;
        if (current == null)
        {
            return;
        }

        string stamped = current.ToString(3);
        if (_current.LastRunVersion != stamped)
        {
            _current.LastRunVersion = stamped;
            SaveSettings();
        }
    }

    /// <summary>
    /// Loads <see cref="Current"/> at startup: the legacy XML file once if it is all there is,
    /// otherwise settings.json through the versioned store, which migrates older files (after
    /// backing them up) and replaces an unreadable one with defaults (keeping it as <c>.bad</c>).
    /// </summary>
    public static UserSettings RestoreSettings()
    {
        bool hasLegacyXml = File.Exists(LegacyXmlPath);

        // Before the store: with no settings.json the store would report a first run and write
        // defaults, burying an install that only ever had the XML file.
        if (!File.Exists(SettingsFilePath) && hasLegacyXml && TryMigrateLegacyXml())
        {
            IsFirstRun = false;
            return _current;
        }

        var result = Store.Load();
        IsFirstRun = result.Outcome == SettingsLoadOutcome.FirstRun && !hasLegacyXml;

        if (result.NeedsResetNotice)
        {
            // Settings opens on this launch, even a silent one, to say so (CONF-2, UI-15).
            _pendingResetNotice = true;
            _resetBackupPath = result.BackupPath;
        }

        // Upgrade notices are for an existing file only: a fresh or reset install has never seen
        // the old behaviour being announced.
        bool existingFile = result.Outcome is SettingsLoadOutcome.Loaded
            or SettingsLoadOutcome.Migrated
            or SettingsLoadOutcome.NewerVersion;
        Adopt(result.Settings, existingFile);
        Logger.Info($"Settings restored ({result.Outcome})");
        return _current;
    }

    /// <summary>
    /// Replaces <see cref="Current"/> with a settings file the user chose, and saves it.
    /// </summary>
    /// <returns>False, with the current settings untouched, when the file cannot be read.</returns>
    /// <remarks>
    /// The store is pointed at a private copy, never the chosen file: loading migrates and
    /// rewrites the file it reads and leaves backups beside it, which must not happen to a file
    /// the user only meant to read. Defaults are never adopted in place of an unreadable file.
    /// </remarks>
    public static bool ImportSettings(string path)
    {
        string folder = Path.Combine(Path.GetTempPath(), "LittleLauncher-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, "settings.json");
            File.Copy(path, copy);

            var result = new JsonSettingsStore<UserSettings>(copy, SettingsSchema.Migrations).Load();
            if (result.Outcome is not (SettingsLoadOutcome.Loaded or SettingsLoadOutcome.Migrated or SettingsLoadOutcome.NewerVersion))
            {
                Logger.Warn($"Imported settings file could not be read ({result.Outcome}); current settings kept");
                return false;
            }

            Adopt(result.Settings, existingFile: true);
            SaveSettings();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error importing settings; current settings kept");
            return false;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not delete the import folder");
            }
        }
    }

    /// <summary>
    /// Saves the app settings to settings.json, atomically, and raises <see cref="Saved"/>.
    /// Failures are logged by the store, not thrown.
    /// </summary>
    public static void SaveSettings()
    {
        Store.Save(_current);
        Saved?.Invoke();
    }

    /// <summary>
    /// "Reset settings" (CONF-4): preferences go back to their defaults and the user's content and
    /// setup carry over (<see cref="SettingsReset"/>). The previous file is kept beside the new one as
    /// <c>settings.json.&lt;UTC time&gt;.reset</c>; credentials and Windows' startup state are untouched.
    /// </summary>
    /// <remarks>
    /// Data only: the caller applies the reset theme and language and rebuilds what shows them.
    /// </remarks>
    public static void ResetPreferences()
    {
        var before = _current;
        var defaults = Store.Reset();
        Adopt(SettingsReset.CarryOver(before, defaults.Settings), existingFile: false);
        SaveSettings();
        Logger.Info($"Settings reset to defaults; previous file kept as {Path.GetFileName(defaults.BackupPath ?? "(none)")}");
    }

    /// <summary>Writes the current settings to a file the user chose, in the settings file format.</summary>
    public static void ExportSettings(string path) =>
        new JsonSettingsStore<UserSettings>(path, SettingsSchema.Migrations).Save(_current);

    /// <summary>
    /// True once, on the launch that replaced an unreadable settings file with defaults, and gives
    /// the backup's path for the notice. Later calls return false, so the notice is shown once.
    /// </summary>
    internal static bool TakeResetNotice(out string? backupPath)
    {
        backupPath = _resetBackupPath;
        bool pending = _pendingResetNotice;
        _pendingResetNotice = false;
        return pending;
    }

    /// <summary>Whether this launch must open Settings to show the reset notice.</summary>
    internal static bool HasPendingResetNotice => _pendingResetNotice;

    /// <summary>
    /// Makes loaded settings current and runs the post-load steps every load path shares.
    /// </summary>
    /// <param name="settings">The settings just read.</param>
    /// <param name="existingFile">True when they came from a file this app wrote before, which
    /// is what earns one-time upgrade notices.</param>
    private static void Adopt(UserSettings settings, bool existingFile)
    {
        _current = settings;
        _current.CompleteInitialization();
        NormalizeAllGlyphs();
        if (existingFile)
        {
            // Before StampVersion, which replaces the value being compared against.
            RaiseUpgradeNotices(_current.LastRunVersion);
        }

        StampVersion();
    }

    /// <summary>
    /// Converts the settings.xml written by early versions, saves it as settings.json
    /// and renames the XML file to <c>.bak</c>.
    /// </summary>
    /// <returns>False when the XML file cannot be read; the caller then loads settings.json as usual.</returns>
    private static bool TryMigrateLegacyXml()
    {
        try
        {
            Logger.Info("Migrating settings from XML to JSON");
            UserSettings? xmlSettings;
            using (var reader = new StreamReader(LegacyXmlPath))
            {
                xmlSettings = new XmlSerializer(typeof(UserSettings)).Deserialize(reader) as UserSettings;
            }

            if (xmlSettings == null)
            {
                return false;
            }

            Adopt(xmlSettings, existingFile: true);
            SaveSettings();
            try
            {
                File.Move(LegacyXmlPath, LegacyXmlPath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Could not rename the legacy XML settings file");
            }

            Logger.Info("Settings migrated from XML to JSON");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Could not migrate the legacy XML settings file");
            return false;
        }
    }

    /// <summary>Normalize legacy glyph text names across all launchers' items.</summary>
    /// <remarks>
    /// Also the hook for <see cref="Models.Launcher.MigrateWebModel"/>, which brings a web
    /// launcher written before single-address and bookmark-bar launchers merged onto the one
    /// model. Both run at the same two moments — after a JSON load and after the legacy XML
    /// migration — and both are idempotent, so a launcher already current is untouched.
    /// </remarks>
    private static void NormalizeAllGlyphs()
    {
        foreach (var launcher in _current.Launchers)
        {
            launcher.MigrateWebModel();

            foreach (var item in launcher.Items)
            {
                item.NormalizeGlyph();
                if (item.IsGroup)
                {
                    foreach (var child in item.Children)
                    {
                        child.NormalizeGlyph();
                    }
                }
            }
        }
    }
}
