using LittleLauncher.Classes.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public sealed partial class LauncherSettingsWindow
{
    /// <summary>Builds profile, password, permission and data actions; opening the section runs none of them.</summary>
    private IReadOnlyList<FrameworkElement> BuildWebPrivacyRows(Launcher launcher)
    {
        // Set once the password row below exists; the profile combo above has to re-read it, and
        // that handler is written before the row it refers to.
        Action? refreshPasswordRow = null;

        // ── Profile ─────────────────────────────────────────────
        // A combo rather than a toggle: "shared with other launchers" is a statement about where
        // the sign-ins live, and naming both ends of it beats an unlabelled switch.
        //
        // Shared leads, being what a new launcher gets. The order is display only; the value is
        // read off each item's Tag, never off the index.
        var profileCombo = new ComboBox { MinWidth = 200 };
        profileCombo.Items.Add(new ComboBoxItem { Content = "Shared with other launchers", Tag = true });
        profileCombo.Items.Add(new ComboBoxItem { Content = "Private to this launcher", Tag = false });
        profileCombo.SelectedIndex = launcher.WebSharedProfile ? 0 : 1;

        var clearSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdateProfileText() => clearSubtitle.Text = launcher.WebSharedProfile
            ? "Signs out of the shared profile; every launcher using it"
            : "Signs out of the page and clears its cookies and cache";

        profileCombo.SelectionChanged += (_, _) =>
        {
            if (profileCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not bool shared) return;
            if (shared == launcher.WebSharedProfile) return;

            launcher.WebSharedProfile = shared;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();

            // The folder is bound when the browser is created, so the running one is still on the
            // old profile until it is dropped.
            WebFlyoutWindow.ReloadProfile(launcher.Id);
            UpdateProfileText();

            // The profile changed, so both the wording and the value belong to a different profile
            // now; a launcher moved onto the shared one adopts whatever the shared one is set to.
            // Through a hook, because that row is built below this handler.
            refreshPasswordRow?.Invoke();
        };

        var profileRow = BuildRow("Sign-ins",
            "Pooled with every launcher set to share, so one sign-in covers them all. Private keeps this "
            + "launcher's cookies to itself; for a second account on a site another launcher already uses",
            profileCombo);

        // ── Saved logins ────────────────────────────────────────
        // Beside Sign-ins deliberately: both are properties of the *profile*, not the launcher, so a
        // launcher on the shared profile is setting this for every launcher on it. Saved passwords
        // live in the profile, so the switch that governs them has to be scoped the same way.
        var passwordToggle = new ToggleSwitch
        {
            IsOn = WebFlyoutWindow.UsesBuiltInPasswordManager(launcher),
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var passwordSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdatePasswordText() => passwordSubtitle.Text = launcher.WebSharedProfile
            ? "Offer to save logins and fill them in, for every launcher on the shared profile. Turn off "
              + "when a password manager extension is doing it, so the two do not compete"
            : "Offer to save logins and fill them in. Turn off when a password manager extension is "
              + "doing it, so the two do not compete";

        passwordToggle.Toggled += (_, _) =>
        {
            WebFlyoutWindow.SetBuiltInPasswordManager(launcher, passwordToggle.IsOn);

            // The setting is read when a browser starts, so a launcher already open keeps the old
            // behaviour until it is reopened. Dropping its browser is what makes that immediate.
            WebFlyoutWindow.ReloadProfile(launcher.Id);
        };

        UpdatePasswordText();
        var passwordRow = BuildRow("Save Logins", passwordSubtitle, passwordToggle);

        refreshPasswordRow = () =>
        {
            UpdatePasswordText();
            passwordToggle.IsOn = WebFlyoutWindow.UsesBuiltInPasswordManager(launcher);
        };

        var forgetLoginsButton = new Button { Content = "Forget" };
        forgetLoginsButton.Click += async (_, _) =>
        {
            forgetLoginsButton.IsEnabled = false;

            // All of them, because WebView2 exposes no way to enumerate saved passwords; only to
            // clear the category. The button is named for what it can actually do.
            bool cleared = await WebFlyoutWindow.ClearSavedPasswordsAsync(launcher);
            forgetLoginsButton.Content = cleared ? "Forgotten" : "Forget";

            if (!cleared)
                await ShowErrorAsync("Open this launcher first; its saved logins can only be cleared while its browser is running.");

            forgetLoginsButton.IsEnabled = true;
        };

        var forgetLoginsRow = BuildRow("Saved Logins",
            "Forget every login and form entry saved for this profile. Logins kept by a password "
            + "manager extension are not affected",
            forgetLoginsButton);

        // ── Sign-out / clear data ───────────────────────────────
        var clearButton = new Button { Content = "Clear" };
        clearButton.Click += async (_, _) =>
        {
            clearButton.IsEnabled = false;
            try
            {
                await WebFlyoutWindow.ClearBrowsingDataAsync(launcher);
                clearButton.Content = "Cleared";
            }
            catch (Exception ex)
            {
                NLog.LogManager.GetCurrentClassLogger().Warn(ex, "Clearing web launcher data failed");
                await ShowErrorAsync("Could not clear this launcher's browsing data. Close its panel and try again.");
                clearButton.Content = "Clear";
            }
            finally
            {
                clearButton.IsEnabled = true;
            }
        };
        UpdateProfileText();
        var clearRow = BuildRow("Browsing Data", clearSubtitle, clearButton);

        // ── Reset stored answers ────────────────────────────────
        // Without this, an accidental Block could only be undone by clearing the whole profile,
        // which also signs the launcher out.
        var permissionsSubtitle = new TextBlock
        {
            Text = "Forget what you have allowed and blocked, so this launcher's pages ask again",
            FontSize = 12,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var resetPermissionsButton = new Button { Content = "Reset" };
        resetPermissionsButton.Click += async (_, _) =>
        {
            resetPermissionsButton.IsEnabled = false;
            try
            {
                // The stored answers live in the WebView2 profile, which is only reachable through
                // a running browser; so a launcher that is not loaded has this applied when it
                // next opens, and is told so rather than being shown a false "Reset".
                bool applied = await WebFlyoutWindow.ResetSitePermissionsAsync(launcher.Id);
                resetPermissionsButton.Content = "Reset";
                permissionsSubtitle.Text = applied
                    ? "Cleared. This launcher's pages will ask again."
                    : "Will be cleared the next time this launcher opens.";
            }
            finally
            {
                resetPermissionsButton.IsEnabled = true;
            }
        };
        var resetPermissionsRow = BuildRow("Site Permissions", permissionsSubtitle, resetPermissionsButton);

        return [profileRow, passwordRow, forgetLoginsRow, resetPermissionsRow, clearRow];
    }
}
