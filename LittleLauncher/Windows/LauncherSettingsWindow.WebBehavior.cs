using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using Launcher = LittleLauncher.Models.Launcher;

namespace LittleLauncher.Windows;

public sealed partial class LauncherSettingsWindow
{
    /// <summary>Builds behavior controls and a visibility refresh that never writes settings.</summary>
    private (IReadOnlyList<FrameworkElement> Rows, Action RefreshVisibility) BuildWebBehaviorRows(Launcher launcher)
    {
        // ── Hidden policy ───────────────────────────────────────
        var policyCombo = new ComboBox { MinWidth = 200 };
        var policies = new[]
        {
            (Label: "Unload when idle", Value: WebHiddenPolicies.UnloadWhenIdle),
            (Label: "Stay loaded (suspended)", Value: WebHiddenPolicies.Suspend),
            (Label: "Keep running", Value: WebHiddenPolicies.KeepRunning),
        };
        foreach (var policy in policies)
            policyCombo.Items.Add(new ComboBoxItem { Content = policy.Label, Tag = policy.Value });
        policyCombo.SelectedIndex = Array.FindIndex(policies, p => p.Value == WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy));

        var policyRow = BuildRow(
            "When Hidden",
            "Unloading frees all memory and stops video and polling; staying loaded reopens instantly",
            policyCombo);

