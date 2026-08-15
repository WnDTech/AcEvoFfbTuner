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
    [ObservableProperty] private float _trueForceForceScale = 0.5f;
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
            TrueForceStatus = tf.IsEngaged
                ? $"TrueForce stream: initializing…"
                : $"TrueForce stream: connected, standby — engages when telemetry runs";
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

    // ── Wheel OLED (Dynamic Display, feature 0x8130 — Experimental) ──
    // The panel is driven over the HID++ interface while our force rides the
    // TrueForce stream, so the HID++ endpoint stays force-free — the only
    // configuration where display writes are hardware-verified safe
    // (TF4ALL/12.5: non-force writes cut force only when force is on HID++).
    [ObservableProperty] private bool _oledAvailable;
    [ObservableProperty] private bool _oledEnabled;
    [ObservableProperty] private int _oledScreen; // 0 = Gear + Speed, 1 = Speed only
    [ObservableProperty] private string _oledStatus = "Wheel OLED: not available";

    private DateTime _lastOledPush = DateTime.MinValue;
    private int _oledPushInFlight;

    partial void OnOledEnabledChanged(bool value)
    {
        if (!value)
        {
            // HID++ calls must never run on the UI thread (up to 1 s timeouts
            // per request froze the app while the wheel was unresponsive).
            var hp = _logitechHidpp;
            Task.Run(() =>
            {
                try { hp?.OledClear(); } catch { }
            });
        }
        UpdateOledStatus();
        AddSystemLog(value
            ? "Wheel OLED enabled (experimental — display writes over HID++ 0x8130)"
            : "Wheel OLED disabled — panel returned to its default screen");
    }

    private void UpdateOledStatus()
    {
        var hp = _logitechHidpp;
        OledAvailable = hp?.OledAvailable == true;
        if (!OledAvailable)
        {
            OledStatus = "Wheel OLED: not available on this wheel";
            return;
        }
        OledStatus = OledEnabled
            ? $"Wheel OLED: live — {hp!.OledLayoutCount} layouts (0x8130)"
            : "Wheel OLED: available (feature 0x8130) — enable to use";
    }

    /// <summary>Paced push of telemetry to the wheel's OLED (max ~2 Hz — the
    /// panel shares a pipe with the rev strip and wants slow, quiet updates).
    /// Layout J: four text fields 19/10/19/10, spaces are content.</summary>
    internal void PushOledTelemetry(float speedKmh, int gear)
    {
        var hp = _logitechHidpp;
        if (!OledEnabled || hp == null || !hp.OledAvailable) return;

        var now = DateTime.UtcNow;
        if ((now - _lastOledPush).TotalMilliseconds < 500) return;
        // Real rate limit: a slow/failed fn3 exchange holds the shared HID++
        // gate for up to its timeout, so never queue more than one write.
        if (Interlocked.CompareExchange(ref _oledPushInFlight, 1, 0) != 0) return;
        _lastOledPush = now;

        string gearText = gear switch
        {
            -1 => "R",
            0 => "N",
            _ => gear.ToString()
        };
        string speedText = $"{speedKmh:F0} km/h";

        string[] fields = OledScreen switch
        {
            1 => new[] { speedText, "", "", "" },
            _ => new[] { gearText, speedText, "", "" }
        };

        Task.Run(() =>
        {
            try
            {
                hp.OledWriteFrame(9, fields, new[] { 19, 10, 19, 10 });
            }
            catch (Exception ex)
            {
                // AddSystemLog touches UI-bound collections — marshal to the
                // dispatcher like every other background log call site.
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    AddSystemLog($"Wheel OLED push failed: {ex.GetType().Name}: {ex.Message}"));
            }
            finally
            {
                Interlocked.Exchange(ref _oledPushInFlight, 0);
            }
        });
    }

    // ── HID++ direct wheel settings (no G HUB needed) ──
    // NOTE: every HID++ call runs on a background thread — the protocol has
    // 800-1000 ms timeouts per request, and blocking the UI thread froze the
    // app while the wheel was unresponsive.
    [ObservableProperty] private bool _logitechHidppConnected;
    [ObservableProperty] private string _logitechHidppStatus = "Logitech wheel settings: not connected";
    [ObservableProperty] private string _logitechModeInfo = "";
    [ObservableProperty] private bool _logitechIsDesktopMode;
    [ObservableProperty] private float _logitechFfbStrengthNm = 8.0f;
    [ObservableProperty] private int _logitechRotationDegrees = 1080;
    [ObservableProperty] private int _logitechProfileSlot = 1;

    /// <summary>Slot selector options: 0 = desktop mode (live, resets on wheel
    /// restart), 1-5 = onboard slots (persist). Users pick the slot so the app
    /// never overwrites their preset profiles.</summary>
    public List<KeyValuePair<int, string>> LogitechProfileSlotOptions { get; } =
    [
        new(0, "Desktop (live — resets on restart)"),
        new(1, "Slot 1 (persists)"),
        new(2, "Slot 2 (persists)"),
        new(3, "Slot 3 (persists)"),
        new(4, "Slot 4 (persists)"),
        new(5, "Slot 5 (persists)"),
    ];

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

                // The wheel boots in onboard mode, which silently ignores live
                // host SETs — and DESKTOP mode (G HUB profile 0) does NOT
                // persist: settings reset on every wheel restart (user-verified
                // on the RS50). Onboard slots 1-5 store settings in the wheel's
                // flash and survive restarts. Apply the USER-CHOSEN slot (from
                // the wheel settings page) so their preset profiles are never
                // overwritten: 0 = desktop mode, 1-5 = onboard slot.
                if (ok)
                {
                    int slot = _appSettings.LogitechProfileSlot;
                    if (slot <= 0)
                    {
                        if (provider.SetDesktopMode())
                            AddSystemLog("Logitech HID++: desktop mode selected — settings apply live but reset on wheel restart");
                        else
                            AddSystemLog($"Logitech HID++: could not switch to desktop mode ({provider.LastError})");
                    }
                    else
                    {
                        if (provider.SetOnboardSlot((byte)slot))
                            AddSystemLog($"Logitech HID++: onboard slot {slot} selected — settings persist across restarts");
                        else
                            AddSystemLog($"Logitech HID++: could not switch to onboard slot {slot} ({provider.LastError})");
                    }
                    provider.ReadAllSettingsForUi();

                    // Read-only OLED layout query — safe, and the descriptor
                    // readback lands in the diag log to validate the frame
                    // format against real hardware.
                    try { provider.OledQueryLayouts(); } catch { }
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
                    LogitechFfbStrengthNm = _appSettings.LogitechFfbStrengthNm;
                    LogitechRotationDegrees = _appSettings.LogitechRotationDegrees;
                    LogitechProfileSlot = _appSettings.LogitechProfileSlot;
                    _logitechUiLoading = false;

                    // The wheel's desktop profile loads DEFAULTS (5 Nm) unless a
                    // host pushes values — force-apply the persisted
                    // strength/rotation at every connect so G HUB is never needed.
                    // BUT only when the wheel's current state is KNOWN: if the
                    // settings read failed (the game holds the HID++ interface),
                    // a blind write could clobber the user's onboard values —
                    // skip and let the wheel keep what it has.
                    if (provider.LastSettingsReadOk)
                    {
                        ScheduleLogitechWrite(force: true);
                    }
                    else
                    {
                        LogitechHidppStatus = "HID++ — wheel settings unreadable (game holding the interface?); not writing";
                    }

                    // Feed the TrueForce stream provider the rotation we are
                    // pushing (not the wheel's pre-write value) so its init
                    // range push is a no-op (the pump thread waits for this
                    // before replaying the captured 2700° sequence).
                    if (ActiveTrueForceProvider is { } tf2)
                    {
                        tf2.RotationDegrees = LogitechRotationDegrees;
                        UpdateTrueForceStatus();
                    }

                    UpdateLogitechModeInfo();
                    LogitechHidppStatus = $"HID++ connected — {provider.ProductName}";
                    UpdateOledStatus();

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
        OledAvailable = false;
        OledStatus = "Wheel OLED: not available";
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
                        // Only echo valid read-backs into the UI — a failed read
                        // (game holding the HID++ interface) must not clamp the
                        // slider to its minimum and trigger a write.
                        if (!provider.LastSettingsReadOk)
                        {
                            LogitechHidppStatus = "HID++ — wheel settings unreadable (game holding the interface?)";
                            return;
                        }
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

    private bool _logitechWriteForceNext;

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

    partial void OnLogitechProfileSlotChanged(int value)
    {
        if (_logitechUiLoading || _logitechHidpp?.IsConnected != true) return;
        _appSettings.LogitechProfileSlot = value;
        _appSettings.Save();
        ApplyLogitechProfileSlot(value);
    }

    /// <summary>Switch the wheel to the user-chosen profile slot (0 = desktop
    /// mode, 1-5 = onboard slots) and push the persisted settings there.</summary>
    private void ApplyLogitechProfileSlot(int slot)
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;
        AddSystemLog(slot <= 0
            ? $"Logitech HID++: switching to desktop mode"
            : $"Logitech HID++: switching to onboard slot {slot}");

        var tf = ActiveTrueForceProvider;
        tf?.Pause();
        Task.Run(() =>
        {
            try
            {
                bool ok = slot <= 0
                    ? provider.SetDesktopMode()
                    : provider.SetOnboardSlot((byte)slot);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!ReferenceEquals(_logitechHidpp, provider)) return;
                    UpdateLogitechModeInfo();
                    LogitechHidppStatus = ok
                        ? (slot <= 0
                            ? "HID++ — desktop mode active (live changes, resets on restart)"
                            : $"HID++ — onboard slot {slot} active (settings persist)")
                        : $"HID++ — slot switch FAILED ({provider.LastError})";
                    if (ok && provider.LastSettingsReadOk)
                        ScheduleLogitechWrite(force: true);
                });
            }
            finally
            {
                tf?.Resume();
            }
        });
    }

    private void ScheduleLogitechWrite(bool force = false)
    {
        if (force) _logitechWriteForceNext = true;
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

        // Never write on unknown wheel state: a failed settings read means the
        // wheel isn't answering (the game holds the HID++ interface) — writing
        // the UI's clamped values can throttle the wheel to 1 Nm.
        if (!provider.LastSettingsReadOk)
        {
            LogitechHidppStatus = "HID++ — wheel settings unreadable, write skipped (is the game holding the settings interface?)";
            return;
        }

        bool force = _logitechWriteForceNext;
        _logitechWriteForceNext = false;

        float strength = LogitechFfbStrengthNm;
        int rotation = LogitechRotationDegrees;

        // Echo-skip: if the values still match what the wheel reported, nothing
        // changed (e.g. a slider clamp round-trip on load) — don't write. The
        // slider Minimum clamps garbage/zero reads to 1.0 Nm, and a blind write
        // of that clamped value previously throttled the wheel's motor gain to 1 Nm.
        // A forced write (connect-time apply) bypasses this check deliberately.
        bool strengthChanged = Math.Abs(strength - provider.FfbStrengthNm) >= 0.01f;
        bool rotationChanged = rotation != provider.RotationDegrees;
        if (!force && !strengthChanged && !rotationChanged)
        {
            LogitechHidppStatus = "HID++ — wheel settings unchanged, nothing written";
            return;
        }

        // Persist what the user wants so the next connect applies it again
        // (the wheel's desktop profile reloads defaults otherwise).
        _appSettings.LogitechFfbStrengthNm = strength;
        _appSettings.LogitechRotationDegrees = rotation;
        _appSettings.Save();

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
                    // Only echo the read-back into the UI when it actually
                    // succeeded — a failed read feeding the slider clamps it to
                    // the 1.0 minimum and schedules another write (the 1 Nm
                    // throttle bug). Keep the user's values and flag the state.
                    if (!provider.LastSettingsReadOk)
                    {
                        LogitechHidppStatus = "HID++ — write attempted but read-back failed; keeping your values (is the game holding the settings interface?)";
                        return;
                    }
                    _logitechUiLoading = true;
                    LogitechFfbStrengthNm = provider.FfbStrengthNm;
                    LogitechRotationDegrees = provider.RotationDegrees;
                    _logitechUiLoading = false;
                    if (ActiveTrueForceProvider is { } tf2)
                        tf2.RotationDegrees = provider.RotationDegrees;
                    UpdateLogitechModeInfo();
                    if (!provider.IsDesktopMode && provider.ProfileMode != 0xFF)
                    {
                        LogitechHidppStatus = $"Written to wheel — stored in onboard slot {provider.ProfileMode} (persists across restarts)";
                    }
                    else
                    {
                        LogitechHidppStatus = "Written to wheel — desktop mode does not persist settings across restarts (use an onboard slot to keep them)";
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

