using LittleLauncher.Classes;
using LittleLauncher.Classes.Settings;
using LittleLauncher.Models;
using LittleLauncher.Pages;
using LittleLauncher.Services;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Microsoft.UI.Xaml.Markup;
using global::Windows.Storage.Pickers;
using WinRT.Interop;
using static LittleLauncher.Classes.NativeMethods;
using Launcher = LittleLauncher.Models.Launcher;
using LittleLauncher.Windows;

namespace LittleLauncher.Controls;

public sealed partial class LauncherItemsView
{
    private void ResizeGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsIconMode || sender is not ResizeGrip grip)
            return;

        _isResizingIconWidth = true;
        _resizeFromLeft = ReferenceEquals(sender, _leftResizeGrip);
        _resizeChangedSetting = false;
        _resizeStartIconsPerRow = GetIconModeIconsPerRow();
        _resizeStartCursorX = _host.GetResizePointerX(e);
        _host.BeginIconResize();

        grip.CapturePointer(e.Pointer);
        grip.SetResizeCursorActive(true);
        e.Handled = true;
    }

    private void ResizeGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingIconWidth)
            return;

        double cursorX = _host.GetResizePointerX(e);
        double dragDistance = _resizeFromLeft ? _resizeStartCursorX - cursorX : cursorX - _resizeStartCursorX;
        int stepWidth = GetIconResizeStepWidth();
        int deltaSteps = (int)Math.Round((double)dragDistance / stepWidth, MidpointRounding.AwayFromZero);
        int targetIconsPerRow = Launcher.ClampIconModeIconsPerRow(_resizeStartIconsPerRow + deltaSteps);

        ApplyIconModeResize(targetIconsPerRow, keepRightEdge: _resizeFromLeft);
        e.Handled = true;
    }

    private void ResizeGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
        e.Handled = true;
    }

    private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CompleteIconResize(sender as ResizeGrip);
    }

    private void CompleteIconResize(ResizeGrip? grip)
    {
        if (!_isResizingIconWidth) return;

        grip?.ReleasePointerCaptures();

        _isResizingIconWidth = false;
        _leftResizeGrip.SetResizeCursorActive(false);
        _rightResizeGrip.SetResizeCursorActive(false);

        if (_resizeChangedSetting)
        {
            // Must also mark the change pending for auto-sync: saving alone lets a periodic
            // sync download overwrite the new icons-per-row with the older remote value.
            SettingsManager.SaveSettings();
            AutoSyncService.NotifyItemsChanged();
        }
    }

    private void ApplyIconModeResize(int iconsPerRow, bool keepRightEdge)
    {
        int clamped = Launcher.ClampIconModeIconsPerRow(iconsPerRow);
        if (_launcher.IconModeIconsPerRow == clamped)
            return;

        _launcher.IconModeIconsPerRow = clamped;
        _resizeChangedSetting = true;
        UpdateFlyoutLayoutInPlace();
        _lastItemsHash = ComputeItemsHash(_launcher);
        _host.ContentSizeChanged(keepRightEdge);
    }

    private void ColumnListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListViewBase listView || listView.Tag is not int columnIndex)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        // Reordering is an edit-mode-only affordance.
        if (!_isEditMode)
        {
            e.Cancel = true;
            return;
        }

        if (_syntheticGroups.Contains(item))
        {
            e.Cancel = true;
            return;
        }

        HideHoverAffordance();
        _dragItem = item;
        _dragSourceCollection = _columnLists[columnIndex];
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void ColumnListView_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListViewBase listView)
            return;

        // No _dragItem means the drag didn't start in this flyout , it came from Explorer,
        // the desktop, the Start Menu or a browser.
        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e, null);
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < listView.Items.Count && listView.Items[dropIndex] is LauncherItem targetItem)
        {
            bool isGrid = IsGridItemsPanel(listView.ItemsPanelRoot);
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {GetItemDisplayName(targetItem)}"
                : $"Move above {GetItemDisplayName(targetItem)}";
        }
        else
            e.DragUIOverride.Caption = "Move to end";

        e.Handled = true;
    }

    private void ColumnListView_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void ColumnListView_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListViewBase listView || listView.Tag is not int columnIndex
            || columnIndex < 0 || columnIndex >= _columnLists.Count)
            return;

        var targetColumn = _columnLists[columnIndex];

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e, targetColumn);
            return;
        }

        int dropIndex = GetDropIndex(listView, e);

        int originalIndex = _dragSourceCollection == targetColumn ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, targetColumn.Count);
        targetColumn.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void ColumnListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void GroupChildList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (e.Items.FirstOrDefault() is not LauncherItem item)
            return;

        // Reordering is an edit-mode-only affordance.
        if (!_isEditMode)
        {
            e.Cancel = true;
            return;
        }

        HideHoverAffordance();
        _dragItem = item;
        _dragSourceCollection = group.Children;
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void GroupChildList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e, group);
            return;
        }

        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
        {
            e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            e.Handled = true;
            return;
        }

        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        int dropIndex = GetDropIndex(listView, e);
        ShowInsertionIndicator(listView, dropIndex);

        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;

        if (dropIndex < group.Children.Count)
        {
            bool isGrid = listView.ItemsPanelRoot is ItemsWrapGrid;
            e.DragUIOverride.Caption = isGrid
                ? $"Move before {GetItemDisplayName(group.Children[dropIndex])}"
                : $"Move above {GetItemDisplayName(group.Children[dropIndex])}";
        }
        else if (group.Children.Count == 0)
        {
            e.DragUIOverride.Caption = $"Move into {GetItemDisplayName(group)}";
        }
        else
        {
            e.DragUIOverride.Caption = $"Move to end of {GetItemDisplayName(group)}";
        }

        e.Handled = true;
    }

    private void GroupChildList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
    }

    private void GroupChildList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e, group.Children);
            return;
        }

        if (_dragItem.IsGroup || _dragItem.IsColumnBreak)
            return;

        int dropIndex = GetDropIndex(listView, e);
        int originalIndex = _dragSourceCollection == group.Children ? _dragSourceCollection.IndexOf(_dragItem) : -1;
        _dragSourceCollection.Remove(_dragItem);
        if (originalIndex >= 0 && originalIndex < dropIndex)
            dropIndex--;

        dropIndex = Math.Clamp(dropIndex, 0, group.Children.Count);
        group.Children.Insert(dropIndex, _dragItem);

        PersistFlyoutReorder();
        ClearDragState();
        e.Handled = true;
    }

    private void GroupChildList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ClearInsertionIndicator();
        ClearDragState();
    }

    private void PersistFlyoutReorder()
    {
        SyncColumnsToFlatList();
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();

        if (_launcher.TrayIconMode == TrayIconModes.Composite)
            MainWindow.Current?.UpdateTrayIcon(_launcher);

        // Full rebuild rather than an in-place update. Synthetic groups are derived from which
        // items are loose, so moving an item between a group and the top level invalidates
        // them: the item that left needs wrapping, and the wrapper it left behind is now empty
        // but still rendered , which is where the stray tinted box came from.
        _lastItemsHash = -1;
        RebuildItemsIfNeeded();

        if (_isEditMode)
        {
            ApplyEditVisuals();
            // Dropping into or out of a group can change the required height.
            ResizeForEditChrome();
        }
    }

    private void ClearDragState()
    {
        _dragItem = null;
        _dragSourceCollection = null;
    }

    private static string GetItemDisplayName(LauncherItem item)
    {
        if (item.IsGroup && string.IsNullOrWhiteSpace(item.Name))
            return "section";

        return string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;
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

    private void ShowInsertionIndicator(ListViewBase listView, int dropIndex)
    {
        ClearInsertionIndicator();

        if (listView.Items.Count == 0)
        {
            _lastIndicatorListView = listView;
            _lastIndicatorListBorderBrush = listView.BorderBrush;
            _lastIndicatorListBorderThickness = listView.BorderThickness;
            listView.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            listView.BorderThickness = new Thickness(2);
            return;
        }

        bool isGrid = IsGridItemsPanel(listView.ItemsPanelRoot);
        int targetIndex = dropIndex < listView.Items.Count ? dropIndex : listView.Items.Count - 1;
        if (targetIndex < 0)
            return;

        if (listView.ContainerFromIndex(targetIndex) is not Control container)
            return;

        _lastIndicatorContainer = container;
        _lastIndicatorContainerBorderBrush = container.BorderBrush;
        _lastIndicatorContainerBorderThickness = container.BorderThickness;
        _lastIndicatorContainerPadding = container.Padding;
        _lastIndicatorContainerMargin = container.Margin;
        container.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        if (isGrid)
        {
            bool appending = dropIndex >= listView.Items.Count;

            // The border's width is given back out of the container's own padding so the grid
            // doesn't reflow , but only as much as it actually has. The icon-mode container
            // style has no padding to give, and WinUI throws on a negative Padding rather than
            // clamping it, which killed the whole drag. The list branch below already clamps.
            var padding = _lastIndicatorContainerPadding;

            // A vertical line reads as "goes beside this item", which is wrong when the drop
            // position actually wraps onto the next row. In that case draw a horizontal line
            // on the row boundary instead, so the landing spot matches what is shown.
            if (GridDropWrapsToNewRow(listView, dropIndex, container, appending))
            {
                container.BorderThickness = appending
                    ? new Thickness(0, 0, 0, 3)
                    : new Thickness(0, 3, 0, 0);
                container.Padding = appending
                    ? new Thickness(padding.Left, padding.Top, padding.Right, Math.Max(0, padding.Bottom - 3))
                    : new Thickness(padding.Left, Math.Max(0, padding.Top - 3), padding.Right, padding.Bottom);
            }
            else if (!appending)
            {
                container.BorderThickness = new Thickness(3, 0, 0, 0);
                container.Padding = new Thickness(Math.Max(0, padding.Left - 3), padding.Top, padding.Right, padding.Bottom);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 3, 0);
                container.Padding = new Thickness(padding.Left, padding.Top, Math.Max(0, padding.Right - 3), padding.Bottom);
            }
        }
        else
        {
            bool insertBefore = dropIndex < listView.Items.Count;
            var padding = _lastIndicatorContainerPadding;

            if (insertBefore)
            {
                container.BorderThickness = new Thickness(0, 2, 0, 0);
                container.Padding = new Thickness(
                    padding.Left,
                    Math.Max(0, padding.Top - 2),
                    padding.Right,
                    padding.Bottom);
            }
            else
            {
                container.BorderThickness = new Thickness(0, 0, 0, 2);
                container.Padding = new Thickness(
                    padding.Left,
                    padding.Top,
                    padding.Right,
                    Math.Max(0, padding.Bottom - 2));
            }

            container.Margin = _lastIndicatorContainerMargin;
        }
    }

    private void ClearInsertionIndicator()
    {
        if (_lastIndicatorContainer != null)
        {
            _lastIndicatorContainer.BorderBrush = _lastIndicatorContainerBorderBrush;
            _lastIndicatorContainer.BorderThickness = _lastIndicatorContainerBorderThickness;
            _lastIndicatorContainer.Padding = _lastIndicatorContainerPadding;
            _lastIndicatorContainer.Margin = _lastIndicatorContainerMargin;
            _lastIndicatorContainer = null;
            _lastIndicatorContainerBorderBrush = null;
            _lastIndicatorContainerBorderThickness = new Thickness(0);
            _lastIndicatorContainerPadding = new Thickness(0);
            _lastIndicatorContainerMargin = new Thickness(0);
        }

        if (_lastIndicatorListView != null)
        {
            _lastIndicatorListView.BorderBrush = _lastIndicatorListBorderBrush;
            _lastIndicatorListView.BorderThickness = _lastIndicatorListBorderThickness;
            _lastIndicatorListView = null;
            _lastIndicatorListBorderBrush = null;
            _lastIndicatorListBorderThickness = new Thickness(0);
        }
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

    private sealed class ResizeGrip : Grid
    {
        private bool _forceResizeCursor;

        public ResizeGrip()
        {
            PointerEntered += ResizeGrip_PointerEntered;
            PointerExited += ResizeGrip_PointerExited;
            PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        }

        public void SetResizeCursorActive(bool active)
        {
            _forceResizeCursor = active;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                active ? Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast : Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }

        private void ResizeGrip_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_forceResizeCursor)
                return;

            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }

        private void ResizeGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _forceResizeCursor = false;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
        }
    }

    // ── Event handlers ──────────────────────────────────────────────

}
