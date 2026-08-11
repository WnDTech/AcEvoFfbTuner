using AcEvoFfbTuner.Core.FfbProviders;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    private LogitechHidppWheelProvider? _logitechHidpp;
    private bool _logitechUiLoading;
    private bool _logitechRefreshInFlight;
    private System.Windows.Threading.DispatcherTimer? _logitechWriteDebounce;
    private System.Windows.Threading.DispatcherTimer? _logitechRefreshTimer;
    private System.Windows.Threading.DispatcherTimer? _logitechSettleTimer;
    private System.Windows.Threading.DispatcherTimer? _trueForceStatusTimer;

    private LogitechTrueForceProvider? ActiveTrueForceProvider =>
        _telemetryLoop.ActiveProvider as LogitechTrueForceProvider;

    // ── TrueForce stream (RS50 / G PRO direct FFB, no G HUB) ──
    [ObservableProperty] private bool _trueForceCardVisible;
    [ObservableProperty] private bool _trueForceEnabled = true;
    [ObservableProperty] private float _trueForceForceScale = 1.0f;
    [ObservableProperty] private string _trueForceStatus = "TrueForce stream: not connected";

    partial void OnTrueForceEnabledChanged(bool value)
    {
        var tf = ActiveTrueForceProvider;
        if (tf == null) return;
        if (value) tf.Resume();
        else tf.Pause();
        UpdateTrueForceStatus();
        RefreshProviderFeatures();
        AddSystemLog(value
            ? "TrueForce stream enabled — app force drives the wheel"
            : "TrueForce stream paused — wheel falls back to its own FFB path");
    }

    partial void OnTrueForceForceScaleChanged(float value)
    {
        var tf = ActiveTrueForceProvider;
        if (tf == null) return;
        tf.ForceScale = value;
        AddSystemLog($"TrueForce force scale: {value:F2}");
    }

    private void UpdateTrueForceStatus()
    {
        var tf = ActiveTrueForceProvider;
        if (tf == null)
        {
            TrueForceStatus = "TrueForce stream: not available (not a Logitech DD wheel)";
            return;
        }
        if (!tf.IsInitialized)
        {
            TrueForceStatus = $"TrueForce stream: connecting… ({(string.IsNullOrEmpty(tf.LastError) ? "waiting for wheel" : tf.LastError)})";
            return;
        }
        double packetsPerSec = 0;
        if (_trueForceStatusTimer != null && _trueForceLastSent > 0 && _trueForceLastSent != tf.PacketsSent)
        {
            double dt = (DateTime.UtcNow - _trueForceLastSentAt).TotalSeconds;
            if (dt > 0) packetsPerSec = (tf.PacketsSent - _trueForceLastSent) / dt;
        }
        _trueForceLastSent = tf.PacketsSent;
        _trueForceLastSentAt = DateTime.UtcNow;
        TrueForceStatus = tf.IsPaused
            ? $"TrueForce stream PAUSED (settings reads) — wheel holds neutral"
            : $"TrueForce stream ACTIVE — {packetsPerSec:F0} pkt/s, force scale {tf.ForceScale:F2}, rotation {tf.RotationDegrees}°, fails {tf.PacketsFailed}";
    }

    private long _trueForceLastSent;
    private DateTime _trueForceLastSentAt = DateTime.UtcNow;

    private void StartTrueForceStatusTimer()
    {
        _trueForceStatusTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trueForceStatusTimer.Tick -= OnTrueForceStatusTick;
        _trueForceStatusTimer.Tick += OnTrueForceStatusTick;
        _trueForceStatusTimer.Start();
    }

    private void OnTrueForceStatusTick(object? sender, EventArgs e)
    {
        if (_telemetryLoop.ActiveProvider is not LogitechTrueForceProvider) return;
        UpdateTrueForceStatus();
    }

    // ── HID++ direct wheel settings (no G HUB needed) ──
    // NOTE: every HID++ call runs on a background thread — the protocol has
    // 800-1000 ms timeouts per request, and blocking the UI thread froze the
    // app while the wheel was unresponsive.
    [ObservableProperty] private bool _logitechHidppConnected;
    [ObservableProperty] private string _logitechHidppStatus = "Logitech wheel settings: not connected";
    [ObservableProperty] private string _logitechModeInfo = "";
    [ObservableProperty] private bool _logitechIsDesktopMode;
    [ObservableProperty] private float _logitechFfbStrengthNm = 4.0f;
    [ObservableProperty] private int _logitechRotationDegrees = 900;

    private void ConnectLogitechHidpp(string productName)
    {
        DisconnectLogitechHidpp(silent: true);

        if (!IsLogitechDevice(productName))
        {
            LogitechHidppConnected = false;
            LogitechHidppStatus = "Logitech wheel settings: not applicable";
            return;
        }

        LogitechHidppConnected = false;
        LogitechHidppStatus = "Connecting to wheel HID++ interface...";
        AddSystemLog($"Connecting Logitech HID++ settings interface for {productName}...");

        TrueForceCardVisible = true;
        TrueForceStatus = "TrueForce stream: connecting…";
        StartTrueForceStatusTimer();

        var provider = new LogitechHidppWheelProvider();
        _logitechHidpp = provider;

        Task.Run(() =>
        {
            // Pause the TrueForce stream while the wheel's settings are read
            // so HID++ GETs are not competing with stream traffic (set-and-hold
            // keeps the last commanded force during the pause).
            var tf = ActiveTrueForceProvider;
            tf?.Pause();
            try
            {
                bool ok = provider.Connect();

                // The wheel boots in onboard mode, which silently ignores live host
                // SETs — switch to desktop mode automatically so slider changes apply
                // immediately with zero user intervention.
                if (ok)
                {
                    provider.SetDesktopMode();
                    provider.ReadAllSettingsForUi();
                }

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_logitechHidpp, provider)) return;
                    if (!ok)
                    {
                        LogitechHidppConnected = false;
                        LogitechHidppStatus = provider.LastError;
                        AddSystemLog($"Logitech HID++ connect FAILED: {provider.LastError}");
                        return;
                    }

                    LogitechHidppConnected = true;
                    AddSystemLog($"Logitech HID++ connected: {provider.DiagnosticSummary}");

                    _logitechUiLoading = true;
                    LogitechFfbStrengthNm = provider.FfbStrengthNm;
                    LogitechRotationDegrees = provider.RotationDegrees;
                    _logitechUiLoading = false;

                    // Feed the TrueForce stream provider the wheel's real rotation
                    // so its init range push is a no-op (the pump thread waits for
                    // this before replaying the captured 2700° sequence).
                    if (ActiveTrueForceProvider is { } tf2)
                    {
                        tf2.RotationDegrees = provider.RotationDegrees;
                        UpdateTrueForceStatus();
                    }

                    UpdateLogitechModeInfo();
                    LogitechHidppStatus = $"HID++ connected — {provider.ProductName}";

                    // Periodic read-back (every 5s) keeps the UI in sync with the wheel.
                    _logitechRefreshTimer ??= new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(5)
                    };
                    _logitechRefreshTimer.Tick -= OnLogitechRefreshTick;
                    _logitechRefreshTimer.Tick += OnLogitechRefreshTick;
                    _logitechRefreshTimer.Start();
                });
            }
            finally
            {
                tf?.Resume();
            }
        });
    }

    private void DisconnectLogitechHidpp(bool silent = false)
    {
        _logitechRefreshTimer?.Stop();
        _trueForceStatusTimer?.Stop();
        var provider = _logitechHidpp;
        _logitechHidpp = null;
        if (provider != null)
        {
            if (!silent) AddSystemLog("Logitech HID++ settings interface disconnected");
            Task.Run(() => provider.Dispose());
        }
        LogitechHidppConnected = false;
        LogitechModeInfo = "";
        LogitechIsDesktopMode = false;
        TrueForceCardVisible = false;
        TrueForceStatus = "TrueForce stream: not connected";
    }

    private void OnLogitechRefreshTick(object? sender, EventArgs e)
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true || _logitechRefreshInFlight) return;
        _logitechRefreshInFlight = true;

        Task.Run(() =>
        {
            try
            {
                // Deliberately NOT pausing the stream here: a pause every 5 s
                // would freeze the force while driving. If the wheel stops
                // answering HID++ reads while the stream runs (first log tells
                // us), wrap this in tf.Pause()/tf.Resume().
                provider.ReadAllSettingsForUi();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_logitechHidpp, provider))
                    {
                        _logitechUiLoading = true;
                        LogitechFfbStrengthNm = provider.FfbStrengthNm;
                        LogitechRotationDegrees = provider.RotationDegrees;
                        _logitechUiLoading = false;
                        if (ActiveTrueForceProvider is { } tf)
                            tf.RotationDegrees = provider.RotationDegrees;
                        UpdateLogitechModeInfo();
                    }
                });
            }
            finally
            {
                _logitechRefreshInFlight = false;
            }
        });
    }

    private void UpdateLogitechModeInfo()
    {
        if (_logitechHidpp == null)
        {
            LogitechModeInfo = "";
            LogitechIsDesktopMode = false;
            return;
        }
        string mode = _logitechHidpp.IsDesktopMode
            ? "Desktop mode — live changes apply immediately"
            : _logitechHidpp.ProfileMode == 0xFF
                ? "Mode unknown"
                : $"Onboard profile slot {_logitechHidpp.ProfileMode} — switch to Desktop mode so live changes apply";
        LogitechModeInfo = mode;
        LogitechIsDesktopMode = _logitechHidpp.IsDesktopMode;
        if (LogitechHidppConnected)
            LogitechHidppStatus = $"HID++ connected — {_logitechHidpp.ProductName} | {mode}";
    }

    // ── Slider changes → debounced write to the wheel (background thread) ──

    partial void OnLogitechFfbStrengthNmChanged(float value)
    {
        if (_logitechUiLoading || _logitechHidpp?.IsConnected != true) return;
        ScheduleLogitechWrite();
    }

    partial void OnLogitechRotationDegreesChanged(int value)
    {
        if (_logitechUiLoading || _logitechHidpp?.IsConnected != true) return;
        ScheduleLogitechWrite();
    }

    private void ScheduleLogitechWrite()
    {
        _logitechWriteDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _logitechWriteDebounce.Tick -= OnLogitechWriteTick;
        _logitechWriteDebounce.Tick += OnLogitechWriteTick;
        _logitechWriteDebounce.Stop();
        _logitechWriteDebounce.Start();
    }

    private void OnLogitechWriteTick(object? sender, EventArgs e)
    {
        _logitechWriteDebounce?.Stop();
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;

        float strength = LogitechFfbStrengthNm;
        int rotation = LogitechRotationDegrees;

        // Echo-skip: if the values still match what the wheel reported, nothing
        // changed (e.g. a slider clamp round-trip on load) — don't write. The
        // slider Minimum clamps garbage/zero reads to 1.0 Nm, and a blind write
        // of that clamped value previously throttled the wheel's motor gain to 1 Nm.
        bool strengthChanged = Math.Abs(strength - provider.FfbStrengthNm) >= 0.01f;
        bool rotationChanged = rotation != provider.RotationDegrees;
        if (!strengthChanged && !rotationChanged)
        {
            LogitechHidppStatus = "HID++ — wheel settings unchanged, nothing written";
            return;
        }

        AddSystemLog($"Logitech HID++ write: strength={strength:F1} Nm{(strengthChanged ? "" : " (unchanged)")}, rotation={rotation}°{(rotationChanged ? "" : " (unchanged)")}");

        var tf = ActiveTrueForceProvider;
        tf?.Pause();
        Task.Run(() =>
        {
            try
            {
                if (strengthChanged) provider.SetFfbStrengthNm(strength);
                if (rotationChanged) provider.SetRotationDegrees(rotation);
            }
            finally
            {
                tf?.Resume();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_logitechHidpp, provider)) return;
                    _logitechUiLoading = true;
                    LogitechFfbStrengthNm = provider.FfbStrengthNm;
                    LogitechRotationDegrees = provider.RotationDegrees;
                    _logitechUiLoading = false;
                    if (ActiveTrueForceProvider is { } tf2)
                        tf2.RotationDegrees = provider.RotationDegrees;
                    UpdateLogitechModeInfo();
                    if (!provider.IsDesktopMode && provider.ProfileMode != 0xFF)
                    {
                        LogitechHidppStatus = "Written to wheel — switch to Desktop mode to hear it immediately (onboard slot settings are stored and apply when the slot is active)";
                    }
                });
            }
        });
    }

    [RelayCommand]
    private void SwitchToDesktopMode()
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: switching wheel to desktop mode");

        var tf = ActiveTrueForceProvider;
        tf?.Pause();
        Task.Run(() =>
        {
            try
            {
                bool ok = provider.SetDesktopMode();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_logitechHidpp, provider)) return;
                    UpdateLogitechModeInfo();
                    LogitechHidppStatus = ok
                        ? "HID++ — desktop mode active, live changes apply now"
                        : $"HID++ — desktop mode switch FAILED ({provider.LastError})";
                });
            }
            finally
            {
                tf?.Resume();
            }
        });
    }

    [RelayCommand]
    private void RefreshLogitechWheel()
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: re-reading wheel settings");

        var tf = ActiveTrueForceProvider;
        tf?.Pause();
        Task.Run(() =>
        {
            try
            {
                provider.ReadAllSettingsForUi();
            }
            finally
            {
                tf?.Resume();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_logitechHidpp, provider)) return;
                    _logitechUiLoading = true;
                    LogitechFfbStrengthNm = provider.FfbStrengthNm;
                    LogitechRotationDegrees = provider.RotationDegrees;
                    _logitechUiLoading = false;
                    if (ActiveTrueForceProvider is { } tf2)
                        tf2.RotationDegrees = provider.RotationDegrees;
                    UpdateLogitechModeInfo();
                    LogitechHidppStatus = $"HID++ — re-read: {provider.DiagnosticSummary}";
                });
            }
        });
    }
}

