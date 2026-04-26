using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcEvoFfbTuner.Views;

public partial class ProfilerOverlay : Window
{
    private bool _isTransparent;

    private const int BufMax = 300;
    private const float WindowSec = 10f;
    private const float SampleHz = 30f;

    private readonly float[] _bForce = new float[BufMax];
    private readonly float[] _bRaw = new float[BufMax];
    private readonly float[] _bSteer = new float[BufMax];
    private readonly float[] _bGas = new float[BufMax];
    private readonly float[] _bBrake = new float[BufMax];
    private int _bN;

    private readonly Polyline _plForce = new() { StrokeThickness = 2 };
    private readonly Polyline _plRaw = new() { StrokeThickness = 1 };
    private readonly Polyline _plSteer = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new[] { 3.0, 3.0 }) };
    private readonly Polyline _plGas = new() { StrokeThickness = 1 };
    private readonly Polyline _plBrake = new() { StrokeThickness = 1 };
    private readonly Polyline _plZero = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };

    private static readonly SolidColorBrush _cForce = new(Color.FromRgb(0xE6, 0x7E, 0x22));
    private static readonly SolidColorBrush _cRaw = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush _cSteer = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly SolidColorBrush _cGas = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush _cBrake = new(Color.FromRgb(0xE5, 0x39, 0x35));

    private float _lastClipPct;

    public ProfilerOverlay()
    {
        InitializeComponent();

        _plForce.Stroke = _cForce;
        _plRaw.Stroke = _cRaw;
        _plSteer.Stroke = _cSteer;
        _plGas.Stroke = _cGas;
        _plBrake.Stroke = _cBrake;

        OvCanvas.Children.Add(_plZero);
        OvCanvas.Children.Add(_plBrake);
        OvCanvas.Children.Add(_plGas);
        OvCanvas.Children.Add(_plSteer);
        OvCanvas.Children.Add(_plRaw);
        OvCanvas.Children.Add(_plForce);
    }

    public void UpdateData(float speed, float forceOut, float rawFF, float steerAngle,
        float gasInput, float brakeInput, float clipPct, float minForce, float maxForce)
    {
        Push(_bForce, forceOut);
        Push(_bRaw, rawFF);
        Push(_bSteer, steerAngle);
        Push(_bGas, gasInput);
        Push(_bBrake, brakeInput);
        if (_bN < BufMax) _bN++;

        OvSpeed.Text = $"{speed:F0} km/h";
        OvForce.Text = forceOut.ToString("F3");
        OvGas.Text = $"{gasInput * 100f:F0}%";
        OvBrake.Text = $"{brakeInput * 100f:F0}%";

        _lastClipPct = clipPct;
        OvClip.Text = $"{clipPct:F1}%";
        OvClip.Foreground = clipPct > 5f ? Brushes.Red : clipPct > 0f ? Brushes.Yellow : new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));

        if (maxForce >= minForce)
            OvMinMax.Text = $"{minForce:F2} / {maxForce:F2}";

        Redraw();
    }

    private void Push(float[] arr, float val)
    {
        if (_bN < BufMax)
        {
            arr[_bN] = val;
        }
        else
        {
            Array.Copy(arr, 1, arr, 0, BufMax - 1);
            arr[BufMax - 1] = val;
        }
    }

    private void Redraw()
    {
        double w = OvCanvas.ActualWidth;
        double h = OvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double yBottom = h - 4;
        double yTop = 4;
        double yRange = yBottom - yTop;

        _plZero.Points = new PointCollection(new[] { new Point(0, yBottom), new Point(w, yBottom) });

        int windowSamples = (int)(WindowSec * SampleHz);
        _plForce.Points = OvShowOutput.IsChecked == true ? BuildPts(_bForce, _bN, w, yBottom, yRange, windowSamples) : new PointCollection();
        _plRaw.Points = OvShowRaw.IsChecked == true ? BuildPts(_bRaw, _bN, w, yBottom, yRange, windowSamples) : new PointCollection();
        _plSteer.Points = OvShowSteer.IsChecked == true ? BuildPtsSteer(_bSteer, _bN, w, yBottom, yRange, windowSamples) : new PointCollection();

        if (OvShowGas.IsChecked == true)
        {
            _plGas.Points = BuildPts(_bGas, _bN, w, yBottom, yRange, windowSamples);
            _plBrake.Points = BuildPts(_bBrake, _bN, w, yBottom, yRange, windowSamples);
        }
        else
        {
            _plGas.Points = new PointCollection();
            _plBrake.Points = new PointCollection();
        }
    }

    private static PointCollection BuildPts(float[] data, int count, double w, double yBottom, double yRange, int windowSamples)
    {
        int n = Math.Min(count, BufMax);
        int displayN = Math.Min(n, windowSamples);
        int startIdx = n - displayN;
        var pts = new PointCollection(displayN);
        double xStep = displayN > 1 ? w / (displayN - 1) : 0;
        for (int i = 0; i < displayN; i++)
            pts.Add(new Point(i * xStep, yBottom - Math.Abs(data[startIdx + i]) * yRange));
        return pts;
    }

    private static PointCollection BuildPtsSteer(float[] data, int count, double w, double yBottom, double yRange, int windowSamples)
    {
        int n = Math.Min(count, BufMax);
        int displayN = Math.Min(n, windowSamples);
        int startIdx = n - displayN;
        var pts = new PointCollection(displayN);
        double xStep = displayN > 1 ? w / (displayN - 1) : 0;
        double yMid = yBottom - yRange * 0.5;
        for (int i = 0; i < displayN; i++)
            pts.Add(new Point(i * xStep, yMid - data[startIdx + i] * yRange * 0.5));
        return pts;
    }

    public void Clear()
    {
        Array.Clear(_bForce); Array.Clear(_bRaw); Array.Clear(_bSteer);
        Array.Clear(_bGas); Array.Clear(_bBrake);
        _bN = 0;
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
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xBC, 0xD4));
            TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x8A, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0xBC, 0xD4));
            TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -20 : 20;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 300 || newH < 250) return;
        if (newW > 700 || newH > 600) return;

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
