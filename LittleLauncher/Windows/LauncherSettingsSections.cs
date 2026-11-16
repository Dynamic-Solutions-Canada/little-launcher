using LittleLauncher.Localization;
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
    /// <summary>The section name in the current interface language.</summary>
    internal string Label => UiText.Get(LabelKey);
}

/// <summary>Owns the section order and localized labels for both settings surfaces.</summary>
internal static class LauncherSettingsSections
{
    internal static IReadOnlyList<LauncherSettingsSectionDefinition> All { get; } = Array.AsReadOnly(
        new LauncherSettingsSectionDefinition[]
        {
            new(LauncherSettingsSection.General, "launcherSettings.section.general"),
            new(LauncherSettingsSection.Appearance, "launcherSettings.section.appearance"),
            new(LauncherSettingsSection.SizeAndPosition, "launcherSettings.section.sizeAndPosition", WebOnly: true),
            new(LauncherSettingsSection.Behavior, "launcherSettings.section.behavior", WebOnly: true),
            new(LauncherSettingsSection.PrivacyAndSignIns, "launcherSettings.section.privacyAndSignIns", WebOnly: true),
        });
}
