using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LittleLauncher.Controls;

public sealed class PackedIconPanel : Panel
{
    public static readonly DependencyProperty MaximumRowsOrColumnsProperty =
        DependencyProperty.Register(
            nameof(MaximumRowsOrColumns),
            typeof(int),
            typeof(PackedIconPanel),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(PackedIconPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(PackedIconPanel),
            new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    private readonly List<Rect> _childRects = [];

    public int MaximumRowsOrColumns
    {
        get => (int)GetValue(MaximumRowsOrColumnsProperty);
        set => SetValue(MaximumRowsOrColumnsProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _childRects.Clear();

        int maxColumns = Math.Max(1, MaximumRowsOrColumns);
        double cellWidth = ItemWidth;

        if (cellWidth <= 0)
        {
            foreach (UIElement child in Children)
            {
                child.Measure(availableSize);
            }

            return new Size(0, 0);
        }

        var rowItems = new List<(int Index, double Width, double Height)>();
        var rects = new Rect[Children.Count];
        int currentRowSpan = 0;
        double currentRowHeight = 0;
        double currentTop = 0;

        void FlushRow()
        {
            if (rowItems.Count == 0)
            {
                return;
            }

            double currentLeft = 0;
            foreach (var rowItem in rowItems)
            {
                rects[rowItem.Index] = new Rect(currentLeft, currentTop, rowItem.Width, rowItem.Height);
                currentLeft += rowItem.Width;
            }

            currentTop += currentRowHeight;
            rowItems.Clear();
            currentRowSpan = 0;
            currentRowHeight = 0;
        }

        for (int index = 0; index < Children.Count; index++)
        {
            UIElement child = Children[index];
            int columnSpan = Math.Clamp(VariableSizedWrapGrid.GetColumnSpan(child), 1, maxColumns);
            double childWidth = columnSpan * cellWidth;

            if (currentRowSpan > 0 && currentRowSpan + columnSpan > maxColumns)
            {
                FlushRow();
            }

            child.Measure(new Size(childWidth, double.PositiveInfinity));
            double childHeight = child.DesiredSize.Height;

            rowItems.Add((index, childWidth, childHeight));
            currentRowSpan += columnSpan;
            currentRowHeight = Math.Max(currentRowHeight, childHeight);

            if (currentRowSpan >= maxColumns)
            {
                FlushRow();
            }
        }

        FlushRow();

        _childRects.AddRange(rects);
        return new Size(maxColumns * cellWidth, currentTop);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double contentWidth = Math.Max(0, MaximumRowsOrColumns) * ItemWidth;
        double horizontalOffset = Math.Max(0, (finalSize.Width - contentWidth) / 2);

        for (int index = 0; index < Children.Count; index++)
        {
            UIElement child = Children[index];
            Rect rect = index < _childRects.Count ? _childRects[index] : Rect.Empty;
            child.Arrange(new Rect(rect.X + horizontalOffset, rect.Y, rect.Width, rect.Height));
        }

        return finalSize;
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PackedIconPanel panel)
        {
            panel.InvalidateMeasure();
        }
    }
}
