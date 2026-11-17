using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using WinUIAppKit.Localization;
using LittleLauncher.Models;
using LittleLauncher.Windows;
using LittleLauncher.Services;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher;

/// <summary>Start menu and taskbar-pin shortcuts, and the companion executable they point to.</summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Returns the icon location string for shortcuts.
    /// Uses the saved AppData icon if custom, otherwise the app exe icon.
    /// </summary>
    private static string GetShortcutIconLocation(string fallback)
    {
        string appDataIcon = Path.Combine(
            GetPhysicalAppDataDir(), "app-icon.ico");
        return File.Exists(appDataIcon) ? $"{appDataIcon},0" : fallback;
    }

    /// <summary>
    /// Removes stale per-launcher flyout shortcuts from the Start Menu.
    /// These legacy root-level links are unrelated to the current explicit pin snapshots,
    /// which live under Little Launcher Pins and must survive startup.
    /// </summary>
    private static void CleanUpStaleFlyoutShortcuts()
    {
        CleanUpStaleFlyoutShortcuts(GetStartMenuProgramsDir());

        if (IsPackaged)
        {
            CleanUpStaleFlyoutShortcuts(GetLegacySharedStartMenuProgramsDir());
        }
    }

    private static void CleanUpStaleFlyoutShortcuts(string startMenuDir)
    {
        try
        {
            if (!Directory.Exists(startMenuDir))
            {
                return;
            }

            foreach (var pattern in new[] { "Little Launcher - *.lnk", "Little Launcher Flyout - *.lnk", "Little Launcher Flyout.lnk" })
            {
                foreach (var lnk in Directory.GetFiles(startMenuDir, pattern))
                {
                    try { File.Delete(lnk); } catch { }
                }
            }
        }
        catch { /* best-effort */ }
    }

    private static void EnsureStartMenuShortcuts()
    {
        // Clean up per-launcher flyout shortcuts from previous versions.
        // Current explicit pin snapshots live in their own folder and are not swept here.
        CleanUpStaleFlyoutShortcuts();

        // MSIX packages register their own Start Menu entry via the manifest.
        // Remove any stale unpackaged shortcut so Windows only sees the
        // packaged entry, then stop before creating a duplicate .lnk.
        if (IsPackaged)
        {
            CleanUpLegacyUnpackagedMainShortcut();
            return;
        }

        try
        {
            string startMenuDir = GetStartMenuProgramsDir();

            string exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return;
            }

            Directory.CreateDirectory(startMenuDir);

            // Remove old-named shortcuts (renamed to "Little Launcher")
            foreach (var old in new[] { "LittleLauncher Settings.lnk", "LittleLauncher.lnk" })
            {
                string oldLnk = Path.Combine(startMenuDir, old);
                if (File.Exists(oldLnk))
                {
                    File.Delete(oldLnk);
                }
            }

            // Remove MSI-era shortcut that lived in a "Little Launcher" subfolder
            RemoveLegacyMsiSubfolderShortcut(startMenuDir);

            // Single Start Menu shortcut — always opens settings when clicked.
            // Always use the exe's embedded icon (blue rocket) for the app identity.
            CreateOrUpdateShortcut(
                Path.Combine(startMenuDir, "Little Launcher.lnk"),
                exePath,
                "--settings",
                "Little Launcher",
                $"{exePath},0");
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create Start Menu shortcuts");
        }
    }

    private static void CleanUpLegacyUnpackagedMainShortcut()
    {
        try
        {
            string startMenuDir = GetLegacySharedStartMenuProgramsDir();
            if (!Directory.Exists(startMenuDir))
            {
                return;
            }

            foreach (string shortcutName in new[] { "Little Launcher.lnk", "LittleLauncher.lnk", "LittleLauncher Settings.lnk" })
            {
                string shortcutPath = Path.Combine(startMenuDir, shortcutName);
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
            }

            RemoveLegacyMsiSubfolderShortcut(startMenuDir);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Removes the MSI-era shortcut that lived in a <c>Little Launcher</c> subfolder.
    /// </summary>
    /// <remarks>
    /// <b>Deletes the shortcut, not the folder.</b> This used to be a recursive delete of the whole
    /// subfolder, which is now the same folder
    /// <see cref="Services.StartMenuShortcutService"/> keeps a shortcut per web launcher in — so it
    /// would have silently emptied that group on every startup, and the shortcuts would have looked
    /// like they were never created. The folder is still removed when nothing is left in it, so a
    /// machine with no web launchers ends up as clean as before.
    /// </remarks>
    private static void RemoveLegacyMsiSubfolderShortcut(string startMenuDir)
    {
        try
        {
            string subfolder = Path.Combine(startMenuDir, Services.StartMenuShortcutService.FolderName);
            if (!Directory.Exists(subfolder))
            {
                return;
            }

            foreach (string legacy in new[] { "Little Launcher.lnk", "LittleLauncher.lnk", "LittleLauncher Settings.lnk" })
            {
                string path = Path.Combine(subfolder, legacy);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.GetFileSystemEntries(subfolder).Length == 0)
            {
                Directory.Delete(subfolder);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Write the interface language in effect to <c>language.txt</c> beside the companion exe.
    /// </summary>
    /// <remarks>
    /// The companion is a separate unpackaged process. Under MSIX it cannot see this app's settings
    /// at the path the app uses, so rather than have it guess the package layout, the app hands it
    /// the resolved identity (for example <c>fr-CA</c>) in the folder the companion already reads
    /// <c>main-exe-path.txt</c> from. Rewritten whenever the choice changes.
    /// </remarks>
    /// <summary>
    /// Relabel what the shell holds on the app's behalf after the interface language changes.
    /// </summary>
    /// <remarks>
    /// The tray menu is built each time it opens, Settings rebuilds itself, and jump lists
    /// rebuild on the settings save that records the choice, so they need nothing here. Start
    /// menu shortcut names and the companion's language are written once and kept by Windows, so
    /// they are rewritten now. Web launcher panels stay loaded in the background and keep their
    /// toolbar text until the app restarts, which the language picker says.
    /// </remarks>
    private static void OnLanguageChanged()
    {
        WriteCompanionLanguage();
        Services.StartMenuShortcutService.Sync(SettingsManager.Current.Launchers);
    }

    private static void WriteCompanionLanguage()
    {
        try
        {
            File.WriteAllText(Path.Combine(GetPhysicalAppDataDir(), "language.txt"), UiText.Locale);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to write the companion language breadcrumb");
        }
    }

    /// <summary>
    /// Copies the companion flyout exe to the app data directory so it has a consistent location
    /// external processes can be pointed at, writes the <c>main-exe-path.txt</c> breadcrumb beside
    /// it, and cleans up legacy Start Menu shortcuts from previous versions.
    /// </summary>
    /// <remarks>
    /// <para><b>One destination, and it has to be <see cref="GetPhysicalAppDataDir"/>.</b> This used
    /// to write a second copy into the real shared <c>%AppData%\LittleLauncher\</c>, so that pins
    /// made by an unpackaged build would stop cold-starting whatever exe they were pinned against.
    /// <b>Under MSIX that copy never happened</b>, and it failed in the worst possible way: silently
    /// and with every call reporting success.</para>
    /// <para>Measured, because the theory alone is not obvious enough to keep anyone from adding it
    /// back. A file written to <c>C:\Users\{user}\AppData\Roaming\LittleLauncher\</c> by a
    /// process running under the package identity lands in the package's
    /// <c>LocalCache\Roaming\LittleLauncher\</c>, and reads from inside the package see it back at
    /// the path that was asked for, so nothing looks wrong from in here. From outside it is not
    /// there, and an external process pointed at the shared path finds whatever an
    /// <em>unpackaged</em> build left behind. On the dev machine that was a complete frozen snapshot
    /// of one: its own settings.json, logs, icons, and a breadcrumb naming a debug build in the
    /// source tree.</para>
    /// <para><b>The surviving destination is genuinely unredirected, which is the distinction worth
    /// keeping straight.</b> <see cref="GetPhysicalAppDataDir"/> resolves through
    /// <c>ApplicationData.Current.RoamingFolder.Path</c> to the package's <c>RoamingState</c>, and
    /// writes there are not filtered - that is the whole reason it exists, and why shortcuts and
    /// jump list tasks can be pointed at it. So the two targets were never the same directory: they
    /// were the real one, and a third one nothing outside the package can find.</para>
    /// <para><b>Start Menu shortcuts are not a counter-example.</b> Those do land in the real profile
    /// (see <see cref="GetPhysicalStartMenuProgramsDir"/>) because MSIX excludes the shell's own
    /// folders from redirection so that desktop apps can still create shortcuts. Ordinary
    /// application folders under AppData get no such exemption.</para>
    /// <para>So an old unpackaged pin is left pointing at whatever it was pinned against, and
    /// nothing this process can do reaches it: writing, deleting and replacing that file are all
    /// redirected identically. Re-pinning the launcher is the fix, and a pin made by any current
    /// build already names this directory.</para>
    /// </remarks>
    /// <summary>
    /// The companion exe every pin, Start Menu shortcut and jump list task runs.
    /// </summary>
    /// <remarks>
    /// <para>The AppData copy first, because that is the one <see cref="EnsureFlyoutShortcut"/>
    /// deploys and the one a pin's relaunch command already names: a portable folder can be moved
    /// or deleted, and under MSIX the install directory is not a path an external process should
    /// be pointed at. The build output is the fallback for a first run that has not deployed yet,
    /// and for a developer running straight out of bin.</para>
    /// <para>Callers must still check it exists. It is a path, not a promise.</para>
    /// </remarks>
    internal static string GetFlyoutCompanionPath()
    {
        string deployed = Path.Combine(GetPhysicalAppDataDir(), "LittleLauncherFlyout.exe");
        return File.Exists(deployed)
            ? deployed
            : Path.Combine(AppContext.BaseDirectory, "LittleLauncherFlyout.exe");
    }

    /// <summary>
    /// Whether the helper confirmed a shortcut-backed pin for this exact local AUMID.
    /// A cancelled request leaves the previous record intact. Never infer identity from the exe path.
    /// </summary>
    internal static bool HasPinShortcutIdentity(Launcher launcher)
    {
        if (string.IsNullOrEmpty(launcher.PinAumid))
        {
            return false;
        }

        try
        {
            string pending = Path.Combine(GetPhysicalAppDataDir(), $"pin-pending-{launcher.Id}.txt");
            if (File.Exists(pending))
            {
                return false;
            }

            string record = Path.Combine(GetPhysicalAppDataDir(), $"pin-shortcut-{launcher.Id}.txt");
            string directory = Path.Combine(GetPhysicalStartMenuProgramsDir(), "Little Launcher Pins", launcher.Id);
            return File.Exists(record)
                && File.ReadAllText(record) == launcher.PinAumid
                && Directory.Exists(directory)
                && Directory.GetFiles(directory, "*.lnk").Length == 1;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not check Start shortcut identity for {Name}", launcher.Name);
            return false;
        }
    }

    private static void EnsureFlyoutShortcut()
    {
        try
        {
            // Source: the companion exe next to the main exe
            string sourceExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LittleLauncherFlyout.exe");
            if (!File.Exists(sourceExe))
            {
                return;
            }

            string mainExePath = Environment.ProcessPath ?? "";
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string targetDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(targetDir);

            string destExe = Path.Combine(targetDir, "LittleLauncherFlyout.exe");
            File.Copy(sourceExe, destExe, overwrite: true);

            // In debug (framework-dependent) builds, the companion exe also
            // needs its .dll, .deps.json, and .runtimeconfig.json to run.
            // Release builds are Native AOT single-file and don't need these.
            foreach (var ext in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                string src = Path.Combine(baseDir, "LittleLauncherFlyout" + ext);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(targetDir, "LittleLauncherFlyout" + ext), overwrite: true);
                }
            }

            // Write a breadcrumb so the companion exe can launch the main app
            // if it isn't running (FindWindow returns null).
            if (!string.IsNullOrEmpty(mainExePath))
            {
                File.WriteAllText(Path.Combine(targetDir, "main-exe-path.txt"), mainExePath);
            }

            WriteCompanionLanguage();
            UiText.Changed -= OnLanguageChanged;
            UiText.Changed += OnLanguageChanged;

            // Remove the old Start Menu shortcut (no longer created — the
            // pin-to-taskbar button in Settings handles pinning directly).
            string oldLnk = Path.Combine(
                GetStartMenuProgramsDir(),
                "Little Launcher Flyout.lnk");
            if (File.Exists(oldLnk))
            {
                File.Delete(oldLnk);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to create flyout shortcut");
        }
    }

    /// <summary>
    /// Updates the icon on the main Start Menu shortcut (non-MSIX only).
    /// <summary>
    /// Writes a launcher's Start Menu shortcut. See <see cref="Services.StartMenuShortcutService"/>.
    /// </summary>
    /// <remarks>
    /// A thin pass-through rather than making <see cref="CreateOrUpdateShortcut"/> internal, so the
    /// one caller outside this class is visible from here — these shortcuts must never gain an
    /// AUMID, and that constraint is easier to keep when the entry point is named.
    /// </remarks>
    internal static void CreateOrUpdateLauncherShortcut(
        string shortcutPath, string exePath, string? arguments,
        string description, string iconLocation) =>
        CreateOrUpdateShortcut(shortcutPath, exePath, arguments, description, iconLocation);

    private static void CreateOrUpdateShortcut(
        string shortcutPath, string exePath, string? arguments,
        string description, string iconLocation)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
        {
            return;
        }

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell == null)
        {
            return;
        }

        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                if (arguments != null)
                {
                    shortcut.Arguments = arguments;
                }

                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = description;
                shortcut.IconLocation = iconLocation;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
