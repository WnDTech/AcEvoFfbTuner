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

        var provider = new LogitechHidppWheelProvider();
        _logitechHidpp = provider;

        Task.Run(() =>
        {
            bool ok = provider.Connect();
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
        });
    }

    private void DisconnectLogitechHidpp(bool silent = false)
    {
        _logitechRefreshTimer?.Stop();
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
                provider.ReadAllSettingsForUi();
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(_logitechHidpp, provider))
                    {
                        _logitechUiLoading = true;
                        LogitechFfbStrengthNm = provider.FfbStrengthNm;
                        LogitechRotationDegrees = provider.RotationDegrees;
                        _logitechUiLoading = false;
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
        AddSystemLog($"Logitech HID++ write: strength={strength:F1} Nm, rotation={rotation}°");

        Task.Run(() =>
        {
            provider.SetFfbStrengthNm(strength);
            provider.SetRotationDegrees(rotation);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_logitechHidpp, provider)) return;
                _logitechUiLoading = true;
                LogitechFfbStrengthNm = provider.FfbStrengthNm;
                LogitechRotationDegrees = provider.RotationDegrees;
                _logitechUiLoading = false;
                UpdateLogitechModeInfo();
                if (!provider.IsDesktopMode && provider.ProfileMode != 0xFF)
                {
                    LogitechHidppStatus = "Written to wheel — switch to Desktop mode to hear it immediately (onboard slot settings are stored and apply when the slot is active)";
                }
            });
        });
    }

    [RelayCommand]
    private void SwitchToDesktopMode()
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: switching wheel to desktop mode");

        Task.Run(() =>
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
        });
    }

    [RelayCommand]
    private void RefreshLogitechWheel()
    {
        var provider = _logitechHidpp;
        if (provider?.IsConnected != true) return;
        AddSystemLog("Logitech HID++: re-reading wheel settings");

        Task.Run(() =>
        {
            provider.ReadAllSettingsForUi();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_logitechHidpp, provider)) return;
                _logitechUiLoading = true;
                LogitechFfbStrengthNm = provider.FfbStrengthNm;
                LogitechRotationDegrees = provider.RotationDegrees;
                _logitechUiLoading = false;
                UpdateLogitechModeInfo();
                LogitechHidppStatus = $"HID++ — re-read: {provider.DiagnosticSummary}";
            });
        });
    }
}

