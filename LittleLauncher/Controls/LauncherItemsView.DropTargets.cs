using LittleLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;

namespace LittleLauncher.Controls;

/// <summary>Shared destination resolution and passive insertion/group feedback for both hosts.</summary>
public sealed partial class LauncherItemsView
{
    private readonly record struct ItemDropTarget(
        ListViewBase List, int Index, LauncherItem? Group = null,
        Control? GroupContainer = null,
        LauncherDropGeometry.GroupZone Zone = LauncherDropGeometry.GroupZone.Outside);

    /// <summary>
    /// Resolves nested-list and header events against the same full-group bounds. Group
    /// drags retain whole-container midpoint reordering; synthetic wrappers stay invisible.
    /// </summary>
    private ItemDropTarget ResolveItemDropTarget(ListViewBase surface, DragEventArgs e)
    {
        if (surface.Tag is LauncherItem owner)
        {
            var column = FindGroupColumn(owner);
            if (_dragItem?.IsGroup == true && column != null)
                return new ItemDropTarget(column, GetDropIndex(column, e));

            if (column != null && TryResolveGroupDrop(column, owner, e, out var nestedTarget))
                return nestedTarget;
        }
        else if (_dragItem?.IsGroup != true)
        {
            foreach (var group in surface.Items.OfType<LauncherItem>().Where(item => item.IsGroup))
            {
                if (TryResolveGroupDrop(surface, group, e, out var groupTarget))
                    return groupTarget;
            }
        }

        return new ItemDropTarget(surface, GetDropIndex(surface, e));
    }

    /// <summary>
    /// Outer bands always target the column, including when the event arrived through a
    /// child list. Inside targets the children; a header drop appends without obscuring a child.
    /// </summary>
    private bool TryResolveGroupDrop(
        ListViewBase column, LauncherItem group, DragEventArgs e, out ItemDropTarget target)
    {
        target = default;
        if (_syntheticGroups.Contains(group)
            || column.ContainerFromItem(group) is not Control container)
            return false;

        var point = e.GetPosition(container);
        var zone = LauncherDropGeometry.GetGroupZone(
            point.X, point.Y, container.ActualWidth, container.ActualHeight);
        if (zone == LauncherDropGeometry.GroupZone.Outside)
            return false;

        int groupIndex = column.Items.IndexOf(group);
        if (zone != LauncherDropGeometry.GroupZone.Inside)
        {
            int index = zone == LauncherDropGeometry.GroupZone.Before ? groupIndex : groupIndex + 1;
            target = new ItemDropTarget(column, index, group, container, zone);
            return true;
        }

        var children = _loadedGroupChildLists.FirstOrDefault(list =>
            ReferenceEquals(list.Tag, group) && list.IsLoaded);
        if (children == null)
            return false;

        int childIndex = GetDropIndex(children, e);
        if (e.GetPosition(children).Y < 0)
            childIndex = children.Items.Count;
        target = new ItemDropTarget(children, childIndex, group, container, zone);
        return true;
    }