        // ── Idle delay ──────────────────────────────────────────
        var idleBox = new NumberBox
        {
            Value = launcher.ResolvedWebIdleUnloadMinutes,
            Minimum = 1,
            Maximum = 720,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };
        idleBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(idleBox.Value)) return;
            launcher.WebIdleUnloadMinutes = (int)idleBox.Value;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var idleRow = BuildRow("Unload After", "Minutes the flyout may sit closed before the page is dropped", idleBox);

        // Gated on the kind as well as the policy: the caller shows every web row at once, and
        // this row is the one exception that must stay hidden even then.
        void UpdateIdleVisibility()
        {
            idleRow.Visibility = launcher.IsWebLauncher &&
                WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy) == WebHiddenPolicies.UnloadWhenIdle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        policyCombo.SelectionChanged += (_, _) =>
        {
            if (policyCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int policy) return;
            launcher.WebHiddenPolicy = policy;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            UpdateIdleVisibility();
        };
        UpdateIdleVisibility();

        // ── Reload on show ──────────────────────────────────────
        var reloadToggle = new ToggleSwitch { IsOn = launcher.WebReloadOnShow, OnContent = "", OffContent = "", MinWidth = 0 };
        reloadToggle.Toggled += (_, _) =>
        {
            launcher.WebReloadOnShow = reloadToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var reloadRow = BuildRow("Reload On Open", "Fetch the page again each time, instead of showing it as you left it", reloadToggle);

        // ── Links in the real browser ───────────────────────────
        // Its only home. It used to live in the flyout's More menu and nowhere else, which made it
        // unfindable for anyone who had not opened that menu; and it is not a per-moment decision
        // in the way the menu's other items are: how this launcher treats links is a property of
        // the launcher, settled once.
        //
        // Named for what turning it on does rather than for the default it switches off, because
        // "open links in tabs" is already what the launcher does.
        var linksToggle = new ToggleSwitch { IsOn = launcher.WebLinksInBrowser, OnContent = "", OffContent = "", MinWidth = 0 };
        linksToggle.Toggled += (_, _) =>
        {
            launcher.WebLinksInBrowser = linksToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var linksRow = BuildRow("Open Links In Browser",
            "Hand a link that opens a new window to your default browser, instead of opening it in a tab here",
            linksToggle);

        // ── Keep open on focus loss ─────────────────────────────
        var pinToggle = new ToggleSwitch { IsOn = launcher.WebPinFlyout, OnContent = "", OffContent = "", MinWidth = 0 };
        pinToggle.Toggled += (_, _) =>
        {
            launcher.WebPinFlyout = pinToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var pinRow = BuildRow("Pin Open", "Stay on screen when you click elsewhere, instead of dismissing like a flyout", pinToggle);

        // ── Regular window ──────────────────────────────────────
        var regularToggle = new ToggleSwitch { IsOn = launcher.WebRegularWindow, OnContent = "", OffContent = "", MinWidth = 0 };
        regularToggle.Toggled += (_, _) =>
        {
            launcher.WebRegularWindow = regularToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        // Names the window kind rather than any one symptom of it. "Show in taskbar" would be the
        // obvious label and would be a promise the shell cannot keep: the taskbar button and the
        // Alt-Tab entry are the same switch, so a launcher cannot have one without the other.
        var regularRow = BuildRow("Regular Window",
            "Behave like an ordinary app window: taskbar button, Alt-Tab entry, not always on top, and stays open until closed",
            regularToggle);

        // ── What the taskbar button's click does ────────────────
        var clickCombo = new ComboBox { MinWidth = 200 };
        clickCombo.Items.Add(new ComboBoxItem { Content = "Minimize it", Tag = false });
        clickCombo.Items.Add(new ComboBoxItem { Content = "Close it", Tag = true });
        clickCombo.SelectedIndex = launcher.WebTaskbarClickCloses ? 1 : 0;
        clickCombo.SelectionChanged += (_, _) =>
        {
            if (clickCombo.SelectedItem is not ComboBoxItem { Tag: bool closes }) return;
            launcher.WebTaskbarClickCloses = closes;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var clickRow = BuildRow("Taskbar Click",
            "What clicking this launcher's taskbar button does while it is open",
            clickCombo);

        // ── Close on focus loss, as a window ────────────────────
        var autoHideToggle = new ToggleSwitch { IsOn = launcher.WebWindowAutoHide, OnContent = "", OffContent = "", MinWidth = 0 };
        autoHideToggle.Toggled += (_, _) =>
        {
            launcher.WebWindowAutoHide = autoHideToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var autoHideRow = BuildRow("Close On Focus Loss",
            "Dismiss like a flyout when you click elsewhere, while keeping the taskbar button and Alt-Tab entry",
            autoHideToggle);

        // Both only mean anything with Regular Window on; a flyout has no taskbar button to click,
        // and already dismisses on focus loss unless pinned. They follow that toggle rather than
        // sitting there inert. Shown and hidden rather than disabled: a greyed row still reads as a
        // setting you are missing out on.
        void UpdateWindowRowVisibility()
        {
            var visibility = launcher.IsWebLauncher && launcher.WebRegularWindow
                ? Visibility.Visible
                : Visibility.Collapsed;
            clickRow.Visibility = visibility;
            autoHideRow.Visibility = visibility;
        }
        regularToggle.Toggled += (_, _) => UpdateWindowRowVisibility();
        UpdateWindowRowVisibility();

        // These are the same flags and dependency exposed by the flyout menu.
        var fullscreenToggle = new ToggleSwitch
        {
            IsOn = launcher.WebFullScreenInWindow,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        var fitVideoToggle = new ToggleSwitch
        {
            IsOn = launcher.WebFitFullscreenToVideo,
            IsEnabled = launcher.WebFullScreenInWindow,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
        };
        fullscreenToggle.Toggled += (_, _) =>
        {
            launcher.WebFullScreenInWindow = fullscreenToggle.IsOn;
            fitVideoToggle.IsEnabled = fullscreenToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        fitVideoToggle.Toggled += (_, _) =>
        {
            launcher.WebFitFullscreenToVideo = fitVideoToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var fullscreenRow = BuildRow(
            LauncherSettingsSections.Text("WebFullscreenFillsLauncher"),
            LauncherSettingsSections.Text("WebFullscreenFillsLauncherDescription"),
            fullscreenToggle);
        var fitVideoRow = BuildRow(
            LauncherSettingsSections.Text("WebFitFullscreenToVideo"),
            LauncherSettingsSections.Text("WebFitFullscreenToVideoDescription"),
            fitVideoToggle);

        void RefreshVisibility()
        {
            UpdateIdleVisibility();
            UpdateWindowRowVisibility();
        }

        return ([regularRow, autoHideRow, clickRow, pinRow, linksRow, reloadRow,
                 policyRow, idleRow, fullscreenRow, fitVideoRow], RefreshVisibility);
    }
}
