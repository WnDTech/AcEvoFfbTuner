using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcEvoFfbTuner.Views.Pages;

public partial class EqualizerPage : UserControl
{
    private readonly Slider[] _eqSliders = new Slider[10];
    private readonly Border?[] _eqFillBorders = new Border?[10];
    private readonly Ellipse?[] _eqThumbEllipses = new Ellipse?[10];
    private readonly SolidColorBrush[] _eqFillBrushes = new SolidColorBrush[10];
    private readonly TextBlock?[] _eqValueLabels = new TextBlock?[10];
    private readonly System.Windows.Shapes.Path _eqCurvePath = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xA5, 0x00)),
        StrokeThickness = 3
    };
    private readonly System.Windows.Shapes.Path _eqCurveFill = new()
    {
        StrokeThickness = 0,
        Fill = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xA5, 0x00))
    };
    private readonly SolidColorBrush _eqZeroBrush = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private readonly Polyline _eqZeroLine = new() { StrokeThickness = 1 };

    public EqualizerPage()
    {
        InitializeComponent();

        for (int i = 0; i < 10; i++)
        {
            _eqSliders[i] = (Slider)FindName($"EqSlider{i}");
            _eqFillBrushes[i] = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            _eqValueLabels[i] = FindName($"EqValueLabel{i}") as TextBlock;
        }

        _eqZeroLine.Stroke = _eqZeroBrush;
        EqCurveCanvas.Children.Add(_eqZeroLine);
        EqCurveCanvas.Children.Add(_eqCurveFill);
        EqCurveCanvas.Children.Add(_eqCurvePath);

        EqSliderGrid.SizeChanged += (s, e) => UpdateEqCurve();

        Loaded += OnEqualizerPageLoaded;
    }

    private void OnEqualizerPageLoaded(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < 10; i++)
        {
            _eqSliders[i]?.ApplyTemplate();

            var track = _eqSliders[i]?.Template?.FindName("PART_Track", _eqSliders[i]) as System.Windows.Controls.Primitives.Track;
            if (track?.DecreaseRepeatButton != null)
            {
                track.DecreaseRepeatButton.ApplyTemplate();
                if (VisualTreeHelper.GetChildrenCount(track.DecreaseRepeatButton) > 0)
                {
                    _eqFillBorders[i] = VisualTreeHelper.GetChild(track.DecreaseRepeatButton, 0) as Border;
                }
            }
            if (track?.Thumb != null)
            {
                track.Thumb.ApplyTemplate();
                if (VisualTreeHelper.GetChildrenCount(track.Thumb) > 0)
                {
                    var thumbGrid = VisualTreeHelper.GetChild(track.Thumb, 0) as Grid;
                    if (thumbGrid != null && thumbGrid.Children.Count >= 2)
                        _eqThumbEllipses[i] = thumbGrid.Children[1] as Ellipse;
                }
            }
        }

        UpdateAllEqSliderColors();
        UpdateEqCurve();
    }

    private void EqSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider s)
        {
            int idx = Array.IndexOf(_eqSliders, s);
            if (idx >= 0)
                UpdateEqSliderColor(idx);
        }
        UpdateEqCurve();
    }

    private void UpdateEqCurve()
    {
        if (_eqSliders[0] == null) return;

        double canvasW = EqCurveCanvas.ActualWidth;
        double canvasH = EqCurveCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        var canvasOrigin = EqCurveCanvas.TransformToAncestor(EqSliderGrid).Transform(new Point(0, 0));

        var pts = new Point[10];
        for (int i = 0; i < 10; i++)
        {
            var slider = _eqSliders[i];
            if (slider == null) continue;

            var track = slider.Template.FindName("PART_Track", slider) as System.Windows.Controls.Primitives.Track;
            double thumbY;
            double thumbCenterX;

            if (track != null && track.Thumb != null)
            {
                var thumbTopLeft = track.Thumb.TransformToAncestor(EqSliderGrid).Transform(new Point(0, 0));
                double thumbHeight = track.Thumb.ActualHeight;
                thumbY = thumbTopLeft.Y + thumbHeight / 2.0 - canvasOrigin.Y;
                thumbCenterX = thumbTopLeft.X + track.Thumb.ActualWidth / 2.0 - canvasOrigin.X;
            }
            else
            {
                var sliderTopLeft = slider.TransformToAncestor(EqSliderGrid).Transform(new Point(0, 0));
                double sliderHeight = slider.ActualHeight;
                double gain = slider.Value;
                thumbY = sliderTopLeft.Y + sliderHeight / 2.0 - (gain / 12.0) * (sliderHeight / 2.0) - canvasOrigin.Y;
                thumbCenterX = sliderTopLeft.X + slider.ActualWidth / 2.0 - canvasOrigin.X;
            }

            pts[i] = new Point(thumbCenterX, thumbY);
        }

        var lineGeo = BuildCatmullRomSpline(pts, 8);
        _eqCurvePath.Data = lineGeo;

        double zeroY;
        {
            var s0TopLeft = _eqSliders[0].TransformToAncestor(EqSliderGrid).Transform(new Point(0, 0));
            double sliderH = _eqSliders[0].ActualHeight;
            zeroY = s0TopLeft.Y + sliderH / 2.0 - canvasOrigin.Y;
        }

        var fillGeo = new StreamGeometry();
        using (var fCtx = fillGeo.Open())
        {
            fCtx.BeginFigure(new Point(pts[0].X, zeroY), true, true);
            AddCatmullRomPoints(fCtx, pts, 8);
            fCtx.LineTo(new Point(pts[9].X, zeroY), true, false);
        }
        _eqCurveFill.Data = fillGeo;

        _eqZeroLine.Points = new PointCollection(new[] { new Point(0, zeroY), new Point(canvasW, zeroY) });
    }

    private static StreamGeometry BuildCatmullRomSpline(Point[] pts, int subdivisions)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            AddCatmullRomPoints(ctx, pts, subdivisions);
        }
        return geo;
    }

    private static void AddCatmullRomPoints(StreamGeometryContext ctx, Point[] pts, int subdivisions)
    {
        int n = pts.Length;
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = i > 0 ? pts[i - 1] : pts[i];
            Point p1 = pts[i];
            Point p2 = pts[i + 1];
            Point p3 = i + 2 < n ? pts[i + 2] : pts[i + 1];

            for (int s = 1; s <= subdivisions; s++)
            {
                double t = (double)s / subdivisions;
                double t2 = t * t;
                double t3 = t2 * t;

                double x = 0.5 * ((2 * p1.X) +
                    (-p0.X + p2.X) * t +
                    (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                    (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                double y = 0.5 * ((2 * p1.Y) +
                    (-p0.Y + p2.Y) * t +
                    (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                    (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);

                ctx.LineTo(new Point(x, y), true, false);
            }
        }
    }

    private static Color GetEqColorForGain(double gain)
    {
        float t = (float)Math.Clamp(gain / 12.0, -1.0, 1.0);

        if (t >= 0f)
        {
            byte r = (byte)(0x55 + (0xFF - 0x55) * t);
            byte g = (byte)(0x55 + (0xFF - 0x55) * t * 0.85f);
            byte b = (byte)(0x77 + (0xFF - 0x77) * t * 0.9f);
            return Color.FromRgb(r, g, b);
        }
        else
        {
            float at = -t;
            byte r = (byte)(0x55 + (0xEF - 0x55) * at);
            byte g = (byte)(0x55 - 0x20 * at);
            byte b = (byte)(0x77 - 0x47 * at);
            return Color.FromRgb(r, g, b);
        }
    }

    private static Color GetEqThumbColorForGain(double gain)
    {
        float t = (float)Math.Clamp(gain / 12.0, -1.0, 1.0);

        if (t >= 0f)
        {
            byte r = (byte)(0xE6 + (0xFF - 0xE6) * t * 0.5f);
            byte g = (byte)(0x7E + (0xDD - 0x7E) * t);
            byte b = (byte)(0x22 + (0xEE - 0x22) * t);
            return Color.FromRgb(r, g, b);
        }
        else
        {
            float at = -t;
            byte r = (byte)(0xE6 + (0xFF - 0xE6) * at * 0.3f);
            byte g = (byte)(0x7E - 0x50 * at);
            byte b = (byte)(0x22 - 0x10 * at);
            return Color.FromRgb(r, g, b);
        }
    }

    private void UpdateAllEqSliderColors()
    {
        for (int i = 0; i < 10; i++)
        {
            if (_eqSliders[i] != null)
                UpdateEqSliderColor(i);
        }
    }

    private void UpdateEqSliderColor(int index)
    {
        var slider = _eqSliders[index];
        if (slider == null) return;

        double gain = slider.Value;
        var fillColor = GetEqColorForGain(gain);
        var thumbColor = GetEqThumbColorForGain(gain);

        var fillBrush = new SolidColorBrush(fillColor);
        if (_eqFillBorders[index] is { } border)
            border.Background = fillBrush;
        _eqFillBrushes[index] = fillBrush;

        if (_eqThumbEllipses[index] is { } thumb)
            thumb.Fill = new SolidColorBrush(thumbColor);

        if (_eqValueLabels[index] is { } label)
            label.Foreground = new SolidColorBrush(fillColor);
    }
}
