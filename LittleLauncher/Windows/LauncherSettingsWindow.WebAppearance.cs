using LittleLauncher.Classes.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public sealed partial class LauncherSettingsWindow
{
    /// <summary>Builds the web chrome controls in the same order as the visible bars.</summary>
    private IReadOnlyList<FrameworkElement> BuildWebAppearanceRows(Launcher launcher)
    {
        // ── Address bar ─────────────────────────────────────────
        var addressToggle = new ToggleSwitch { IsOn = launcher.WebShowAddressBar, OnContent = "", OffContent = "", MinWidth = 0 };
        addressToggle.Toggled += (_, _) =>
        {
            launcher.WebShowAddressBar = addressToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        // Not in Advanced: it is one of the few things that changes what the flyout *is*; a page
        // versus a small browser; rather than tuning one that already works, and it is the setting
        // the star for bookmarking the current page lives in.
        var addressRow = BuildRow("Address Bar",
            "Show the page address under the header, with a star for bookmarking it",
            addressToggle);

        // ── Tab bar ─────────────────────────────────────────────
        // Beside the address bar, because they are the same question asked twice: which rows of
        // chrome does this launcher keep. It was reachable only from the flyout's "…" menu, which
        // is the wrong place to be the *only* place; the menu is for changing your mind while
        // looking at a launcher, not for finding out that an option exists.
        //
        // **Listed before the address bar**, in both the returned rows and the More menu,
        // because that is the order the two strips appear in on the flyout: header, then tabs,
        // then the address of whichever tab they chose. Two toggles for two adjacent rows read
        // as mislabelled when the list disagrees with what is on screen.
        var tabBarToggle = new ToggleSwitch { IsOn = launcher.WebAlwaysShowTabs, OnContent = "", OffContent = "", MinWidth = 0 };
        tabBarToggle.Toggled += (_, _) =>
        {
            launcher.WebAlwaysShowTabs = tabBarToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        var tabBarRow = BuildRow("Tab Bar",
            "Keep the tab strip on screen with one page open. Off, it appears as soon as there is a second",
            tabBarToggle);

        return [tabBarRow, addressRow, BuildBookmarksRow(launcher), BuildBookmarkIconsOnlyRow(launcher)];
    }
}
