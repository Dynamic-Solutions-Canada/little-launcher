using LittleLauncher.Classes.Settings;
using Microsoft.UI.Xaml;
using WinUIAppKit.Theming;

namespace LittleLauncher.Classes;

/// <summary>
/// Connects the saved theme choice to winui-app-kit's <see cref="AppTheme"/>, which applies it to every
/// tracked window and keeps them in step when it changes. Also answers whether Windows itself is in
/// dark app mode, for icon artwork that follows Windows rather than the app's own choice.
/// </summary>
internal static class ThemeManager
{
    /// <summary>
    /// Apply the saved choice to <paramref name="window"/> now, and whenever the choice changes,
    /// until the window closes.
    /// </summary>
    public static void ApplySavedTheme(Window window)
    {
        SyncFromSettings();
        AppTheme.Track(window);
    }

    /// <summary>Make the kit's current choice match the loaded settings (after a load or import).</summary>
    public static void SyncFromSettings() => AppTheme.Set((AppThemeChoice)SettingsManager.Current.AppTheme);

    /// <summary>
    /// Save a new choice (0 system, 1 light, 2 dark; the same numbers <see cref="AppThemeChoice"/>
    /// uses) and apply it to every tracked window.
    /// </summary>
    public static void ApplyAndSaveTheme(int theme)
    {
        SettingsManager.Current.AppTheme = theme;
        SettingsManager.SaveSettings();
        AppTheme.Set((AppThemeChoice)theme);
    }

    private static readonly global::Windows.UI.ViewManagement.UISettings UiSettings = new();

    /// <summary>
    /// True when Windows is in dark app mode, whatever the app's own theme choice. Tray and jump-list
    /// artwork use this, so it is deliberately not <see cref="AppTheme.IsDark"/>, which honours the
    /// app's choice.
    /// </summary>
    public static bool IsDarkTheme()
    {
        var fg = UiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
        // Bright foreground = dark theme
        return fg.R > 128;
    }
}
