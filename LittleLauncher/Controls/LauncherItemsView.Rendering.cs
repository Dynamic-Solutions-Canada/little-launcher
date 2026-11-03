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
    /// <summary>
    /// The widest a list-mode column may be, and the fixed width every one of them used to have.
    /// </summary>
    /// <remarks>
    /// Columns are now sized to the longest label they actually hold
    /// (<see cref="ComputeListColumnWidth"/>), because a column of one-word shortcuts spent most
    /// of its width on nothing. This is the ceiling on that, so nothing is ever wider than the
    /// flyout has always been and long names go on trimming exactly as they did.
    /// </remarks>
    private const int MaxListColumnWidth = 175;

    /// <summary>
    /// The narrowest one may be: the width of the column header edit mode draws over it, which is
    /// the widest fixed thing a list column ever has to show: a 20px remove button and its
    /// margin, then "Column N" and its own.
    /// </summary>
    /// <remarks>
    /// Deliberately low. It is a guard against a launcher of one-letter names collapsing into a
    /// sliver, not a second opinion about how wide the labels need to be. An empty launcher never
    /// reaches it: with nothing to measure, <see cref="ComputeListColumnWidth"/> keeps the full
    /// width, which is what the empty-state placeholder is sized against.
    /// </remarks>
    private const int MinListColumnWidth = 80;

    /// <summary>
    /// Everything a list row spends on something other than its label: the container's 8px
    /// padding either side, the 24px icon cell, and the 8px gap after it.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>LauncherItemContainerStyle</c> and <c>LauncherItemTemplate</c> in the XAML,
    /// which carry a comment pointing back here. Measured content is never fed back into this;
    /// see the "compute, don't measure" rule in the drag-drop guide.
    /// </remarks>
    private const int ListItemChromeWidth = 8 + 24 + 8 + 8;

    /// <summary>The same for a group header, which has no icon: 8px of margin either side.</summary>
    private const int ListGroupHeaderChromeWidth = 8 + 8;

    /// <summary>The same for the launcher title, which spans every column.</summary>
    private const int LauncherTitleChromeWidth = 8 + 8;

    private const double ListItemFontSize = 13;
    private const double ListGroupHeaderFontSize = 12;
    private const int DefaultIconColumnWidth = 260;
    private const int IconCellWidth = 80;
    private const int IconCellHeight = 84;
    private const int IconSize = 32;
    private const int IconColumnChromeWidth = DefaultIconColumnWidth - (IconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int DefaultSmallIconColumnWidth = 136;
    private const int SmallIconCellWidth = 40;
    private const int SmallIconCellHeight = 40;
    private const int SmallIconSize = 20;
    private const int SmallIconColumnChromeWidth = DefaultSmallIconColumnWidth - (SmallIconCellWidth * Launcher.DefaultIconModeIconsPerRow);
    private const int IconGroupHeaderHeight = 30;
    private const int SmallIconGroupHeaderHeight = 30;
    private const int FlyoutOuterPadding = 8;
    private const int LauncherTitleHeight = 32;
    private const double DefaultMinFlyoutHeight = 80;
    private const double SmallIconMinFlyoutHeight = 52;
    private const int ResizeGripWidth = 4;
    private List<ObservableCollection<LauncherItem>> BuildColumnLists()
    {
        var columns = new List<ObservableCollection<LauncherItem>>();
        var current = new ObservableCollection<LauncherItem>();
        columns.Add(current);

        foreach (var item in _launcher.Items)
        {
            if (item.IsColumnBreak)
            {
                current = new ObservableCollection<LauncherItem>();
                columns.Add(current);
                continue;
            }

            current.Add(item);
        }

        return columns;
    }

    private void WrapUngroupedItemsIntoSyntheticGroups()
    {
        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var column = _columnLists[columnIndex];
            var newColumn = new ObservableCollection<LauncherItem>();
            LauncherItem? currentSynthetic = null;

            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    currentSynthetic = null;
                    newColumn.Add(item);
                    continue;
                }

                if (currentSynthetic == null)
                {
                    currentSynthetic = LauncherItem.CreateGroup(string.Empty);
                    currentSynthetic.IsExpanded = true;
                    _syntheticGroups.Add(currentSynthetic);
                    newColumn.Add(currentSynthetic);
                }

                currentSynthetic.Children.Add(item);
            }

            _columnLists[columnIndex] = newColumn;
        }
    }

    private void SyncColumnsToFlatList()
    {
        // Reordering can persist columns only during an active editing session.
        if (!_isEditMode)
            throw new InvalidOperationException("Launcher items can only be reordered while editing.");
        _launcher.Items.Clear();

        foreach (var item in LauncherItemReorder.Flatten(_columnLists, _syntheticGroups))
            _launcher.Items.Add(item);
    }

    private ListViewBase CreateColumnListView(int columnIndex)
    {
        bool isIconMode = IsIconMode;
        string templateSelectorKey = isIconMode
            ? (IsSmallIconMode ? "SmallIconItemTemplateSelector" : "IconItemTemplateSelector")
            : "ListItemTemplateSelector";

        ListViewBase lv = isIconMode ? new GridView() : new ListView();
        lv.Width = isIconMode ? GetIconColumnWidth(_columnLists[columnIndex]) : _listColumnWidth;
        lv.Padding = new Thickness(0);
        lv.IsItemClickEnabled = true;
        lv.SelectionMode = ListViewSelectionMode.None;
        lv.IsTabStop = false;
        lv.CanDragItems = true;
        lv.AllowDrop = true;
        lv.Tag = columnIndex;
        lv.TabNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Once;
        lv.ItemTemplateSelector = (DataTemplateSelector)RootGrid.Resources[templateSelectorKey];
        lv.ItemContainerTransitions = new TransitionCollection();
        lv.Transitions = new TransitionCollection();

        if (isIconMode)
        {
            lv.ItemContainerStyle = (Style)RootGrid.Resources["IconGroupGridItemContainerStyle"];
            lv.ItemsPanel = (ItemsPanelTemplate)RootGrid.Resources["IconColumnItemsPanel"];
            lv.ContainerContentChanging += IconColumn_ContainerContentChanging;
        }
        else
            lv.ItemContainerStyleSelector = (StyleSelector)RootGrid.Resources["ItemContainerStyleSelector"];

        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        lv.Loaded += ColumnListView_Loaded;
        lv.ItemClick += ItemsListControl_ItemClick;
        lv.ContextRequested += ItemsListControl_ContextRequested;
        lv.DragItemsStarting += ColumnListView_DragItemsStarting;
        lv.DragOver += ColumnListView_DragOver;
        lv.DragLeave += ColumnListView_DragLeave;
        lv.Drop += ColumnListView_Drop;
        lv.DragItemsCompleted += ColumnListView_DragItemsCompleted;
        return lv;
    }

    private static void DisableListViewTransitions(ListViewBase listView)
    {
        listView.ItemContainerTransitions = new TransitionCollection();
        listView.Transitions = new TransitionCollection();

        if (listView.ItemsPanelRoot is Panel panel)
            panel.ChildrenTransitions = new TransitionCollection();
    }

    private void ColumnListView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewBase listView)
        {
            DisableListViewTransitions(listView);
            WireHoverAffordance(listView);
            // Containers are realised after a rebuild, so edit visuals are applied here
            // rather than at the point the mode was entered.
            if (_isEditMode)
                ApplyEditVisuals();
            if (IsIconMode)
                ApplyTopLevelIconSpans(listView);
        }
    }

    private void IconColumn_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not LauncherItem item || args.ItemContainer is not Control container)
            return;

        if (sender.ItemsPanelRoot is PackedIconPanel wrapGrid)
        {
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
            wrapGrid.ItemWidth = GetActiveIconCellWidth();
            wrapGrid.ItemHeight = GetActiveIconCellHeight();
            wrapGrid.InvalidateMeasure();
        }

        VariableSizedWrapGrid.SetColumnSpan(container, GetTopLevelIconSpan(item));
        VariableSizedWrapGrid.SetRowSpan(container, GetTopLevelIconRowSpan(item));
    }

    private void InitializeResizeGrips()
    {
        _leftResizeGrip = CreateResizeGrip(HorizontalAlignment.Left);
        _rightResizeGrip = CreateResizeGrip(HorizontalAlignment.Right);
        RootGrid.Children.Add(_leftResizeGrip);
        RootGrid.Children.Add(_rightResizeGrip);
    }

    private ResizeGrip CreateResizeGrip(HorizontalAlignment alignment)
    {
        var grip = new ResizeGrip
        {
            Width = 10,
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };

        grip.SetValue(Canvas.ZIndexProperty, 10);
        grip.PointerPressed += ResizeGrip_PointerPressed;
        grip.PointerMoved += ResizeGrip_PointerMoved;
        grip.PointerReleased += ResizeGrip_PointerReleased;
        grip.PointerCaptureLost += ResizeGrip_PointerCaptureLost;
        return grip;
    }

    // ── Icon mode rendering ─────────────────────────────────────────

    private int GetIconModeIconsPerRow() => Launcher.ClampIconModeIconsPerRow(_launcher.IconModeIconsPerRow);

    private int GetActiveIconCellWidth() => IsSmallIconMode ? SmallIconCellWidth : IconCellWidth;

    private int GetActiveIconCellHeight() => IsSmallIconMode ? SmallIconCellHeight : IconCellHeight;

    private int GetActiveIconSize() => IsSmallIconMode ? SmallIconSize : IconSize;

    private int GetActiveIconColumnChromeWidth() => IsSmallIconMode ? SmallIconColumnChromeWidth : IconColumnChromeWidth;

    private int GetActiveGroupHeaderHeight() => IsSmallIconMode ? SmallIconGroupHeaderHeight : IconGroupHeaderHeight;

    private double GetMinimumFlyoutHeight() => IsSmallIconMode ? SmallIconMinFlyoutHeight : DefaultMinFlyoutHeight;

    private int GetIconColumnWidth() => GetActiveIconColumnChromeWidth() + (GetIconModeIconsPerRow() * GetActiveIconCellWidth());

    private int GetIconColumnWidth(ObservableCollection<LauncherItem> items)
    {
        return GetIconColumnWidth();
    }

    private int GetIconGroupContentWidth(LauncherItem group)
    {
        int maxIconsPerRow = GetIconModeIconsPerRow();
        // Must match GetTopLevelIconSpan, or the rendered width and the packing span disagree.
        int visibleIcons = Math.Clamp(group.Children.Count, 1, maxIconsPerRow);
        return visibleIcons * GetActiveIconCellWidth();
    }

    private ItemsPanelTemplate CreateIconGroupItemsPanel()
    {
        string xaml = "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                      $"<ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='{GetIconModeIconsPerRow()}'/>" +
                      "</ItemsPanelTemplate>";
        return (ItemsPanelTemplate)XamlReader.Load(xaml);
    }

    private int GetTopLevelIconSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        // A group spans as many columns as it has children, so several narrow groups can
        // share a row. A group with more children than fit clamps to the full width, which
        // makes PackedIconPanel start it on a new row (see its row-flush check) and keeps
        // the group's outer shape rectangular instead of starting mid-row and wrapping.
        return Math.Clamp(item.Children.Count, 1, GetIconModeIconsPerRow());
    }

    private int GetTopLevelIconRowSpan(LauncherItem item)
    {
        if (!item.IsGroup)
            return 1;

        int childRows = Math.Max(1, (item.Children.Count + GetIconModeIconsPerRow() - 1) / GetIconModeIconsPerRow());
        return _syntheticGroups.Contains(item) ? childRows : childRows + 1;
    }

    private static bool IsGridItemsPanel(Panel? panel) =>
        panel is ItemsWrapGrid or PackedIconPanel;

    private void ApplyTopLevelIconSpans(ListViewBase listView)
    {
        if (listView.ItemsPanelRoot is not PackedIconPanel wrapGrid)
            return;

        wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();
        wrapGrid.ItemWidth = GetActiveIconCellWidth();
        wrapGrid.ItemHeight = GetActiveIconCellHeight();

        listView.UpdateLayout();

        for (int index = 0; index < listView.Items.Count; index++)
        {
            if (listView.Items[index] is not LauncherItem item || listView.ContainerFromIndex(index) is not Control container)
                continue;

            int columnSpan = GetTopLevelIconSpan(item);
            int rowSpan = GetTopLevelIconRowSpan(item);
            VariableSizedWrapGrid.SetColumnSpan(container, columnSpan);
            VariableSizedWrapGrid.SetRowSpan(container, rowSpan);

            if (index < wrapGrid.Children.Count && wrapGrid.Children[index] is UIElement panelChild)
            {
                VariableSizedWrapGrid.SetColumnSpan(panelChild, columnSpan);
                VariableSizedWrapGrid.SetRowSpan(panelChild, rowSpan);
            }
        }

        wrapGrid.InvalidateMeasure();
        wrapGrid.InvalidateArrange();
        listView.UpdateLayout();
    }

    /// <summary>
    /// Sizes a list-mode column to the longest label the launcher actually holds, capped at
    /// <see cref="MaxListColumnWidth"/> and floored at <see cref="MinListColumnWidth"/>.
    /// </summary>
    /// <remarks>
    /// <para>One width for every column, not one each. Columns of different widths read as a
    /// broken table rather than as a tidy one, and the flyout has no column rules to make the
    /// ragged edges look deliberate, so the widest label in the launcher sets the width of all
    /// of them.</para>
    /// <para>Group headers count too, and against their own chrome: they carry no icon, so a
    /// header needs 32px less than an item with the same text.</para>
    /// <para>The cap is what keeps this from being a behaviour change for anyone: a launcher with
    /// one long name is exactly as wide as it was, and only the launchers that were wasting the
    /// space get narrower.</para>
    /// </remarks>
    private int ComputeListColumnWidth()
    {
        double widest = 0;
        bool measuredAnything = false;

        void Consider(string? label, double fontSize, global::Windows.UI.Text.FontWeight weight, int chrome)
        {
            double width = MeasureLabelWidth(label, fontSize, weight);
            if (width <= 0) return;

            measuredAnything = true;
            widest = Math.Max(widest, width + chrome);
        }

        foreach (var column in _columnLists)
        {
            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    Consider(item.Name, ListGroupHeaderFontSize, Microsoft.UI.Text.FontWeights.SemiBold, ListGroupHeaderChromeWidth);
                    foreach (var child in item.Children)
                        Consider(child.Name, ListItemFontSize, Microsoft.UI.Text.FontWeights.Normal, ListItemChromeWidth);
                }
                else
                {
                    Consider(item.Name, ListItemFontSize, Microsoft.UI.Text.FontWeights.Normal, ListItemChromeWidth);
                }
            }
        }

        // Nothing measurable: an empty launcher, or a tree not yet live enough to lay text out.
        // Either way the honest answer is the width the flyout has always had: an empty launcher
        // still has its placeholder to show, and a premature call is retaken on Loaded.
        if (!measuredAnything) return MaxListColumnWidth;

        // The launcher's title spans every column, so it is a floor on their shared width rather
        // than on any one of them. Without it a launcher of short items would start ellipsizing a
        // name that fits perfectly well today.
        if (LauncherTitle.Visibility == Visibility.Visible)
        {
            double title = MeasureLabelWidth(LauncherTitle.Text, ListGroupHeaderFontSize, Microsoft.UI.Text.FontWeights.SemiBold);
            if (title > 0)
                widest = Math.Max(widest, (title + LauncherTitleChromeWidth) / Math.Max(1, _columnLists.Count));
        }

        return Math.Clamp((int)Math.Ceiling(widest), MinListColumnWidth, MaxListColumnWidth);
    }

    /// <summary>
    /// How wide a label draws, in DIPs, at the font a list row or group header gives it.
    /// </summary>
    /// <remarks>
    /// <para>Measuring a <i>string</i>, which is the one thing the arithmetic cannot know up
    /// front. It is not measuring laid-out content, which is what the "compute, don't measure"
    /// rule in the drag-drop guide forbids: nothing here reads a container's <c>DesiredSize</c>,
    /// so no result can feed back into the geometry that produced it.</para>
    /// <para><c>LabelMeasure</c> is a TextBlock parked in a zero-sized Canvas in the flyout's own
    /// tree, so it resolves the same font, theme and scale the real rows do. Returns 0 before the
    /// tree is live, which every caller reads as "no answer" rather than as "no width".</para>
    /// </remarks>
    private double MeasureLabelWidth(string? label, double fontSize, global::Windows.UI.Text.FontWeight weight)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;

        LabelMeasure.FontSize = fontSize;
        LabelMeasure.FontWeight = weight;
        LabelMeasure.Text = label;
        LabelMeasure.InvalidateMeasure();
        LabelMeasure.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        return LabelMeasure.DesiredSize.Width;
    }

    /// <summary>
    /// Retakes the column width once the flyout's tree is live, and rebuilds if the answer moved.
    /// </summary>
    /// <remarks>
    /// The first rebuild runs from the constructor, where a TextBlock has nothing to lay text out
    /// against and measures zero, and <c>PreRenderOffScreen</c> sizes the window from that same
    /// pass. This is the one correction, wired to <c>Loaded</c> and unhooked after it fires. The
    /// parked window's own size does not matter: every <c>Toggle</c> resizes from
    /// <see cref="GetFlyoutWidth"/>, which reads the field this writes.
    /// </remarks>
    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;

        int width = ComputeListColumnWidth();
        if (width == _listColumnWidth) return;

        _listColumnWidth = width;
        if (IsListMode)
        {
            foreach (var columnListView in ColumnListViews())
                columnListView.Width = width;

            ResizeIfVisible();
        }
    }

    private int GetFlyoutWidth()
    {
        int contentWidth;

        if (IsListMode)
            contentWidth = _listColumnWidth * Math.Max(1, ColumnsPanel.Children.Count);
        else
        {
            contentWidth = 0;
            foreach (var column in _columnLists)
                contentWidth += GetIconColumnWidth(column);
        }

        return contentWidth + (FlyoutOuterPadding * 2);
    }

    private int GetIconResizeStepWidth()
    {
        int columnCount = Math.Max(1, _columnLists.Count);
        int cellWidth = GetActiveIconCellWidth();
        return Math.Max(cellWidth, columnCount * cellWidth);
    }

    /// <summary>
    /// Edge resize is an editing affordance, so the grips only exist in icon mode <i>and</i>
    /// while editing , otherwise a stray drag on the flyout's edge silently changes the
    /// launcher's layout.
    /// </summary>
    private void UpdateResizeGripVisibility()
    {
        var visibility = IsIconMode && _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        _leftResizeGrip.Visibility = visibility;
        _rightResizeGrip.Visibility = visibility;
    }

    private ScrollViewer CreateIconModeColumn(ObservableCollection<LauncherItem> items)
    {
        int iconsPerRow = GetIconModeIconsPerRow();
        var column = new StackPanel { Width = GetIconColumnWidth(items), Padding = new Thickness(8, 2, 8, 2) };
        var currentRow = new StackPanel { Orientation = Orientation.Horizontal };
        int itemsInRow = 0;

        void FlushRow()
        {
            if (currentRow.Children.Count > 0)
            {
                column.Children.Add(currentRow);
                currentRow = new StackPanel { Orientation = Orientation.Horizontal };
                itemsInRow = 0;
            }
        }

        void AddTile(LauncherItem child)
        {
            currentRow.Children.Add(CreateIconTile(child));
            itemsInRow++;
            if (itemsInRow >= iconsPerRow)
                FlushRow();
        }

        foreach (var item in items)
        {
            if (item.IsGroup)
            {
                FlushRow();
                column.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Opacity = 0.7,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }
            else
            {
                AddTile(item);
            }
        }

        if (currentRow.Children.Count > 0)
            column.Children.Add(currentRow);

        return new ScrollViewer
        {
            Content = column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private Button CreateIconTile(LauncherItem item)
    {
        UIElement iconElement;
        int iconSize = GetActiveIconSize();
        if (!string.IsNullOrEmpty(item.IconPath) && File.Exists(item.IconPath))
        {
            var bmp = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Logical,
                DecodePixelWidth = iconSize + 4,
                // The stale-icon refresh rewrites cached files in place (same path),
                // so the per-URI decoded-image cache would keep showing the old bitmap.
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            };
            bmp.UriSource = new Uri(item.IconPath, UriKind.Absolute);
            iconElement = new Image
            {
                Source = bmp,
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        else if (Classes.IconGallery.IsFluentGlyph(item.IconGlyph))
        {
            iconElement = new FontIcon
            {
                Glyph = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush1)
                ((FontIcon)iconElement).Foreground = brush1;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = item.IconGlyph,
                FontSize = iconSize - 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            if (ParseIconColor(item.IconColor) is SolidColorBrush brush2)
                ((TextBlock)iconElement).Foreground = brush2;
        }

        var nameText = new TextBlock
        {
            Text = item.Name,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            MaxWidth = GetActiveIconCellWidth() - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        iconElement.SetValue(Grid.RowProperty, 0);
        iconElement.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        iconElement.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.Children.Add(iconElement);

        nameText.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(nameText, 1);
        content.Children.Add(nameText);

        var tile = new Button
        {
            Width = GetActiveIconCellWidth(),
            Height = GetActiveIconCellHeight(),
            Padding = new Thickness(4),
            Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = item,
        };
        tile.Click += IconTile_Click;
        tile.ContextRequested += IconTile_ContextRequested;
        return tile;
    }

    private void IconTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
            LaunchItem(item);
    }

    private void IconTile_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is LauncherItem item && !item.IsGroup)
        {
            ShowItemContextMenu(btn, item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The column list views, whether or not they are wrapped in a per-column header panel.
    /// Always use this rather than <c>ColumnsPanel.Children.OfType&lt;ListViewBase&gt;()</c>,
    /// which finds nothing once columns are wrapped.
    /// </summary>
    private IEnumerable<ListViewBase> ColumnListViews()
    {
        foreach (var child in ColumnsPanel.Children)
        {
            if (child is ListViewBase direct)
            {
                yield return direct;
            }
            else if (child is Panel wrapper)
            {
                foreach (var inner in wrapper.Children.OfType<ListViewBase>())
                    yield return inner;
            }
        }
    }

    private void RebuildColumnsPanel()
    {
        // A sync or structural edit invalidates every derived drag source and pending preview.
        ClearInsertionIndicator();
        ClearDragState();
        _columnLists = BuildColumnLists();
        _loadedIconChildLists.Clear();
        _syntheticGroups.Clear();
        // Containers are about to be discarded, so their saved edit styling is moot.
        _editStyledContainers.Clear();
        // ...and their hover wiring, which otherwise pins every container ever realised
        // (this window is permanent) , the root of a large native-memory leak.
        ClearHoverWiring();
        if (IsIconMode)
            WrapUngroupedItemsIntoSyntheticGroups();

        if (RootGrid.Resources["IconItemTemplateSelector"] is FlyoutItemTemplateSelector iconSelector)
            iconSelector.SyntheticGroups = _syntheticGroups;
        if (RootGrid.Resources["SmallIconItemTemplateSelector"] is FlyoutItemTemplateSelector smallIconSelector)
            smallIconSelector.SyntheticGroups = _syntheticGroups;

        ColumnsPanel.Children.Clear();
        UpdateResizeGripVisibility();

        // Show/hide launcher title at the top
        if (_launcher.ShowTitle)
        {
            LauncherTitle.Text = _launcher.Name;
            LauncherTitle.Visibility = Visibility.Visible;
            ContentStack.VerticalAlignment = VerticalAlignment.Top;
            ColumnsPanel.Margin = new Thickness(0);
        }
        else
        {
            LauncherTitle.Visibility = Visibility.Collapsed;
            if (IsIconMode)
            {
                ContentStack.VerticalAlignment = VerticalAlignment.Center;
                ColumnsPanel.Margin = new Thickness(0);
            }
            else
            {
                ContentStack.VerticalAlignment = VerticalAlignment.Top;
                ColumnsPanel.Margin = new Thickness(0, 0, 0, 4);
            }
        }

        // Before the columns are built, and after the title is decided: both feed it.
        _listColumnWidth = ComputeListColumnWidth();

        bool multipleColumns = _columnLists.Count > 1;

        for (int columnIndex = 0; columnIndex < _columnLists.Count; columnIndex++)
        {
            var lv = CreateColumnListView(columnIndex);
            lv.ItemsSource = _columnLists[columnIndex];

            // With a single column there is nothing to distinguish, so the list view goes in
            // bare and normal mode keeps its existing geometry exactly.
            if (!multipleColumns)
            {
                ColumnsPanel.Children.Add(lv);
                continue;
            }

            ColumnsPanel.Children.Add(BuildColumnContainer(lv, columnIndex));
        }

        ApplyColumnChrome();
        UpdateEmptyPlaceholder();
    }

    /// <summary>
    /// Resizes the window to its rebuilt content, but only while it is on screen.
    /// </summary>
    /// <remarks>
    /// A dismissed flyout resizes on its next <c>Toggle</c>, which is what <c>InvalidateItems</c>
    /// historically relied on. Edit mode pins the flyout open, so a change made from launcher
    /// settings , view mode or icon density especially , would otherwise rebuild the content
    /// without ever resizing the window around it. Resizing a parked window would fight the
    /// placement the next <c>Toggle</c> computes, hence the open check.
    /// </remarks>
    private static SolidColorBrush? ParseIconColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        hex = hex.TrimStart('#');
        try
        {
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, r, g, b));
            }
        }
        catch { /* fall through */ }
        return null;
    }
    private void UpdateFlyoutLayoutInPlace()
    {
        foreach (var columnListView in ColumnListViews())
        {
            DisableListViewTransitions(columnListView);

            if (IsIconMode && columnListView.Tag is int columnIndex && columnIndex >= 0 && columnIndex < _columnLists.Count)
            {
                columnListView.Width = GetIconColumnWidth(_columnLists[columnIndex]);
                ApplyTopLevelIconSpans(columnListView);
            }
        }

        // A group that just gained or lost its last child changes drop-target state.
        ApplyEmptyGroupDropTargets();

        if (!IsIconMode)
            return;

        foreach (var childListView in _loadedIconChildLists.ToList())
        {
            DisableListViewTransitions(childListView);

            if (childListView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
                wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

            ApplyIconGroupChildListLayout(childListView);
        }

        foreach (var groupRoot in _loadedIconGroupRoots.ToList())
            ApplyIconGroupRootLayout(groupRoot);
    }

    /// <summary>Applies current icon geometry and edit/drop behavior to a realised group list.</summary>
    private void IconGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListView listView)
            return;

        _loadedIconChildLists.Add(listView);
        listView.Unloaded -= IconGroupChildList_Unloaded;
        listView.Unloaded += IconGroupChildList_Unloaded;
        TrackGroupChildList(listView);


        DisableListViewTransitions(listView);
        ScrollViewer.SetVerticalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(listView, ScrollBarVisibility.Disabled);

        listView.ItemsPanel = CreateIconGroupItemsPanel();
        listView.UpdateLayout();

        if (listView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            wrapGrid.MaximumRowsOrColumns = GetIconModeIconsPerRow();

        ApplyIconGroupChildListLayout(listView);

        if (listView.Tag is LauncherItem group && _syntheticGroups.Contains(group))
        {
            VariableSizedWrapGrid.SetColumnSpan(listView, GetTopLevelIconSpan(group));
            VariableSizedWrapGrid.SetRowSpan(listView, GetTopLevelIconRowSpan(group));
        }
    }

    private void IconGroupRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not LauncherItem group)
            return;

        _loadedIconGroupRoots.Add(element);
        element.Unloaded -= IconGroupRoot_Unloaded;
        element.Unloaded += IconGroupRoot_Unloaded;

        ApplyIconGroupRootLayout(element);
    }

    private void ApplyIconGroupRootLayout(FrameworkElement element)
    {
        if (element.DataContext is not LauncherItem group)
            return;

        element.ClearValue(FrameworkElement.WidthProperty);
        element.MaxWidth = GetIconGroupContentWidth(group);
        element.HorizontalAlignment = HorizontalAlignment.Center;
        VariableSizedWrapGrid.SetColumnSpan(element, GetTopLevelIconSpan(group));
        VariableSizedWrapGrid.SetRowSpan(element, GetTopLevelIconRowSpan(group));
    }

    private void ApplyIconGroupChildListLayout(ListView listView)
    {
        if (listView.Tag is not LauncherItem group)
            return;

        listView.Width = GetIconGroupContentWidth(group);
        listView.MaxWidth = GetIconModeIconsPerRow() * GetActiveIconCellWidth();
        listView.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private void IconGroupChildList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
            _loadedIconChildLists.Remove(listView);
    }

    private void IconGroupRoot_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            _loadedIconGroupRoots.Remove(element);
    }

    private void ListGroupChildList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListView listView)
        {
            DisableListViewTransitions(listView);
            TrackGroupChildList(listView);

        }
    }

    private double _lastMeasuredHeight = 80;

    private double MeasureContentHeight()
    {
        if (IsIconMode)
            return MeasureIconModeHeight();

        // Calculate height arithmetically instead of calling UpdateLayout()/Measure()
        // on a potentially hidden window. Forcing a XAML layout pass on a window hidden
        // via ShowWindow(SW_HIDE) while another WinUI 3 window is active causes a fatal
        // ExecutionEngineException in Microsoft.WinUI.dll.
        //
        // Each ListViewItem container: MinHeight=0, Padding="8,6" → 12px vertical padding.
        // Regular item content: Icon 20px tall → total ~32px.
        // Group header content: 12px label with 4px top/bottom margin → ~24px.
        var items = _launcher.Items;
        if (items == null) return _lastMeasuredHeight;

        const double itemHeight = 32;
        const double groupHeight = 24;

        // Compute the height of each column and take the tallest.
        double maxColumnHeight = 0;
        foreach (var column in BuildColumnLists())
        {
            double currentColumnHeight = 0;

            foreach (var item in column)
            {
                if (item.IsGroup)
                {
                    currentColumnHeight += groupHeight;
                    foreach (var child in item.Children)
                        currentColumnHeight += itemHeight;

                    // An empty group still occupies one item-sized row, matching the
                    // MinHeight applied to its child list.
                    if (item.Children.Count == 0)
                        currentColumnHeight += EmptyGroupListModeHeight;
                }
                else
                {
                    currentColumnHeight += itemHeight;
                }
            }

            maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
        }

        // Add a small buffer to cover accumulated sub-pixel font-height rounding.
        // Hosts constrain the viewport; this value is the unchanged natural content extent.
        double titleHeight = _launcher.ShowTitle ? LauncherTitleHeight : 0;
        double editHeight = CurrentEditChromeHeight + CurrentColumnHeaderHeight + CurrentEmptyPlaceholderHeight;
        double outerPadding = FlyoutOuterPadding * 2;
        _lastMeasuredHeight = Math.Max(maxColumnHeight + titleHeight + editHeight + outerPadding + 2, GetMinimumFlyoutHeight());
        return _lastMeasuredHeight;
    }

    private double MeasureIconModeHeight()
    {
        int cellHeight = GetActiveIconCellHeight();
        double groupHeight = GetActiveGroupHeaderHeight();

        double maxColumnHeight = 0;

        foreach (var column in _columnLists)
        {
            double currentColumnHeight = 0;
            int currentRowSpan = 0;
            double currentRowHeight = 0;

            void FlushRow()
            {
                if (currentRowSpan == 0)
                    return;

                currentColumnHeight += currentRowHeight;
                currentRowSpan = 0;
                currentRowHeight = 0;
            }

            foreach (var item in column)
            {
                int span = GetTopLevelIconSpan(item);
                int perRow = GetIconModeIconsPerRow();
                int iconRows = Math.Max(1, (item.Children.Count + perRow - 1) / perRow);

                // Groups are sized like regular items: even an empty one occupies a full
                // cell row (plus its heading). ApplyEmptyGroupDropTarget gives the empty
                // group's child list a matching MinHeight so the rendering agrees.
                double itemHeight = item.IsGroup && !_syntheticGroups.Contains(item)
                    ? groupHeight + (iconRows * cellHeight)
                    : iconRows * cellHeight;

                if (currentRowSpan > 0 && currentRowSpan + span > GetIconModeIconsPerRow())
                    FlushRow();

                currentRowSpan += span;
                currentRowHeight = Math.Max(currentRowHeight, itemHeight);

                if (currentRowSpan >= GetIconModeIconsPerRow())
                    FlushRow();
            }

            FlushRow();
            maxColumnHeight = Math.Max(maxColumnHeight, currentColumnHeight);
        }

        double titleHeight = _launcher.ShowTitle ? LauncherTitleHeight : 0;
        double editHeight = CurrentEditChromeHeight + CurrentColumnHeaderHeight + CurrentEmptyPlaceholderHeight;
        double outerPadding = FlyoutOuterPadding * 2;
        _lastMeasuredHeight = Math.Max(maxColumnHeight + titleHeight + editHeight + outerPadding + 2, GetMinimumFlyoutHeight());
        return _lastMeasuredHeight;
    }

}
