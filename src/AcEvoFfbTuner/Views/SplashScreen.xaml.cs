using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AcEvoFfbTuner.Views;

public partial class SplashScreen : Window
{
    private readonly DispatcherTimer _spinTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _fadeOutTimer;
    private readonly MediaPlayer _mediaPlayer;
    private readonly string? _customSoundPath;
    private string? _tempAudioFile;
    private double _angle;
    private int _progressStep;

    public event Action? LoadingComplete;

    public SplashScreen(string? customSoundPath = null)
    {
        InitializeComponent();

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
        var rpmFactor = _progressStep switch
        {
            < 8 => 2.0,
            < 15 => 8.0,
            < 22 => 6.0,
            < 28 => 3.0,
            _ => 4.0
        };
        _angle = (_angle + rpmFactor) % 360;
        WheelRotation.Angle = _angle;
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
        var vol = _mediaPlayer.Volume - 0.05;
        if (vol <= 0)
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
                var uri = new Uri("pack://application:,,,/Resources/engine_start.mp3");
                var sri = Application.GetResourceStream(uri);
                if (sri == null) return;

                var tempFile = Path.Combine(Path.GetTempPath(), "acevo_startup.mp3");
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
