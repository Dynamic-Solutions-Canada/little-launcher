using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using global::Windows.Foundation;

namespace LittleLauncher.Controls;

/// <summary>Places children at their natural widths and wraps complete controls onto new rows.</summary>
/// <remarks>Unlike the packed item panel, this panel never sizes children to fixed cells.</remarks>
internal sealed class WrappingPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrappingPanel),
        new PropertyMetadata(8d, (sender, _) => ((WrappingPanel)sender).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            // Match the sibling strip panels: a star column inside a button must first see
            // unconstrained width to report its natural size instead of filling the row.
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.DesiredSize.Width > availableSize.Width)
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return LayoutRows(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRows(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Uses the same row boundaries for measurement and arrangement, in child/tab order.</summary>
    private Size LayoutRows(double availableWidth, bool arrange)
    {
        double left = 0;
        double top = 0;
        double rowHeight = 0;
        double widest = 0;
        bool hasRowItem = false;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var desired = child.DesiredSize;
            double gap = hasRowItem ? Spacing : 0;

            if (hasRowItem && left + gap + desired.Width > availableWidth)
            {
                top += rowHeight + Spacing;
                left = 0;
                rowHeight = 0;
                hasRowItem = false;
                gap = 0;
            }

            left += gap;
            if (arrange)
                child.Arrange(new Rect(left, top, desired.Width, desired.Height));

            left += desired.Width;
            rowHeight = Math.Max(rowHeight, desired.Height);
            widest = Math.Max(widest, left);
            hasRowItem = true;
        }

        return new Size(widest, top + rowHeight);
    }
}
