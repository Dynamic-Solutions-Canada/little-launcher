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

/// <summary>Launcher icon files on disk and keeping item icons current.</summary>
public sealed partial class MainWindow
{
    /// <summary>Raised after effective icon bytes are saved, including automatic favicon/composite updates.</summary>
    internal static event Action<string>? LauncherIconSaved;

    /// <summary>
    /// Ensures the per-launcher .ico file exists in AppData. Called before
    /// pinning to the taskbar so the companion exe and Start Menu shortcut
    /// can reference the correct icon.
    /// </summary>
    internal static void EnsureLauncherIconSaved(Launcher launcher) =>
        SaveResolvedIconToAppData(launcher);

    /// <summary>Where a launcher's notification icon lives. PNG, because a toast cannot use an .ico.</summary>
    internal static string GetToastIconPath(string launcherId) =>
        Path.Combine(GetPhysicalAppDataDir(), $"toast-icon-{launcherId}.png");

    /// <summary>
    /// Writes the launcher's resolved icon as a PNG for Windows notifications to show, and returns
    /// its path.
    /// </summary>
    /// <remarks>
    /// <para>A toast used to carry no launcher icon at all: the notification code looked only for
    /// the *adopted page* icon, which is written solely for a web launcher that has not had an icon
    /// chosen for it — so a launcher with a custom image or a glyph had no file at that path and
    /// Windows fell back to Little Launcher's own logo. Every notification therefore wore the app's
    /// icon rather than the site's, blurred by being scaled up from the small logo the manifest
    /// declares.</para>
    /// <para>Resolved through <see cref="ResolveBaseIconBitmap"/> like every other surface, so a
    /// toast shows exactly what the tray shows — composite, preset, glyph, adopted favicon or
    /// custom image alike — at the 256px that bitmap is already produced at, comfortably above what
    /// a toast asks for at any DPI.</para>
    /// <para>PNG rather than the existing <c>.ico</c>: the notification platform does not render
    /// multi-image icon files, and pointing it at one is how you get nothing at all.</para>
    /// </remarks>
    internal static string? SaveToastIconToAppData(Launcher launcher)
    {
        try
        {
            string path = GetToastIconPath(launcher.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap == null)
            {
                return null;
            }

            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Saving the notification icon failed for launcher {Name}", launcher.Name);
            return null;
        }
    }

    /// <summary>Returns the launcher's notification icon, writing it first if it is not there yet.</summary>
    internal static string? EnsureToastIconSaved(Launcher launcher)
    {
        string path = GetToastIconPath(launcher.Id);
        return File.Exists(path) ? path : SaveToastIconToAppData(launcher);
    }

