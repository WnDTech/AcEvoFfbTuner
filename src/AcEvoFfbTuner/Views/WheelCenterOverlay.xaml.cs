using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcEvoFfbTuner.Views;

public partial class WheelCenterOverlay : Window
{
    private bool _isTransparent;
    private bool _isCompact;

    private float _displayedAngle; // degrees (-lock/2 .. +lock/2), 0 = centered
    private float _physicalNormalized; // -1..+1 from hardware

    private const float CenterThresholdGreen = 3f;  // degrees considered "centered"
    private const float CenterThresholdYellow = 10f; // degrees before turning red

    private static readonly SolidColorBrush _green = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush _yellow = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly SolidColorBrush _red = new(Color.FromRgb(0xE5, 0x39, 0x35));
    public WheelCenterOverlay()
    {
        InitializeComponent();
    }

    public void UpdateWheel(float physicalNormalized, float angleDegrees)
    {
        _physicalNormalized = physicalNormalized;
        _displayedAngle = angleDegrees;

        Dispatcher.Invoke(UpdateGauge);
    }

    private void UpdateGauge()
    {
        double angleRad = _physicalNormalized * Math.PI;
        double cos = Math.Cos(angleRad);
        double sin = Math.Sin(angleRad);

        double cx = 80, cy = 80;
        double radius = 64; // distance from center to indicator dot
        double dotX = cx + sin * radius;
        double dotY = cy - cos * radius;

        Canvas.SetLeft(IndicatorDot, dotX - 6);
        Canvas.SetTop(IndicatorDot, dotY - 6);

        double markerLen = 68;
        double markerX = cx + sin * markerLen;
        double markerY = cy - cos * markerLen;
        MarkerLine.X2 = markerX;
        MarkerLine.Y2 = markerY;

        double oppLen = 68;
        double oppX = cx - sin * oppLen;
        double oppY = cy + cos * oppLen;
        MarkerLineOpposite.X2 = oppX;
        MarkerLineOpposite.Y2 = oppY;

        AngleText.Text = $"{_displayedAngle:F0}°";

        double distDeg = Math.Abs(_displayedAngle);
        if (distDeg <= CenterThresholdGreen)
        {
            AngleText.Foreground = _green;
            IndicatorDot.Fill = _green;
            MarkerLine.Stroke = _green;
        }
        else if (distDeg <= CenterThresholdYellow)
        {
            AngleText.Foreground = _yellow;
            IndicatorDot.Fill = _yellow;
            MarkerLine.Stroke = _yellow;
        }
        else
        {
            AngleText.Foreground = _red;
            IndicatorDot.Fill = _red;
            MarkerLine.Stroke = _red;
        }
    }

    public void Clear()
    {
        _physicalNormalized = 0f;
        _displayedAngle = 0f;
        Dispatcher.Invoke(() =>
        {
            IndicatorDot.Fill = _green;
            Canvas.SetLeft(IndicatorDot, 74);
            Canvas.SetTop(IndicatorDot, 6);
            MarkerLine.X2 = 80; MarkerLine.Y2 = 16;
            MarkerLineOpposite.X2 = 80; MarkerLineOpposite.Y2 = 144;
            AngleText.Text = "0°";
            AngleText.Foreground = _green;
            MarkerLine.Stroke = _green;
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = screen.Bottom - Height - 100;
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
            if (e.ClickCount == 2)
            {
                OnToggleCompact();
                return;
            }
            try { DragMove(); } catch { }
        }
    }

    private void OnToggleCompact()
    {
        _isCompact = !_isCompact;
        HeaderBar.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        FooterBar.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        ApplyBorderStyle();
    }

    private void ToggleTransparency(object sender, RoutedEventArgs e)
    {
        _isTransparent = !_isTransparent;
        ApplyBorderStyle();
    }

    private void ApplyBorderStyle()
    {
        if (_isCompact)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "MIN";
        }
        else if (_isTransparent)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0x7E, 0x22));
            if (TransparencyIcon != null) TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -15 : 15;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 160 || newH < 200) return;
        if (newW > 400 || newH > 450) return;

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
