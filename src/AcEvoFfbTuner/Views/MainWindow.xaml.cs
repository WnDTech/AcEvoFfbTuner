using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

public partial class MainWindow : Window
{
    // === PROFILER ===
    private const int PMax = 900;
    private const int PStatN = 300;
    private const float ProfWindowSeconds = 30f;
    private const float ProfSampleRateHz = 30f;

    private readonly float[] _pOut = new float[PMax];
    private readonly float[] _pRaw = new float[PMax];
    private readonly float[] _pCmp = new float[PMax];
    private readonly float[] _pSlp = new float[PMax];
    private readonly float[] _pDmp = new float[PMax];
    private readonly float[] _pDyn = new float[PMax];
    private readonly float[] _pStr = new float[PMax];
    private readonly float[] _pSpd = new float[PMax];
    private readonly float[] _pGas = new float[PMax];
    private readonly float[] _pBrk = new float[PMax];
    private int _pN;

    private readonly float[] _pStatBuf = new float[PStatN];
    private int _pStatIdx, _pStatFrames, _pStatClips;
    private float _pPkMz, _pPkFx, _pPkFy;
    private readonly List<string> _pCsv = new();

    private readonly SolidColorBrush _cOut = new(Color.FromRgb(0xFF, 0x45, 0x00));
    private readonly SolidColorBrush _cRaw = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private readonly SolidColorBrush _cCmp = new(Color.FromRgb(0x00, 0xBC, 0xD4));
    private readonly SolidColorBrush _cDmp = new(Color.FromRgb(0x9C, 0x27, 0xB0));
    private readonly SolidColorBrush _cSlp = new(Color.FromRgb(0xFF, 0x57, 0x22));
    private readonly SolidColorBrush _cDyn = new(Color.FromRgb(0x79, 0x55, 0x48));
    private readonly SolidColorBrush _cStr = new(Color.FromRgb(0xFF, 0xD6, 0x00));
    private readonly SolidColorBrush _cSpd = new(Color.FromRgb(0x55, 0x55, 0x55));
    private readonly SolidColorBrush _cGas = new(Color.FromRgb(0x4C, 0xAF, 0x50));
    private readonly SolidColorBrush _cBrk = new(Color.FromRgb(0xE5, 0x39, 0x35));

