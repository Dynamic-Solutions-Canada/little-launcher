using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using global::Windows.Graphics.Imaging;

namespace LittleLauncher.Controls;

public sealed partial class LauncherItemsView
{
    private const double DragHintHeight = 20;
    private const double DragHintGap = 4;
    private LauncherItem? _dragPreviewItem;
    private string? _dragPreviewCaption;
    private int _dragPreviewMode;
    private int _dragPreviewFeedbackVersion;
    private Task<SoftwareBitmap?>? _dragPreviewTask;
    private CancellationTokenSource? _dragPreviewCancellation;

    /// <summary>Starts one native drag session; target feedback is rendered only when needed.</summary>
    private void BeginDragPreview(LauncherItem item)
    {
        ClearDragPreview();
        _dragPreviewItem = item;
    }

    /// <summary>
    /// Caches the current destination and mode, rather than drawing on every pointer move.
    /// A new destination cancels the old render. Only one bitmap is retained per session.
    /// </summary>
    private Task<SoftwareBitmap?> GetDragPreviewAsync(string caption)
    {
        if (_dragPreviewTask != null && _dragPreviewCaption == caption && _dragPreviewMode == _launcher.ViewMode)
            return _dragPreviewTask;

        ReleaseDragPreviewBitmap();
        _dragPreviewCaption = caption;
        _dragPreviewMode = _launcher.ViewMode;
        _dragPreviewCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        _dragPreviewTask = RenderDragPreviewAsync(_dragPreviewItem!, caption, _dragPreviewCancellation.Token);
        return _dragPreviewTask;
    }

    /// <summary>Sizes identity content for the active item mode, independent of the hint band.</summary>
    private (double Width, double BodyHeight) GetDragPreviewDimensions(bool isGroup)
    {
        if (IsSmallIconMode)
        {
            if (isGroup) return (136, 36);
            return (128, 28);
        }
        if (IsIconMode)
            return (176, 44);
        if (isGroup)
            return (156, 40);
        return (156, 32);
    }

    /// <summary>
    /// Draws a mode-sized identity card with its live destination hint above it. The native
    /// caption has no positioning API, so both belong to the same bounded bitmap. Normal
    /// layout in a zero-size Canvas preserves the launcher extent and never forces layout.
    /// </summary>
    private async Task<SoftwareBitmap?> RenderDragPreviewAsync(LauncherItem item, string caption, CancellationToken cancellation)
    {
        Canvas? host = null;
        Grid? visual = null;
        EventHandler<object>? onLayout = null;
        try
        {
            var dimensions = GetDragPreviewDimensions(item.IsGroup);
            visual = (Grid)XamlReader.Load(
                """
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      AutomationProperties.AccessibilityView="Raw">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="20" />
                        <RowDefinition Height="4" />
                        <RowDefinition Height="*" />
                    </Grid.RowDefinitions>
                    <Border Padding="6,0" CornerRadius="4"
                            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}">
                        <TextBlock x:Name="DragHint" FontSize="11" VerticalAlignment="Center"
                                   Foreground="{ThemeResource TextFillColorPrimaryBrush}"
                                   TextTrimming="CharacterEllipsis" MaxLines="1" />
                    </Border>
                    <Border x:Name="PreviewBody" Grid.Row="2" Padding="6,4" CornerRadius="6"
                            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                            BorderBrush="{ThemeResource AccentFillColorDefaultBrush}" BorderThickness="1">
                        <Grid ColumnSpacing="6">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="18" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <FontIcon x:Name="KindIcon" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                            <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="0">
                                <TextBlock x:Name="DragName" FontWeight="SemiBold"
                                           Foreground="{ThemeResource TextFillColorPrimaryBrush}"
                                           TextTrimming="CharacterEllipsis" MaxLines="1" />
                                <TextBlock x:Name="DragDetail" FontSize="10"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                           TextTrimming="CharacterEllipsis" MaxLines="1" />
                            </StackPanel>
                        </Grid>
                    </Border>
                </Grid>
                """);
            visual.Width = dimensions.Width;
            visual.Height = DragHintHeight + DragHintGap + dimensions.BodyHeight;
            // Two compact group lines need 28 DIPs inside the 36-DIP body.
            if (IsSmallIconMode && item.IsGroup)
                ((Border)visual.FindName("PreviewBody")).Padding = new Thickness(6, 3, 6, 3);
            ((TextBlock)visual.FindName("DragHint")).Text = caption;
            var icon = (FontIcon)visual.FindName("KindIcon");
            icon.Glyph = item.IsGroup ? "\uE8B7" : "\uE8A5";
            icon.FontSize = IsSmallIconMode ? 14 : 16;
            var name = (TextBlock)visual.FindName("DragName");
            name.Text = GetItemDisplayName(item);
            name.FontSize = IsSmallIconMode ? 11 : 12;
            var detail = (TextBlock)visual.FindName("DragDetail");
            detail.Text = DragPreviewDetail(item);
            detail.Visibility = item.IsGroup ? Visibility.Visible : Visibility.Collapsed;

            host = new Canvas
            {
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            Canvas.SetLeft(visual, -4096);
            host.Children.Add(visual);
            var laidOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            onLayout = (_, _) =>
            {
                if (visual.ActualWidth > 0 && visual.ActualHeight > 0)
                    laidOut.TrySetResult();
            };
            visual.LayoutUpdated += onLayout;
            using var registration = cancellation.Register(() => laidOut.TrySetCanceled(cancellation));
            RootGrid.Children.Add(host);
            await laidOut.Task;
            cancellation.ThrowIfCancellationRequested();
            var rendered = new RenderTargetBitmap();
            await rendered.RenderAsync(visual);
            cancellation.ThrowIfCancellationRequested();
            if (rendered.PixelWidth == 0 || rendered.PixelHeight == 0)
                return null;

            var pixels = await rendered.GetPixelsAsync();
            cancellation.ThrowIfCancellationRequested();
            return SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8,
                rendered.PixelWidth, rendered.PixelHeight, BitmapAlphaMode.Premultiplied);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not render launcher drag preview");
            return null;
        }
        finally
        {
            if (visual != null && onLayout != null)
                visual.LayoutUpdated -= onLayout;
            if (host != null)
                RootGrid.Children.Remove(host);
        }
    }

