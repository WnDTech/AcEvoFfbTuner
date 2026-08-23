using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class DevicesPage : UserControl
{
    private MainViewModel? _vm;
    private readonly Dictionary<string, SectionCard> _cardsByTag = new();
    private readonly Dictionary<string, FrameworkElement> _panels = new();

    public event EventHandler? Hf8MotorTestRequested;

    public DevicesPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CacheCards();
        BuildPanelMap();
        ShowSection("Wheelbase");
    }

    private void CacheCards()
    {
        foreach (var card in FindSectionCards(this))
        {
            if (card.Tag is string tag && !string.IsNullOrEmpty(tag) && !_cardsByTag.ContainsKey(tag))
            {
                _cardsByTag[tag] = card;
                card.Selected += OnCardSelected;
            }
        }
    }

    private void BuildPanelMap()
    {
        _panels["Wheelbase"] = WheelPanel;
        _panels["Led"] = LedPanel;
        _panels["Haptic"] = HapticPanel;
        _panels["Buttons"] = ButtonsPanel;
        _panels["Pedals"] = PedalPanel;
    }

    private void OnCardSelected(object sender, RoutedEventArgs e)
    {
        if (sender is SectionCard card && card.Tag is string tag)
            ShowSection(tag);
    }

    private void ShowSection(string tag)
    {
        if (!_cardsByTag.TryGetValue(tag, out var card)) return;
        if (!_panels.TryGetValue(tag, out var panel)) return;

        foreach (var kv in _panels)
            kv.Value.Visibility = kv.Key == tag ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kv in _cardsByTag)
            kv.Value.IsSelected = kv.Key == tag;

        DetailHeader.Text = card.Title;
        var brush = card.SectionBrush ?? (Brush)FindResource("SectionOutput");
        DetailHeader.Foreground = brush;
        DetailHeaderAccent.Background = brush;

        if (tag == "Led")
            UpdateLedPreview();
    }

    private static IEnumerable<SectionCard> FindSectionCards(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is SectionCard sectionCard)
                yield return sectionCard;

            if (child is DependencyObject depChild)
            {
                foreach (var nested in FindSectionCards(depChild))
                    yield return nested;
            }
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveLedCount) ||
            e.PropertyName == nameof(MainViewModel.LedVisibleCount))
        {
            Dispatcher.BeginInvoke(UpdateLedPreview);
        }
    }

    private void UpdateLedPreview()
    {
        if (LedPreviewBar == null || _vm == null) return;

        int ledCount = _vm.LedVisibleCount;
        if (ledCount <= 0) ledCount = 10;
        int activeCount = _vm.ActiveLedCount;

        if (LedPreviewBar.Children.Count != ledCount)
            BuildLedDots(ledCount);

        var offColor = Color.FromRgb(0x21, 0x26, 0x2D);
        var colors = new[]
        {
            Color.FromRgb(0x00, 0xE6, 0x76),
            Color.FromRgb(0x00, 0xE6, 0x76),
            Color.FromRgb(0x66, 0xBB, 0x6A),
            Color.FromRgb(0xFF, 0xD6, 0x00),
            Color.FromRgb(0xFF, 0xD6, 0x00),
            Color.FromRgb(0xFF, 0x98, 0x00),
            Color.FromRgb(0xFF, 0x98, 0x00),
            Color.FromRgb(0xF4, 0x43, 0x36),
            Color.FromRgb(0xF4, 0x43, 0x36),
            Color.FromRgb(0xF4, 0x43, 0x36),
        };

        for (int i = 0; i < LedPreviewBar.Children.Count; i++)
        {
            if (LedPreviewBar.Children[i] is Ellipse dot)
            {
                bool active = i < activeCount;
                Color c = active && i < colors.Length ? colors[i] : offColor;
                dot.Fill = new SolidColorBrush(c);
                dot.Opacity = active ? 1.0 : 0.4;
                dot.StrokeThickness = active ? 2 : 1;
                dot.Stroke = active
                    ? new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B))
                    : new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));
            }
        }
    }

    private void BuildLedDots(int count)
    {
        LedPreviewBar.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            var ellipse = new Ellipse
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(6, 0, 6, 0),
                Fill = new SolidColorBrush(Color.FromRgb(0x21, 0x26, 0x2D)),
                Stroke = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D)),
                StrokeThickness = 1,
            };
            LedPreviewBar.Children.Add(ellipse);
        }
    }

    private void OnOpenMotorTest(object sender, RoutedEventArgs e)
    {
        Hf8MotorTestRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSavePedalConfig(object sender, RoutedEventArgs e)
    {
        _vm?.SavePedalConfig();
    }
}