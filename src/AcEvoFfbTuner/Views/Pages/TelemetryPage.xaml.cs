using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class TelemetryPage : UserControl
{
    private const int PMax = 900;
    private const int PStatN = 300;
    private const float ProfWindowSeconds = 30f;
    private const float ProfSampleRateHz = 30f;

    private static readonly string[] ProColumns = [
        "Time","SpeedKmh","SteerAngle","ForceOut","RawFF","Compress","LUT","PostDamp","GainOut","Dynamic",
        "MzFront","FxFront","FyFront","Clipping","Gas","Brake",
        "SuspFL","SuspFR","SuspRL","SuspRR",
        "LoadFL","LoadFR","LoadRL","LoadRR",
        "SlipAFL","SlipAFR","SlipARL","SlipARR",
        "NormalXFL","NormalXFR","NormalXRL","NormalXRR",
        "NormalZFL","NormalZFR","NormalZRL","NormalZRR",
        "CpYFL","CpYFR",
        "TireGripFL","TireGripFR","TireGripRL","TireGripRR",
        "KerbVib","RoadVib","Wetness"
    ];

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
    private float _pPkOutMin, _pPkOutMax;
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

    private ProfilerOverlay? _profilerOverlay;

    public event EventHandler? ProfilerOverlayRequested;

    public TelemetryPage()
    {
        InitializeComponent();

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
    }

    private int _steeringLockDeg = 900;

    public void UpdateProfiler(float speed, float steerAngle, float forceOut, float rawFF,
        float compress, float postDampCore, float postGainCore, float dynEff,
        float mzFront, float fxFront, float fyFront, float lut, bool clipping,
        float gasInput, float brakeInput, FfbRawData? rawPhysics = null, float wetness = 0f,
        int steeringLockDeg = 900)
    {
        if (steeringLockDeg > 90 && steeringLockDeg <= 1440)
            _steeringLockDeg = steeringLockDeg;

        ProfPush(_pOut, forceOut);
        ProfPush(_pRaw, rawFF);
        ProfPush(_pCmp, compress);
        ProfPush(_pSlp, postDampCore);
        ProfPush(_pDmp, postGainCore);
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

        float absOut = Math.Abs(forceOut);
        if (_pStatFrames == 1 || absOut < _pPkOutMin) _pPkOutMin = absOut;
        if (absOut > _pPkOutMax) _pPkOutMax = absOut;

        ProfStats(speed, steerAngle, forceOut, rawFF, clipping, gasInput, brakeInput);
        RedrawProfilerTS();

        float[] barVals = { mzFront, fxFront, fyFront, compress, lut, postDampCore, postGainCore, dynEff, forceOut };
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

        if (rawPhysics != null)
        {
            _pCsv.Add($"{DateTime.Now:HH:mm:ss.fff},{speed:F1},{steerAngle:F4},{forceOut:F6},{rawFF:F6},{compress:F6},{lut:F6},{postDampCore:F6},{postGainCore:F6},{dynEff:F6},{mzFront:F6},{fxFront:F6},{fyFront:F6},{clipping},{gasInput:F3},{brakeInput:F3},{rawPhysics.SuspensionTravel[0]:F6},{rawPhysics.SuspensionTravel[1]:F6},{rawPhysics.SuspensionTravel[2]:F6},{rawPhysics.SuspensionTravel[3]:F6},{rawPhysics.WheelLoad[0]:F2},{rawPhysics.WheelLoad[1]:F2},{rawPhysics.WheelLoad[2]:F2},{rawPhysics.WheelLoad[3]:F2},{rawPhysics.SlipAngle[0]:F6},{rawPhysics.SlipAngle[1]:F6},{rawPhysics.SlipAngle[2]:F6},{rawPhysics.SlipAngle[3]:F6},{rawPhysics.TyreContactNormalX[0]:F6},{rawPhysics.TyreContactNormalX[1]:F6},{rawPhysics.TyreContactNormalX[2]:F6},{rawPhysics.TyreContactNormalX[3]:F6},{rawPhysics.TyreContactNormalZ[0]:F6},{rawPhysics.TyreContactNormalZ[1]:F6},{rawPhysics.TyreContactNormalZ[2]:F6},{rawPhysics.TyreContactNormalZ[3]:F6},{rawPhysics.TyreContactPointY[0]:F6},{rawPhysics.TyreContactPointY[1]:F6},{rawPhysics.TyreGrip[0]:F3},{rawPhysics.TyreGrip[1]:F3},{rawPhysics.TyreGrip[2]:F3},{rawPhysics.TyreGrip[3]:F3},{rawPhysics.KerbVibration:F6},{rawPhysics.RoadVibrations:F6},{wetness:F4}");
        }
        else
        {
            _pCsv.Add($"{DateTime.Now:HH:mm:ss.fff},{speed:F1},{steerAngle:F4},{forceOut:F6},{rawFF:F6},{compress:F6},{lut:F6},{postDampCore:F6},{postGainCore:F6},{dynEff:F6},{mzFront:F6},{fxFront:F6},{fyFront:F6},{clipping},{gasInput:F3},{brakeInput:F3},0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,{wetness:F4}");
        }
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
        ProfSteer.Text = $"{steer * (_steeringLockDeg / 2f):F1}°";
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
            ProfMinMax.Text = $"{_pPkOutMin:F2} / {_pPkOutMax:F2}";
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
                    string header = "Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,PostDamp,GainOut,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake\n";
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
        sb.AppendLine($"TyreCompoundFront: {vm?.CurrentTyreCompoundFront ?? ""}  Category: {vm?.CurrentTyreCategoryName ?? "Unknown"}");
        sb.AppendLine($"TyreCompoundRear:  {vm?.CurrentTyreCompoundRear ?? ""}");
        sb.AppendLine($"WetnessFactor:     {vm?.WetWeatherCurrentFactor ?? 0f:F3}");
        sb.AppendLine();

        sb.AppendLine($"=== PROFILER STATISTICS (last ~{count} frames, global peak across {_pStatFrames}) ===");
        sb.AppendLine($"OutputMin:          {_pPkOutMin:F6}  ({_pPkOutMin * torqueNm:F2} Nm)");
        sb.AppendLine($"OutputMax:          {_pPkOutMax:F6}  ({_pPkOutMax * torqueNm:F2} Nm)");
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
            sb.AppendLine();
            sb.AppendLine($"--- AUTO-NORMALIZATION (adaptive channel scaling) ---");
            var autoNorm = p.ChannelMixer.GetAutoNormDiagnostics();
            sb.AppendLine($"MzPeak:             {autoNorm.MzPeak:F2}  (auto: {autoNorm.MzAutoActive})  manual={autoNorm.ManualMzScale:F0}  effective={autoNorm.EffectiveMzScale:F1}");
            sb.AppendLine($"FxPeak:             {autoNorm.FxPeak:F2}  (auto: {autoNorm.FxAutoActive})  manual={autoNorm.ManualFxScale:F0}  effective={autoNorm.EffectiveFxScale:F1}");
            sb.AppendLine($"FyPeak:             {autoNorm.FyPeak:F2}  (auto: {autoNorm.FyAutoActive})  manual={autoNorm.ManualFyScale:F0}  effective={autoNorm.EffectiveFyScale:F1}");
            sb.AppendLine($"DampingFloors:      Viscous={Math.Max(p.Damping.ViscousCoefficient, 0.04f):F3}  Friction={Math.Max(p.Damping.FrictionLevel, 0.02f):F3}");
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
        }
        sb.AppendLine();

        if (_pCsv.Count > 0)
        {
            sb.AppendLine($"=== TIME SERIES DATA ({_pCsv.Count} rows) ===");
            sb.AppendLine(string.Join(",", ProColumns));
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
        sb.AppendLine("Time,SpeedKmh,SteerAngle,ForceOut,RawFF,Compress,LUT,Slip,Damping,Dynamic,MzFront,FxFront,FyFront,Clipping,Gas,Brake,SuspFL,SuspFR,SuspRL,SuspRR,LoadFL,LoadFR,LoadRL,LoadRR,SlipAFL,SlipAFR,SlipARL,SlipARR,NormalXFL,NormalXFR,NormalXRL,NormalXRR,NormalZFL,NormalZFR,NormalZRL,NormalZRR,CpYFL,CpYFR,KerbVib,RoadVib,Wetness");
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
        _pPkOutMin = 0; _pPkOutMax = 0;
        _pCsv.Clear();
        _profilerOverlay?.Clear();

        if (DataContext is ViewModels.MainViewModel vm)
            vm.StatusText = "Profiler data cleared";
    }

    private void OpenProfilerOverlay(object sender, RoutedEventArgs e)
    {
        ProfilerOverlayRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetProfilerOverlay(ProfilerOverlay? overlay)
    {
        _profilerOverlay = overlay;
    }

    public ProfilerOverlay? GetProfilerOverlay()
    {
        return _profilerOverlay;
    }
}
