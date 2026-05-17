using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
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
    private readonly Random _rng = new();

    public event Action? LoadingComplete;

    public SplashScreen(string? customSoundPath = null)
    {
        InitializeComponent();

        var idx = Random.Shared.Next(SplashWheelUris.Length);
        WheelImage.Source = new BitmapImage(new Uri(SplashWheelUris[idx]));

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayEngineStartup();
        _spinTimer.Start();
        _progressStep = 0;
        _progressTimer.Start();
    }

    private void OnSpinTick(object? sender, EventArgs e)
    {
        var t = _progressStep * 0.09;

        var phase = t % 7.0;

        double target;

        if (phase < 0.5)
        {
            target = Math.Sin(phase * 2 * Math.PI) * 10;
        }
        else if (phase < 1.5)
        {
            var p = (phase - 0.5) / 1.0;
            target = EaseInOut(p) * 130;
        }
        else if (phase < 3.5)
        {
            var p = (phase - 1.5) / 2.0;
            var baseAngle = 130 - p * 25;
            var slip = Math.Sin(p * Math.PI * 5) * 20;
            var selfAlign = Math.Sin(p * Math.PI * 13) * 12;
            var rumble = Math.Sin(p * Math.PI * 40) * 7;
            target = baseAngle + slip + selfAlign + rumble;
        }
        else if (phase < 5.0)
        {
            var p = (phase - 3.5) / 1.5;
            var unwind = EaseInOut(p);
            var baseAngle = 105 * (1.0 - unwind);
            var rumble = Math.Sin(p * Math.PI * 45) * 10 * (1 - p);
            target = baseAngle + rumble;
        }
        else
        {
            var p = (phase - 5.0) / 2.0;
            target = Math.Sin(p * Math.PI) * 8;
        }

        _angle += (target - _angle) * 0.7;
        WheelRotation.Angle = _angle;
        WheelShake.X = 0;
        WheelShake.Y = 0;
    }

    private static double EaseInOut(double t) =>
        t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private double NextGaussian()
    {
        var u1 = 1.0 - _rng.NextDouble();
        var u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
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