    /// <summary>Validates the resolved collection before showing any allowed-drop feedback.</summary>
    private void ShowInternalDropFeedback(ItemDropTarget target, DragEventArgs e)
    {
        if (!CanMoveTo(target.List))
        {
            RejectInternalDrag(e);
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        ShowDropTarget(target);
        e.Handled = true;
        ApplyDragPreview(e, GetDropCaption(target));
    }

    /// <summary>Names group membership or sibling placement explicitly, including column ends.</summary>
    private string GetDropCaption(ItemDropTarget target)
    {
        if (target.Group != null)
        {
            string groupName = GetItemDisplayName(target.Group);
            if (target.Zone == LauncherDropGeometry.GroupZone.Before)
                return FormatDropCaption("LauncherDragBeforeGroup", "Move before {0}", groupName);
            if (target.Zone == LauncherDropGeometry.GroupZone.After)
                return FormatDropCaption("LauncherDragAfterGroup", "Move after {0}", groupName);

            if (target.Index < target.Group.Children.Count)
                return FormatDropCaption("LauncherDragIntoGroupBefore", "Into {0}: before {1}",
                    groupName, GetItemDisplayName(target.Group.Children[target.Index]));
            return FormatDropCaption("LauncherDragIntoGroupEnd", "Into {0}: at end", groupName);
        }

        if (target.Index < target.List.Items.Count && target.List.Items[target.Index] is LauncherItem next)
        {
            return IsGridItemsPanel(target.List.ItemsPanelRoot)
                ? $"Move before {GetItemDisplayName(next)}"
                : $"Move above {GetItemDisplayName(next)}";
        }
        return "Move to end";
    }

    private static string FormatDropCaption(string key, string fallback, params object[] names) =>
        string.Format(DragPreviewText(key, fallback), names);

    /// <summary>Uses a full-width boundary for siblings and a tinted, inset cue for membership.</summary>
    private void ShowDropTarget(ItemDropTarget target)
    {
        ShowInsertionIndicator(target.List, target.Index);
        if (target.GroupContainer is not { } container)
            return;

        if (target.Zone == LauncherDropGeometry.GroupZone.Inside)
        {
            ShowGroupDropHighlight(container);
            if (target.Group != null && target.Index >= target.Group.Children.Count)
                ShowGroupEndFeedback(target);
            return;
        }

        try
        {
            var origin = container.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            double y = origin.Y;
            if (target.Zone == LauncherDropGeometry.GroupZone.After)
                y += container.ActualHeight;
            _insertionIndicator!.Width = container.ActualWidth;
            _insertionIndicator.Height = 2;
            _insertionIndicator.Margin = new Thickness(origin.X, y, 0, 0);
            _insertionIndicator.BorderThickness = new Thickness(0, 2, 0, 0);
            _insertionIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            ClearInsertionIndicator();
        }
    }

    private Grid? _groupDropHighlight;
    private Border? _groupEndFade;
    private string? _failedGroupDropToken;

    /// <summary>
    /// Tints the complete receiving group without changing layout or hit testing. Resource
    /// dictionaries precede child consumers for XamlReader; construction failure retains the
    /// caret and allowed drop, with at most one failed attempt per drag/edit session.
    /// </summary>
    private void ShowGroupDropHighlight(Control container)
    {
        string attemptKey = _dragToken ?? $"external:{_sessionVersion}";
        if (_failedGroupDropToken == attemptKey)
            return;

        try
        {
            if (_groupDropHighlight == null)
            {
                _groupDropHighlight = (Grid)XamlReader.Load(
                    """
                    <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                          HorizontalAlignment="Left" VerticalAlignment="Top"
                          IsHitTestVisible="False" Visibility="Collapsed"
                          AutomationProperties.AccessibilityView="Raw">
                        <Grid.Resources>
                            <ResourceDictionary>
                                <ResourceDictionary.ThemeDictionaries>
                                    <ResourceDictionary x:Key="Default">
                                        <SolidColorBrush x:Key="GroupDropFill" Color="{ThemeResource SystemAccentColor}" Opacity="0.10" />
                                        <SolidColorBrush x:Key="GroupDropStroke" Color="{ThemeResource SystemAccentColor}" />
                                        <LinearGradientBrush x:Key="GroupEndScrim" StartPoint="0,0" EndPoint="0,1">
                                            <GradientStop Offset="0" Color="Transparent" />
                                            <GradientStop Offset="0.55" Color="Transparent" />
                                            <GradientStop Offset="0.85" Color="{ThemeResource SolidBackgroundFillColorBase}" />
                                            <GradientStop Offset="1" Color="{ThemeResource SolidBackgroundFillColorBase}" />
                                        </LinearGradientBrush>
                                    </ResourceDictionary>
                                    <ResourceDictionary x:Key="HighContrast">
                                        <SolidColorBrush x:Key="GroupDropFill" Color="Transparent" />
                                        <SolidColorBrush x:Key="GroupDropStroke" Color="{ThemeResource SystemColorHighlightColor}" />
                                        <SolidColorBrush x:Key="GroupEndScrim" Color="Transparent" />
                                    </ResourceDictionary>
                                </ResourceDictionary.ThemeDictionaries>
                            </ResourceDictionary>
                        </Grid.Resources>
                        <Border CornerRadius="6" Background="{ThemeResource GroupDropFill}" />
                        <Border x:Name="GroupEndFade" CornerRadius="4" Visibility="Collapsed"
                                HorizontalAlignment="Left" VerticalAlignment="Top"
                                Background="{ThemeResource GroupEndScrim}" />
                        <!-- Paint the outline after the fade so the entire group stays crisp. -->
                        <Border CornerRadius="6" BorderThickness="1"
                                BorderBrush="{ThemeResource GroupDropStroke}" />
                    </Grid>
                    """);
                _groupEndFade = (Border)_groupDropHighlight.FindName("GroupEndFade");
                _groupDropHighlight.SetValue(Canvas.ZIndexProperty, 9);
                RootGrid.Children.Add(_groupDropHighlight);
            }

            var origin = container.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            _groupDropHighlight.Width = container.ActualWidth;
            _groupDropHighlight.Height = container.ActualHeight;
            _groupDropHighlight.Margin = new Thickness(origin.X, origin.Y, 0, 0);
            _groupDropHighlight.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _failedGroupDropToken = attemptKey;
            if (_groupDropHighlight != null)
                _groupDropHighlight.Visibility = Visibility.Collapsed;
            Logger.Warn(ex, "Could not show the receiving group highlight");
        }
    }

    /// <summary>
    /// Gradually fades only the last child's lower area beneath the receiving overlay,
    /// making room for the overlapping end marker. Opacity, dimensions and input remain untouched, so
    /// hiding the overlay restores its exact presentation even if it is the drag source.
    /// High contrast omits the fade and keeps the inset marker and explicit caption.
    /// </summary>
    private void ShowGroupEndFeedback(ItemDropTarget target)
    {
        if (target.List.Items.Count == 0
            || target.List.ContainerFromIndex(target.List.Items.Count - 1) is not Control lastChild)
            return;

        var marker = LauncherDropGeometry.GetEndMarker(lastChild.ActualWidth, lastChild.ActualHeight);
        if (marker == null)
            return;

        try
        {
            if (_groupEndFade != null && _groupDropHighlight?.Visibility == Visibility.Visible)
            {
                ConfigureGroupEndGradient(marker.Value.Top, lastChild.ActualHeight);
                // The overlay's new Margin may not have been arranged yet. Its origin
                // follows the already-arranged group, so use that stable coordinate space.
                var childOrigin = lastChild.TransformToVisual(target.GroupContainer!)
                    .TransformPoint(new global::Windows.Foundation.Point(0, 0));
                _groupEndFade.Width = lastChild.ActualWidth;
                _groupEndFade.Height = lastChild.ActualHeight;
                _groupEndFade.Margin = new Thickness(childOrigin.X, childOrigin.Y, 0, 0);
                _groupEndFade.Visibility = Visibility.Visible;
            }

            var origin = lastChild.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            _insertionIndicator!.Width = marker.Value.Width;
            _insertionIndicator.Height = marker.Value.Height;
            _insertionIndicator.Margin = new Thickness(
                origin.X + marker.Value.Left, origin.Y + marker.Value.Top, 0, 0);
            _insertionIndicator.BorderThickness = new Thickness(0, marker.Value.Height, 0, 0);
            _insertionIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            // A rebuild can detach the child during feedback. Retain the ordinary caret
            // and accepted move; decoration must never interrupt a valid drop.
            if (_groupEndFade != null)
                _groupEndFade.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Leaves most content clear, then fades toward the inset marker in every view mode.
    /// Transparent stops keep the opaque stop's RGB to avoid a dark fringe in light theme;
    /// the opaque stop retains its live ThemeResource color. High contrast uses no gradient.
    /// </summary>
    private void ConfigureGroupEndGradient(double markerTop, double childHeight)
    {
        if (_groupEndFade?.Background is not LinearGradientBrush gradient)
            return;

        var edgeColor = gradient.GradientStops[2].Color;
        var transparent = global::Windows.UI.Color.FromArgb(0, edgeColor.R, edgeColor.G, edgeColor.B);
        gradient.GradientStops[0].Color = transparent;
        gradient.GradientStops[1].Color = transparent;

        double edge = markerTop / childHeight;
        gradient.GradientStops[1].Offset = Math.Min(edge, Math.Max(0.55, edge - 0.22));
        gradient.GradientStops[2].Offset = Math.Min(1, edge + 0.02);
    }

    /// <summary>
    /// True when an icon-grid drop position lands at the start of a row rather than beside
    /// the item the indicator is drawn on.
    /// </summary>
    /// <remarks>
    /// Two cases: inserting before an item that is first in its row, and appending when the
    /// final row is already full so the new item wraps below it.
    /// </remarks>
    private bool GridDropWrapsToNewRow(ListViewBase listView, int dropIndex, Control container, bool appending)
    {
        try
        {
            if (appending)
            {
                // Does another cell fit to the right of the last item?
                //
                // Measured against the widest a row may become, not the list's current width:
                // a group's child list is only as wide as the children it already has, but it
                // grows up to the icons-per-row limit. Using ActualWidth reported "wraps" for
                // any group that still had room to expand.
                double maxRowWidth = GetIconModeIconsPerRow() * GetActiveIconCellWidth();
                double available = Math.Max(listView.ActualWidth, maxRowWidth);

                var origin = container.TransformToVisual(listView)
                    .TransformPoint(new global::Windows.Foundation.Point(0, 0));
                return origin.X + (container.ActualWidth * 2) > available + 1;
            }

            if (dropIndex <= 0) return false;

            // Starts a row if the preceding item sits on an earlier row.
            if (listView.ContainerFromIndex(dropIndex - 1) is not Control previous) return false;

            double targetY = container.TransformToVisual(listView)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;
            double previousY = previous.TransformToVisual(listView)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y;

            return targetY > previousY + 1;
        }
        catch
        {
            return false;
        }
    }

    private Border? _insertionIndicator;

    /// <summary>
    /// Draws the insertion caret in the shared overlay plane. Changing container borders
    /// changes grid geometry when there is no padding to compensate, causing the hit-tested
    /// midpoint to move under a stationary pointer. This cue never changes item dimensions.
    /// </summary>
    private void ShowInsertionIndicator(ListViewBase listView, int dropIndex)
    {
        ClearInsertionIndicator();
        if (_insertionIndicator == null)
        {
            _insertionIndicator = (Border)XamlReader.Load(
                """
                <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        HorizontalAlignment="Left" VerticalAlignment="Top"
                        BorderBrush="{ThemeResource AccentFillColorDefaultBrush}"
                        IsHitTestVisible="False" Visibility="Collapsed"
                        AutomationProperties.AccessibilityView="Raw" />
                """);
            _insertionIndicator.SetValue(Canvas.ZIndexProperty, 10);
            RootGrid.Children.Add(_insertionIndicator);
        }

        try
        {
            FrameworkElement anchor = listView;
            double width = listView.ActualWidth;
            double height = listView.ActualHeight;
            double xOffset = 0;
            double yOffset = 0;
            Thickness stroke = new(2);
            if (listView.Items.Count == 0 && listView.Tag is LauncherItem)
            {
                height = 2;
                yOffset = 4;
                stroke = new Thickness(0, 2, 0, 0);
            }
            if (listView.Items.Count > 0)
            {
                int index = Math.Min(dropIndex, listView.Items.Count - 1);
                if (index < 0 || listView.ContainerFromIndex(index) is not Control container)
                    return;

                anchor = container;
                width = container.ActualWidth;
                height = 2;
                stroke = new Thickness(0, 2, 0, 0);
                bool appending = dropIndex >= listView.Items.Count;
                if (IsGridItemsPanel(listView.ItemsPanelRoot)
                    && !GridDropWrapsToNewRow(listView, dropIndex, container, appending))
                {
                    width = 3;
                    height = container.ActualHeight;
                    stroke = new Thickness(3, 0, 0, 0);
                    if (appending)
                        xOffset = Math.Max(0, container.ActualWidth - width);
                }
                else if (appending)
                {
                    yOffset = Math.Max(0, container.ActualHeight - height);
                }
            }

            // Membership carets sit inside the group's child area, so appending a child
            // cannot look like the full-width sibling caret at the outer group boundary.
            if (listView.Tag is LauncherItem group && !_syntheticGroups.Contains(group))
            {
                if (height == 2)
                {
                    double inset = Math.Min(8, width / 4);
                    xOffset += inset;
                    width -= inset * 2;
                }
                else if (width == 3)
                {
                    double inset = Math.Min(4, height / 4);
                    yOffset += inset;
                    height -= inset * 2;
                }
            }

            if (width <= 0 || height <= 0)
                return;

            var origin = anchor.TransformToVisual(RootGrid)
                .TransformPoint(new global::Windows.Foundation.Point(0, 0));
            _insertionIndicator.Width = width;
            _insertionIndicator.Height = height;
            _insertionIndicator.Margin = new Thickness(origin.X + xOffset, origin.Y + yOffset, 0, 0);
            _insertionIndicator.BorderThickness = stroke;
            _insertionIndicator.Visibility = Visibility.Visible;
        }
        catch
        {
            // A rebuild may have detached the container between native drag events.
            ClearInsertionIndicator();
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_insertionIndicator != null)
            _insertionIndicator.Visibility = Visibility.Collapsed;
        if (_groupDropHighlight != null)
            _groupDropHighlight.Visibility = Visibility.Collapsed;
        if (_groupEndFade != null)
            _groupEndFade.Visibility = Visibility.Collapsed;
    }

    private static int GetDropIndex(ListViewBase listView, DragEventArgs e)
    {
        if (IsGridItemsPanel(listView.ItemsPanelRoot))
            return GetDropIndexGrid(listView, e);

        var position = e.GetPosition(listView);
        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.ContainerFromIndex(index) is not Control container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));
            if (position.Y < point.Y + (container.ActualHeight / 2))
                return index;
        }

        return listView.Items.Count;
    }

