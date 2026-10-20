using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace LittleLauncher.Classes;

/// <summary>
/// Displays a local symbol catalog with native GridView virtualization and recycling.
/// Put this view directly in a finite Grid row, never inside an outer ScrollViewer.
/// Filtering creates plain data only; XAML tiles exist for the viewport and its small cache.
/// </summary>
internal sealed class IconCatalogView : Grid
{
    private readonly GridView _grid;
    private readonly TextBlock _empty;
    private readonly CollectionViewSource _source = new() { IsSourceGrouped = true };
    private readonly GroupStyle _groupStyle;
    private readonly Action<string> _onSelected;
    private readonly HashSet<IconCatalogEntry> _entries = new();
    private bool _updating;
    private bool _active;
    private int _sourceVersion;
    private Action? _cancelSelectionReveal;

    internal IconCatalogView(Action<string> onSelected)
    {
        _onSelected = onSelected;
        MinHeight = 0;
        _grid = new GridView
        {
            MinHeight = 0,
            Padding = new Thickness(0),
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            IsMultiSelectCheckBoxEnabled = false,
            ItemTemplate = Parse<DataTemplate>("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <TextBlock Text="{Binding Glyph}" FontFamily="{Binding FontFamily}"
                               IsColorFontEnabled="True" FontSize="{Binding FontSize}"
                               HorizontalAlignment="Center" VerticalAlignment="Center" />
                </DataTemplate>
                """),
            ItemsPanel = Parse<ItemsPanelTemplate>("""
                <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <ItemsWrapGrid Orientation="Horizontal" MaximumRowsOrColumns="9"
                                   ItemWidth="44" ItemHeight="44" CacheLength="1" />
                </ItemsPanelTemplate>
                """),
            ItemContainerStyle = BuildContainerStyle(),
            ItemContainerTransitions = new TransitionCollection(),
            Transitions = new TransitionCollection()
        };
        AutomationProperties.SetAutomationId(_grid, "IconGallerySymbols");
        ScrollViewer.SetVerticalScrollBarVisibility(_grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_grid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollMode(_grid, ScrollMode.Disabled);
        _groupStyle = new GroupStyle { HidesIfEmpty = true };
        _grid.GroupStyle.Add(_groupStyle);
        _grid.ContainerContentChanging += UpdateContainer;
        _grid.SelectionChanged += SelectionChanged;
        Children.Add(_grid);
        _empty = IconGallery.NoResults();
        _empty.Visibility = Visibility.Collapsed;
        Children.Add(_empty);
    }

    /// <summary>
    /// Replaces the lightweight grouped data and restores selection without committing it.
    /// Searches match names exactly as the gallery did before; categories are hidden while searching.
    /// The framework creates/recycles only nearby containers and retains native keyboard navigation.
    /// </summary>
    internal void ShowCatalog(
        IEnumerable<(string Category, (string Glyph, string Name)[] Items)> categories,
        string query, bool emoji, Brush foreground, string? currentGlyph, bool revealSelection = false)
    {
        Clear();
        _active = true;
        _updating = true;
        try
        {
            _grid.Foreground = foreground;
            AutomationProperties.SetName(_grid, emoji ? "Emoji" : "Segoe Fluent Icons");
            _groupStyle.HeaderTemplate = string.IsNullOrEmpty(query)
                ? Parse<DataTemplate>("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                        <TextBlock Text="{Binding Name}" FontSize="12" FontWeight="SemiBold"
                                   Opacity="0.7" Margin="4,4,0,6" />
                    </DataTemplate>
                    """)
                : Parse<DataTemplate>("""
                    <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                        <Grid Height="0" />
                    </DataTemplate>
                    """);
            var groups = new List<IconCatalogGroup>();
            IconCatalogEntry? selected = null;
            foreach (var (category, icons) in categories)
            {
                var group = new IconCatalogGroup(category);
                foreach (var (glyph, name) in icons)
                {
                    if (!string.IsNullOrEmpty(query) && !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var entry = new IconCatalogEntry(glyph, name, emoji ? "Segoe UI Emoji" : "Segoe Fluent Icons", emoji ? 22 : 18);
                    group.Add(entry);
                    _entries.Add(entry);
                    if (glyph == currentGlyph)
                    {
                        selected = entry;
                    }
                }
                if (group.Count > 0)
                {
                    groups.Add(group);
                }
            }
            _source.Source = groups;
            _grid.ItemsSource = _source.View;
            _grid.SelectedItem = selected;
            _empty.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (revealSelection && selected != null)
            {
                int version = _sourceVersion;
                _cancelSelectionReveal = SelectionReveal.AfterLayout(_grid,
                    () => _active && version == _sourceVersion && ReferenceEquals(_grid.SelectedItem, selected),
                    () => _grid.ScrollIntoView(selected, ScrollIntoViewAlignment.Leading));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Releases the source and live selection on tab changes or close; late recycling events become inert.</summary>
    internal void Clear()
    {
        _active = false;
        _sourceVersion++;
        _cancelSelectionReveal?.Invoke();
        _cancelSelectionReveal = null;
        _updating = true;
        try
        {
            _grid.SelectedItem = null;
            _grid.ItemsSource = null;
            _source.Source = null;
            _entries.Clear();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Rebinds accessible metadata and tint; native selection chrome follows GridViewItem.IsSelected.</summary>
    private void UpdateContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var container = (GridViewItem)args.ItemContainer;
        if (args.InRecycleQueue || !_active || args.Item is not IconCatalogEntry entry || !_entries.Contains(entry))
        {
            ToolTipService.SetToolTip(container, null);
            AutomationProperties.SetName(container, "");
            return;
        }
        container.Foreground = _grid.Foreground;
        AutomationProperties.SetName(container, entry.Name);
        ToolTipService.SetToolTip(container, entry.Name);
    }

    /// <summary>Keeps native mouse and keyboard selection pending until the gallery's Confirm action.</summary>
    private void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating && _active && _grid.SelectedItem is IconCatalogEntry entry && _entries.Contains(entry))
        {
            _onSelected(entry.Glyph);
        }
    }

    /// <summary>
    /// Uses WinUI's GridView selection chrome, including hover/press and high-contrast brushes.
    /// ContainerContentChanging phase zero precedes Content assignment, so selection must not
    /// depend on comparing that content during realization. Keep container border/geometry fixed;
    /// the presenter's separate selection border follows native IsSelected through recycling.
    /// </summary>
    private static Style BuildContainerStyle() => Parse<Style>("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               TargetType="GridViewItem">
            <Setter Property="MinWidth" Value="0" />
            <Setter Property="MinHeight" Value="0" />
            <Setter Property="Width" Value="40" />
            <Setter Property="Height" Value="40" />
            <Setter Property="Margin" Value="2" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="UseSystemFocusVisuals" Value="True" />
            <Setter Property="HorizontalContentAlignment" Value="Center" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="GridViewItem">
                        <ListViewItemPresenter Content="{TemplateBinding Content}"
                            ContentTemplate="{TemplateBinding ContentTemplate}"
                            Foreground="{TemplateBinding Foreground}"
                            SelectedForeground="{TemplateBinding Foreground}"
                            HorizontalContentAlignment="Center" VerticalContentAlignment="Center"
                            CornerRadius="4" SelectionCheckMarkVisualEnabled="False"
                            SelectedBorderThickness="{ThemeResource GridViewItemSelectedBorderThickness}"
                            SelectedBorderBrush="{ThemeResource GridViewItemSelectedBorderBrush}"
                            SelectedPointerOverBorderBrush="{ThemeResource GridViewItemSelectedPointerOverBorderBrush}"
                            SelectedPressedBorderBrush="{ThemeResource GridViewItemSelectedPressedBorderBrush}"
                            SelectedDisabledBorderBrush="{ThemeResource GridViewItemSelectedDisabledBorderBrush}"
                            SelectedInnerBorderBrush="{ThemeResource GridViewItemSelectedInnerBorderBrush}"
                            SelectedBackground="Transparent" SelectedPointerOverBackground="Transparent"
                            SelectedPressedBackground="Transparent"
                            PointerOverBackground="{ThemeResource SubtleFillColorSecondaryBrush}"
                            PressedBackground="{ThemeResource SubtleFillColorTertiaryBrush}" />
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);

    private static T Parse<T>(string xaml) where T : class => (T)XamlReader.Load(xaml);

    /// <summary>One data item, without any XAML objects; ToString also gives automation a useful fallback name.</summary>
    public sealed record IconCatalogEntry(string Glyph, string Name, string FontFamily, int FontSize)
    {
        public override string ToString() => Name;
    }

    /// <summary>CollectionViewSource projects each enumerable group and its named header.</summary>
    public sealed class IconCatalogGroup(string name) : List<IconCatalogEntry>
    {
        public string Name { get; } = name;
        public override string ToString() => Name;
    }
}
