using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AcEvoFfbTuner.Controls;

public partial class SectionCard : UserControl
{
    public static readonly RoutedEvent SelectedEvent = EventManager.RegisterRoutedEvent(
        nameof(Selected), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SectionCard));

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

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(SectionCard),
            new PropertyMetadata(false, OnVisibilityModeChanged));

    public static readonly DependencyProperty SelectableProperty =
        DependencyProperty.Register(nameof(Selectable), typeof(bool), typeof(SectionCard),
            new PropertyMetadata(false, OnVisibilityModeChanged));

    public static readonly DependencyProperty SummaryContentProperty =
        DependencyProperty.Register(nameof(SummaryContent), typeof(object), typeof(SectionCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty InnerContentProperty =
        DependencyProperty.Register(nameof(InnerContent), typeof(object), typeof(SectionCard),
            new PropertyMetadata(null));

    public event RoutedEventHandler Selected
    {
        add => AddHandler(SelectedEvent, value);
        remove => RemoveHandler(SelectedEvent, value);
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public Brush SectionBrush { get => (Brush)GetValue(SectionBrushProperty); set => SetValue(SectionBrushProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsAlwaysVisible { get => (bool)GetValue(IsAlwaysVisibleProperty); set => SetValue(IsAlwaysVisibleProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool Selectable { get => (bool)GetValue(SelectableProperty); set => SetValue(SelectableProperty, value); }
    public object SummaryContent { get => GetValue(SummaryContentProperty); set => SetValue(SummaryContentProperty, value); }
    public object InnerContent { get => GetValue(InnerContentProperty); set => SetValue(InnerContentProperty, value); }

    public SectionCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        CardBorder.MouseLeftButtonUp += OnCardClick;
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

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (!Selectable) return;
        IsSelected = true;
        RaiseEvent(new RoutedEventArgs(SelectedEvent));
    }

    private void UpdateVisualState()
    {
        if (SummaryPart == null || ContentPart == null || ExpandToggle == null) return;

        if (Selectable)
        {
            SummaryPart.Visibility = Visibility.Visible;
            ContentPart.Visibility = Visibility.Collapsed;
            ExpandToggle.Visibility = Visibility.Collapsed;
            CardBorder.Cursor = Cursors.Hand;
        }
        else
        {
            var showContent = IsAlwaysVisible || IsExpanded;
            var showSummary = !IsAlwaysVisible && !IsExpanded;

            SummaryPart.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;
            ContentPart.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
            ExpandToggle.Visibility = IsAlwaysVisible ? Visibility.Collapsed : Visibility.Visible;
            CardBorder.Cursor = null;
        }
    }
}