    private static int GetDropIndexGrid(ListViewBase listView, DragEventArgs e)
    {
        var position = e.GetPosition(listView);
        int count = listView.Items.Count;
        if (count == 0)
            return 0;

        int bestIndex = count;
        double bestDistance = double.MaxValue;
        double contentBottom = 0;

        for (int index = 0; index < count; index++)
        {
            if (listView.ContainerFromIndex(index) is not Control container)
                continue;

            var transform = container.TransformToVisual(listView);
            var point = transform.TransformPoint(new global::Windows.Foundation.Point(0, 0));

            double top = point.Y;
            double bottom = top + container.ActualHeight;
            double left = point.X;
            double midX = left + (container.ActualWidth / 2);
            contentBottom = Math.Max(contentBottom, bottom);

            if (position.Y >= top && position.Y < bottom)
            {
                if (position.X < midX)
                    return index;

                bestIndex = index + 1;
                bestDistance = 0;
            }
            else if (bestDistance > 0)
            {
                double verticalDistance = position.Y < top ? top - position.Y : position.Y - bottom;
                if (verticalDistance < bestDistance)
                {
                    bestDistance = verticalDistance;
                    bestIndex = position.X < midX ? index : index + 1;
                }
            }
        }

        // Anywhere in the empty space below the last row means "put it at the end". Without
        // this the nearest-band fallback above could return an index *before* the last item
        // whenever the pointer happened to sit left of its midpoint, so appending required
        // landing precisely beside the final item.
        if (position.Y >= contentBottom)
            return count;

        return Math.Min(bestIndex, count);
    }

}
