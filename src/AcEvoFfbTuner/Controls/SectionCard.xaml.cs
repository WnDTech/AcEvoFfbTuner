using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AcEvoFfbTuner.Controls;

public partial class SectionCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SectionCard), new PropertyMetadata(""));

    public static readonly DependencyProperty SectionBrushProperty =
        DependencyProperty.Register(nameof(SectionBrush), typeof(Brush), typeof(SectionCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(SectionCard),
            new PropertyMetadata(true, OnExpandedChanged));

    public static readonly DependencyProperty IsAlwaysVisibleProperty =
        DependencyProperty.Register(nameof(IsAlwaysVisible), typeof(bool), typeof(SectionCard),
            new PropertyMetadata(false, OnVisibilityModeChanged));

    public static readonly DependencyProperty SummaryContentProperty =
        DependencyProperty.Register(nameof(SummaryContent), typeof(object), typeof(SectionCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty InnerContentProperty =
        DependencyProperty.Register(nameof(InnerContent), typeof(object), typeof(SectionCard),
            new PropertyMetadata(null));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public Brush SectionBrush { get => (Brush)GetValue(SectionBrushProperty); set => SetValue(SectionBrushProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsAlwaysVisible { get => (bool)GetValue(IsAlwaysVisibleProperty); set => SetValue(IsAlwaysVisibleProperty, value); }
    public object SummaryContent { get => GetValue(SummaryContentProperty); set => SetValue(SummaryContentProperty, value); }
    public object InnerContent { get => GetValue(InnerContentProperty); set => SetValue(InnerContentProperty, value); }

    public SectionCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateVisualState();
    }

    private static void OnExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SectionCard)d).UpdateVisualState();
    }

    private static void OnVisibilityModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SectionCard)d).UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        if (SummaryPart == null) return;

        var showContent = IsAlwaysVisible || IsExpanded;
        var showSummary = !IsAlwaysVisible && !IsExpanded;

        SummaryPart.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
        ContentPart.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        ExpandToggle.Visibility = IsAlwaysVisible ? Visibility.Collapsed : Visibility.Visible;
    }
}
