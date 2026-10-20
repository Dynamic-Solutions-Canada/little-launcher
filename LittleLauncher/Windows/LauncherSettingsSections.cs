using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;

namespace LittleLauncher.Windows;

/// <summary>Stable destinations shared by launcher settings and the web flyout menu.</summary>
public enum LauncherSettingsSection
{
    General,
    Appearance,
    SizeAndPosition,
    Behavior,
    PrivacyAndSignIns,
}

/// <summary>Defines a settings destination without retaining any window or control.</summary>
internal sealed record LauncherSettingsSectionDefinition(
    LauncherSettingsSection Id,
    string LabelKey,
    bool WebOnly = false)
{
    internal string Label => LauncherSettingsSections.Text(LabelKey);
}

/// <summary>Owns the section order and localized labels for both settings surfaces.</summary>
internal static class LauncherSettingsSections
{
    internal static IReadOnlyList<LauncherSettingsSectionDefinition> All { get; } = Array.AsReadOnly(
        new LauncherSettingsSectionDefinition[]
        {
            new(LauncherSettingsSection.General, "LauncherSectionGeneral"),
            new(LauncherSettingsSection.Appearance, "LauncherSectionAppearance"),
            new(LauncherSettingsSection.SizeAndPosition, "LauncherSectionSizeAndPosition", WebOnly: true),
            new(LauncherSettingsSection.Behavior, "LauncherSectionBehavior", WebOnly: true),
            new(LauncherSettingsSection.PrivacyAndSignIns, "LauncherSectionPrivacyAndSignIns", WebOnly: true),
        });

    /// <summary>Reads the app's string dictionary; a missing key remains identifiable.</summary>
    internal static string Text(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is string text ? text : key;
}
