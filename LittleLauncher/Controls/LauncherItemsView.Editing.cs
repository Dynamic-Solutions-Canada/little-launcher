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
using Microsoft.UI.Xaml.Data;
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
    /// <summary>Padding inside the toolbar's tinted container.</summary>
    private const int EditToolbarPadding = 3;

    /// <summary>Bottom margin under the edit toolbar.</summary>
    private const int EditToolbarGap = 8;

    /// <summary>
    /// Height the edit toolbar adds to the flyout: none. It floats in its own window above the
    /// flyout, so edit mode no longer changes the flyout's height at all.
    /// </summary>
    private static double CurrentEditChromeHeight => 0;

    /// <summary>Size of the floating toolbar bar, in DIPs, including its padding.</summary>
    private (int Width, int Height) EditToolbarSizeDips
    {
        get
        {
            if (_editToolbar is not VariableSizedWrapGrid compactToolbar) return (0, 0);

            int count = compactToolbar.Children.Count;
            int perRow = Math.Max(1, compactToolbar.MaximumRowsOrColumns);
            int rows = (int)Math.Ceiling(count / (double)perRow);
            int columns = Math.Min(count, perRow);

            int width = (columns * EditButtonSlot) + (EditToolbarPadding * 2);
            int height = (rows * EditButtonSlot) + (EditToolbarPadding * 2);

            return (width, height);
        }
    }

    private const int EditButtonSize = 28;

    /// <summary>Grid slot per toolbar button: the button plus its surrounding margin.</summary>
    private const int EditButtonSlot = 32;

    private bool _isEditMode;
    private Button? _editPencil;
    private Panel? _editToolbar;

    /// <summary>The host places the toolbar outside the launcher body; only the popup adds padding.</summary>
    private Border? _editToolbarHost;

    // ── Empty state ─────────────────────────────────────────────────

    /// <summary>Reserved height for the empty-launcher placeholder.</summary>
    private const int EmptyPlaceholderHeight = 56;

    private TextBlock? _emptyPlaceholder;

    /// <summary>True when the launcher has no real items (column breaks are not content).</summary>
    private bool LauncherIsEmpty => !_launcher.Items.Any(i => !i.IsColumnBreak);

    private double CurrentEmptyPlaceholderHeight => LauncherIsEmpty ? EmptyPlaceholderHeight : 0;

    /// <summary>
    /// Creates the empty-state text.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <c>InitializeEditChrome</c>: that returns early for read-only
    /// launchers and runs after the first rebuild, so the placeholder was still null at the
    /// exact moment an empty launcher needed it.
    /// </remarks>
    private void InitializeEmptyPlaceholder()
    {
        _emptyPlaceholder = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(8, 4, 8, 8),
            Visibility = Visibility.Collapsed,
        };
        ContentStack.Children.Add(_emptyPlaceholder);
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Shows guidance inside an empty launcher, so it never renders as a blank box.
    /// </summary>
    /// <remarks>
    /// In the flyout rather than a one-off tip on creation: an empty launcher needs this
    /// explanation whenever it is empty, not only in the moments after it is made.
    /// </remarks>
    private void UpdateEmptyPlaceholder()
    {
        if (_emptyPlaceholder == null) return;

        bool empty = LauncherIsEmpty;
        _emptyPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;

        _emptyPlaceholder.Text = _isEditMode
            ? "This launcher is empty.\nAdd your first item with + above."
            : "This launcher is empty.\nHover here and click the pencil to add items.";

        _emptyPlaceholder.MaxWidth = Math.Max(120, GetFlyoutWidth() - (FlyoutOuterPadding * 4));
    }

    private bool IsEditableGroup(LauncherItem item) =>
        item.IsGroup && !_syntheticGroups.Contains(item);

    // ── Chrome ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds dimension-neutral pencil overlays and a shared toolbar for the current host.
    /// The toolbar stays outside ContentStack in both hosts, preserving item geometry.
    /// </summary>
    private void InitializeEditChrome()
    {
        // Shared launchers the user doesn't own are read-only , no entry point at all.
        if (IsReadOnlyLauncher) return;

        _editPencil = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        ToolTipService.SetToolTip(_editPencil, "Edit items");
        _editPencil.SetValue(Canvas.ZIndexProperty, 11);
        _editPencil.Click += (_, _) => EnterEditMode();
        RootGrid.Children.Add(_editPencil);

        // Hover reveal , the pencil is invisible until the pointer is over the flyout.
        RootGrid.PointerEntered += (_, _) => SetPencilVisible(true);
        RootGrid.PointerExited += (_, _) => SetPencilVisible(false);

        // Each host places this toolbar outside the launcher's content geometry.
        _editToolbar = BuildEditToolbar();

        _editToolbarHost = new Border
        {
            Child = _editToolbar,
            Padding = _host.IsRegularWindow ? new Thickness(0) : new Thickness(EditToolbarPadding),
        };

        InitializeHoverAffordance();
    }

    private void SetPencilVisible(bool visible)
    {
        if (_editPencil == null || _isEditMode) return;
        _editPencil.Opacity = visible ? 1 : 0;
        _editPencil.IsHitTestVisible = visible;
    }

    /// <summary>Builds the same edit commands with presentation appropriate to the window host.</summary>
    private Panel BuildEditToolbar()
    {
        Panel toolbar;
        if (_host.IsRegularWindow)
        {
            toolbar = new WrappingPanel();
        }
        else
        {
            toolbar = new VariableSizedWrapGrid
            {
                Orientation = Orientation.Horizontal,
                ItemWidth = EditButtonSlot,
                ItemHeight = EditButtonSlot,
            };
        }

        toolbar.Children.Add(EditButton("", EditActionLabel("LauncherItemsAddItem", "Add item"), () => _ = AddItemAsync()));
        toolbar.Children.Add(EditButton("", EditActionLabel("LauncherItemsAddGroup", "Add group"), () => _ = AddGroupAsync()));
        toolbar.Children.Add(EditButtonDrawn(BuildAddColumnIcon(), EditActionLabel("LauncherItemsAddColumn", "Add column"), AddColumn));
        toolbar.Children.Add(EditButton("", EditActionLabel("LauncherItemsSettingsAction", "Launcher settings"), () => _ = OpenLauncherSettingsAsync()));
        if (!_host.IsRegularWindow)
            toolbar.Children.Add(EditButton("", "Done", ExitEditMode));
        return toolbar;
    }

    private static string EditActionLabel(string key, string fallback) =>
        Application.Current.Resources.TryGetValue(key, out object value) ? (string)value : fallback;

    /// <summary>Regular buttons wrap at natural widths; native buttons retain their fixed slots.</summary>
    private void UpdateEditToolbarColumns()
    {
        double usable = _toolbarAvailableWidth ?? (GetFlyoutWidth() - (FlyoutOuterPadding * 2));
        if (_editToolbar is WrappingPanel regularToolbar)
        {
            regularToolbar.MaxWidth = Math.Max(1, usable);
            return;
        }

        if (_editToolbar is not VariableSizedWrapGrid compactToolbar) return;
        int perRow = Math.Max(1, (int)(usable / EditButtonSlot));
        compactToolbar.MaximumRowsOrColumns = Math.Min(perRow, compactToolbar.Children.Count);
    }

    /// <summary>
    /// Single overload by design. A second <c>Func&lt;Task&gt;</c> overload is not safe here:
    /// <c>null</c> converts to both it and <c>Action?</c>, so a delegating call would bind to
    /// itself and recurse until the stack overflows. Async handlers pass
    /// <c>() =&gt; _ = SomethingAsync()</c> instead.
    /// </summary>
    private Button EditButton(string glyph, string tooltip, Action? onClick = null) =>
        EditButtonDrawn(new FontIcon { Glyph = glyph, FontSize = _host.IsRegularWindow ? 16 : 12 }, tooltip, onClick);

    /// <summary>Same button, but with arbitrary drawn content instead of a font glyph.</summary>
    private Button EditButtonDrawn(UIElement content, string tooltip, Action? onClick = null) =>
        _host.IsRegularWindow
            ? LabeledEditButton(content, tooltip, onClick)
            : EditButtonCore(content, tooltip, onClick);

    /// <summary>Uses normal button chrome with an icon and a wrapping localized label.</summary>
    private static Button LabeledEditButton(UIElement icon, string label, Action? onClick)
    {
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(icon);
        var text = new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        return WireEditAction(new Button { Content = content }, label, onClick);
    }

    private static Button EditButtonCore(UIElement content, string tooltip, Action? onClick)
    {
        var button = new Button
        {
            Content = content,
            Width = EditButtonSize,
            Height = EditButtonSize,
            Padding = new Thickness(0),
            MinWidth = 0,
            // Centres the button inside its EditButtonSlot grid cell.
            Margin = new Thickness((EditButtonSlot - EditButtonSize) / 2.0),
        };
        return WireEditAction(button, tooltip, onClick);
    }

    /// <summary>Keeps accessible names, tooltips and command dispatch identical in both presentations.</summary>
    private static Button WireEditAction(Button button, string tooltip, Action? onClick)
    {
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        if (onClick != null)
            button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Draws the "add column" icon: two solid bars plus an outlined one.
    /// </summary>
    /// <remarks>
    /// Drawn rather than a Segoe Fluent glyph. There is no obviously column-shaped glyph in
    /// the set, and the one first chosen read as something else entirely , three bars where
    /// the new one is hollow states the intent without depending on font contents.
    /// </remarks>
    private static UIElement BuildAddColumnIcon()
    {
        var fg = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        for (int i = 0; i < 2; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 3,
                Height = 13,
                Background = fg,
                Opacity = 0.55,
                CornerRadius = new CornerRadius(1),
            });
        }

        panel.Children.Add(new Border
        {
            Width = 5,
            Height = 13,
            BorderBrush = fg,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1),
        });

        return panel;
    }

    // ── Mode transitions ────────────────────────────────────────────

    private void ApplyEditVisuals()
    {
        if (_isEditMode)
        {
            foreach (var columnListView in ColumnListViews())
            {
                if (columnListView.ItemsSource is not IEnumerable<LauncherItem> items) continue;

                foreach (var item in items)
                {
                    if (!IsEditableGroup(item)) continue;
                    if (columnListView.ContainerFromItem(item) is not Control container) continue;
                    ApplyGroupEditVisual(container);
                }
            }
        }
        else
        {
            // Containers no longer realised (rebuilt columns) can simply be dropped , they
            // were discarded along with their styling.
            foreach (var container in _editStyledContainers)
                ClearGroupEditVisual(container);
            _editStyledContainers.Clear();
        }

        // These three must run on *both* paths , each handles its own edit-mode check, and
        // an early return here is what left column headers visible in normal mode.
        ApplyEmptyGroupDropTargets();
        ApplyEmptyColumnPlaceholders();
        ApplyColumnChrome();
        // Wording differs between modes ("+ above" only makes sense while editing).
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Gives an empty column a droppable area while editing.
    /// </summary>
    /// <remarks>
    /// Size only, no tint: the column header already shows that the column exists, so a
    /// background as well was redundant. Without the <c>MinHeight</c> an empty column is
    /// zero-height and there is nothing to drag onto.
    /// </remarks>
    private void ApplyEmptyColumnPlaceholders()
    {
        // When the whole launcher is empty the placeholder text is the empty state, and there
        // is nothing to drag anyway. Reserving column height on top of it pushed the
        // placeholder out of the window, since only one of the two is in the arithmetic.
        bool launcherEmpty = LauncherIsEmpty;

        foreach (var columnListView in ColumnListViews())
        {
            bool isEmpty = columnListView.Items.Count == 0;

            if (isEmpty && _isEditMode && !launcherEmpty)
            {
                columnListView.MinHeight = IsIconMode ? GetActiveIconCellHeight() : EmptyGroupListModeHeight;
                columnListView.AllowDrop = true;
            }
            else
            {
                columnListView.ClearValue(FrameworkElement.MinHeightProperty);
            }
        }
    }

    /// <summary>Containers currently carrying edit styling, so it can be cleared again.</summary>
    private readonly HashSet<Control> _editStyledContainers = [];

    /// <summary>
    /// Every realised group child list, across all view modes, so empty groups can be given
    /// a drop target in edit mode.
    /// </summary>
    private readonly HashSet<ListView> _loadedGroupChildLists = [];

    /// <summary>
    /// Registers a group's child list. Called from the per-mode <c>Loaded</c> handlers.
    /// </summary>
    private void TrackGroupChildList(ListView listView)
    {
        _loadedGroupChildLists.Add(listView);
        listView.Unloaded -= GroupChildList_UnloadedForEdit;
        listView.Unloaded += GroupChildList_UnloadedForEdit;
        ApplyEmptyGroupDropTarget(listView);
        WireHoverAffordance(listView);
    }

    private void GroupChildList_UnloadedForEdit(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            _loadedGroupChildLists.Remove(listView);
    }

    private void ApplyEmptyGroupDropTargets()
    {
        foreach (var listView in _loadedGroupChildLists.ToList())
            ApplyEmptyGroupDropTarget(listView);
    }

    /// <summary>
    /// Keeps an empty group the same size as a regular item, and tints it as a drop target
    /// while editing.
    /// </summary>
    /// <remarks>
    /// An empty group otherwise renders as a bare heading over a zero-height child list ,
    /// nothing to drag onto, and inconsistent with how every other entry is sized. The
    /// <c>MinHeight</c> is applied in <b>both</b> modes so the rendering matches what
    /// <c>MeasureIconModeHeight</c>/<c>MeasureContentHeight</c> already reserve for a group;
    /// applying it only while editing left dead space outside edit mode. Only the tint is
    /// edit-mode-specific.
    /// </remarks>
    private void ApplyEmptyGroupDropTarget(ListView listView)
    {
        bool isEmpty = listView.Tag is LauncherItem group
            ? group.Children.Count == 0
            : listView.Items.Count == 0;

        if (isEmpty)
            listView.MinHeight = IsIconMode ? GetActiveIconCellHeight() : EmptyGroupListModeHeight;
        else
            listView.ClearValue(FrameworkElement.MinHeightProperty);

        if (isEmpty && _isEditMode)
        {
            listView.Background = new SolidColorBrush(EditTintColor(0x2E));
            listView.CornerRadius = new CornerRadius(6);
            listView.AllowDrop = true;
        }
        else
        {
            listView.ClearValue(Control.BackgroundProperty);
            listView.ClearValue(Control.CornerRadiusProperty);
        }
    }

    private const double EmptyGroupListModeHeight = 32;

    /// <summary>
    /// Deliberately background-only: <c>Background</c> and <c>CornerRadius</c> have no effect
    /// on layout, so group containers cannot change size and <c>PackedIconPanel</c> cannot
    /// repack. A border was tried and rejected , compensating for it with negative padding
    /// works only when the container already has padding on every side to give back, which
    /// the icon-mode container style does not.
    /// </summary>
    private void ApplyGroupEditVisual(Control container)
    {
        if (!_editStyledContainers.Add(container)) return;

        container.Background = new SolidColorBrush(EditTintColor(0x59));
        container.CornerRadius = new CornerRadius(6);
    }

    /// <summary>Accent-derived tint, so the highlight is clearly visible in both themes.</summary>
    private static global::Windows.UI.Color EditTintColor(byte alpha)
    {
        if (Application.Current.Resources["AccentFillColorDefaultBrush"] is SolidColorBrush accent)
            return global::Windows.UI.Color.FromArgb(alpha, accent.Color.R, accent.Color.G, accent.Color.B);
        return global::Windows.UI.Color.FromArgb(alpha, 0x60, 0xA0, 0xE0);
    }

    private static void ClearGroupEditVisual(Control container)
    {
        container.ClearValue(Control.BackgroundProperty);
        container.ClearValue(Control.CornerRadiusProperty);
    }

    // ── Structural operations ───────────────────────────────────────

    /// <summary>
    /// Persists a structural edit and rebuilds.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately does <b>not</b> call <c>SyncColumnsToFlatList()</c>. That method
    /// clears <c>_launcher.Items</c> and regenerates it from <c>_columnLists</c>, so calling
    /// it here would resurrect anything just removed from the flat list , which is exactly
    /// what broke remove. Column lists are a derived view; <c>_launcher.Items</c> is the
    /// source of truth, and the rebuild triggered below regenerates the columns from it.</para>
    /// <para>Drag-and-drop is the opposite case: it mutates <c>_columnLists</c> directly, so
    /// it must flush the other way via <c>PersistFlyoutReorder</c>.</para>
    /// </remarks>
    private void PersistStructuralChange()
    {
        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    /// <summary>
    /// Target collection for newly added top-level content. Appending to the flat list lands
    /// after the final column break, i.e. in the last column.
    /// </summary>
    private ObservableCollection<LauncherItem> GetAddTarget() => _launcher.Items;

    private async Task AddItemAsync()
    {
        int version = _sessionVersion;
        var target = GetAddTarget();
        var result = await RunModalAsync(track => ItemEditorWindow.ShowAsync(null, target, _hwnd, track));
        if (!IsCurrentSession(version)) return;
        if (result == ItemEditorResult.Saved)
            PersistStructuralChange();
    }

    internal async Task EditItemAsync(LauncherItem item)
    {
        int version = _sessionVersion;
        var parent = FindParentCollection(item) ?? GetAddTarget();
        var result = await RunModalAsync(track => ItemEditorWindow.ShowAsync(item, parent, _hwnd, track));

        if (!IsCurrentSession(version)) return;
        switch (result)
        {
            case ItemEditorResult.Saved:
                PersistStructuralChange();
                break;
            // RemoveItem persists on its own.
            case ItemEditorResult.Deleted:
                RemoveItem(item);
                break;
        }
    }

    private async Task AddGroupAsync()
    {
        string? name = await PromptForGroupNameAsync(null);
        if (name == null) return;

        var group = LauncherItem.CreateGroup(name);
        GetAddTarget().Add(group);
        PersistStructuralChange();
    }

    internal async Task RenameGroupAsync(LauncherItem group)
    {
        string? name = await PromptForGroupNameAsync(group.Name);
        if (name == null) return;

        group.Name = name;
        PersistStructuralChange();
    }

    /// <summary>
    /// Small name prompt for groups. Uses a standalone window, not a <c>ContentDialog</c> ,
    /// a dialog cannot overflow the flyout's HWND, and the flyout is frequently shorter than
    /// even a one-field dialog, which clips the input and buttons.
    /// </summary>
    private async Task<string?> PromptForGroupNameAsync(string? existingName)
    {
        int version = _sessionVersion;
        bool isEdit = existingName != null;

        string? name = await RunModalAsync(track => TextPromptWindow.ShowAsync(
            title: isEdit ? "Rename Group" : "Add Group",
            placeholder: "Group name",
            initialText: existingName,
            acceptText: isEdit ? "Save" : "Add",
            ownerHwnd: _hwnd,
            onCreated: track));

        return !IsCurrentSession(version) || string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private void AddColumn()
    {
        _launcher.Items.Add(LauncherItem.CreateColumnBreak());
        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    internal void RemoveItem(LauncherItem item)
    {
        // Synthetic groups are ephemeral wrappers, not real content , never removable.
        if (item.IsGroup && !IsEditableGroup(item)) return;

        var parent = FindParentCollection(item);
        if (parent == null || !parent.Remove(item)) return;
        PersistStructuralChange();
    }

    // ── Column chrome ───────────────────────────────────────────────

    /// <summary>Height of a column header row (edit mode only).</summary>
    private const int ColumnHeaderHeight = 24;

    /// <summary>
    /// Height contributed by per-column headers. Added once, not per column: every column
    /// carries a header, and the flyout's height is driven by the tallest column.
    /// </summary>
    private double CurrentColumnHeaderHeight =>
        _isEditMode && _columnLists.Count > 1 ? ColumnHeaderHeight : 0;

    /// <summary>
    /// Wraps a column's list view with a header so multi-column layouts are legible and each
    /// column can be removed individually.
    /// </summary>
    /// <remarks>
    /// Only used when there is more than one column , a lone column needs no distinguishing
    /// chrome, and skipping the wrapper keeps the single-column case byte-identical to before.
    /// The header is edit-mode-only (it adds height, which is allowed); the column tint is
    /// <c>Background</c>-only, so it never affects width or item sizing.
    /// </remarks>
    private FrameworkElement BuildColumnContainer(ListViewBase columnListView, int columnIndex)
    {
        var header = new Grid
        {
            Height = ColumnHeaderHeight,
            Tag = "ColumnHeader",
            Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed,
        };

        // Remove button first, then the title.
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        ToolTipService.SetToolTip(remove, "Remove this column");
        int captured = columnIndex;
        remove.Click += (_, _) => RemoveColumnAt(captured);

        var label = new TextBlock
        {
            Text = $"Column {columnIndex + 1}",
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 4, 0),
        };

        ConditionalNameToolTip.SetText(label, label.Text);

        Grid.SetColumn(remove, 0);
        Grid.SetColumn(label, 1);
        header.Children.Add(remove);
        header.Children.Add(label);

        // A Grid, not a StackPanel: the list view must *fill* the remaining column height so
        // empty space below the items is still a drop target. A StackPanel gives children only
        // their desired height, which is why dropping required landing precisely on the last
        // item, and why an empty column could only be dropped onto at its very top.
        //
        // Constrain the header to the item column, so narrow captions trim instead of
        // widening the wrapper beyond GetFlyoutWidth. Bind to the live Width: a snapshot
        // would freeze wrapped columns when icons-per-row or initial label sizing changes.
        var container = new Grid();
        container.SetBinding(WidthProperty, new Binding
        {
            Source = columnListView,
            Path = new PropertyPath(nameof(FrameworkElement.Width)),
            Mode = BindingMode.OneWay,
        });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(header, 0);
        Grid.SetRow(columnListView, 1);
        columnListView.VerticalAlignment = VerticalAlignment.Stretch;

        container.Children.Add(header);
        container.Children.Add(columnListView);
        return container;
    }

    /// <summary>Shows or hides the per-column headers for the current mode.</summary>
    private void ApplyColumnChrome()
    {
        foreach (var child in ColumnsPanel.Children.OfType<Panel>())
        {
            foreach (var header in child.Children.OfType<Grid>())
            {
                if (header.Tag as string == "ColumnHeader")
                    header.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Removes a specific column, merging its items into the neighbouring one.
    /// </summary>
    /// <remarks>
    /// Column <c>i</c> is preceded by break <c>i-1</c>; removing that break concatenates the
    /// column into its predecessor. For the first column the following break is removed
    /// instead, which merges it with the second , the same resulting flat list either way.
    /// </remarks>
    private void RemoveColumnAt(int columnIndex)
    {
        var breaks = _launcher.Items.Where(i => i.IsColumnBreak).ToList();
        if (breaks.Count == 0) return;

        int breakIndex = Math.Clamp(columnIndex == 0 ? 0 : columnIndex - 1, 0, breaks.Count - 1);
        _launcher.Items.Remove(breaks[breakIndex]);

        PersistFlyoutItemChanges();
        ApplyEditVisuals();
        ResizeForEditChrome();
    }

    private void ItemsListControl_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LauncherItem item) return;

        // In edit mode a click edits rather than launches.
        if (_isEditMode)
        {
            if (item.IsGroup)
            {
                if (!_syntheticGroups.Contains(item))
                    _ = RenameGroupAsync(item);
                return;
            }
            _ = EditItemAsync(item);
            return;
        }

        if (item.IsGroup) return;
        LaunchItem(item);
    }

    private void LaunchItem(LauncherItem item)
    {
        _host.LaunchItem(item);
    }

    /// <summary>Opens item actions at the pointer using the shared list/grid hit test.</summary>
    private void ItemsListControl_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is ListViewBase listView
            && e.TryGetPosition(listView, out var point)
            && TryGetContextMenuItem(listView, point, out var item))
        {
            ShowItemContextMenu(listView, item, point);
            e.Handled = true;
        }
    }

    private bool TryGetContextMenuItem(ListViewBase listView, global::Windows.Foundation.Point point, out LauncherItem item)
    {
        for (int i = 0; i < listView.Items.Count; i++)
        {
            if (listView.ContainerFromIndex(i) is not Control container) continue;
            // Column breaks are invisible sentinels and synthetic groups are ephemeral, so
            // neither is a valid context-menu target. Real groups are, in edit mode.
            if (listView.Items[i] is not LauncherItem candidate || candidate.IsColumnBreak) continue;
            // Groups are only a context-menu target while editing; synthetic ones never are.
            if (candidate.IsGroup && !(_isEditMode && IsEditableGroup(candidate))) continue;

            var origin = container.TransformToVisual(listView).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var bounds = new global::Windows.Foundation.Rect(origin.X, origin.Y, container.ActualWidth, container.ActualHeight);
            if (bounds.Contains(point))
            {
                item = candidate;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void ShowItemContextMenu(FrameworkElement anchor, LauncherItem item, global::Windows.Foundation.Point? position = null)
    {
        if (item.IsColumnBreak)
            return;
        if (item.IsGroup && !IsEditableGroup(item))
            return;

        var flyout = new MenuFlyout();

        // Outside edit mode the menu carries only the launcher-level entries , reorder,
        // move, edit and remove are editing affordances and would be misleading (and, for a
        // read-only shared launcher, simply unavailable).
        if (!_isEditMode || IsReadOnlyLauncher)
        {
            AppendLegacyContextMenuItems(flyout);
            if (position is global::Windows.Foundation.Point p)
                flyout.ShowAt(anchor, p);
            else
                flyout.ShowAt(anchor);
            return;
        }

        string moveBackwardText = IsIconMode ? "Move left" : "Move up";
        string moveForwardText = IsIconMode ? "Move right" : "Move down";
        string moveBackwardGlyph = IsIconMode ? "\uE76B" : "\uE70E";
        string moveForwardGlyph = IsIconMode ? "\uE76C" : "\uE70D";

        var moveUp = new MenuFlyoutItem { Text = moveBackwardText, Icon = new FontIcon { Glyph = moveBackwardGlyph } };
        moveUp.Click += (_, _) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;

            int index = parent.IndexOf(item);
            if (index <= 0) return;

            parent.Move(index, index - 1);
            PersistFlyoutItemChanges();
        };
        flyout.Items.Add(moveUp);

        var moveDown = new MenuFlyoutItem { Text = moveForwardText, Icon = new FontIcon { Glyph = moveForwardGlyph } };
        moveDown.Click += (_, _) =>
        {
            var parent = FindParentCollection(item);
            if (parent == null) return;

            int index = parent.IndexOf(item);
            if (index < 0 || index >= parent.Count - 1) return;

            parent.Move(index, index + 1);
            PersistFlyoutItemChanges();
        };
        flyout.Items.Add(moveDown);

        ObservableCollection<LauncherItem>? currentParent = null;
        LauncherItem? currentGroup = null;
        foreach (var group in _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group.Children.Contains(item))
            {
                currentParent = group.Children;
                currentGroup = group;
                break;
            }
        }

        var moveToSub = new MenuFlyoutSubItem { Text = "Move to\u2026", Icon = new FontIcon { Glyph = "\uE8DE" } };

        if (currentParent != null)
        {
            var topLevel = new MenuFlyoutItem { Text = "Top Level", Icon = new FontIcon { Glyph = "\uE74B" } };
            topLevel.Click += (_, _) =>
            {
                currentParent.Remove(item);
                _launcher.Items.Add(item);
                PersistFlyoutItemChanges();
            };
            moveToSub.Items.Add(topLevel);
        }

        // Groups cannot nest, so a group is never offered another group as a destination.
        foreach (var group in item.IsGroup ? [] : _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group == currentGroup) continue;

            var targetGroup = group;
            var groupOption = new MenuFlyoutItem { Text = group.Name, Icon = new FontIcon { Glyph = "\uF168" } };
            groupOption.Click += (_, _) =>
            {
                (currentParent ?? _launcher.Items).Remove(item);
                targetGroup.Children.Add(item);
                PersistFlyoutItemChanges();
            };
            moveToSub.Items.Add(groupOption);
        }

        var otherLaunchers = SettingsManager.Current.Launchers.Where(CanMoveToLauncher).ToList();
        if (otherLaunchers.Count > 0)
        {
            if (moveToSub.Items.Count > 0)
                moveToSub.Items.Add(new MenuFlyoutSeparator());

            foreach (var launcher in otherLaunchers)
            {
                var targetLauncher = launcher;
                var launcherOption = new MenuFlyoutItem
                {
                    Text = $"{targetLauncher.Name} (launcher)",
                    Icon = new FontIcon { Glyph = "\uF0E2" }
                };
                launcherOption.Click += (_, _) =>
                {
                    // Recheck live policy before touching the source: the destination may
                    // have been deleted, changed kind or become read-only while the menu was open.
                    if (_disposed || !_isEditMode || IsReadOnlyLauncher || !CanMoveToLauncher(targetLauncher))
                        return;

                    var source = FindParentCollection(item);
                    if (source == null || !source.Remove(item))
                        return;

                    targetLauncher.Items.Add(item);
                    PersistFlyoutItemChanges(targetLauncher);
                };
                moveToSub.Items.Add(launcherOption);
            }
        }

        if (moveToSub.Items.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(moveToSub);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var edit = new MenuFlyoutItem
        {
            Text = item.IsGroup ? "Rename\u2026" : "Edit",
            Icon = new FontIcon { Glyph = "\uE70F" }
        };
        edit.Click += (_, _) =>
        {
            if (item.IsGroup)
                _ = RenameGroupAsync(item);
            else
                _ = EditItemAsync(item);
        };
        flyout.Items.Add(edit);

        var remove = new MenuFlyoutItem { Text = "Remove", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (_, _) => RemoveItem(item);
        flyout.Items.Add(remove);

        AppendLegacyContextMenuItems(flyout);

        if (position is global::Windows.Foundation.Point point)
            flyout.ShowAt(anchor, point);
        else
            flyout.ShowAt(anchor);
    }

    /// <summary>
    /// Allows cross-launcher moves only into a live, writable item launcher. Web launchers
    /// cannot display items, and subscriber launchers must keep their read-only policy.
    /// Reused when constructing the menu and immediately before removing the source item.
    /// </summary>
    private bool CanMoveToLauncher(Launcher target) =>
        !ReferenceEquals(target, _launcher)
        && !target.IsWebLauncher
        && target is not { IsShared: true, IsSharedOwner: false }
        && SettingsManager.Current.Launchers.Contains(target);

    private ObservableCollection<LauncherItem>? FindParentCollection(LauncherItem item)
    {
        if (_launcher.Items.Contains(item))
            return _launcher.Items;

        foreach (var group in _launcher.Items.Where(candidate => candidate.IsGroup))
        {
            if (group.Children.Contains(item))
                return group.Children;
        }

        // A synthetic group is a display wrapper and is deliberately not in the launcher. Anything
        // else means this row is bound to an item the launcher no longer contains, which is what
        // silently disables remove, move and edit for it: each of them looks the item up here and
        // gives up when it is not found, so the user sees a menu entry that does nothing. See
        // LauncherPayload.ItemsMatch for the sync merge that used to leave rows in that state.
        if (!_syntheticGroups.Contains(item))
            Logger.Warn($"Flyout item '{item.Name}' is not in launcher '{_launcher.Name}' " +
                        "(row bound to a stale object); remove and move will do nothing");

        return null;
    }

    private void PersistFlyoutItemChanges(params Launcher[] additionalLaunchers)
        => PersistItemChanges(new[] { _launcher }.Concat(additionalLaunchers));

    /// <summary>Publishes immediate changes once for the launchers that actually contain them.</summary>
    private static void PersistItemChanges(IEnumerable<Launcher> launchers)
    {
        SettingsManager.SaveSettings();
        AutoSyncService.NotifyItemsChanged();

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var launcher in launchers)
        {
            if (string.IsNullOrEmpty(launcher.Id) || !seenIds.Add(launcher.Id))
                continue;

            FlyoutWindow.InvalidateItems(launcher.Id);

        }
    }

    private void AppendLegacyContextMenuItems(MenuFlyout flyout)
    {
        // Only separate from preceding entries if there are any , outside edit mode these
        // launcher-level entries are the whole menu.
        if (flyout.Items.Count > 0)
            flyout.Items.Add(new MenuFlyoutSeparator());

        // Enters edit mode rather than opening launcher settings directly \u2014 settings are
        // reachable from the edit toolbar, so this keeps one obvious way in.
        var editMode = new MenuFlyoutItem
        {
            Text = "Edit items",
            Icon = new FontIcon { Glyph = "" }
        };
        editMode.Click += ContextEditMode_Click;
        flyout.Items.Add(editMode);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var settingsItem = new MenuFlyoutItem
        {
            Text = "App Settings",
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        settingsItem.Click += ContextSettingsItem_Click;
        flyout.Items.Add(settingsItem);
    }

    private void ContextEditMode_Click(object sender, RoutedEventArgs e)
    {
        EnterEditMode();
    }

    private async Task OpenLauncherSettingsAsync()
    {
        int version = _sessionVersion;
        await RunModalAsync(track => LauncherSettingsWindow.ShowAsync(_launcher, _hwnd, track));

        if (!IsCurrentSession(version)) return;
        // View mode, density and title all affect layout, so rebuild rather than assume.
        FlyoutWindow.InvalidateItems(_launcher.Id);

        // Resize only after the rebuilt containers have actually been laid out , measuring
        // immediately would read stale desired sizes.
        if (_isEditMode)
            DispatcherQueue.TryEnqueue(ResizeForEditChrome);
    }


    private void ContextSettingsItem_Click(object sender, RoutedEventArgs e)
    {
        _host.OpenAppSettings();
    }


    private double? _toolbarAvailableWidth;

    /// <summary>Wraps toolbar commands independently of the item's natural dimensions.</summary>
    internal void UpdateToolbarWidth(double width)
    {
        _toolbarAvailableWidth = Math.Max(1, width);
        UpdateEditToolbarColumns();
    }

    /// <summary>Enters the same edit behavior in either host, preserving all item dimensions.</summary>
    internal void EnterEditMode() => SetEditing(true);

    internal void ExitEditMode() => _host.FinishEditing();

    /// <summary>Ends a session before owned dialogs or deferred results can use its old UI.</summary>
    internal void SetEditing(bool editing, bool notifyHost = true)
    {
        if (_disposed || _isEditMode == editing || (editing && IsReadOnlyLauncher)) return;
        _isEditMode = editing;
        _sessionVersion++;
        if (_editPencil != null)
        {
            _editPencil.Opacity = 0;
            _editPencil.IsHitTestVisible = false;
        }
        CompleteIconResize(_resizeFromLeft ? _leftResizeGrip : _rightResizeGrip);
        ClearInsertionIndicator();
        ClearDragState();
        HideHoverAffordance();
        UpdateEditToolbarColumns();
        UpdateResizeGripVisibility();
        ApplyEditVisuals();
        if (notifyHost) _host.EditModeChanged();
    }
}
