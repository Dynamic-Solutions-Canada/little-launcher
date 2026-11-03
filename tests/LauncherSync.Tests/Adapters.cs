// Only native/service boundaries are adapted. Model, payload, merge and icon IO are production code.
using System.Collections.ObjectModel;
using LittleLauncher.Models;
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public static DispatcherQueue GetForCurrentThread() => new();
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
namespace NLog
{
    public class Logger
    {
        public void Info(string message) { }
        public void Debug(Exception exception, string message) { }
        public void Warn(Exception exception, string message) { }
    }
    public static class LogManager { public static Logger GetCurrentClassLogger() => new(); }
}
namespace LittleLauncher
{
    public static class MainWindow
    {
        public static string DirectoryPath = "";
        public static TestMainWindow? Current => null;
        public static byte[]? ExportLauncherGlyphPng(Launcher launcher) => null;
        public static string GetPhysicalAppDataDir() => DirectoryPath;
    }
    public sealed class TestMainWindow { public void RefreshTrayIcons() { } }
    public static class App { public static Microsoft.UI.Dispatching.DispatcherQueue MainDispatcherQueue = new(); }
}
namespace LittleLauncher.Classes.Settings
{
    public class TestSettings
    {
        public ObservableCollection<Launcher> Launchers { get; } = [];
        public DateTime LaunchersModifiedUtc { get; set; }
    }
    public static class SettingsManager
    {
        public static LittleLauncher.ViewModels.UserSettings Current { get; } = new();
        public static void SaveSettings() { }
    }
}
namespace LittleLauncher.Windows
{
    public static class LauncherPanels { public static void Dispose(string id) { } }
    public static class FlyoutWindow { public static void InvalidateItems(string id, bool force) { } }
}
namespace LittleLauncher.Services
{
    public static class BrowserExtensionService
    {
        public static List<BrowserExtension> Portable() => [];
        public static Task ReconcileAsync(List<BrowserExtension> extensions) => Task.CompletedTask;
    }
    public static class FaviconService
    {
        public static Task FetchMissingItemIconsAsync(ObservableCollection<LauncherItem> items) => Task.CompletedTask;
    }
}

namespace LittleLauncher.Classes { public static class ThemeManager { public static void ApplyAndSaveTheme(int theme) { } } }
namespace LittleLauncher.Services {
 public static class AutoSyncService { public static void RestartPeriodicTimer() { } }
 public static class StartMenuShortcutService { public static void Sync(ObservableCollection<Launcher> launchers) { } }
}
