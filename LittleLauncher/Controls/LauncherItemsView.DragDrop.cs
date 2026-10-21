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
        if (!_isEditMode || IsReadOnlyLauncher)
        {
            e.Cancel = true;
            return;
        }

        if (item.IsColumnBreak || _syntheticGroups.Contains(item)
            || columnIndex < 0 || columnIndex >= _columnLists.Count)
        {
            e.Cancel = true;
            return;
        }

        HideHoverAffordance();
        _dragItem = item;
        _dragSourceCollection = _columnLists[columnIndex];
        e.Data.Properties[InternalItemDragFormat] = _dragToken = Guid.NewGuid().ToString();
        BeginDragPreview(item);
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void ColumnListView_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListViewBase listView)
            return;

        // No _dragItem means the drag didn't start in this flyout , it came from Explorer,
        // the desktop, the Start Menu or a browser.
        if (RejectStaleInternalDrag(e))
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e);
            return;
        }

        ShowInternalDropFeedback(ResolveItemDropTarget(listView, e), e);
    }

    private void ColumnListView_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        InvalidateDragPreviewFeedback();
    }

    private void ColumnListView_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListViewBase listView || listView.Tag is not int columnIndex
            || columnIndex < 0 || columnIndex >= _columnLists.Count)
            return;

        if (RejectStaleInternalDrag(e))
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e);
            return;
        }

        var target = ResolveItemDropTarget(listView, e);
        if (target.List.ItemsSource is ObservableCollection<LauncherItem> collection)
            CompleteInternalDrop(collection, target.Index);
        else
            RejectInternalDrag(e);
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
        if (!_isEditMode || IsReadOnlyLauncher)
        {
            e.Cancel = true;
            return;
        }

        HideHoverAffordance();
        _dragItem = item;
        _dragSourceCollection = group.Children;
        e.Data.Properties[InternalItemDragFormat] = _dragToken = Guid.NewGuid().ToString();
        BeginDragPreview(item);
        e.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void GroupChildList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (RejectStaleInternalDrag(e))
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDragOver(listView, e);
            return;
        }

        ShowInternalDropFeedback(ResolveItemDropTarget(listView, e), e);
    }

    /// <summary>Finds the top-level surface owning a real or synthetic group's child list.</summary>
    private ListViewBase? FindGroupColumn(LauncherItem group) =>
        ColumnListViews().FirstOrDefault(column => column.Items.Contains(group));

    private void GroupChildList_DragLeave(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        InvalidateDragPreviewFeedback();
    }

    private void GroupChildList_Drop(object sender, DragEventArgs e)
    {
        ClearInsertionIndicator();
        if (sender is not ListView listView || listView.Tag is not LauncherItem group)
            return;

        if (RejectStaleInternalDrag(e))
            return;

        if (_dragItem == null || _dragSourceCollection == null)
        {
            ExternalDrop(listView, e);
            return;
        }

        var target = ResolveItemDropTarget(listView, e);
        if (target.List.ItemsSource is ObservableCollection<LauncherItem> collection)
            CompleteInternalDrop(collection, target.Index);
        else
            RejectInternalDrag(e);
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

    private const string InternalItemDragFormat = "LittleLauncher.InternalItemDrag";
    private string? _dragToken;

    /// <summary>Rejects a cancelled/rebuilt drag rather than interpreting it as an external import.</summary>
    private bool RejectStaleInternalDrag(DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue(InternalItemDragFormat, out var token))
            return false;

        if (_isEditMode && !IsReadOnlyLauncher && _dragToken != null && Equals(token, _dragToken)
            && _dragItem != null && _dragSourceCollection != null)
            return false;

        RejectInternalDrag(e);
        return true;
    }

    /// <summary>Clears the old insertion cue whenever the current surface cannot accept this move.</summary>
    private void RejectInternalDrag(DragEventArgs e)
    {
        ClearInsertionIndicator();
        e.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        e.Handled = true;
        ApplyDragPreview(e, DragPreviewText("LauncherDragCannotMove", "Cannot move here"));
    }

    private bool CanMoveTo(ListViewBase listView)
    {
        var target = listView.ItemsSource as ObservableCollection<LauncherItem>;
        return _isEditMode && !IsReadOnlyLauncher && _dragItem != null
            && _dragSourceCollection != null && target != null
            && LauncherItemReorder.CanMove(_dragItem, _dragSourceCollection, target, _columnLists, _syntheticGroups);
    }

    /// <summary>Commits only a valid change; stale sources and no-op drops never save or rebuild.</summary>
    private void CompleteInternalDrop(ObservableCollection<LauncherItem> target, int dropIndex)
    {
        if (_isEditMode && !IsReadOnlyLauncher && _dragItem != null && _dragSourceCollection != null
            && LauncherItemReorder.TryMove(_dragItem, _dragSourceCollection, target, dropIndex,
                _columnLists, _syntheticGroups))
            PersistFlyoutReorder();

        ClearDragState();
    }

    private void ClearDragState()
    {
        ClearInsertionIndicator();
        _dragItem = null;
        _dragSourceCollection = null;
        _dragToken = null;
        ClearDragPreview();
    }

    private static string GetItemDisplayName(LauncherItem item)
    {
        if (item.IsGroup && string.IsNullOrWhiteSpace(item.Name))
            return "section";

        return string.IsNullOrWhiteSpace(item.Name) ? "item" : item.Name;
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
