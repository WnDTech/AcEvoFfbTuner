using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class DevicesPage : UserControl
{
    private MainViewModel? _vm;

    public DevicesPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (LedPanel == null) return;

        if (sender is not RadioButton rb) return;

        LedPanel.Visibility = rb == LedTab ? Visibility.Visible : Visibility.Collapsed;
        HapticPanel.Visibility = rb == HapticTab ? Visibility.Visible : Visibility.Collapsed;
        ButtonsPanel.Visibility = rb == ButtonsTab ? Visibility.Visible : Visibility.Collapsed;

        if (rb == LedTab)
            UpdateLedPreview();
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
}
