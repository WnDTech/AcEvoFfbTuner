using System.Windows;
using System.Windows.Controls;

namespace AcEvoFfbTuner.Controls;

/// <summary>
/// Lays children out in equal-width columns, choosing as many columns as fit
/// <see cref="MinItemWidth"/> (capped by <see cref="MaxColumns"/>). Rows take
/// the height of their tallest child. Use for tile lists that must stay
/// readable at any window width: wide = several columns, narrow = one.
///
/// Usage as an ItemsControl panel:
///   &lt;ItemsPanelTemplate&gt;&lt;controls:AdaptiveGrid MinItemWidth="280" /&gt;&lt;/ItemsPanelTemplate&gt;
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptiveGrid),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private int Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return 1;
        int cols = (int)((width + Gap) / (MinItemWidth + Gap));
        return Math.Clamp(cols, 1, Math.Max(1, MaxColumns));
    }

    private double ItemWidth(double width, int cols) =>
        double.IsInfinity(width) ? MinItemWidth : Math.Max(0, (width - Gap * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double itemW = ItemWidth(available.Width, cols);
        double total = 0, rowH = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(itemW, double.PositiveInfinity));
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH + Gap; rowH = 0; }
        }
        if (i % cols != 0) total += rowH; else if (i > 0) total -= Gap;
        double w = double.IsInfinity(available.Width) ? itemW * cols + Gap * (cols - 1) : available.Width;
        return new Size(w, Math.Max(0, total));
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double itemW = ItemWidth(final.Width, cols);
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int start = 0; start < visible.Count; start += cols)
        {
            var row = visible.Skip(start).Take(cols).ToList();
            double rowH = row.Max(c => c.DesiredSize.Height);
            for (int c = 0; c < row.Count; c++)
                row[c].Arrange(new Rect(c * (itemW + Gap), y, itemW, rowH));
            y += rowH + Gap;
        }
        return final;
    }
}
