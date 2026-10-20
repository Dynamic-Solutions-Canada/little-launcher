using Microsoft.UI.Xaml.Controls;

namespace LittleLauncher.Windows;

public sealed partial class WebFlyoutWindow
{
    /// <summary>
    /// Builds a fresh section with immediate quick controls and one link to its existing editor.
    /// Complex settings are edited only in the owned settings window.
    /// </summary>
    private MenuFlyoutSubItem BuildSettingsSectionMenu(LauncherSettingsSectionDefinition definition)
    {
        var submenu = new MenuFlyoutSubItem { Text = definition.Label };
        switch (definition.Id)
        {
            case LauncherSettingsSection.Appearance:
                AddAppearanceQuickSettings(submenu);
                break;
            case LauncherSettingsSection.SizeAndPosition:
                AddSizeQuickSettings(submenu);
                break;
            case LauncherSettingsSection.Behavior:
                AddBehaviorQuickSettings(submenu);
                break;
        }

        if (submenu.Items.Count > 0)
            submenu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem
        {
            Text = string.Format(LauncherSettingsSections.Text("LauncherSectionSettingsLink"), definition.Label),
        };
        settings.Click += (_, _) => _ = OpenLauncherSettingsAsync(definition.Id);
        submenu.Items.Add(settings);
        return submenu;
    }

    /// <summary>Adds live bar toggles in the order those bars appear on screen.</summary>
    private void AddAppearanceQuickSettings(MenuFlyoutSubItem submenu)
    {
        // ── What it shows ───────────────────────────────────────
        // Tabs above the address, which is the order the two strips sit in on the flyout ;
        // header, then tabs, then the address of whichever tab they chose. The settings dialog
        // lists them the same way round.
        // Off, the strip appears on its own as soon as there is a second tab and goes away again
        // when there is not; so this reads as "keep it", not "switch tabs on". Turning it on with
        // one tab open is also the only way to reach the "+" without following a link first.
        submenu.Items.Add(Toggle("Tab bar", _launcher.WebAlwaysShowTabs, on =>
        {
            _launcher.WebAlwaysShowTabs = on;
            ApplyTabBarVisibility();
        }));

        submenu.Items.Add(Toggle("Address bar", _launcher.WebShowAddressBar, on =>
        {
            _launcher.WebShowAddressBar = on;
            ApplyAddressBarVisibility();
        }));

        // The bar is where bookmarks are managed, so being able to bring it back without walking to
        // launcher settings matters more than it did when it appeared on its own. Listed after the
        // address bar because that is the order the two strips appear in on the flyout.
        submenu.Items.Add(Toggle("Bookmarks bar", _launcher.ShowsBookmarkBar, on =>
        {
            _launcher.WebShowBookmarkBar = on;
            RebuildBookmarkBar(force: true);
        }));
    }

    /// <summary>Adds existing zoom, anchor and size-memory controls with their original live behavior.</summary>
    private void AddSizeQuickSettings(MenuFlyoutSubItem submenu)
    {
        // ── How big it is, and where it opens ───────────────────
        // Zoom leads the section because it is the size question asked most often, and the only one
        // a user arrives at the menu already needing fixed.
        submenu.Items.Add(BuildZoomSubmenu());

        submenu.Items.Add(BuildAnchorSubmenu());

        // The one a user reaches for straight after dragging a flyout and finding the change did
        // not stick; which is exactly a moment spent looking at the flyout, so the menu is the
        // right place for it rather than two windows away. "Where you last dragged it" is one of the
        // Opens at values, because remembering a position and choosing one are the same question.
        // WebLockSize is the inverse of what is shown, because this one is on by default and a
        // bool defaulting to true cannot be turned off under WhenWritingDefault. Inverted here, in
        // the line that builds the item; never in the model.
        submenu.Items.Add(Toggle("Remember size changes", !_launcher.WebLockSize, on =>
        {
            _launcher.WebLockSize = !on;
        }));
    }

    /// <summary>Adds mode-dependent window controls and fullscreen options, reading current state each time.</summary>
    private void AddBehaviorQuickSettings(MenuFlyoutSubItem submenu)
    {
        // ── How it presents itself ──────────────────────────────
        submenu.Items.Add(Toggle("Regular window", _launcher.WebRegularWindow, on =>
        {
            _launcher.WebRegularWindow = on;
            ApplyWindowMode();
        }));

        // Only meaningful as a window: a flyout already dismisses on focus loss unless pinned, and
        // its pin is the control for that. Hidden rather than disabled; a greyed item still reads
        // as something you are missing out on.
        if (_launcher.WebRegularWindow)
        {
            submenu.Items.Add(Toggle("Close when focus is lost", _launcher.WebWindowAutoHide, on =>
            {
                _launcher.WebWindowAutoHide = on;
            }));
        }

        submenu.Items.Add(Toggle(PinMenuLabel(), _launcher.WebPinFlyout, on =>
        {
            _launcher.WebPinFlyout = on;
            SetTopmost(true);
            UpdatePinButton();
        }));

        submenu.Items.Add(Toggle(LauncherSettingsSections.Text("WebFullscreenFillsLauncher"), _launcher.WebFullScreenInWindow, on =>
        {
            _launcher.WebFullScreenInWindow = on;
        }));
        var fitFullscreenToVideo = Toggle(LauncherSettingsSections.Text("WebFitFullscreenToVideo"), _launcher.WebFitFullscreenToVideo, on =>
        {
            _launcher.WebFitFullscreenToVideo = on;
        });
        fitFullscreenToVideo.IsEnabled = _launcher.WebFullScreenInWindow;
        submenu.Items.Add(fitFullscreenToVideo);
    }

}
