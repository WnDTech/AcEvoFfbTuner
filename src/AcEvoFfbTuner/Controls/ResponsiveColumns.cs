using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AcEvoFfbTuner.Controls;

/// <summary>
/// Attached behaviour for side-by-side card grids (e.g. "* | gap | *").
/// When the grid is narrower than <c>StackBelow</c> px, every child in a star
/// column is moved onto its own row spanning the full width (in column order),
/// with a vertical gap; when it widens again the original layout is restored.
/// Only for grids whose children all sit in row 0.
///
/// Usage: &lt;Grid controls:ResponsiveColumns.StackBelow="640"&gt;
/// </summary>
public static class ResponsiveColumns
{
    public static readonly DependencyProperty StackBelowProperty =
        DependencyProperty.RegisterAttached("StackBelow", typeof(double), typeof(ResponsiveColumns),
            new PropertyMetadata(0.0, OnStackBelowChanged));

    public static double GetStackBelow(DependencyObject d) => (double)d.GetValue(StackBelowProperty);
    public static void SetStackBelow(DependencyObject d, double v) => d.SetValue(StackBelowProperty, v);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(State), typeof(ResponsiveColumns));

    private sealed class State
    {
        public bool Stacked;
        public readonly Dictionary<UIElement, (int Col, int Span, Thickness Margin)> Original = new();
        public int AddedRows;
    }

    private const double Gap = 12;

    private static void OnStackBelowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g) return;
        g.SizeChanged -= OnSizeChanged;
        g.SizeChanged += OnSizeChanged;
        g.Loaded -= OnLoaded;
        g.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((Grid)sender);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((Grid)sender);

    private static void Apply(Grid g)
    {
        double threshold = GetStackBelow(g);
        if (threshold <= 0 || g.ActualWidth <= 0) return;

        var st = (State?)g.GetValue(StateProperty);
        if (st == null) { st = new State(); g.SetValue(StateProperty, st); }

        bool wantStacked = g.ActualWidth < threshold;
        if (wantStacked == st.Stacked) return;

        if (wantStacked)
        {
            // Star columns are the content columns; fixed-width ones are gaps.
            var starCols = Enumerable.Range(0, g.ColumnDefinitions.Count)
                .Where(i => g.ColumnDefinitions[i].Width.IsStar).ToList();
            if (starCols.Count < 2) return;

            st.Original.Clear();
            int colCount = g.ColumnDefinitions.Count;
            foreach (UIElement child in g.Children)
            {
                int col = Grid.GetColumn(child);
                int rank = starCols.IndexOf(col);
                if (rank < 0) rank = 0;
                var fe = child as FrameworkElement;
                st.Original[child] = (col, Grid.GetColumnSpan(child), fe?.Margin ?? default);
                Grid.SetColumn(child, 0);
                Grid.SetColumnSpan(child, colCount);
                Grid.SetRow(child, rank);
                if (fe != null && rank > 0)
                    fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + Gap, fe.Margin.Right, fe.Margin.Bottom);
            }
            st.AddedRows = 0;
            while (g.RowDefinitions.Count < starCols.Count)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                st.AddedRows++;
            }
            st.Stacked = true;
        }
        else
        {
            foreach (var (child, o) in st.Original)
            {
                Grid.SetColumn(child, o.Col);
                Grid.SetColumnSpan(child, o.Span);
                Grid.SetRow(child, 0);
                if (child is FrameworkElement fe) fe.Margin = o.Margin;
            }
            for (int i = 0; i < st.AddedRows && g.RowDefinitions.Count > 0; i++)
                g.RowDefinitions.RemoveAt(g.RowDefinitions.Count - 1);
            st.AddedRows = 0;
            st.Stacked = false;
        }
    }
}