    /// <summary>Names the moving object without exposing its execution target or synthetic wrapper.</summary>
    private static string DragPreviewDetail(LauncherItem item)
    {
        if (!item.IsGroup)
            return DragPreviewText("LauncherDragItem", "Move item");
        if (item.Children.Count == 1)
            return DragPreviewText("LauncherDragGroupOne", "Group, 1 item");
        return string.Format(CultureInfo.CurrentCulture,
            DragPreviewText("LauncherDragGroup", "Group, {0} items"), item.Children.Count);
    }

    private static string DragPreviewText(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out object value) && value is string text ? text : fallback;

    /// <summary>
    /// Applies feedback only to the latest live target event. Hides old content immediately
    /// while a changed hint renders, including rejected targets. Deferrals have a bounded wait
    /// and always complete; leaving, rebuilding, cancellation and disposal invalidate them.
    /// </summary>
    private async void ApplyDragPreview(DragEventArgs e, string caption)
    {
        int feedbackVersion = ++_dragPreviewFeedbackVersion;
        string? token = _dragToken;
        bool applied = false;
        DragOperationDeferral? deferral = null;
        try
        {
            e.DragUIOverride.Caption = caption;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.IsContentVisible = false;
            if (_dragPreviewItem == null)
            {
                e.DragUIOverride.IsGlyphVisible = true;
                return;
            }

            deferral = e.GetDeferral();
            var pending = GetDragPreviewAsync(caption);
            var bitmap = await pending.WaitAsync(TimeSpan.FromSeconds(1), _dragPreviewCancellation!.Token);
            if (bitmap == null || _disposed || token != _dragToken || _dragItem == null
                || feedbackVersion != _dragPreviewFeedbackVersion || caption != _dragPreviewCaption)
                return;

            e.DragUIOverride.SetContentFromSoftwareBitmap(bitmap,
                new global::Windows.Foundation.Point(8, DragHintHeight + DragHintGap + 8));
            e.DragUIOverride.IsContentVisible = true;
            applied = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not apply launcher drag preview");
        }
        finally
        {
            // Rendering failure must still identify the current operation. Never restore
            // feedback after another target, departure, rebuild or ended session won.
            if (!applied && !_disposed && _dragPreviewItem != null && _dragItem != null
                && token == _dragToken && feedbackVersion == _dragPreviewFeedbackVersion)
            {
                try
                {
                    e.DragUIOverride.Caption = caption;
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.IsGlyphVisible = true;
                }
                catch (Exception ex) { Logger.Debug(ex, "Could not restore launcher drag feedback"); }
            }
            try { deferral?.Complete(); }
            catch (Exception ex) { Logger.Debug(ex, "Could not complete launcher drag preview feedback"); }
        }
    }

    /// <summary>Stops a departed target's asynchronous feedback without discarding the cached card.</summary>
    private void InvalidateDragPreviewFeedback() => _dragPreviewFeedbackVersion++;

    /// <summary>Invalidates the session and releases a pending or completed native bitmap.</summary>
    private void ClearDragPreview()
    {
        InvalidateDragPreviewFeedback();
        _dragPreviewItem = null;
        _dragPreviewCaption = null;
        ReleaseDragPreviewBitmap();
    }

    private void ReleaseDragPreviewBitmap()
    {
        _dragPreviewCancellation?.Cancel();
        _dragPreviewCancellation?.Dispose();
        _dragPreviewCancellation = null;
        if (_dragPreviewTask is { } pending)
            _ = DisposeDragPreviewAsync(pending);
        _dragPreviewTask = null;
    }

    private static async Task DisposeDragPreviewAsync(Task<SoftwareBitmap?> pending)
    {
        try { using var bitmap = await pending; }
        catch (Exception ex) { Logger.Debug(ex, "Could not release launcher drag preview"); }
    }
}