    private readonly Polyline _plOut = new() { StrokeThickness = 2 };
    private readonly Polyline _plRaw = new() { StrokeThickness = 1 };
    private readonly Polyline _plCmp = new() { StrokeThickness = 1 };
    private readonly Polyline _plDmp = new() { StrokeThickness = 1 };
    private readonly Polyline _plSlp = new() { StrokeThickness = 1 };
    private readonly Polyline _plDyn = new() { StrokeThickness = 1 };
    private readonly Polyline _plStr = new() { StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new[] { 3.0, 3.0 }) };
    private readonly Polyline _plSpd = new() { StrokeThickness = 1 };
    private readonly Polyline _plGas = new() { StrokeThickness = 1 };
    private readonly Polyline _plBrk = new() { StrokeThickness = 1 };
    private readonly Polyline _plPZero = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 };

    private readonly SolidColorBrush _profGridBrush = new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private readonly List<TextBlock> _timeLabels = new();

    private readonly SolidColorBrush _trackLineBrush = new(Color.FromRgb(0x00, 0xBC, 0xD4));
    private readonly SolidColorBrush _trackFillBrush = new(Color.FromArgb(0x18, 0x00, 0xBC, 0xD4));
    private readonly SolidColorBrush _trackStartBrush = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private readonly SolidColorBrush _carDotBrush = new(Color.FromRgb(0xFF, 0x45, 0x00));
    private readonly SolidColorBrush _carDotOffTrackBrush = new(Color.FromRgb(0xFF, 0x00, 0x00));
    private readonly Polyline _trackPolyline = new() { StrokeThickness = 2 };
    private readonly Polyline _trackFillPolyline = new() { StrokeThickness = 0 };
    private readonly Ellipse _carDot = new() { Width = 10, Height = 10 };
    private readonly Ellipse _startDot = new() { Width = 8, Height = 8 };
    private readonly Line _headingLine = new() { StrokeThickness = 2 };
    private readonly Polyline _recordingTrail = new() { StrokeThickness = 1 };
    private readonly SolidColorBrush _recordingTrailBrush = new(Color.FromArgb(0x80, 0xFF, 0x45, 0x00));

    private TrackMap? _displayedMap;
    private float _mapMinX, _mapMaxX, _mapMinZ, _mapMaxZ, _mapScale;
    private double _mapOffsetX, _mapOffsetY;
    private int _lastDisplayedWaypointCount;
    private int _lastDisplayedSectorCount;
    private bool _lastDisplayedPitDetected;
    private int _heatmapRedrawCounter;

    private readonly List<Point> _recordingWorldPts = new();

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

    public MainWindow()
    {
        InitializeComponent();

        DataContext = App.ViewModel;

        _plOut.Stroke = _cOut; _plRaw.Stroke = _cRaw; _plCmp.Stroke = _cCmp;
        _plDmp.Stroke = _cDmp; _plSlp.Stroke = _cSlp; _plDyn.Stroke = _cDyn;
        _plStr.Stroke = _cStr; _plSpd.Stroke = _cSpd;
        _plGas.Stroke = _cGas; _plBrk.Stroke = _cBrk;

        ProfilerCanvas.Children.Add(_plPZero);
        for (int i = 0; i < 4; i++)
            ProfilerCanvas.Children.Insert(0, new Polyline { Stroke = _profGridBrush, StrokeThickness = 1 });
        ProfilerCanvas.Children.Add(_plBrk);
        ProfilerCanvas.Children.Add(_plGas);
        ProfilerCanvas.Children.Add(_plSpd);
        ProfilerCanvas.Children.Add(_plStr);
        ProfilerCanvas.Children.Add(_plDyn);
        ProfilerCanvas.Children.Add(_plSlp);
        ProfilerCanvas.Children.Add(_plDmp);
        ProfilerCanvas.Children.Add(_plCmp);
        ProfilerCanvas.Children.Add(_plRaw);
        ProfilerCanvas.Children.Add(_plOut);

        for (int i = 0; i <= 6; i++)
        {
            var tb = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), FontSize = 9, FontFamily = new FontFamily("Consolas") };
            _timeLabels.Add(tb);
            ProfilerOverlayCanvas.Children.Add(tb);
        }

        _trackPolyline.Stroke = _trackLineBrush;
        _trackFillPolyline.Fill = _trackFillBrush;
        _carDot.Fill = _carDotBrush;
        _startDot.Fill = _trackStartBrush;
        _headingLine.Stroke = _carDotBrush;
        _recordingTrail.Stroke = _recordingTrailBrush;

        TrackMapCanvas.Children.Add(_trackFillPolyline);
        TrackMapCanvas.Children.Add(_trackPolyline);
        TrackMapCanvas.Children.Add(_recordingTrail);
        TrackMapCanvas.Children.Add(_startDot);
        TrackMapCanvas.Children.Add(_headingLine);
        TrackMapCanvas.Children.Add(_carDot);

        for (int i = 0; i < 10; i++)
        {
            _eqSliders[i] = (Slider)FindName($"EqSlider{i}");
            _eqFillBrushes[i] = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x77));
            _eqValueLabels[i] = FindName($"EqValueLabel{i}") as TextBlock;
        }

        Loaded += OnMainWindowLoaded;

        _eqZeroLine.Stroke = _eqZeroBrush;
        EqCurveCanvas.Children.Add(_eqZeroLine);
        EqCurveCanvas.Children.Add(_eqCurveFill);
        EqCurveCanvas.Children.Add(_eqCurvePath);

        EqSliderGrid.SizeChanged += (s, e) => UpdateEqCurve();
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _guideOverlay?.Close();
        _profilerOverlay?.Close();
        _trackMapPopout?.Close();
        Application.Current.Shutdown();
    }

    private void CopyDebugToClipboard(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
        {
            Clipboard.SetText(vm.DebugSnapshot);
            vm.StatusText = "Debug info copied to clipboard";
        }
    }

    private TestingGuideOverlay? _guideOverlay;
    private ProfilerOverlay? _profilerOverlay;
    private TrackMapPopout? _trackMapPopout;

    private void OpenGuideOverlay(object sender, RoutedEventArgs e)
    {
        if (_guideOverlay != null)
        {
            _guideOverlay.Activate();
            return;
        }

        _guideOverlay = new TestingGuideOverlay();
        _guideOverlay.Closed += (_, _) => _guideOverlay = null;
        _guideOverlay.Show();
    }

    private void OpenProfilerOverlay(object sender, RoutedEventArgs e)
    {
        if (_profilerOverlay != null)
        {
            _profilerOverlay.Activate();
            return;
        }

        _profilerOverlay = new ProfilerOverlay();
        _profilerOverlay.Closed += (_, _) => _profilerOverlay = null;
        _profilerOverlay.Show();
    }

    private void OpenTrackMapPopout(object sender, RoutedEventArgs e)
    {
        if (_trackMapPopout != null)
        {
            _trackMapPopout.Activate();
            return;
        }

        _trackMapPopout = new TrackMapPopout();
        _trackMapPopout.Closed += (_, _) => _trackMapPopout = null;
        _trackMapPopout.Show();
    }

    // === PROFILER METHODS ===

    public void UpdateProfiler(float speed, float steerAngle, float forceOut, float rawFF,
        float compress, float slip, float damping, float dynEff,
        float mzFront, float fxFront, float fyFront, float lut, bool clipping,
        float gasInput, float brakeInput)
    {
        ProfPush(_pOut, forceOut);
        ProfPush(_pRaw, rawFF);
        ProfPush(_pCmp, compress);
        ProfPush(_pSlp, slip);
        ProfPush(_pDmp, damping);
        ProfPush(_pDyn, dynEff);
        ProfPush(_pStr, steerAngle);
        ProfPush(_pSpd, speed / 400f);
        ProfPush(_pGas, gasInput);
        ProfPush(_pBrk, brakeInput);

        _pStatBuf[_pStatIdx % PStatN] = forceOut;
        _pStatIdx++;
        _pStatFrames++;
        if (clipping) _pStatClips++;

        float amz = Math.Abs(mzFront), afx = Math.Abs(fxFront), afy = Math.Abs(fyFront);
        if (amz > _pPkMz) _pPkMz = amz;
        if (afx > _pPkFx) _pPkFx = afx;
        if (afy > _pPkFy) _pPkFy = afy;

        ProfStats(speed, steerAngle, forceOut, rawFF, clipping, gasInput, brakeInput);
        RedrawProfilerTS();

        float[] barVals = { mzFront, fxFront, fyFront, compress, lut, slip, damping, dynEff, forceOut };
        RedrawProfilerBars(barVals);

        if (_profilerOverlay != null)
        {
            int sc = Math.Min(_pStatIdx, PStatN);
            float sMin = float.MaxValue, sMax = float.MinValue;
            for (int si = 0; si < sc; si++)
            {
                float sv = _pStatBuf[si];
                if (sv < sMin) sMin = sv;
                if (sv > sMax) sMax = sv;
            }
            float sClip = _pStatFrames > 0 ? (float)_pStatClips / _pStatFrames * 100f : 0f;
            _profilerOverlay.UpdateData(speed, forceOut, rawFF, steerAngle, gasInput, brakeInput, sClip, sc > 0 ? sMin : 0, sc > 0 ? sMax : 0);
        }

        _pCsv.Add($"{DateTime.Now:HH:mm:ss.fff},{speed:F1},{steerAngle:F4},{forceOut:F6},{rawFF:F6},{compress:F6},{lut:F6},{slip:F6},{damping:F6},{dynEff:F6},{mzFront:F6},{fxFront:F6},{fyFront:F6},{clipping},{gasInput:F3},{brakeInput:F3}");
    }

    private void ProfPush(float[] arr, float val)
    {
        if (_pN < PMax)
        {
            arr[_pN] = val;
        }
        else
        {
            Array.Copy(arr, 1, arr, 0, PMax - 1);
            arr[PMax - 1] = val;
        }
    }

    private void ProfStats(float speed, float steer, float force, float rawFF, bool clip, float gas, float brake)
    {
        if (_pN < PMax) _pN++;

        ProfSpeed.Text = $"{speed:F0} km/h";
        ProfSteer.Text = $"{steer * 450f:F1}°";
        ProfForce.Text = force.ToString("F3");
        ProfRawFF.Text = rawFF.ToString("F3");

        int count = Math.Min(_pStatIdx, PStatN);
        if (count > 0)
        {
            float min = float.MaxValue, max = float.MinValue, sum = 0;
            for (int i = 0; i < count; i++)
            {
                float v = _pStatBuf[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            ProfMinMax.Text = $"{min:F2} / {max:F2}";
            ProfAvg.Text = (sum / count).ToString("F3");
        }

        float clipPct = _pStatFrames > 0 ? (float)_pStatClips / _pStatFrames * 100f : 0f;
        ProfClipPct.Text = $"{clipPct:F1}%";
        ProfClipPct.Foreground = clipPct > 5f ? Brushes.Red : clipPct > 0f ? Brushes.Yellow : new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        ProfPeakChannels.Text = $"{_pPkMz:F3} / {_pPkFx:F3} / {_pPkFy:F3}";
        ProfGas.Text = $"{gas * 100f:F0}%";
        ProfBrake.Text = $"{brake * 100f:F0}%";

        float elapsedSec = _pN / ProfSampleRateHz;
        ProfTimeWindow.Text = $"{elapsedSec:F1}s / {ProfWindowSeconds:F0}s";
    }

    private void RedrawProfilerTS()
    {
        double w = ProfilerCanvas.ActualWidth;
        double h = ProfilerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double yc = h / 2;
        double ys = h / 2.2;

        _plPZero.Points = new PointCollection(new[] { new Point(0, yc), new Point(w, yc) });

        int gi = 0;
        foreach (var child in ProfilerCanvas.Children)
        {
            if (child is Polyline p && ReferenceEquals(p.Stroke, _profGridBrush))
            {
                double yf = (double)(gi + 1) / 4;
                p.Points = new PointCollection(new[] { new Point(0, yc - yf * ys), new Point(w, yc - yf * ys) });
                gi++;
            }
        }

        _plOut.Points = ShowOutput.IsChecked == true ? BuildPts(_pOut, _pN, w, yc, ys) : new PointCollection();
        _plRaw.Points = ShowRaw.IsChecked == true ? BuildPts(_pRaw, _pN, w, yc, ys) : new PointCollection();
        _plCmp.Points = ShowCompress.IsChecked == true ? BuildPts(_pCmp, _pN, w, yc, ys) : new PointCollection();
        _plDmp.Points = ShowDamping.IsChecked == true ? BuildPts(_pDmp, _pN, w, yc, ys) : new PointCollection();
        _plSlp.Points = ShowSlip.IsChecked == true ? BuildPts(_pSlp, _pN, w, yc, ys) : new PointCollection();
        _plDyn.Points = ShowDynamic.IsChecked == true ? BuildPts(_pDyn, _pN, w, yc, ys) : new PointCollection();
        _plStr.Points = ShowSteer.IsChecked == true ? BuildPts(_pStr, _pN, w, yc, ys) : new PointCollection();
        _plSpd.Points = ShowSpeed.IsChecked == true ? BuildPts(_pSpd, _pN, w, yc, ys) : new PointCollection();
        _plGas.Points = ShowGas.IsChecked == true ? BuildPts(_pGas, _pN, w, yc, ys) : new PointCollection();
        _plBrk.Points = ShowBrake.IsChecked == true ? BuildPts(_pBrk, _pN, w, yc, ys) : new PointCollection();

        DrawTimeAxis(w, h);
    }

    private void DrawTimeAxis(double w, double h)
    {
        if (_pN < 2) return;

        float totalSec = _pN / ProfSampleRateHz;
        float windowSec = Math.Min(totalSec, ProfWindowSeconds);
        float startSec = totalSec - windowSec;
        int labelCount = _timeLabels.Count;

        double stepSec = windowSec / (labelCount - 1);
        for (int i = 0; i < labelCount; i++)
        {
            double x = i * w / (labelCount - 1);
            float t = startSec + (float)(i * stepSec);
            _timeLabels[i].Text = $"-{ProfWindowSeconds - t:F0}s";
            Canvas.SetLeft(_timeLabels[i], x - 12);
            Canvas.SetTop(_timeLabels[i], h - 14);
        }
    }

    private PointCollection BuildPts(float[] data, int count, double w, double yc, double ys)
    {
        int n = Math.Min(count, PMax);
        int windowSamples = (int)(ProfWindowSeconds * ProfSampleRateHz);
        int displayN = Math.Min(n, windowSamples);
        int startIdx = n - displayN;
        var pts = new PointCollection(displayN);
        double xStep = displayN > 1 ? w / (displayN - 1) : 0;
        for (int i = 0; i < displayN; i++)
            pts.Add(new Point(i * xStep, yc - data[startIdx + i] * ys));
        return pts;
    }

    private void RedrawProfilerBars(float[] values)
    {
        float mz = Math.Abs(values[0]), fx = Math.Abs(values[1]), fy = Math.Abs(values[2]);

        double barMaxW = 80;

        BarMz.Width = Math.Max(Math.Min(mz / 1.0, 1.0) * barMaxW, 1);
        BarMzVal.Text = values[0].ToString("F3");
        BarFx.Width = Math.Max(Math.Min(fx / 1.0, 1.0) * barMaxW, 1);
        BarFxVal.Text = values[1].ToString("F3");
        BarFy.Width = Math.Max(Math.Min(fy / 1.0, 1.0) * barMaxW, 1);
        BarFyVal.Text = values[2].ToString("F3");
    }

    private void ProfilerSnapshot(object sender, RoutedEventArgs e)
    {
        string text = BuildAnalysisText();
        Clipboard.SetText(text);
        if (DataContext is ViewModels.MainViewModel vm)
            vm.StatusText = "Profiler snapshot copied to clipboard (includes graph data)";
    }

    public string AutoSaveSnapshot()
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner", "snapshots");
        Directory.CreateDirectory(dir);
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string path = System.IO.Path.Combine(dir, $"snapshot_{ts}.txt");
        string analysis = BuildAnalysisText();
        File.WriteAllText(path, analysis);
        try { GenerateReplayHtml(dir, ts, analysis); } catch { }
        System.Media.SystemSounds.Asterisk.Play();
        return path;
    }

    private void GenerateReplayHtml(string dir, string ts, string analysis)
    {
        var vm = DataContext as ViewModels.MainViewModel;
        string profileName = vm?.SelectedProfile?.Name ?? "Unknown";
        float torqueNm = vm?.WheelMaxTorqueNm ?? 5.5f;

        string csvSection = "";
        int csvStart = analysis.IndexOf("=== TIME SERIES DATA");
        if (csvStart >= 0)
        {
            int headerEnd = analysis.IndexOf('\n', csvStart);
            if (headerEnd >= 0)
            {
                int dataStart = analysis.IndexOf("Time,", headerEnd);
                if (dataStart >= 0)
                {
                    string header = "Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,Slip,Damping,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake\n";
                    csvSection = header + analysis.Substring(dataStart);
                }
            }
        }

        if (string.IsNullOrEmpty(csvSection)) return;

        string statsSection = "";
        int statsStart = analysis.IndexOf("=== PROFILER STATISTICS");
        if (statsStart >= 0)
        {
            int statsEnd = analysis.IndexOf("=== PROFILE RECOMMENDATIONS");
            if (statsEnd < 0) statsEnd = analysis.IndexOf("=== CURRENT PROFILE SETTINGS");
            if (statsEnd < 0) statsEnd = analysis.IndexOf("=== TIME SERIES DATA");
            if (statsEnd > statsStart)
                statsSection = analysis.Substring(statsStart, statsEnd - statsStart).Trim();
        }

        string html = Services.ReplayVisualizerService.GenerateHtml(csvSection, profileName, torqueNm, statsSection);
        string htmlPath = System.IO.Path.Combine(dir, $"replay_{ts}.html");
        File.WriteAllText(htmlPath, html);
    }

    private string BuildAnalysisText()
    {
        var sb = new System.Text.StringBuilder();
        var vm = DataContext as ViewModels.MainViewModel;
        var p = vm?.Pipeline;
        float torqueNm = vm?.WheelMaxTorqueNm ?? 5.5f;
        var prof = vm?.SelectedProfile;

        int count = Math.Min(_pStatIdx, PStatN);
        float min = float.MaxValue, max = float.MinValue, sum = 0;
        for (int i = 0; i < count; i++)
        {
            float v = _pStatBuf[i];
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }
        float avg = count > 0 ? sum / count : 0f;
        float clipPct = _pStatFrames > 0 ? (float)_pStatClips / _pStatFrames * 100f : 0f;
        int lastIdx = _pN > 0 ? Math.Min(_pN - 1, PMax - 1) : 0;

        sb.AppendLine($"=== FFB FULL ANALYSIS @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        sb.AppendLine($"Profile: {prof?.Name ?? "None"}");
        sb.AppendLine();

        sb.AppendLine($"=== PROFILER STATISTICS (last ~{count} frames) ===");
        sb.AppendLine($"OutputMin:          {min:F6}  ({min * torqueNm:F2} Nm)");
        sb.AppendLine($"OutputMax:          {max:F6}  ({max * torqueNm:F2} Nm)");
        sb.AppendLine($"OutputAvg:          {avg:F6}  ({avg * torqueNm:F2} Nm)");
        sb.AppendLine($"ClippingPct:        {clipPct:F1}% ({_pStatClips}/{_pStatFrames})");
        sb.AppendLine($"PeakMz/Fx/Fy:       {_pPkMz:F4} / {_pPkFx:F4} / {_pPkFy:F4}  ({_pPkMz * torqueNm:F1} / {_pPkFx * torqueNm:F1} / {_pPkFy * torqueNm:F1} Nm)");
        sb.AppendLine($"CurrentOutput:      {_pOut[lastIdx]:F4}");
        sb.AppendLine($"CurrentRawFF:       {_pRaw[lastIdx]:F4}");
        sb.AppendLine();

        sb.AppendLine($"=== PROFILE RECOMMENDATIONS ===");
        sb.AppendLine($"  MaxForceLimit:  {(max > 0.95f ? "REDUCE GAINS (clipping)" : max < 0.3f ? "INCREASE GAINS (weak signal)" : "OK (0.3-0.95 range)")}");
        sb.AppendLine($"  OutputGain:     {(avg < 0.1f ? "Try increasing" : avg > 0.8f ? "Try decreasing" : "OK")}");
        sb.AppendLine($"  MzScale:        {(_pPkMz > 0.5f ? "Consider increasing divisor" : _pPkMz < 0.01f ? "Consider decreasing divisor" : "OK")}");
        sb.AppendLine();

        sb.AppendLine($"=== CURRENT PROFILE SETTINGS ===");
        if (p != null)
        {
            sb.AppendLine($"MixMode:            {p.ChannelMixer.MixMode}");
            sb.AppendLine($"OutputGain:         {p.OutputGain:F2}");
            sb.AppendLine($"MasterGain:         {p.MasterGain:F4}");
            sb.AppendLine($"ForceScale:         {p.ForceScale:F2}");
            sb.AppendLine($"CompressionPower:   {p.CompressionPower:F2}");
            sb.AppendLine($"SignCorrection:     {p.SignCorrectionEnabled}");
            sb.AppendLine($"SoftClipThreshold:  {p.OutputClipper.SoftClipThreshold:F2}  ({p.OutputClipper.SoftClipThreshold * torqueNm:F1} Nm)");
            sb.AppendLine($"WheelMaxTorque:     {torqueNm:F1} Nm");
            sb.AppendLine();
            sb.AppendLine($"--- CHANNEL MIXER ---");
            sb.AppendLine($"MzFront:  Gain={p.ChannelMixer.MzFrontGain:F2}  Enabled={p.ChannelMixer.MzFrontEnabled}  Scale={p.ChannelMixer.MzScale:F0}");
            sb.AppendLine($"FxFront:  Gain={p.ChannelMixer.FxFrontGain:F2}  Enabled={p.ChannelMixer.FxFrontEnabled}  Scale={p.ChannelMixer.FxScale:F0}");
            sb.AppendLine($"FyFront:  Gain={p.ChannelMixer.FyFrontGain:F2}  Enabled={p.ChannelMixer.FyFrontEnabled}  Scale={p.ChannelMixer.FyScale:F0}");
            sb.AppendLine($"MzRear:   Gain={p.ChannelMixer.MzRearGain:F2}  Enabled={p.ChannelMixer.MzRearEnabled}");
            sb.AppendLine($"FxRear:   Gain={p.ChannelMixer.FxRearGain:F2}  Enabled={p.ChannelMixer.FxRearEnabled}");
            sb.AppendLine($"FyRear:   Gain={p.ChannelMixer.FyRearGain:F2}  Enabled={p.ChannelMixer.FyRearEnabled}");
            sb.AppendLine($"FinalFf:  Gain={p.ChannelMixer.FinalFfGain:F2}  Enabled={p.ChannelMixer.FinalFfEnabled}");
            sb.AppendLine($"WheelLoadWeighting: {p.ChannelMixer.WheelLoadWeighting:F2}");
            sb.AppendLine();
            sb.AppendLine($"--- DAMPING ---");
            sb.AppendLine($"SpeedDamping:       {p.Damping.SpeedDampingCoefficient:F2}");
            sb.AppendLine($"Friction:           {p.Damping.FrictionLevel:F2}");
            sb.AppendLine($"Inertia:            {p.Damping.InertiaWeight:F2}");
            sb.AppendLine($"MaxSpeedRef:        {p.Damping.MaxSpeedReference:F0}");
            sb.AppendLine($"LowSpeedBoost:      {p.Damping.LowSpeedDampingBoost:F1}");
            sb.AppendLine($"LowSpeedThreshold:  {p.Damping.LowSpeedThreshold:F0}");
            sb.AppendLine();
            sb.AppendLine($"--- SLIP ENHANCER ---");
            sb.AppendLine($"SlipRatioGain:      {p.SlipEnhancer.SlipRatioGain:F2}");
            sb.AppendLine($"SlipAngleGain:      {p.SlipEnhancer.SlipAngleGain:F2}");
            sb.AppendLine($"SlipThreshold:      {p.SlipEnhancer.SlipThreshold:F2}");
            sb.AppendLine($"UseFrontOnly:       {p.SlipEnhancer.UseFrontOnly}");
            sb.AppendLine();
            sb.AppendLine($"--- DYNAMIC EFFECTS ---");
            sb.AppendLine($"LateralG:           {p.DynamicEffects.LateralGGain:F2}");
            sb.AppendLine($"LongitudinalG:      {p.DynamicEffects.LongitudinalGGain:F2}");
            sb.AppendLine($"Suspension:         {p.DynamicEffects.SuspensionGain:F2}");
            sb.AppendLine($"YawRate:            {p.DynamicEffects.YawRateGain:F2}");
            sb.AppendLine();
            sb.AppendLine($"--- AUTO GAIN ---");
            sb.AppendLine($"Enabled:            {p.AutoGainEnabled}");
            sb.AppendLine($"Scale:              {p.AutoGainScale:F2}");
            sb.AppendLine();
            sb.AppendLine($"--- VIBRATIONS ---");
            sb.AppendLine($"Kerb:               {p.VibrationMixer.KerbGain:F2}");
            sb.AppendLine($"Slip:               {p.VibrationMixer.SlipGain:F2}");
            sb.AppendLine($"Road:               {p.VibrationMixer.RoadGain:F2}");
            sb.AppendLine($"ABS:                {p.VibrationMixer.AbsGain:F2}");
            sb.AppendLine($"Master:             {p.VibrationMixer.MasterGain:F2}");
            sb.AppendLine();
            sb.AppendLine($"--- PIPELINE INTERNALS ---");
            sb.AppendLine($"MaxSlewRate:        {p.MaxSlewRate:F3}");
            sb.AppendLine($"CenterDeadzone:     {p.CenterDeadzone:F3}");
            sb.AppendLine($"CenterSuppressionDeg: {p.CenterSuppressionDegrees:F1}");
            sb.AppendLine($"HysteresisThreshold: {p.HysteresisThreshold:F4}");
            sb.AppendLine($"NoiseFloor:         {p.NoiseFloor:F4}");
            sb.AppendLine($"SteerDirDeadzone:   {p.SteerDirDeadzone:F4}");
            sb.AppendLine($"CenterKneePower:    {p.CenterKneePower:F2}");
            sb.AppendLine($"GearShiftSmooth:    {p.GearShiftSmoothingTicks} ticks @ {p.GearShiftSlewRate:F3}");
        }
        sb.AppendLine();

        if (_pCsv.Count > 0)
        {
            sb.AppendLine($"=== TIME SERIES DATA ({_pCsv.Count} rows) ===");
            sb.AppendLine("Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,Slip,Damping,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake");
            foreach (var line in _pCsv)
                sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private void ProfilerExportAnalysis(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Analysis|*.txt",
            FileName = $"ffb_analysis_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, BuildAnalysisText());

        if (DataContext is ViewModels.MainViewModel vm)
            vm.StatusText = $"Analysis exported: {dlg.FileName}";
    }

    private void ProfilerExportCsv(object sender, RoutedEventArgs e)
    {
        if (_pCsv.Count == 0) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV|*.csv",
            FileName = $"ffb_profiler_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,Slip,Damping,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake");
        foreach (var line in _pCsv)
            sb.AppendLine(line);
        File.WriteAllText(dlg.FileName, sb.ToString());

        if (DataContext is ViewModels.MainViewModel vm)
            vm.StatusText = $"CSV exported: {dlg.FileName} ({_pCsv.Count} rows)";
    }

    private void ProfilerClear(object sender, RoutedEventArgs e)
    {
        Array.Clear(_pOut); Array.Clear(_pRaw); Array.Clear(_pCmp);
        Array.Clear(_pSlp); Array.Clear(_pDmp); Array.Clear(_pDyn);
        Array.Clear(_pStr); Array.Clear(_pSpd);
        Array.Clear(_pGas); Array.Clear(_pBrk);
        _pN = 0;
        Array.Clear(_pStatBuf);
        _pStatIdx = 0; _pStatFrames = 0; _pStatClips = 0;
        _pPkMz = 0; _pPkFx = 0; _pPkFy = 0;
        _pCsv.Clear();
        _profilerOverlay?.Clear();

        if (DataContext is ViewModels.MainViewModel vm)
            vm.StatusText = "Profiler data cleared";
    }

    public void UpdateTrackMapDisplay(float carX, float carZ, float heading, float speedKmh,
        bool isOnTrack, float trackProgress, float distanceFromCenter,
        float trackLengthM, int waypointCount, bool isRecording, bool hasMap,
        TrackMap? currentMap,
        WaypointForceSample[]? forceHeatmap = null,
        bool showHeatmap = false,
        bool showTrackEdges = false,
        WaypointDiagnosticSample[]? diagnosticHeatmap = null,
        bool showDiagnostics = false)
    {
        double w = TrackMapCanvas.ActualWidth;
        double h = TrackMapCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        TrackMapCarX.Text = carX.ToString("F1");
        TrackMapCarZ.Text = carZ.ToString("F1");
        TrackMapWaypoints.Text = waypointCount > 0 ? waypointCount.ToString() : "--";
        TrackMapProgress.Text = hasMap ? $"{trackProgress * 100f:F1}%" : "--";
        TrackMapDistance.Text = hasMap ? $"{distanceFromCenter:F1} m" : "-- m";

        if (Application.Current?.MainWindow is MainWindow self && self.DataContext is ViewModels.MainViewModel vm)
        {
            TrackMapCorner.Text = vm.CurrentCornerName;
            TrackMapSector.Text = vm.CurrentSectorNumber > 0 ? $"S{vm.CurrentSectorNumber}" : "--";
            TrackMapSectorStats.Text = vm.SectorStats;
        }

        if (hasMap && currentMap != null)
        {
            bool redraw = currentMap != _displayedMap
                || currentMap.Waypoints.Count != _lastDisplayedWaypointCount
                || currentMap.Sectors.Count != _lastDisplayedSectorCount
                || currentMap.PitLane.IsDetected != _lastDisplayedPitDetected;

            if (showHeatmap && forceHeatmap != null)
            {
                _heatmapRedrawCounter++;
                if (_heatmapRedrawCounter >= 30)
                {
                    _heatmapRedrawCounter = 0;
                    redraw = true;
                }
            }

            if (redraw)
            {
                _displayedMap = currentMap;
                _lastDisplayedWaypointCount = currentMap.Waypoints.Count;
                _lastDisplayedSectorCount = currentMap.Sectors.Count;
                _lastDisplayedPitDetected = currentMap.PitLane.IsDetected;
                ComputeMapTransform(currentMap, w, h);
                DrawTrackLine(currentMap, w, h, forceHeatmap, showHeatmap, showTrackEdges,
                    diagnosticHeatmap, showDiagnostics);
                _recordingWorldPts.Clear();
            }

            DrawCarOnMap(carX, carZ, heading, isOnTrack, w, h);
            _startDot.Visibility = Visibility.Visible;
            _recordingTrail.Points = new PointCollection();
        }
        else if (isRecording)
        {
            if (currentMap != _displayedMap)
            {
                _displayedMap = null;
                _lastDisplayedWaypointCount = 0;
            }

            if (speedKmh > 5f &&
                (_recordingWorldPts.Count == 0 ||
                Math.Abs(carX - (float)_recordingWorldPts[^1].X) > 0.5f ||
                Math.Abs(carZ - (float)_recordingWorldPts[^1].Y) > 0.5f))
            {
                _recordingWorldPts.Add(new Point(carX, carZ));
            }

            if (_recordingWorldPts.Count >= 2)
            {
                double padding = 40;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in _recordingWorldPts)
                {
                    if ((float)p.X < minX) minX = (float)p.X;
                    if ((float)p.X > maxX) maxX = (float)p.X;
                    if ((float)p.Y < minZ) minZ = (float)p.Y;
                    if ((float)p.Y > maxZ) maxZ = (float)p.Y;
                }
                float rangeX = maxX - minX;
                float rangeZ = maxZ - minZ;
                if (rangeX < 1f) rangeX = 1f;
                if (rangeZ < 1f) rangeZ = 1f;
                double scaleX = (w - padding * 2) / rangeX;
                double scaleZ = (h - padding * 2) / rangeZ;
                double scale = Math.Min(scaleX, scaleZ);
                double offX = (w - rangeX * scale) / 2;
                double offY = (h - rangeZ * scale) / 2;

                var displayPts = new PointCollection(_recordingWorldPts.Count);
                foreach (var p in _recordingWorldPts)
                    displayPts.Add(new Point((p.X - minX) * scale + offX, (p.Y - minZ) * scale + offY));
                _recordingTrail.Points = displayPts;

                float cx = (float)((carX - minX) * scale + offX);
                float cz = (float)((carZ - minZ) * scale + offY);
                DrawCarDot(cx, cz, heading, isOnTrack);
            }

            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
        }
        else if (!hasMap)
        {
            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
            _carDot.Visibility = Visibility.Collapsed;
            _headingLine.Visibility = Visibility.Collapsed;
            _recordingTrail.Points = new PointCollection();
            _recordingWorldPts.Clear();
        }

        if (_trackMapPopout != null)
        {
            _trackMapPopout.UpdateFromMainWindow(
                carX, carZ, heading, speedKmh,
                isOnTrack, trackProgress, distanceFromCenter,
                trackLengthM, waypointCount, isRecording, hasMap,
                currentMap, forceHeatmap, showHeatmap,
                showTrackEdges, diagnosticHeatmap, showDiagnostics);
        }
    }

    private void ComputeMapTransform(TrackMap map, double canvasW, double canvasH)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var wp in map.Waypoints)
        {
            if (wp.X < minX) minX = wp.X;
            if (wp.X > maxX) maxX = wp.X;
            if (wp.Z < minZ) minZ = wp.Z;
            if (wp.Z > maxZ) maxZ = wp.Z;
        }

        _mapMinX = minX; _mapMaxX = maxX;
        _mapMinZ = minZ; _mapMaxZ = maxZ;

        float rangeX = maxX - minX;
        float rangeZ = maxZ - minZ;
        if (rangeX < 1f) rangeX = 1f;
        if (rangeZ < 1f) rangeZ = 1f;

        double padding = 40;
        double scaleX = (canvasW - padding * 2) / rangeX;
        double scaleZ = (canvasH - padding * 2) / rangeZ;
        _mapScale = (float)Math.Min(scaleX, scaleZ);
        _mapOffsetX = (canvasW - rangeX * _mapScale) / 2;
        _mapOffsetY = (canvasH - rangeZ * _mapScale) / 2;
    }

    private void DrawTrackLine(TrackMap map, double canvasW, double canvasH,
        WaypointForceSample[]? forceHeatmap, bool showHeatmap, bool showTrackEdges,
        WaypointDiagnosticSample[]? diagnosticHeatmap = null, bool showDiagnostics = false)
    {
        _trackPolyline.Points = new PointCollection();
        _trackFillPolyline.Points = new PointCollection();

        if (showHeatmap && forceHeatmap != null && forceHeatmap.Length == map.Waypoints.Count)
        {
            var heatmapCanvas = TrackMapCanvas.Children.OfType<System.Windows.Shapes.Path>()
                .FirstOrDefault(p => p.Tag as string == "heatmap");
            if (heatmapCanvas == null)
            {
                heatmapCanvas = new System.Windows.Shapes.Path
                {
                    Tag = "heatmap",
                    StrokeThickness = 4
                };
                TrackMapCanvas.Children.Insert(TrackMapCanvas.Children.IndexOf(_trackPolyline), heatmapCanvas);
            }

            int step = Math.Max(1, map.Waypoints.Count / 500);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < map.Waypoints.Count; i += step)
                {
                    int next = (i + step) % map.Waypoints.Count;
                    if (next >= map.Waypoints.Count) next = map.Waypoints.Count - 1;

                    var p1 = MapToCanvas(map.Waypoints[i].X, map.Waypoints[i].Z);
                    var p2 = MapToCanvas(map.Waypoints[next].X, map.Waypoints[next].Z);

                    ctx.BeginFigure(p1, false, false);
                    ctx.LineTo(p2, true, false);
                }
            }
            heatmapCanvas.Data = geo;

            var brush = new LinearGradientBrush();
            int colorSteps = Math.Min(map.Waypoints.Count, 100);
            for (int i = 0; i < colorSteps; i++)
            {
                int idx = (int)((float)i / colorSteps * map.Waypoints.Count);
                var s = forceHeatmap[idx];
                Color c = ForceColor(s.OutputForce, s.IsClipping);
                brush.GradientStops.Add(new GradientStop(c, (double)i / colorSteps));
            }
            heatmapCanvas.Stroke = brush;
            heatmapCanvas.Visibility = Visibility.Visible;
            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
        }
        else
        {
            var heatmapEl = TrackMapCanvas.Children.OfType<System.Windows.Shapes.Path>()
                .FirstOrDefault(p => p.Tag as string == "heatmap");
            if (heatmapEl != null) heatmapEl.Visibility = Visibility.Collapsed;

            var pts = new PointCollection(map.Waypoints.Count + 1);
            foreach (var wp in map.Waypoints)
                pts.Add(MapToCanvas(wp.X, wp.Z));
            pts.Add(MapToCanvas(map.Waypoints[0].X, map.Waypoints[0].Z));
            _trackPolyline.Points = pts;
            _trackFillPolyline.Points = pts;
            _trackPolyline.Visibility = Visibility.Visible;
            _trackFillPolyline.Visibility = Visibility.Visible;
        }

        DrawTrackEdges(map, showTrackEdges);

        var startPt = MapToCanvas(map.Waypoints[0].X, map.Waypoints[0].Z);
        Canvas.SetLeft(_startDot, startPt.X - 4);
        Canvas.SetTop(_startDot, startPt.Y - 4);

        DrawCorners(map);
        DrawSectorBoundaries(map);
        DrawPitMarker(map);
        DrawDiagnosticMarkers(map, diagnosticHeatmap, showDiagnostics);
    }

    private void DrawSectorBoundaries(TrackMap map)
    {
        foreach (var child in TrackMapCanvas.Children.OfType<System.Windows.Shapes.Line>()
            .Where(l => l.Tag as string == "sector").ToList())
            TrackMapCanvas.Children.Remove(child);

        foreach (var child in TrackMapCanvas.Children.OfType<TextBlock>()
            .Where(t => t.Tag as string == "sector").ToList())
            TrackMapCanvas.Children.Remove(child);

        if (map.Sectors.Count <= 1) return;

        var cumDist = map.GetCumulativeDistances();
        float totalLen = map.TrackLengthM;
        if (totalLen < 1f) return;

        foreach (var sector in map.Sectors)
        {
            if (sector.StartWaypointIndex < 0 || sector.StartWaypointIndex >= map.Waypoints.Count)
                continue;

            var wp = map.Waypoints[sector.StartWaypointIndex];
            var pt = MapToCanvas(wp.X, wp.Z);

            int prev = sector.StartWaypointIndex > 0 ? sector.StartWaypointIndex - 1 : map.Waypoints.Count - 1;
            int next = (sector.StartWaypointIndex + 1) % map.Waypoints.Count;
            float tangX = map.Waypoints[next].X - map.Waypoints[prev].X;
            float tangZ = map.Waypoints[next].Z - map.Waypoints[prev].Z;
            float tangLen = MathF.Sqrt(tangX * tangX + tangZ * tangZ);
            if (tangLen > 0.001f)
            {
                tangX /= tangLen;
                tangZ /= tangLen;
            }
            float normX = -tangZ;
            float normZ = tangX;
            float lineHalfLen = 30f / _mapScale;
            var p1 = MapToCanvas(wp.X - normX * lineHalfLen, wp.Z - normZ * lineHalfLen);
            var p2 = MapToCanvas(wp.X + normX * lineHalfLen, wp.Z + normZ * lineHalfLen);

            var line = new System.Windows.Shapes.Line
            {
                X1 = p1.X, Y1 = p1.Y,
                X2 = p2.X, Y2 = p2.Y,
                StrokeThickness = 1.5,
                Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0xBC, 0xD4)),
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Tag = "sector"
            };
            TrackMapCanvas.Children.Add(line);

            var label = new TextBlock
            {
                Text = $"S{sector.SectorNumber}",
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0xBC, 0xD4)),
                Tag = "sector"
            };
            Canvas.SetLeft(label, p1.X + 2);
            Canvas.SetTop(label, p1.Y - 10);
            TrackMapCanvas.Children.Add(label);
        }
    }

    private void DrawPitMarker(TrackMap map)
    {
        foreach (var child in TrackMapCanvas.Children.OfType<Ellipse>()
            .Where(e => e.Tag as string == "pit").ToList())
            TrackMapCanvas.Children.Remove(child);

        foreach (var child in TrackMapCanvas.Children.OfType<System.Windows.Shapes.Line>()
            .Where(l => l.Tag as string == "pit").ToList())
            TrackMapCanvas.Children.Remove(child);

        if (!map.PitLane.IsDetected) return;

        if (map.PitLane.EntryWaypointIndex >= 0 && map.PitLane.EntryWaypointIndex < map.Waypoints.Count)
        {
            var entryWp = map.Waypoints[map.PitLane.EntryWaypointIndex];
            var pt = MapToCanvas(entryWp.X, entryWp.Z);

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)),
                Tag = "pit"
            };
            Canvas.SetLeft(dot, pt.X - 4);
            Canvas.SetTop(dot, pt.Y - 4);
            TrackMapCanvas.Children.Add(dot);
        }

        if (map.PitLane.ExitWaypointIndex >= 0 && map.PitLane.ExitWaypointIndex < map.Waypoints.Count
            && map.PitLane.ExitWaypointIndex != map.PitLane.EntryWaypointIndex)
        {
            var exitWp = map.Waypoints[map.PitLane.ExitWaypointIndex];
            var pt = MapToCanvas(exitWp.X, exitWp.Z);

            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD6, 0x00)),
                Tag = "pit"
            };
            Canvas.SetLeft(dot, pt.X - 4);
            Canvas.SetTop(dot, pt.Y - 4);
            TrackMapCanvas.Children.Add(dot);
        }
    }

    private void DrawTrackEdges(TrackMap map, bool showTrackEdges)
    {
        foreach (var child in TrackMapCanvas.Children.OfType<Polyline>()
            .Where(p => p.Tag as string == "edge").ToList())
            TrackMapCanvas.Children.Remove(child);

        if (!showTrackEdges || map.TrackEdges.Count != map.Waypoints.Count) return;

        int step = Math.Max(1, map.Waypoints.Count / 300);

        var leftPts = new PointCollection();
        var rightPts = new PointCollection();

        for (int i = 0; i < map.Waypoints.Count; i += step)
        {
            var edge = map.TrackEdges[i];
            if (edge.LeftX != 0 || edge.LeftZ != 0)
                leftPts.Add(MapToCanvas(edge.LeftX, edge.LeftZ));
            if (edge.RightX != 0 || edge.RightZ != 0)
                rightPts.Add(MapToCanvas(edge.RightX, edge.RightZ));
        }

        if (leftPts.Count > 1)
        {
            var leftLine = new Polyline
            {
                Points = leftPts,
                StrokeThickness = 1,
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Tag = "edge"
            };
            TrackMapCanvas.Children.Add(leftLine);
        }

        if (rightPts.Count > 1)
        {
            var rightLine = new Polyline
            {
                Points = rightPts,
                StrokeThickness = 1,
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                Tag = "edge"
            };
            TrackMapCanvas.Children.Add(rightLine);
        }
    }

    private void DrawDiagnosticMarkers(TrackMap map,
        WaypointDiagnosticSample[]? diagnosticHeatmap, bool showDiagnostics)
    {
        foreach (var child in TrackMapCanvas.Children.OfType<Ellipse>()
            .Where(e => e.Tag as string == "diag").ToList())
            TrackMapCanvas.Children.Remove(child);

        if (!showDiagnostics || diagnosticHeatmap == null || diagnosticHeatmap.Length != map.Waypoints.Count)
            return;

        int step = Math.Max(1, map.Waypoints.Count / 200);

        for (int i = 0; i < map.Waypoints.Count; i += step)
        {
            var d = diagnosticHeatmap[i];
            if (d.TotalEventCount == 0) continue;

            var wp = map.Waypoints[i];
            var pt = MapToCanvas(wp.X, wp.Z);

            Color markerColor;
            double size;

            if (d.SuspiciousCount > 0)
            {
                markerColor = Color.FromRgb(0xFF, 0x00, 0x00);
                size = Math.Min(4 + d.SuspiciousCount, 10);
            }
            else if (d.ExpectedCount > 0)
            {
                markerColor = Color.FromRgb(0x00, 0xE6, 0x76);
                size = Math.Min(3 + d.ExpectedCount * 0.5, 8);
            }
            else
            {
                markerColor = Color.FromRgb(0xFF, 0xD6, 0x00);
                size = 3;
            }

            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(markerColor),
                Tag = "diag"
            };
            Canvas.SetLeft(dot, pt.X - size / 2);
            Canvas.SetTop(dot, pt.Y - size / 2);
            TrackMapCanvas.Children.Add(dot);
        }
    }

    private static Color ForceColor(float force, bool isClipping)
    {
        float absF = MathF.Abs(force);
        if (isClipping || absF > 0.95f)
            return Color.FromRgb(0xFF, 0x00, 0x00);
        if (absF > 0.3f)
        {
            float t = (absF - 0.3f) / 0.65f;
            byte g = (byte)(0xE6 * (1f - t));
            return Color.FromRgb((byte)(0x00 + 0xFF * t), g, 0x00);
        }
        float t2 = absF / 0.3f;
        return Color.FromRgb(0x00, (byte)(0x99 * t2), (byte)(0xFF * (1f - t2)));
    }

    private void DrawCorners(TrackMap map)
    {
        foreach (var child in TrackMapCanvas.Children.OfType<TextBlock>()
            .Where(t => t.Tag as string == "corner").ToList())
            TrackMapCanvas.Children.Remove(child);

        foreach (var corner in map.Corners)
        {
            if (corner.ApexWaypointIndex < map.Waypoints.Count)
            {
                var apex = map.Waypoints[corner.ApexWaypointIndex];
                var pt = MapToCanvas(apex.X, apex.Z);

                var label = new TextBlock
                {
                    Text = corner.DisplayName,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)),
                    Tag = "corner"
                };
                Canvas.SetLeft(label, pt.X + 6);
                Canvas.SetTop(label, pt.Y - 6);
                TrackMapCanvas.Children.Add(label);
            }
        }
    }

    private void DrawCarOnMap(float carX, float carZ, float heading, bool isOnTrack, double canvasW, double canvasH)
    {
        var pt = MapToCanvas(carX, carZ);
        DrawCarDot((float)pt.X, (float)pt.Y, heading, isOnTrack);
    }

    private void DrawCarDot(float screenX, float screenZ, float heading, bool isOnTrack)
    {
        _carDot.Fill = isOnTrack ? _carDotBrush : _carDotOffTrackBrush;
        Canvas.SetLeft(_carDot, screenX - 5);
        Canvas.SetTop(_carDot, screenZ - 5);
        _carDot.Visibility = Visibility.Visible;

        float lineLen = 20f;
        float endX = screenX + MathF.Sin(heading) * lineLen;
        float endZ = screenZ - MathF.Cos(heading) * lineLen;
        _headingLine.X1 = screenX;
        _headingLine.Y1 = screenZ;
        _headingLine.X2 = endX;
        _headingLine.Y2 = endZ;
        _headingLine.Stroke = isOnTrack ? _carDotBrush : _carDotOffTrackBrush;
        _headingLine.Visibility = Visibility.Visible;
    }

    private Point MapToCanvas(float worldX, float worldZ)
    {
        return new Point(
            (worldX - _mapMinX) * _mapScale + _mapOffsetX,
            (worldZ - _mapMinZ) * _mapScale + _mapOffsetY);
    }

    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://paypal.me/willndad") { UseShellExecute = true });
    }

    private void OnWhatsNewClick(object sender, RoutedEventArgs e)
    {
        var dialog = new WhatsNewDialog { Owner = this, ShowAllEntries = true };
        dialog.ShowDialog();
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
