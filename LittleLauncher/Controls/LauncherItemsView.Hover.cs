using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace LittleLauncher.Controls;

/// <summary>Shared passive hover cues and their container-scoped pointer subscriptions.</summary>
public sealed partial class LauncherItemsView
{
    // ── Hover affordance ────────────────────────────────────────────

    private Border? _hoverAffordance;
    private Control? _hoverTarget;
    private Border? _hoverGrip;
    private Border? _hoverPencil;
    private TextBlock? _hoverLabel;
    private object? _hoverLabelMarginValue;

    /// <summary>
    /// Creates the single hover overlay reused for every item and group.
    /// </summary>
    /// <remarks>
    /// Edge/corner cues never take input or contribute to item measurement. Solid themed
    /// backing keeps the glyphs readable over icons; list/header labels temporarily trim
    /// before the cues, with their original local spacing restored when hover ends.
    /// </remarks>
    private void InitializeHoverAffordance()
    {
        // Draw the grip explicitly so its six-dot shape does not depend on a font version.
        // SolidBackground is opaque in light/dark and follows the system window color in HC.
        _hoverAffordance = (Border)XamlReader.Load(
            """
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    HorizontalAlignment="Left" VerticalAlignment="Top"
                    IsHitTestVisible="False" Visibility="Collapsed"
                    AutomationProperties.AccessibilityView="Raw">
                <Grid>
                    <Border x:Name="HoverGrip" Width="12" Height="20" Padding="1" CornerRadius="3"
                            HorizontalAlignment="Left" VerticalAlignment="Center" Margin="2,0,0,0"
                            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                            BorderBrush="{ThemeResource ControlStrongStrokeColorDefaultBrush}"
                            BorderThickness="1">
                        <Canvas Width="6" Height="12" HorizontalAlignment="Center" VerticalAlignment="Center">
                            <Ellipse Width="2" Height="2" Canvas.Left="0" Canvas.Top="0" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                            <Ellipse Width="2" Height="2" Canvas.Left="4" Canvas.Top="0" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                            <Ellipse Width="2" Height="2" Canvas.Left="0" Canvas.Top="5" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                            <Ellipse Width="2" Height="2" Canvas.Left="4" Canvas.Top="5" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                            <Ellipse Width="2" Height="2" Canvas.Left="0" Canvas.Top="10" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                            <Ellipse Width="2" Height="2" Canvas.Left="4" Canvas.Top="10" Fill="{ThemeResource TextFillColorPrimaryBrush}" />
                        </Canvas>
                    </Border>
                    <Border x:Name="HoverPencil" Width="20" Height="20" Padding="1" CornerRadius="3"
                            HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,2,0"
                            Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                            BorderBrush="{ThemeResource ControlStrongStrokeColorDefaultBrush}"
                            BorderThickness="1">
                        <FontIcon Glyph="&#xE70F;" FontSize="14"
                                  Foreground="{ThemeResource TextFillColorPrimaryBrush}"
                                  AutomationProperties.AccessibilityView="Raw" />
                    </Border>
                </Grid>
            </Border>
            """);
        _hoverGrip = (Border)_hoverAffordance.FindName("HoverGrip");
        _hoverPencil = (Border)_hoverAffordance.FindName("HoverPencil");
        _hoverAffordance.Clip = new RectangleGeometry();
        _hoverAffordance.SetValue(Canvas.ZIndexProperty, 12);
        RootGrid.Children.Add(_hoverAffordance);

        RootGrid.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler((_, _) => HideHoverAffordance()),
            handledEventsToo: true);
    }

    private readonly HashSet<Control> _hoverWiredContainers = [];

    /// <summary>
    /// Subscribes a list so its item containers get hover handlers as they are realised.
    /// </summary>
    /// <remarks>
    /// Handlers go on the containers themselves rather than on <c>RootGrid</c>. A root-level
    /// <c>PointerMoved</c> handler does not work here: <c>ListViewItem</c> marks the event
    /// handled for its own hover visuals, so the handler never fires over an actual item ,
    /// only over empty space, which is why empty groups appeared to work and real items did
    /// not. Container-level <c>PointerEntered</c> is the same mechanism the item's own hover
    /// visual uses, so it is reliable.
    /// </remarks>
    private void WireHoverAffordance(ListViewBase list)
    {
        list.ContainerContentChanging -= EditHover_ContainerContentChanging;
        list.ContainerContentChanging += EditHover_ContainerContentChanging;


        // ContainerContentChanging only fires for containers realised after subscribing,
        // so pick up anything already on screen.
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is Control container)
                WireHoverContainer(container);
        }
    }

    private void EditHover_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Phase zero can run before incoming Content is assigned. Restore by container
        // identity, without reading either the old or the incoming item's presentation.
        if (ReferenceEquals(args.ItemContainer, _hoverTarget))
            HideHoverAffordance();

        if (!args.InRecycleQueue && args.ItemContainer is Control container)
            WireHoverContainer(container);
    }

    private void WireHoverContainer(Control container)
    {
        bool isNew = _hoverWiredContainers.Add(container);
        if (!isNew) return;
        container.PointerEntered += Container_PointerEnteredForEdit;
        container.PointerExited += Container_PointerExitedForEdit;
        container.Unloaded += HoverContainer_Unloaded;
    }

    /// <summary>
    /// Detaches the hover pointer handlers from every wired container and forgets them.
    /// Called from <c>RebuildColumnsPanel</c>, which discards all containers anyway.
    /// </summary>
    /// <remarks>
    /// Native views remain registered for warm reopening, while regular editors live until
    /// their window closes. Both must detach before discarding realised containers: retained
    /// pointer handlers previously kept old containers and their native surfaces alive after
    /// repeated sync rebuilds. Dispose also uses this cleanup for a closing regular editor.
    /// </remarks>
    private void ClearHoverWiring()
    {
        HideHoverAffordance();
        foreach (var container in _hoverWiredContainers)
        {
            container.PointerEntered -= Container_PointerEnteredForEdit;
            container.PointerExited -= Container_PointerExitedForEdit;
            container.Unloaded -= HoverContainer_Unloaded;
        }
        _hoverWiredContainers.Clear();
    }

    /// <summary>
    /// Gets the <see cref="LauncherItem"/> a container is displaying.
    /// </summary>
    /// <remarks>
    /// Reads <c>Content</c>, not <c>DataContext</c>. These lists are populated via
    /// <c>ItemsSource</c>, which puts the item in the container's <c>Content</c> and leaves
    /// <c>DataContext</c> null , checking <c>DataContext</c> silently rejects every item.
    /// Re-read on each event rather than captured, since containers are recycled.
    /// </remarks>
    private static LauncherItem? GetContainerItem(Control container) =>
        (container as ContentControl)?.Content as LauncherItem
        ?? container.DataContext as LauncherItem;

    private void Container_PointerEnteredForEdit(object sender, PointerRoutedEventArgs e)
    {
        if (!_isEditMode || IsReadOnlyLauncher || sender is not Control container) return;

        var item = GetContainerItem(container);
        if (item == null) return;
        if (item.IsColumnBreak) return;
        if (item.IsGroup && !IsEditableGroup(item)) return;

        PositionHoverAffordance(container);

        // Stop the enclosing group container from claiming the hover: the child's handler
        // runs first, so without this the parent would immediately overwrite the position
        // with the whole group's bounds.
        e.Handled = true;
    }

    private void Container_PointerExitedForEdit(object sender, PointerRoutedEventArgs e)
    {
        HideHoverAffordance();

        // Deliberately not marked handled. Starting a drag moves the pointer out of the
        // container, and swallowing that exit interfered with the ListView's own drag
        // gesture , items inside groups became hard to pick up.
    }

    private void HoverContainer_Unloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _hoverTarget))
            HideHoverAffordance();
    }

    /// <summary>
    /// Places cues at item edges or icon corners; groups use only their header bounds.
    /// Label margins affect native ellipsis only, never item or column dimensions.
    /// </summary>
    private void PositionHoverAffordance(Control container)
    {
        HideHoverAffordance();
        if (_hoverAffordance == null || _hoverGrip == null || _hoverPencil == null)
            return;

        try
        {
            bool isGroup = GetContainerItem(container)?.IsGroup == true;
            TextBlock? label = isGroup || IsListMode ? FindHoverLabel(container, isGroup) : null;
            FrameworkElement bounds = container;
            Thickness outerMargin = new(0);
            if (isGroup)
            {
                // The header Grid's margin is part of the header, but none of the child
                // list is. Centering on the group container would cover its child items.
                if (label == null || VisualTreeHelper.GetParent(label) is not FrameworkElement header)
                    return;
                bounds = header;
                outerMargin = header.Margin;
            }

            var origin = bounds.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            double width = bounds.ActualWidth + outerMargin.Left + outerMargin.Right;
            double height = bounds.ActualHeight + outerMargin.Top + outerMargin.Bottom;
            if (width <= 0 || height <= 0)
                return;

            _hoverTarget = container;
            bool isIconTile = IsIconMode && !isGroup;
            ConfigureHoverCues(isIconTile, IsSmallIconMode);
            if (label != null)
                ReserveHoverLabelSpace(label, reserveLeading: isGroup);

            _hoverAffordance.Width = width;
            _hoverAffordance.Height = height;
            if (_hoverAffordance.Clip is RectangleGeometry clip)
                clip.Rect = new global::Windows.Foundation.Rect(0, 0, width, height);
            _hoverAffordance.Margin = new Thickness(
                origin.X - outerMargin.Left, origin.Y - outerMargin.Top, 0, 0);
            _hoverAffordance.Visibility = Visibility.Visible;
        }
        catch { HideHoverAffordance(); }
    }

    /// <summary>Uses row edges, large-icon top corners or compact-icon opposite corners.</summary>
    private void ConfigureHoverCues(bool isIconTile, bool useCompactCues)
    {
        double inset = useCompactCues ? 1 : 2;
        double topInset = isIconTile ? inset : 0;
        var gripAlignment = VerticalAlignment.Center;
        var pencilAlignment = VerticalAlignment.Center;
        if (isIconTile)
        {
            gripAlignment = VerticalAlignment.Top;
            pencilAlignment = useCompactCues ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        }

        _hoverGrip!.Width = useCompactCues ? 10 : 12;
        _hoverGrip.Height = useCompactCues ? 16 : 20;
        _hoverGrip.VerticalAlignment = gripAlignment;
        _hoverGrip.Margin = new Thickness(inset, topInset, 0, 0);

        _hoverPencil!.Width = useCompactCues ? 16 : 20;
        _hoverPencil.Height = useCompactCues ? 16 : 20;
        _hoverPencil.VerticalAlignment = pencilAlignment;
        _hoverPencil.Margin = isIconTile && useCompactCues
            ? new Thickness(0, 0, inset, inset)
            : new Thickness(0, topInset, inset, 0);
        ((FontIcon)_hoverPencil.Child).FontSize = useCompactCues ? 12 : 14;
    }

    /// <summary>
    /// Finds the named item label or the group's first header text without entering nested
    /// lists, whose labels and hover state belong to their own child containers.
    /// </summary>
    private static TextBlock? FindHoverLabel(DependencyObject element, bool isGroup)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var child = VisualTreeHelper.GetChild(element, index);
            if (child is ListViewBase)
                continue;
            if (child is TextBlock label && (isGroup || label.Name == "ItemNameLabel"))
                return label;
            if (FindHoverLabel(child, isGroup) is { } descendant)
                return descendant;
        }
        return null;
    }

    /// <summary>
    /// Borrows label spacing inside the unchanged row/header. Native CharacterEllipsis and
    /// IsTextTrimmedChanged continue to drive full-name tooltips without covering text.
    /// </summary>
    private void ReserveHoverLabelSpace(TextBlock label, bool reserveLeading)
    {
        _hoverLabel = label;
        _hoverLabelMarginValue = label.ReadLocalValue(MarginProperty);
        Thickness margin = label.Margin;
        // Rows/headers already leave 8 DIPs on each outer edge. Reserve only the extra
        // space needed by each cue plus its text gap; narrow small-icon headers use compact cues.
        double gap = IsSmallIconMode ? 2 : 4;
        double leading = reserveLeading ? Math.Max(0, _hoverGrip!.Width + _hoverGrip.Margin.Left + gap - 8) : 0;
        double trailing = Math.Max(0, _hoverPencil!.Width + _hoverPencil.Margin.Right + gap - 8);
        label.Margin = new Thickness(margin.Left + leading, margin.Top, margin.Right + trailing, margin.Bottom);
    }

    /// <summary>Restores borrowed label presentation before releasing hover/container state.</summary>
    private void HideHoverAffordance()
    {
        if (_hoverLabel is { } label)
        {
            _hoverLabel = null;
            if (ReferenceEquals(_hoverLabelMarginValue, DependencyProperty.UnsetValue))
                label.ClearValue(MarginProperty);
            else
                label.SetValue(MarginProperty, _hoverLabelMarginValue);
            _hoverLabelMarginValue = null;
        }

        _hoverTarget = null;
        if (_hoverAffordance != null)
            _hoverAffordance.Visibility = Visibility.Collapsed;
    }
}
