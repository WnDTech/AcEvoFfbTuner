using AcEvoFfbTuner.Core.FfbProviders;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    private LogitechHidppWheelProvider? _logitechHidpp;
    private bool _logitechUiLoading;
    private System.Windows.Threading.DispatcherTimer? _logitechWriteDebounce;
    private System.Windows.Threading.DispatcherTimer? _logitechRefreshTimer;

    // ── HID++ direct wheel settings (no G HUB needed) ──
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

        AddSystemLog($"Connecting Logitech HID++ settings interface for {productName}...");
        _logitechHidpp = new LogitechHidppWheelProvider();
        if (!_logitechHidpp.Connect())
        {
            LogitechHidppConnected = false;
            LogitechHidppStatus = _logitechHidpp.LastError;
            AddSystemLog($"Logitech HID++ connect FAILED: {_logitechHidpp.LastError}");
            return;
        }

        LogitechHidppConnected = true;
        LogitechHidppStatus = $"HID++ connected — {_logitechHidpp.ProductName}";
        AddSystemLog($"Logitech HID++ connected: {_logitechHidpp.DiagnosticSummary}");

        _logitechUiLoading = true;
        LogitechFfbStrengthNm = _logitechHidpp.FfbStrengthNm;
        LogitechRotationDegrees = _logitechHidpp.RotationDegrees;
        _logitechUiLoading = false;

        UpdateLogitechModeInfo();

        // Periodic read-back (every 5s) keeps the UI in sync with the wheel
        // (e.g. profile switches via the wheel's OLED menu).
        _logitechRefreshTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _logitechRefreshTimer.Tick -= OnLogitechRefreshTick;
        _logitechRefreshTimer.Tick += OnLogitechRefreshTick;
        _logitechRefreshTimer.Start();
    }

    private void DisconnectLogitechHidpp(bool silent = false)
    {
        _logitechRefreshTimer?.Stop();
        if (_logitechHidpp != null)
        {
            if (!silent) AddSystemLog("Logitech HID++ settings interface disconnected");
            _logitechHidpp.Dispose();
            _logitechHidpp = null;
        }
        LogitechHidppConnected = false;
        LogitechModeInfo = "";
    }

    private void OnLogitechRefreshTick(object? sender, EventArgs e)
    {
        if (_logitechHidpp?.IsConnected != true) return;
        _logitechUiLoading = true;
        _logitechHidpp.ReadAllSettingsForUi();
        LogitechFfbStrengthNm = _logitechHidpp.FfbStrengthNm;
        LogitechRotationDegrees = _logitechHidpp.RotationDegrees;
        _logitechUiLoading = false;
        UpdateLogitechModeInfo();
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
        LogitechHidppStatus = $"HID++ connected — {_logitechHidpp.ProductName} | {mode}";
    }

    // ── Slider changes → debounced write to the wheel ──

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
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _logitechWriteDebounce.Tick -= OnLogitechWriteTick;
        _logitechWriteDebounce.Tick += OnLogitechWriteTick;
        _logitechWriteDebounce.Stop();
        _logitechWriteDebounce.Start();
    }

    private void OnLogitechWriteTick(object? sender, EventArgs e)
    {
        _logitechWriteDebounce?.Stop();
        if (_logitechHidpp?.IsConnected != true) return;

        AddSystemLog($"Logitech HID++ write: strength={LogitechFfbStrengthNm:F1} Nm, rotation={LogitechRotationDegrees}°");
        _logitechHidpp.SetFfbStrengthNm(LogitechFfbStrengthNm);
        _logitechHidpp.SetRotationDegrees(LogitechRotationDegrees);

        if (!_logitechHidpp.IsDesktopMode && _logitechHidpp.ProfileMode != 0xFF)
        {
            LogitechHidppStatus = "Written to wheel — switch to Desktop mode to hear it immediately (onboard slot settings are stored and apply when the slot is active)";
        }
        UpdateLogitechModeInfo();
    }

    [RelayCommand]
    private void SwitchToDesktopMode()
    {
        if (_logitechHidpp?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: switching wheel to desktop mode");
        bool ok = _logitechHidpp.SetDesktopMode();
        LogitechIsDesktopMode = _logitechHidpp.IsDesktopMode;
        UpdateLogitechModeInfo();
        LogitechHidppStatus = ok
            ? $"HID++ — desktop mode active, live changes apply now"
            : $"HID++ — desktop mode switch FAILED ({_logitechHidpp.LastError})";
    }

    [RelayCommand]
    private void RefreshLogitechWheel()
    {
        if (_logitechHidpp?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: re-reading wheel settings");
        _logitechUiLoading = true;
        _logitechHidpp.ReadAllSettingsForUi();
        LogitechFfbStrengthNm = _logitechHidpp.FfbStrengthNm;
        LogitechRotationDegrees = _logitechHidpp.RotationDegrees;
        _logitechUiLoading = false;
        UpdateLogitechModeInfo();
        LogitechHidppStatus = $"HID++ — re-read: {_logitechHidpp.DiagnosticSummary}";
    }
}
