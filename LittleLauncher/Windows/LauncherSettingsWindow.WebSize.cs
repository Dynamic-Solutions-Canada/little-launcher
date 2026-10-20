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
    /// <summary>Builds dimensions, zoom and opening-position controls without changing the live page on reveal.</summary>
    private (IReadOnlyList<FrameworkElement> Rows, Action Refresh) BuildWebSizeRows(Launcher launcher)
    {
        // ── Panel size ──────────────────────────────────────────
        var widthBox = new NumberBox
        {
            Value = launcher.ResolvedWebFlyoutWidth,
            Minimum = Launcher.MinWebFlyoutWidth,
            Maximum = Launcher.MaxWebFlyoutDimension,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };
        var heightBox = new NumberBox
        {
            Value = launcher.ResolvedWebFlyoutHeight,
            Minimum = Launcher.MinWebFlyoutHeight,
            Maximum = Launcher.MaxWebFlyoutDimension,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };

        var sizeControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        sizeControls.Children.Add(widthBox);
        sizeControls.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5 });
        sizeControls.Children.Add(heightBox);

        widthBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(widthBox.Value)) return;
            launcher.WebFlyoutWidth = (int)widthBox.Value;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        heightBox.ValueChanged += (_, _) =>
        {
            if (double.IsNaN(heightBox.Value)) return;
            launcher.WebFlyoutHeight = (int)heightBox.Value;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };

        var sizeRow = BuildRow("Flyout Size", "Width × height of the web flyout, in pixels", sizeControls);

        // ── Zoom ────────────────────────────────────────────────
        var zoomCombo = new ComboBox { MinWidth = 100 };

        // The launcher's own level is folded into the ladder when it is not already a rung, so the
        // combo always has the current value to select. It used to fall back to the first entry
        // instead, which read as 50%, in the one place a user comes to put an odd zoom back.
        int currentZoom = launcher.ResolvedWebZoomPercent;
        int[] zoomLevels = Launcher.WebZoomLevelsIncluding(currentZoom);
        foreach (int zoom in zoomLevels)
            zoomCombo.Items.Add(new ComboBoxItem { Content = $"{zoom}%", Tag = zoom });
        zoomCombo.SelectedIndex = Array.IndexOf(zoomLevels, currentZoom);
        zoomCombo.SelectionChanged += (_, _) =>
        {
            if (zoomCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int zoom) return;
            launcher.WebZoomPercent = zoom;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            WebFlyoutWindow.ApplyLauncherChanges(launcher.Id);
        };
        var zoomRow = BuildRow("Zoom", "Page zoom inside the flyout, also on the flyout's “…” menu", zoomCombo);

        // ── Opening position ────────────────────────────────────
        var anchorCombo = new ComboBox { MinWidth = 200 };
        (string Label, int Value)[] anchors =
        [
            ("Near its tray icon", WebAnchors.Tray),
            // Second, beside the other answer that is not a fixed spot, and named for the gesture
            // that sets it; so the list says how to change it as well as what it does.
            ("Where you last dragged it", WebAnchors.LastPosition),
            ("Top left", WebAnchors.TopLeft),
            ("Top centre", WebAnchors.TopCenter),
            ("Top right", WebAnchors.TopRight),
            ("Left", WebAnchors.Left),
            ("Centre", WebAnchors.Center),
            ("Right", WebAnchors.Right),
            ("Bottom left", WebAnchors.BottomLeft),
            ("Bottom centre", WebAnchors.BottomCenter),
            ("Bottom right", WebAnchors.BottomRight),
        ];
        foreach (var a in anchors)
            anchorCombo.Items.Add(new ComboBoxItem { Content = a.Label, Tag = a.Value });
        anchorCombo.SelectedIndex = Array.FindIndex(anchors, a => a.Value == WebAnchors.Normalize(launcher.WebAnchor));

        var anchorSubtitle = new TextBlock { FontSize = 12, Opacity = 0.5, TextWrapping = TextWrapping.Wrap };

        void UpdateAnchorText() => anchorSubtitle.Text =
            WebAnchors.Normalize(launcher.WebAnchor) == WebAnchors.LastPosition
                ? "Drag the flyout anywhere and it opens there next time"
                : "Where it opens on the screen holding its tray icon. A move lasts until you close it";

        anchorCombo.SelectionChanged += (_, _) =>
        {
            if (anchorCombo.SelectedItem is not ComboBoxItem selected || selected.Tag is not int anchor) return;
            if (anchor == WebAnchors.Normalize(launcher.WebAnchor)) return;

            launcher.WebAnchor = anchor;

            // Any answer but "where you last dragged it" forgets where that was; otherwise picking
            // a corner would leave the flyout opening exactly where it did before, and switching
            // back would restore a position the user had stopped using.
            if (anchor != WebAnchors.LastPosition) launcher.WebFlyoutPosition = "";

            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
            UpdateAnchorText();
        };

        var anchorRow = BuildRow("Opens At", anchorSubtitle, anchorCombo);

        // ── Remember size ───────────────────────────────────────
        // Stored inverted (WebLockSize) because this one is ON by default, and a bool defaulting
        // to true cannot be turned off under WhenWritingDefault.
        var rememberSizeToggle = new ToggleSwitch { IsOn = !launcher.WebLockSize, OnContent = "", OffContent = "", MinWidth = 0 };
        rememberSizeToggle.Toggled += (_, _) =>
        {
            launcher.WebLockSize = !rememberSizeToggle.IsOn;
            SettingsManager.SaveSettings();
            Services.AutoSyncService.NotifyLaunchersChanged();
        };
        var rememberSizeRow = BuildRow("Remember Size Changes",
            "Keep this flyout at the size you drag it to; otherwise it reopens at the size above",
            rememberSizeToggle);

        void Refresh()
        {
            UpdateAnchorText();
            widthBox.Value = launcher.ResolvedWebFlyoutWidth;
            heightBox.Value = launcher.ResolvedWebFlyoutHeight;
        }

        return ([sizeRow, zoomRow, anchorRow, rememberSizeRow], Refresh);
    }
}
