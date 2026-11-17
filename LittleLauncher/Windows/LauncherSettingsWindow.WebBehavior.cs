using LittleLauncher.Classes.Settings;
using LittleLauncher.Localization;
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
            (Label: UiText.Get("launcherSettings.hiddenPolicy.unloadWhenIdle"), Value: WebHiddenPolicies.UnloadWhenIdle),
            (Label: UiText.Get("launcherSettings.hiddenPolicy.suspend"), Value: WebHiddenPolicies.Suspend),
            (Label: UiText.Get("launcherSettings.hiddenPolicy.keepRunning"), Value: WebHiddenPolicies.KeepRunning),
        };
        foreach (var policy in policies)
        {
            policyCombo.Items.Add(new ComboBoxItem { Content = policy.Label, Tag = policy.Value });
        }

        policyCombo.SelectedIndex = Array.FindIndex(policies, p => p.Value == WebHiddenPolicies.Normalize(launcher.WebHiddenPolicy));

        var policyRow = BuildRow(
            UiText.Get("launcherSettings.hiddenPolicy.title"),
            UiText.Get("launcherSettings.hiddenPolicy.description"),
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
            if (double.IsNaN(idleBox.Value))
            {
                return;
            }

            launcher.WebIdleUnloadMinutes = (int)idleBox.Value;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var idleRow = BuildRow(UiText.Get("launcherSettings.unloadAfter.title"), UiText.Get("launcherSettings.unloadAfter.description"), idleBox);

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
            if (policyCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int policy)
            {
                return;
            }

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
        var reloadRow = BuildRow(UiText.Get("launcherSettings.reloadOnOpen.title"), UiText.Get("launcherSettings.reloadOnOpen.description"), reloadToggle);

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
        var linksRow = BuildRow(UiText.Get("launcherSettings.openLinksInBrowser.title"),
            UiText.Get("launcherSettings.openLinksInBrowser.description"),
            linksToggle);

        // ── Keep open on focus loss ─────────────────────────────
        var pinToggle = new ToggleSwitch { IsOn = launcher.WebPinFlyout, OnContent = "", OffContent = "", MinWidth = 0 };
        pinToggle.Toggled += (_, _) =>
        {
            launcher.WebPinFlyout = pinToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var pinRow = BuildRow(UiText.Get("launcherSettings.pinOpen.title"), UiText.Get("launcherSettings.pinOpen.description"), pinToggle);

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
        var regularRow = BuildRow(UiText.Get("launcherSettings.regularWindow.title"),
            UiText.Get("launcherSettings.regularWindow.description"),
            regularToggle);

        // ── What the taskbar button's click does ────────────────
        var clickCombo = new ComboBox { MinWidth = 200 };
        clickCombo.Items.Add(new ComboBoxItem { Content = UiText.Get("launcherSettings.taskbarClick.minimize"), Tag = false });
        clickCombo.Items.Add(new ComboBoxItem { Content = UiText.Get("launcherSettings.taskbarClick.close"), Tag = true });
        clickCombo.SelectedIndex = launcher.WebTaskbarClickCloses ? 1 : 0;
        clickCombo.SelectionChanged += (_, _) =>
        {
            if (clickCombo.SelectedItem is not ComboBoxItem { Tag: bool closes })
            {
                return;
            }

            launcher.WebTaskbarClickCloses = closes;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var clickRow = BuildRow(UiText.Get("launcherSettings.taskbarClick.title"),
            UiText.Get("launcherSettings.taskbarClick.description"),
            clickCombo);

        // ── Close on focus loss, as a window ────────────────────
        var autoHideToggle = new ToggleSwitch { IsOn = launcher.WebWindowAutoHide, OnContent = "", OffContent = "", MinWidth = 0 };
        autoHideToggle.Toggled += (_, _) =>
        {
            launcher.WebWindowAutoHide = autoHideToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var autoHideRow = BuildRow(UiText.Get("launcherSettings.closeOnFocusLoss.title"),
            UiText.Get("launcherSettings.closeOnFocusLoss.description"),
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
            UiText.Get("launcherSettings.fullscreenFillsLauncher.title"),
            UiText.Get("launcherSettings.fullscreenFillsLauncher.description"),
            fullscreenToggle);
        var fitVideoRow = BuildRow(
            UiText.Get("launcherSettings.fitFullscreenToVideo.title"),
            UiText.Get("launcherSettings.fitFullscreenToVideo.description"),
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
