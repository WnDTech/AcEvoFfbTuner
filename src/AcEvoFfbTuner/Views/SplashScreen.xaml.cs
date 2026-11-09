using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AcEvoFfbTuner.Views;

public partial class SplashScreen : Window
{
    private static readonly string[] SplashWheelUris =
    {
        "pack://application:,,,/Resources/splash-wheels/FanCSLElite.png",
        "pack://application:,,,/Resources/splash-wheels/G27.png",
        "pack://application:,,,/Resources/splash-wheels/GPro.png",
        "pack://application:,,,/Resources/splash-wheels/MOZA-KS-PRO_1.png",
    };
    // One vsync-locked frame callback replaces two free-running 16 ms timers
    // (which drifted against the display and doubled the per-frame work).
    private bool _framesHooked;
    private bool _spinning = true;
    private double _lastFrameSec;
    private readonly MediaPlayer _mediaPlayer;
    private readonly string? _customSoundPath;
    private string? _tempAudioFile;
    private double _angle;
    private readonly Stopwatch _spinStopwatch = new();

    public SplashScreen(string? customSoundPath = null, ResourceDictionary? theme = null)
    {
        // Theme loaded on THIS (splash) thread — app resources belong to the main thread.
        if (theme != null) Resources.MergedDictionaries.Add(theme);
        InitializeComponent();

        // Source PNGs are up to 1160 px; decode at display size (x2 for high DPI)
        // instead of resampling a full-size bitmap every frame.
        var idx = Random.Shared.Next(SplashWheelUris.Length);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(SplashWheelUris[idx]);
        bmp.DecodePixelWidth = 400;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        WheelImage.Source = bmp;

        LoadVersionInfo();

        _customSoundPath = customSoundPath;
        _mediaPlayer = new MediaPlayer();


        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void LoadVersionInfo()
    {
        try
        {
            // Beta builds show their full tag ("1.29.0-beta.1").
            var version = Services.BuildInfo.IsBeta
                ? Services.BuildInfo.ReleaseTag
                : Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            VersionText.Text = $"v{version}";

            var commit = GitHash.Commit;
            if (!string.IsNullOrEmpty(commit) && commit != "dev")
                CommitText.Text = commit;
            else
                CommitText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            VersionText.Text = "v0.0.0";
            CommitText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyThemeColors();
        PlayEngineStartup();
        RunEntranceAnimations();
        _spinStopwatch.Start();
        CompositionTarget.Rendering += OnFrame;
        _framesHooked = true;
    }

    private void ApplyThemeColors()
    {
        var accentBrush = TryFindResource("AccentBrush") as SolidColorBrush;
        if (accentBrush == null) return;

        var accent = accentBrush.Color;

        RootShadow.Color = accent;

        var dashBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.42),
                new GradientStop(Color.FromArgb((byte)(accent.A * 0.6), accent.R, accent.G, accent.B), 0.47),
                new GradientStop(accent, 0.5),
                new GradientStop(Color.FromArgb((byte)(accent.A * 0.6), accent.R, accent.G, accent.B), 0.53),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.58),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
            }
        };
        dashBrush.RelativeTransform = new RotateTransform(0, 0.5, 0.5);
        DashBorder.BorderBrush = dashBrush;
    }

    private void RunEntranceAnimations()
    {
        var rootFade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5));
        rootFade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootLayer.BeginAnimation(OpacityProperty, rootFade);

        AnimateElement(WheelHost, 0.1, 0.6);
        AnimateTransform(WheelScale, ScaleTransform.ScaleXProperty, 0.7, 1.0, 0.1, 0.6);
        AnimateTransform(WheelScale, ScaleTransform.ScaleYProperty, 0.7, 1.0, 0.1, 0.6);

        AnimateElement(TitlePanel, 0.35, 0.5);
        AnimateTransform(TitleSlide, TranslateTransform.YProperty, 12, 0, 0.35, 0.5);

        AnimateElement(VersionBadge, 0.55, 0.4);
        AnimateTransform(VersionSlide, TranslateTransform.YProperty, 8, 0, 0.55, 0.4);

        AnimateElement(StatusText, 0.7, 0.4);
        AnimateElement(ProgressContainer, 0.85, 0.4);

        StartDashTravel();
        StartShimmer();
    }

    private void AnimateElement(UIElement element, double delaySeconds, double durationSeconds)
    {
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(durationSeconds))
        {
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, anim);
    }

    private void AnimateTransform(Animatable target, DependencyProperty property,
        double from, double to, double delaySeconds, double durationSeconds)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(durationSeconds))
        {
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        target.BeginAnimation(property, anim);
    }

    private void StartDashTravel()
    {
        if (DashBorder.BorderBrush is not LinearGradientBrush { RelativeTransform: RotateTransform rt })
            return;
        var dash = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, dash);
    }

    private void StartShimmer()
    {
        var shimmer = new DoubleAnimation(-80, 400, TimeSpan.FromSeconds(1.8))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ShimmerHighlight.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, shimmer);
    }

    private void OnSpinTick(double keep, double t)
    {
        var target = Math.Sin(t * 4) * 60 + Math.Sin(t * 9) * 15;
        _angle += (target - _angle) * (1 - keep);
        WheelRotation.Angle = _angle;
    }

    // ── Real progress ────────────────────────────────────────────────
    // The splash used to run a fixed ~5 s fake progress bar and only THEN
    // build the main window (freezing the finished splash for the real
    // load). App now drives progress from the actual startup steps.

    private static readonly TimeSpan MinimumDisplay = TimeSpan.FromSeconds(2.2);
    private double _progressTarget;
    private double _progressShown;
    private double _progressCeiling;

    /// <summary>
    /// Report a real startup step. <paramref name="fraction"/> is where the
    /// step starts; <paramref name="upTo"/> is where it ends — the bar creeps
    /// toward it while the step runs so long steps never look frozen.
    /// </summary>
    public void Report(double fraction, string status, double upTo = -1)
    {
        _progressTarget = Math.Clamp(fraction, _progressTarget, 1.0);
        _progressCeiling = Math.Max(_progressTarget, upTo);
        StatusText.Text = status;
    }

    // Eases the bar toward the reported target so steps glide instead of jumping.
    private void OnProgressTick(double keep, double dt)
    {
        // Creep: close ~40 % of the gap to the step's end per second (never reaches it).
        if (_progressCeiling > _progressTarget)
            _progressTarget += (_progressCeiling - _progressTarget) * (1 - Math.Exp(-dt * 0.5));
        _progressShown += (_progressTarget - _progressShown) * (1 - keep);
        if (_progressTarget - _progressShown < 0.002) _progressShown = _progressTarget;
        var parentWidth = ((FrameworkElement)ProgressFill.Parent).ActualWidth;
        ProgressFill.Width = Math.Max(0, parentWidth * _progressShown);
    }

    /// <summary>
    /// Completes the bar, holds for the minimum display time (so the intro
    /// and engine sound aren't cut off on fast machines), then fades the
    /// window and the sound out together.
    /// </summary>
    public async Task FinishAsync()
    {
        Report(1.0, "Ready!");
        var remaining = MinimumDisplay - _spinStopwatch.Elapsed;
        var settle = TimeSpan.FromMilliseconds(350); // let the bar visibly reach 100 %
        await Task.Delay(remaining > settle ? remaining : settle);

        _spinning = false;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        RootLayer.BeginAnimation(OpacityProperty, fade);
        var startVol = _mediaPlayer.Volume;
        for (int i = 1; i <= 7; i++)
        {
            await Task.Delay(50);
            _mediaPlayer.Volume = startVol * (1 - i / 7.0);
        }
        UnhookFrames();
    }

    private void UnhookFrames()
    {
        if (!_framesHooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _framesHooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var t = _spinStopwatch.Elapsed.TotalSeconds;
        var dt = Math.Clamp(t - _lastFrameSec, 0, 0.1);
        _lastFrameSec = t;
        // Frame-rate independent smoothing (same feel at 60/144/240 Hz).
        var k60 = dt * 60.0;
        if (_spinning)
            OnSpinTick(Math.Pow(0.85, k60), t);
        OnProgressTick(Math.Pow(0.65, k60), dt);
    }

    private void PlayEngineStartup()
    {
        try
        {
            string audioPath;

            if (!string.IsNullOrEmpty(_customSoundPath) && File.Exists(_customSoundPath))
            {
                audioPath = _customSoundPath;
            }
            else
            {
                var uri = new Uri("pack://application:,,,/Resources/engine_start_default.wav");
                var sri = Application.GetResourceStream(uri);
                if (sri == null) return;

                var tempFile = Path.Combine(Path.GetTempPath(), "acevo_startup.wav");
                _tempAudioFile = tempFile;
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                {
                    sri.Stream.CopyTo(fs);
                }
                audioPath = tempFile;
            }

            _mediaPlayer.Open(new Uri(audioPath));
            _mediaPlayer.MediaOpened += (s, e) => _mediaPlayer.Play();
            _mediaPlayer.MediaFailed += (s, e) => { };
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        UnhookFrames();
        _mediaPlayer.Close();
        try { if (_tempAudioFile != null) File.Delete(_tempAudioFile); } catch { }
    }

}
