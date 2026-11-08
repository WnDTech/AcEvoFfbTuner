using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using AcEvoFfbTuner.Core.DeviceDetection;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProviders;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

/// <summary>
/// System-wide sim-hardware detection (wheelbase, pedals, haptics, shifters,
/// handbrakes, button boxes) and robust wheelbase auto-selection.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Every sim device found by the last hardware scan (Devices page).</summary>
    public ObservableCollection<RaceHardwareItem> DetectedHardware { get; } = [];

    [ObservableProperty]
    private string _hardwareScanSummary = "Not scanned yet";

    private int _hardwareScanQueued;

    /// <summary>VID/PID keys of every FFB-capable DirectInput device.</summary>
    private HashSet<uint> CollectFfbVidPids()
    {
        var set = new HashSet<uint>();
        foreach (var d in AvailableDevices)
        {
            if (!d.IsFfbCapable || d.DeviceInstance == null) continue;
            if (RaceHardwareScanner.VidPidFromProductGuid(d.DeviceInstance.ProductGuid) is { } key)
                set.Add(key);
        }
        return set;
    }

    private static uint VidPidOf(FfbDeviceInfo d) =>
        d.DeviceInstance != null && RaceHardwareScanner.VidPidFromProductGuid(d.DeviceInstance.ProductGuid) is { } k ? k : 0u;

    private static bool IsVirtualDevice(FfbDeviceInfo d) =>
        d.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)
        || d.ProductName.Contains("virtual", StringComparison.OrdinalIgnoreCase);

    /// <summary>Kicks off a background HID scan; results land on the UI thread.</summary>
    public void QueueHardwareScan()
    {
        if (Interlocked.Exchange(ref _hardwareScanQueued, 1) == 1) return;
        var ffb = CollectFfbVidPids();
        Task.Run(() =>
        {
            try { _deviceRegistry.ScanHardware(ffb); }
            catch (Exception ex)
            {
                var msg = ex.Message;
                Application.Current?.Dispatcher.BeginInvoke(() => AddSystemLog($"Hardware scan failed: {msg}"));
            }
            finally { Interlocked.Exchange(ref _hardwareScanQueued, 0); }

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var items = _deviceRegistry.Hardware;
                DetectedHardware.Clear();
                foreach (var h in items) DetectedHardware.Add(h);

                HardwareScanSummary = items.Count == 0
                    ? "No sim hardware detected"
                    : string.Join("  ·  ", items.GroupBy(h => h.CategoryLabel)
                        .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()}× {g.Key}"));
                UpdateDeviceStatuses();
            });
        });
    }

    [RelayCommand]
    private void RescanHardware()
    {
        RefreshDevices();
        if (!IsDeviceConnected)
            PreselectBestWheelbase();
        QueueHardwareScan();
        AddSystemLog("Rescanning connected sim hardware...");
    }

    /// <summary>
    /// Ranks DirectInput devices for the wheelbase slot:
    /// 1 saved instance GUID → 2 saved VID/PID → 3 saved product name →
    /// 4 best FFB-capable, non-virtual device (known sim brand first).
    /// Tiers 1–3 survive a GUID change (new USB port, driver reinstall).
    /// </summary>
    private FfbDeviceInfo? PickWheelbase(out string reason)
    {
        var devices = AvailableDevices.Where(d => d.DeviceInstance != null && !IsVirtualDevice(d)).ToList();

        if (Guid.TryParse(_appSettings.LastConnectedDeviceInstanceId, out var guid)
            && devices.FirstOrDefault(d => d.DeviceInstance.InstanceGuid == guid) is { } byGuid)
        {
            reason = "last used wheelbase";
            return byGuid;
        }

        uint savedVidPid = _appSettings.LastConnectedDeviceVidPid;
        if (savedVidPid != 0 && devices.FirstOrDefault(d => VidPidOf(d) == savedVidPid) is { } byVidPid)
        {
            reason = "last used wheelbase (USB id match)";
            return byVidPid;
        }

        string? savedName = _appSettings.LastConnectedDeviceProductName;
        if (!string.IsNullOrEmpty(savedName)
            && devices.FirstOrDefault(d => string.Equals(d.ProductName, savedName, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            reason = "last used wheelbase (name match)";
            return byName;
        }

        var ffb = devices.Where(d => d.IsFfbCapable).ToList();
        var known = ffb.Where(d => WheelbaseFactory.DetectVendorFromProductName(d.ProductName) != WheelbaseVendor.Unknown).ToList();
        if (known.Count > 0)
        {
            reason = known.Count == 1 ? "only sim-brand FFB device" : $"first of {known.Count} sim-brand FFB devices";
            return known[0];
        }
        if (ffb.Count == 1)
        {
            reason = "only force-feedback device";
            return ffb[0];
        }

        reason = ffb.Count == 0 ? "no force-feedback device found" : $"{ffb.Count} unbranded FFB devices — pick one";
        return null;
    }

    /// <summary>Points the device picker at the best wheelbase without connecting.</summary>
    private void PreselectBestWheelbase()
    {
        if (IsDeviceConnected) return;
        var pick = PickWheelbase(out _);
        if (pick != null) SelectedDevice = pick;
    }

    /// <summary>
    /// Auto-connects the best wheelbase when Auto Connect is on and the user
    /// has not deliberately disconnected. Returns true when it connected.
    /// </summary>
    private bool TryAutoConnectWheelbase(string trigger)
    {
        if (IsDeviceConnected || !AutoConnect || _appSettings.WheelbaseUserDisconnected)
            return false;

        var pick = PickWheelbase(out string reason);
        if (pick == null)
        {
            AddSystemLog($"Wheelbase auto-connect ({trigger}): {reason}");
            return false;
        }

        AddSystemLog($"Wheelbase auto-connect ({trigger}): {pick.ProductName} — {reason}");
        SelectedDevice = pick;
        ConnectDevice();
        return IsDeviceConnected;
    }

    /// <summary>Hot-plug: a USB device came or went.</summary>
    private void OnHardwareChanged(bool arrived)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_deviceManager.IsDeviceAcquired)
                RefreshDeviceRegistry();

            if (!IsDeviceConnected)
            {
                RefreshDevices();
                if (arrived && !TryAutoConnectWheelbase("device plugged in"))
                    PreselectBestWheelbase();
            }

            if (arrived)
                TryAutoConnectHaptics();

            QueueHardwareScan();
        });
    }

    /// <summary>
    /// Haptics powered on after launch: Moza PHF re-enumerates its ports and
    /// auto-connects (only when PHF is enabled); HF8 retries its SDK connect
    /// (only when HF8 is enabled and not already connected).
    /// </summary>
    private void TryAutoConnectHaptics()
    {
        try
        {
            if (MozaPhfEnabled && !MozaPhfIsConnected)
            {
                OnRefreshMozaPhf();   // re-lists ports, then TryAutoConnectMozaPhf()
                if (MozaPhfIsConnected)
                    AddSystemLog("Moza pedal haptics detected and connected");
            }
        }
        catch (Exception ex) { AddSystemLog($"PHF auto-connect failed: {ex.Message}"); }

        try
        {
            if (Hf8Enabled && !_deviceManager.IsHf8Connected)
            {
                _deviceManager.Hf8Controller.TryConnect();
                Hf8Connected = _deviceManager.IsHf8Connected;
                if (Hf8Connected)
                    AddSystemLog("HF8 haptic pad detected and connected");
            }
        }
        catch (Exception ex) { AddSystemLog($"HF8 auto-connect failed: {ex.Message}"); }
    }

    /// <summary>Persist every identity of the wheelbase so a GUID change
    /// still finds it next launch.</summary>
    private void RememberConnectedWheelbase(FfbDeviceInfo device)
    {
        _appSettings.LastConnectedDeviceInstanceId = device.DeviceInstance.InstanceGuid.ToString();
        _appSettings.LastConnectedDeviceProductName = device.ProductName;
        _appSettings.LastConnectedDeviceVidPid = VidPidOf(device);
        _appSettings.WheelbaseUserDisconnected = false;
    }
}