    /// <summary>
    /// Persists a launcher's resolved icon as an .ico file in AppData so that
    /// shortcuts (Start Menu, pinned taskbar) can reference it.
    /// Returns the path to the saved .ico, or null on failure.
    /// The first launcher's icon is also saved as the canonical app-icon.ico.
    /// </summary>
    private static string? SaveResolvedIconToAppData(Launcher launcher)
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);

            // Per-launcher icon file
            string launcherIcoPath = Path.Combine(appDataDir, $"app-icon-{launcher.Id}.ico");

            using var bitmap = ResolveBaseIconBitmap(launcher);
            if (bitmap == null)
            {
                return null;
            }

            var icoBytes = BitmapToIcoBytes(bitmap);

            File.WriteAllBytes(launcherIcoPath, icoBytes);
            LauncherIconSaved?.Invoke(launcher.Id);

            // Also update the canonical app-icon.ico if this is the first (or only) launcher
            var firstLauncher = SettingsManager.Current.Launchers.FirstOrDefault();
            if (firstLauncher != null && firstLauncher.Id == launcher.Id)
            {
                string canonicalPath = Path.Combine(appDataDir, "app-icon.ico");
                File.Copy(launcherIcoPath, canonicalPath, overwrite: true);
            }

            return launcherIcoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save resolved icon to AppData for launcher {Name}", launcher.Name);
        }
        return null;
    }

    /// <summary>
    /// Generates a settings-specific icon by compositing the app's blue rocket icon
    /// with a small gear overlay in the bottom-right corner.
    /// Saved as settings-icon.ico alongside app-icon.ico.
    /// </summary>
    internal static string? SaveSettingsIconToAppData()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            Directory.CreateDirectory(appDataDir);
            string icoPath = Path.Combine(appDataDir, "settings-icon.ico");

            // Always use the blue rocket (app identity) as the base icon
            string bluePng = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcons", "Blue.png");
            if (!File.Exists(bluePng))
            {
                return null;
            }

            using var original = new System.Drawing.Bitmap(bluePng);
            using var baseBitmap = TrimAndResizeTo256(original);
            if (baseBitmap == null)
            {
                return null;
            }

            // Draw gear overlay in the bottom-right corner
            const int overlaySize = 112; // ~44% of 256
            const int padding = 4;
            using (var g = System.Drawing.Graphics.FromImage(baseBitmap))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                // Semi-transparent dark circle background for contrast
                int cx = 256 - overlaySize / 2 - padding;
                int cy = 256 - overlaySize / 2 - padding;
                using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 30, 30, 30));
                g.FillEllipse(bgBrush, cx - overlaySize / 2, cy - overlaySize / 2, overlaySize, overlaySize);

                // Gear glyph (\uE713 = Settings in Segoe Fluent Icons)
                using var font = new System.Drawing.Font("Segoe Fluent Icons", overlaySize * 0.7f, System.Drawing.GraphicsUnit.Pixel);
                using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                using var fmt = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericTypographic);
                fmt.Alignment = System.Drawing.StringAlignment.Center;
                fmt.LineAlignment = System.Drawing.StringAlignment.Center;
                var rect = new System.Drawing.RectangleF(
                    cx - overlaySize / 2f, cy - overlaySize / 2f,
                    overlaySize, overlaySize);
                g.DrawString("\uE713", font, brush, rect, fmt);
            }

            File.WriteAllBytes(icoPath, BitmapToIcoBytes(baseBitmap));
            return icoPath;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to save settings icon to AppData");
        }
        return null;
    }

    /// <summary>
    /// Updates the tray icon for a specific launcher and refreshes associated shortcuts.
    /// Called from the launcher's PropertyChanged handler for single-launcher changes.
    /// For batch updates (sync, startup), use <see cref="RefreshLauncherIcon"/> and
    /// call <see cref="SaveSettingsIconToAppData"/> once at the end.
    /// </summary>
    internal void UpdateTrayIcon(Launcher launcher)
    {
        RefreshLauncherIcon(launcher);
        SaveSettingsIconToAppData();
        CleanUpStaleIconFiles();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    /// <summary>
    /// Refreshes a single launcher's tray icon and persists its .ico to disk.
    /// Does NOT update settings-icon.ico or clean up stale files — callers
    /// that process multiple launchers should do that once at the end.
    /// </summary>
    private void RefreshLauncherIcon(Launcher launcher)
    {
        // Written alongside the .ico, and before the early return below: a launcher whose tray
        // icon is hidden still raises notifications, and they should not be the one surface left
        // wearing a stale icon.
        SaveToastIconToAppData(launcher);

        if (!_trayIcons.TryGetValue(launcher.Id, out var entry))
        {
            return;
        }

        UpdateTrayIconVisibility(launcher);
        RenderTrayImage(launcher, entry);
        SaveResolvedIconToAppData(launcher);

        // A web launcher presenting as a regular window draws that same .ico on its taskbar button
        // and its task-switcher entry, and nothing else would tell it the file has just moved. It
        // matters most for exactly the icon a web launcher gets automatically: the page's favicon
        // is adopted after the window is already on screen, so without this the switcher kept the
        // placeholder the launcher opened with while the tray showed the site.
        Windows.WebFlyoutWindow.InvalidateWindowIcon(launcher.Id);
    }

    /// <summary>
    /// Cleans up stale versioned icon files from AppData.
    /// Windows 11's taskbar does not re-read pinned icon bitmaps at runtime,
    /// so we no longer attempt to update pinned .lnk icons or restart Explorer.
    /// To change a pinned icon, users must unpin and re-pin the launcher.
    /// </summary>
    private static void CleanUpStaleIconFiles()
    {
        try
        {
            string appDataDir = GetPhysicalAppDataDir();
            // Remove versioned icon copies (app-icon-{id}-v*.ico)
            foreach (string f in Directory.GetFiles(appDataDir, "app-icon-*-v*.ico"))
            {
                try { File.Delete(f); } catch { }
            }

            // Retain every pin snapshot. A cancelled re-pin or an older manual pin may still
            // reference any one of them; the newest file is not evidence that older pins are gone.

            // Legacy single-icon versioned files
            foreach (string f in Directory.GetFiles(appDataDir, "app-icon-v*.ico"))
            {
                try { File.Delete(f); } catch { }
            }

            string altPath = Path.Combine(appDataDir, "app-icon-alt.ico");
            if (File.Exists(altPath))
            {
                try { File.Delete(altPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to clean up stale icon files");
        }
    }

    /// <summary>
    /// Scans launcher items on startup, fetches any missing icons, and refreshes
    /// stale auto-fetched icons (favicons, app icons, PWA icons older than
    /// <see cref="FaviconService.IconMaxAge"/>). Custom icons are never touched.
    /// Covers settings-import-then-restart and machine-migration scenarios.
    /// </summary>
    private async Task FetchMissingIconsOnStartupAsync()
    {
        try
        {
            // Check across all launchers' items for missing icons
            bool anyMissing = false;
            foreach (var launcher in SettingsManager.Current.Launchers)
            {
                var allItems = launcher.Items.SelectMany(i => i.IsGroup ? new[] { i }.Concat(i.Children) : [i]);
                if (allItems.Any(i =>
                    !i.IsGroup &&
                    !string.IsNullOrWhiteSpace(i.Path) &&
                    (string.IsNullOrEmpty(i.IconPath) || !File.Exists(i.IconPath))))
                {
                    anyMissing = true;
                    break;
                }
            }

            if (anyMissing)
            {
                foreach (var launcher in SettingsManager.Current.Launchers)
                {
                    await FaviconService.FetchMissingItemIconsAsync(launcher.Items);
                }
            }

            bool anyRefreshed = await RefreshStaleIconsCoreAsync();

            if (!anyMissing && !anyRefreshed)
            {
                return;
            }

            ApplyItemIconChanges();
        }
        catch (Exception ex)
        {
            NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Startup icon fetch failed");
        }
    }

    /// <summary>
    /// Runs the stale-icon refresh across all launchers with one shared
    /// visited-path set so items sharing a favicon aren't fetched twice.
    /// </summary>
    private static async Task<bool> RefreshStaleIconsCoreAsync()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            changed |= await FaviconService.RefreshStaleItemIconsAsync(launcher.Items, visited);
        }

        return changed;
    }

    /// <summary>
    /// Persists item icon changes and pushes them to every surface: flyouts,
    /// tray icons (Composite mode reads item icons), and the Settings window icon.
    /// </summary>
    private void ApplyItemIconChanges()
    {
        SettingsManager.SaveSettings();
        FlyoutWindow.InvalidateAllItems();

        // Re-save tray icons now that item icons have changed. This matters for
        // Composite mode, whose bitmap is built from the first 4 item icons.
        // Use RefreshLauncherIcon (batch-friendly) and SaveSettingsIconToAppData once.
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            RefreshLauncherIcon(launcher);
        }

        SaveSettingsIconToAppData();
        SettingsWindow.GetCurrent()?.RefreshIcon();
    }

    /// <summary>
    /// Periodic stale-icon refresh while the app stays resident in the tray.
    /// The daily cadence only checks file timestamps; downloads happen when a
    /// cached icon crosses <see cref="FaviconService.IconMaxAge"/>.
    /// </summary>
    private async Task RefreshStaleIconsPeriodicAsync()
    {
        try
        {
            if (!await RefreshStaleIconsCoreAsync())
            {
                return;
            }

            ApplyItemIconChanges();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Periodic icon refresh failed");
        }
    }

    /// <summary>
    /// Ensures the Windows startup registry entry includes --silent so the app
    /// starts silently on login. Migrates existing entries that lack the flag.
    /// Runs unconditionally so entries created by old versions (before --silent
    /// <summary>
    /// Clears icon paths that point to non-existent files, typically caused by
    /// switching install types (MSIX → portable or vice versa). MSIX VFS-redirects
    /// %AppData% into the package's RoamingState folder; those paths become dead
    /// after uninstalling the MSIX. Clearing them lets FetchMissingIconsOnStartupAsync
    /// re-fetch into the current cache directory, and lets ResolveBaseIconBitmap
    /// fall back to color presets instead of returning null.
    /// </summary>
    private static void MigrateStaleIconPaths()
    {
        bool changed = false;
        foreach (var launcher in SettingsManager.Current.Launchers)
        {
            // A missing local path is not a change to the user's icon selection. Restore
            // portable content if present; never upload a cleared selection from this device.
            try { Services.PortableLauncherIcon.Materialize(launcher); }
            catch (Exception ex) { Logger.Warn(ex, "Could not restore a portable launcher icon"); }

            // Item icon paths (recursive into groups)
            changed |= ClearStaleItemIconPaths(launcher.Items);
        }
        if (changed)
        {
            SettingsManager.SaveSettings();
        }
    }

    private static bool ClearStaleItemIconPaths(IEnumerable<LauncherItem> items)
    {
        bool changed = false;
        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                changed |= ClearStaleItemIconPaths(item.Children);
                continue;
            }
            if (!string.IsNullOrEmpty(item.IconPath) && !File.Exists(item.IconPath))
            {
                item.IconPath = "";
                changed = true;
            }
        }
        return changed;
    }
}
