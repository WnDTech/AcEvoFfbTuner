using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AcEvoFfbTuner.Views;

public partial class TestingGuideOverlay : Window
{
    private bool _isTransparent;

    public TestingGuideOverlay()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = screen.Bottom - Height - 20;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Topmost = false;
        Topmost = true;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleTransparency(object sender, RoutedEventArgs e)
    {
        _isTransparent = !_isTransparent;

        if (_isTransparent)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x73, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x80, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -20 : 20;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 340 || newH < 420) return;
        if (newW > 700 || newH > 1000) return;

        Left += delta / 2;
        Top += delta / 2;
        Width = newW;
        Height = newH;
    }

    private void CloseOverlay(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
