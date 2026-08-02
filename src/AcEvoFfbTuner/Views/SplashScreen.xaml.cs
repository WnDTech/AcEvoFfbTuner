using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    private readonly DispatcherTimer _spinTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _fadeOutTimer;
    private readonly MediaPlayer _mediaPlayer;
    private readonly string? _customSoundPath;
    private string? _tempAudioFile;
    private double _angle;
    private int _progressStep;
    private int _fadeOutTicks;
    private readonly Stopwatch _spinStopwatch = new();

    public event Action? LoadingComplete;

    public SplashScreen(string? customSoundPath = null)
    {
        InitializeComponent();

        var idx = Random.Shared.Next(SplashWheelUris.Length);
        WheelImage.Source = new BitmapImage(new Uri(SplashWheelUris[idx]));

        LoadVersionInfo();

        _customSoundPath = customSoundPath;
        _mediaPlayer = new MediaPlayer();

        _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spinTimer.Tick += OnSpinTick;

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _progressTimer.Tick += OnProgressTick;

        _fadeOutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _fadeOutTimer.Tick += OnFadeOutTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void LoadVersionInfo()
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
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
        _spinTimer.Start();
        _progressStep = 0;
        _progressTimer.Start();
    }

    private void ApplyThemeColors()
    {
        var accentBrush = TryFindResource("AccentBrush") as SolidColorBrush;
        if (accentBrush == null) return;

        var accent = accentBrush.Color;

        RootShadow.Color = accent;
        ProgressShadow.Color = accent;

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
        RootBorder.BeginAnimation(OpacityProperty, rootFade);

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

    private void OnSpinTick(object? sender, EventArgs e)
    {
        var t = _spinStopwatch.Elapsed.TotalSeconds;
        var target = Math.Sin(t * 4) * 60 + Math.Sin(t * 9) * 15;
        _angle += (target - _angle) * 0.15;
        WheelRotation.Angle = _angle;
        WheelShake.X = 0;
        WheelShake.Y = 0;
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        _progressStep++;
        var totalSteps = 35;

        var fraction = Math.Min(1.0, (double)_progressStep / totalSteps);
        var parentWidth = ((FrameworkElement)ProgressFill.Parent).ActualWidth;
        ProgressFill.Width = Math.Max(0, parentWidth * fraction);

        StatusText.Text = _progressStep switch
        {
            < 5 => "Cranking engine...",
            < 10 => "Engine started!",
            < 18 => "Loading FFB pipeline...",
            < 25 => "Calibrating wheel input...",
            < 30 => "Preparing telemetry...",
            _ => "Ready!"
        };

        if (_progressStep >= totalSteps)
        {
            _progressTimer.Stop();
            _spinTimer.Stop();
            _fadeOutTimer.Start();
        }
    }

    private void OnFadeOutTick(object? sender, EventArgs e)
    {
        _fadeOutTicks++;
        var vol = _mediaPlayer.Volume - 0.05;
        if (vol <= 0 || _fadeOutTicks >= 20)
        {
            _fadeOutTimer.Stop();
            _mediaPlayer.Volume = 0;
            LoadingComplete?.Invoke();
        }
        else
        {
            _mediaPlayer.Volume = vol;
        }
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
        _mediaPlayer.Close();
        try { if (_tempAudioFile != null) File.Delete(_tempAudioFile); } catch { }
    }

}
