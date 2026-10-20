using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;

namespace LittleLauncher.Windows;

public sealed partial class LauncherSettingsWindow
{
    private readonly Dictionary<LauncherSettingsSection, Expander> _sectionExpanders = new();
    private LauncherSettingsSection? _requestedSection;
    private bool _formLoaded;
    private bool _settingsClosed;

    /// <summary>The existing close completion, also awaited when a section reuses this window.</summary>
    internal System.Threading.Tasks.Task<bool> Completion => _completion.Task;

    /// <summary>Limits reuse to this launcher's still-open settings window.</summary>
    internal bool IsOpenFor(string launcherId) => !_settingsClosed && _launcher.Id == launcherId;

    /// <summary>Builds the shared ordered form with common setup sections initially expanded.</summary>
    private StackPanel BuildSettingsSections(
        IReadOnlyDictionary<LauncherSettingsSection, IReadOnlyList<FrameworkElement>> sectionRows)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var definition in LauncherSettingsSections.All)
        {
            var content = new StackPanel { Spacing = 12 };
            foreach (var row in sectionRows[definition.Id])
                content.Children.Add(row);

            var expander = new Expander
            {
                Header = definition.Label,
                // Behavior and Privacy and sign-ins start collapsed on a normal opening.
                IsExpanded = definition.Id != LauncherSettingsSection.Behavior &&
                    definition.Id != LauncherSettingsSection.PrivacyAndSignIns,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = content,
            };
            _sectionExpanders.Add(definition.Id, expander);
            panel.Children.Add(expander);
        }
        return panel;
    }

    /// <summary>
    /// Reveals a section in this owned window. Before Loaded it queues the destination; after a
    /// type change an unavailable web section falls back to General without changing the type.
    /// </summary>
    internal void ShowSection(LauncherSettingsSection section)
    {
        if (_settingsClosed)
            return;

        _requestedSection = section;
        if (!_formLoaded)
            return;

        if (!_sectionExpanders.TryGetValue(section, out var expander) || expander.Visibility != Visibility.Visible)
            expander = _sectionExpanders[LauncherSettingsSection.General];

        expander.IsExpanded = true;
        expander.StartBringIntoView();
    }
}
