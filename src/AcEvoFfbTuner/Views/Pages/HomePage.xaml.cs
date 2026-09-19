using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public sealed partial class HomePage : UserControl
{
    public event EventHandler? SetupWizardRequested;

    // G-G diagram: 160px canvas, centre 80,80. Outer ring auto-scales
    // 2G -> 3G -> 5G based on measured load; inner ring is always half scale.
    private const double GCanvasSize = 160.0;
    private const double GCenter = 80.0;
    private const double GForceDotHalf = 6.0;
    private const int GTrailLength = 40;

    private double _gScaleMax = 2.0;
    private int _gScaleCooldown;

    private static readonly Brush ForceNormalBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    private static readonly Brush ForceWarningBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26));
    private static readonly Brush ForceClipBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));

    private static readonly Brush GCalmBrush = new SolidColorBrush(Color.FromRgb(0x79, 0xC0, 0xFF));
    private static readonly Brush GMidBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00));
    private static readonly Brush GHighBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x72));

    // Trail stores raw G values so points rescale correctly when the range steps up/down.
    private readonly Queue<(float Lat, float Lon)> _gTrail = new();
    private MainViewModel? _vm;

    // Peak-hold state (normalized 0..1 + side).
    private double _peakMag;
    private int _peakSide;

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            Attach(vm);
        DataContextChanged += OnDataContextChanged;
        ForceTrack.SizeChanged += OnTrackSizeChanged;
        SteerTrack.SizeChanged += OnTrackSizeChanged;
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach();
        DataContextChanged -= OnDataContextChanged;
        ForceTrack.SizeChanged -= OnTrackSizeChanged;
        SteerTrack.SizeChanged -= OnTrackSizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is MainViewModel vm)
            Attach(vm);
        RefreshAll();
    }

    private void Attach(MainViewModel vm)
    {
        _vm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void Detach()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = null;
    }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        if (_vm == null) return;
        UpdateGForce(_vm.LatG, _vm.LongG, pushTrail: false);
        UpdateForceBar(_vm.CurrentForceOutput, _vm.IsClipping);
        UpdateSteerMarker(_vm.SteerAngle, _vm.SteerNormalized);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName is nameof(MainViewModel.CurrentForceOutput) or nameof(MainViewModel.IsClipping))
            UpdateForceBar(_vm.CurrentForceOutput, _vm.IsClipping);
        else if (e.PropertyName is nameof(MainViewModel.LatG) or nameof(MainViewModel.LongG))
            UpdateGForce(_vm.LatG, _vm.LongG, pushTrail: true);
        else if (e.PropertyName is nameof(MainViewModel.SteerAngle) or nameof(MainViewModel.SteerNormalized))
            UpdateSteerMarker(_vm.SteerAngle, _vm.SteerNormalized);
    }

    // ── Force: bidirectional from centre, peak-hold tick ─────────────────────
    private void UpdateForceBar(float force, bool isClipping)
    {
        double trackWidth = ForceTrack.ActualWidth;
        if (trackWidth <= 0) trackWidth = ForceTrack.Width > 0 ? ForceTrack.Width : 220;
        double centerX = trackWidth / 2.0;
        double halfAvail = Math.Max(0, centerX - 2);

        double mag = Math.Clamp(Math.Abs(force), 0, 1);
        int side = force > 0.02f ? -1 : force < -0.02f ? 1 : 0; // +force pulls left

        // Peak-hold: adopt new peaks instantly, decay slowly otherwise.
        if (mag >= _peakMag - 0.001)
        {
            _peakMag = mag;
            _peakSide = side;
        }
        else
        {
            _peakMag = Math.Max(mag, _peakMag - 0.008);
            if (_peakMag <= 0.005) _peakSide = 0;
        }

        Canvas.SetLeft(ForceCenterTick, centerX - 1);
        Canvas.SetTop(ForceCenterTick, 0);

        Brush fill = isClipping || mag >= 0.95 ? ForceClipBrush
            : mag < 0.7 ? ForceNormalBrush : ForceWarningBrush;

        double barW = mag * halfAvail;
        if (side < 0)
        {
            ForceBarLeft.Visibility = Visibility.Visible;
            ForceBarRight.Visibility = Visibility.Collapsed;
            ForceBarLeft.Width = barW;
            ForceBarLeft.Background = fill;
            Canvas.SetLeft(ForceBarLeft, centerX - barW);
            Canvas.SetTop(ForceBarLeft, 3);
        }
        else if (side > 0)
        {
            ForceBarRight.Visibility = Visibility.Visible;
            ForceBarLeft.Visibility = Visibility.Collapsed;
            ForceBarRight.Width = barW;
            ForceBarRight.Background = fill;
            Canvas.SetLeft(ForceBarRight, centerX);
            Canvas.SetTop(ForceBarRight, 3);
        }
        else
        {
            ForceBarLeft.Visibility = Visibility.Collapsed;
            ForceBarRight.Visibility = Visibility.Collapsed;
        }

        if (_peakMag > 0.02 && _peakSide != 0)
        {
            double px = centerX + _peakSide * _peakMag * halfAvail;
            var tick = _peakSide < 0 ? ForcePeakLeft : ForcePeakRight;
            var hide = _peakSide < 0 ? ForcePeakRight : ForcePeakLeft;
            tick.Visibility = Visibility.Visible;
            hide.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(tick, px - 1);
            Canvas.SetTop(tick, 1);
        }
        else
        {
            ForcePeakLeft.Visibility = Visibility.Collapsed;
            ForcePeakRight.Visibility = Visibility.Collapsed;
        }
    }

    // ── G-G diagram: auto-ranging dot + fading trail ─────────────────────────
    private void UpdateGForce(float latG, float longG, bool pushTrail)
    {
        double total = Math.Sqrt(latG * latG + (double)longG * longG);
        UpdateGScale(total);

        double pxPerG = (GCanvasSize / 2.0) / _gScaleMax;
        double x = Math.Clamp(latG, -(float)_gScaleMax, (float)_gScaleMax) * pxPerG;
        double y = Math.Clamp(-longG, -(float)_gScaleMax, (float)_gScaleMax) * pxPerG;

        double dotX = GCenter + x - GForceDotHalf;
        double dotY = GCenter + y - GForceDotHalf;
        Canvas.SetLeft(GForceDot, dotX);
        Canvas.SetTop(GForceDot, dotY);

        // Colour relative to current scale so thresholds stay meaningful at 3G/5G.
        double warnAt = _gScaleMax * 0.45;
        double highAt = _gScaleMax * 0.75;
        Brush b = total < warnAt ? GCalmBrush : total < highAt ? GMidBrush : GHighBrush;
        GForceDot.Fill = b;
        if (GForceDot.Effect is DropShadowEffect glow && b is SolidColorBrush scb)
            glow.Color = scb.Color;

        if (pushTrail)
        {
            _gTrail.Enqueue((latG, longG));
            while (_gTrail.Count > GTrailLength) _gTrail.Dequeue();
        }
        GTrail.Points.Clear();
        foreach (var (tLat, tLon) in _gTrail)
        {
            double tx = GCenter + Math.Clamp(tLat, -(float)_gScaleMax, (float)_gScaleMax) * pxPerG;
            double ty = GCenter + Math.Clamp(-tLon, -(float)_gScaleMax, (float)_gScaleMax) * pxPerG;
            GTrail.Points.Add(new Point(tx, ty));
        }
    }

    private void UpdateGScale(double total)
    {
        double required = total <= 1.9 ? 2.0 : total <= 2.8 ? 3.0 : 5.0;
        if (required > _gScaleMax)
        {
            _gScaleMax = required;
            _gScaleCooldown = 180; // ~3 s at 60 Hz before allowing a step back down
            UpdateGScaleLabels();
        }
        else if (required < _gScaleMax)
        {
            if (_gScaleCooldown > 0)
                _gScaleCooldown--;
            else
            {
                _gScaleMax = required;
                UpdateGScaleLabels();
            }
        }
        else if (_gScaleCooldown > 0)
        {
            _gScaleCooldown--;
        }
    }

    private void UpdateGScaleLabels()
    {
        GOuterLabel.Text = $"{_gScaleMax:0}G";
        double inner = _gScaleMax / 2.0;
        GInnerLabel.Text = inner % 1 == 0 ? $"{inner:0}G" : $"{inner:0.0}G";
    }

    // ── Steering lock position ───────────────────────────────────────────────
    // Position comes from normalized telemetry (±1 = the game's real lock),
    // so the marker reaches both ends at full deflection even when the
    // profile lock doesn't match the car. Degrees only feed the centre snap.
    private void UpdateSteerMarker(float steerAngleDeg, float normFrac)
    {
        double trackWidth = SteerTrack.ActualWidth;
        if (trackWidth <= 0) trackWidth = 200;
        double centerX = trackWidth / 2.0;

        Canvas.SetLeft(SteerCenterTick, centerX - 1);
        Canvas.SetTop(SteerCenterTick, 0);

        // Snap to centre inside the straight-ahead deadband so the marker
        // never looks off-centre when the readout says 0° / "–".
        if (Math.Abs(steerAngleDeg) < 2f) normFrac = 0f;
        double frac = Math.Clamp(normFrac, -1, 1);
        double x = centerX + frac * (centerX - 7) - 5;
        Canvas.SetLeft(SteerMarker, x);
        Canvas.SetTop(SteerMarker, 0);
    }

    private void OnLaunchSetupWizard(object sender, RoutedEventArgs e)
    {
        SetupWizardRequested?.Invoke(this, EventArgs.Empty);
    }
}
