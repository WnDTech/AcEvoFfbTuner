using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.FfbProviders;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.Services;
using AcEvoFfbTuner.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AcEvoFfbTuner.ViewModels;

public sealed partial class MainViewModel
{
    private async Task EnsureFfmpegAsync()
    {
        if (Services.FfmpegDownloader.IsInstalled)
        {
            IsFfmpegReady = true;
            FfmpegStatusText = "FFmpeg ready";
            return;
        }

        IsFfmpegDownloading = true;
        FfmpegStatusText = "Downloading FFmpeg...";

        var result = await Services.FfmpegDownloader.EnsureFfmpegAsync(
            new Progress<(int percent, string message)>(p =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    FfmpegStatusText = p.message;
                });
            }));

        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsFfmpegDownloading = false;
            if (result != null)
            {
                IsFfmpegReady = true;
                FfmpegStatusText = "FFmpeg ready";
            }
            else
            {
                FfmpegStatusText = "FFmpeg unavailable — recording disabled";
            }
        });
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        try
        {
            var allDevices = _deviceManager.EnumerateFfbDevices();

            AvailableDevices.Clear();
            foreach (var device in allDevices)
                AvailableDevices.Add(device);

            PanicDevices.Clear();
            PanicDevices.Add(new FfbDeviceInfo { ProductName = "None", IsFfbCapable = false });
            foreach (var device in allDevices)
                PanicDevices.Add(device);
        }
        catch (Exception ex)
        {
            StatusText = $"Device enumeration failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ConnectDevice()
    {
        if (SelectedDevice == null) return;

        var window = Application.Current?.MainWindow;
        if (window != null)
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);
            _deviceManager.SetWindowHandle(helper.Handle);
        }

        if (_deviceManager.TryConnectDevice(SelectedDevice))
        {
            IsDeviceConnected = true;
            DeviceName = SelectedDevice.ProductName;
            AutoDetectWheelTorque(SelectedDevice.ProductName);
            ForceInvertEnabled = _deviceManager.AutoDetectedForceInvert;
            IsAutoSetupAvailable = WheelMaxTorqueNm > 0;
            RefreshSnapshotButtonNames();
            RefreshPanicButtonNames();
            RestoreButtonSettings();
            if (!_uiUpdateTimer.IsEnabled)
                _uiUpdateTimer.Start();
            _telemetryLoop.AutoDetectAndSetProvider();
            RefreshProviderFeatures();
            string providerInfo = _telemetryLoop.ActiveProviderName != "DirectInput (Built-in)"
                ? $" | Provider: {_telemetryLoop.ActiveProviderName}"
                : "";
            string ledStatus = _deviceManager.IsLedControllerConnected
                ? $" | LEDs: {_deviceManager.LedControllerVendor}"
                : $" | LEDs: {_deviceManager.LedDiagnosticInfo.Split('\n').LastOrDefault() ?? "not found"}";
            string vibStatus = _deviceManager.SupportsPeriodicEffects
                ? ""
                : " | WARNING: wheel does not report periodic effect support — kerb/slip vibration may not work";
            StatusText = (_deviceManager.LastError ?? $"Connected to {SelectedDevice.ProductName}") + providerInfo + ledStatus + vibStatus;
            UpdateLedCapabilities();
            PushLedConfig();
            _appSettings.LastConnectedDeviceInstanceId = SelectedDevice.DeviceInstance.InstanceGuid.ToString();
            _appSettings.Save();
            AddSystemLog($"Device connected: {SelectedDevice.ProductName}");
            _voiceService.Speak("Wheelbase connected");
        }
        else
        {
            StatusText = _deviceManager.LastError ?? "Failed to connect to device";
        }
    }

    [RelayCommand]
    private void DisconnectDevice()
    {
        _telemetryLoop.SetFfbProvider(null);
        RefreshProviderFeatures();
        _deviceManager.DisconnectDevice();
        IsDeviceConnected = false;
        IsAutoSetupAvailable = false;
        DeviceName = "No device";
        ResetLedCapabilities();
        _appSettings.LastConnectedDeviceInstanceId = null;
        _appSettings.Save();
        AddSystemLog("Device disconnected");
        _voiceService.Speak("Wheelbase disconnected");
    }

    private void AutoDetectWheelTorque(string productName)
    {
        float torque = DetectTorqueFromProductName(productName);
        if (torque > 0)
        {
            WheelMaxTorqueNm = torque;
            StatusText = $"Detected {productName} — {torque:F1} Nm wheelbase";
        }
    }

    private static float DetectTorqueFromProductName(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0f;
        var n = name.ToUpperInvariant();

        if (n.Contains("R5")) return 5.5f;
        if (n.Contains("R9")) return 9f;
        if (n.Contains("R12")) return 12f;
        if (n.Contains("R16")) return 16f;
        if (n.Contains("R21")) return 21f;

        if (n.Contains("CSL DD")) return 5f;
        if (n.Contains("CLUBSPORT DD")) return n.Contains("8") ? 8f : 15f;
        if (n.Contains("DD PRO")) return 8f;
        if (n.Contains("DD1")) return 18f;
        if (n.Contains("DD2")) return 25f;

        if (n.Contains("ALPHA MINI") || n.Contains("ALPHA-MINI")) return 10f;
        if (n.Contains("ALPHA U")) return 22f;
        if (n.Contains("ALPHA")) return 15f;

        if (n.Contains("T818")) return 10f;
        if (n.Contains("T300")) return 2f;
        if (n.Contains("T150")) return 1.5f;
        if (n.Contains("G27")) return 2.5f;
        if (n.Contains("G29") || n.Contains("G920")) return 2.1f;

        return 0f;
    }

    [RelayCommand]
    private void AutoSetup()
    {
        if (!IsAutoSetupAvailable || !IsDeviceConnected) return;

        string deviceName = DeviceName;
        float torque = WheelMaxTorqueNm;
        if (torque <= 0) return;

        EvoDetectedSettings? evoSettings = null;
        var raw = _telemetryLoop.LatestRaw;
        if (raw != null)
        {
            evoSettings = EvoSettingsDetector.DetectFromRaw(raw);
            if (evoSettings != null)
                evoSettings = new EvoDetectedSettings
                {
                    FfbStrength = evoSettings.FfbStrength,
                    CarFfbMultiplier = evoSettings.CarFfbMultiplier,
                    SteerDegrees = evoSettings.SteerDegrees,
                    RecommendedOutputGain = evoSettings.RecommendedOutputGain,
                    RecommendedNormalizationScale = evoSettings.RecommendedNormalizationScale,
                    RecommendedSteeringLock = evoSettings.RecommendedSteeringLock,
                    IsValid = false
                };
        }

        var profile = WheelbaseAutoConfigurator.GenerateProfile(torque, deviceName, null);
        if (evoSettings != null)
            profile.SteeringLockDegrees = evoSettings.RecommendedSteeringLock;

        profile.Name = $"Auto Setup - {deviceName}";

        var existing = Profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing != null)
            _profileManager.DeleteProfile(existing);

        _profileManager.SaveProfile(profile);
        RefreshProfiles();

        SelectedProfile = profile;
        profile.ApplyToPipeline(_pipeline);
        profile.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
        LoadProfileValues(profile);
        _profileManager.SetActiveProfile(profile);

        var wheelType = WheelbaseAutoConfigurator.DetectWheelType(deviceName);
        AutoSetupStatus = $"Auto Setup — {deviceName} ({torque:F1}Nm, {wheelType})";
        StatusText = AutoSetupStatus;
    }

    [RelayCommand]
    private void PanicStop()
    {
        IsRunning = false;
        _telemetryLoop.Stop();
        _uiUpdateTimer.Stop();
        _deviceManager.ZeroForce();
        StatusText = "PANIC STOP - Telemetry stopped, FFB zeroed";
    }

    [RelayCommand(CanExecute = nameof(CanTestHaptics))]
    private async Task TestHaptics()
    {
        if (IsHapticTestRunning) return;
        IsHapticTestRunning = true;
        TestHapticsCommand.NotifyCanExecuteChanged();

        try
        {
            var provider = _telemetryLoop.ActiveProvider;
            const int durationMs = 500;
            const double frequencyHz = 50.0;
            const double amplitude = 0.5;
            int stepMs = 2;
            int steps = durationMs / stepMs;

            StatusText = "Haptic test: 50Hz sine wave (500ms)...";

            if (provider is FanatecProvider fanatec)
                fanatec.TriggerAbsRumble(true);

            for (int i = 0; i < steps; i++)
            {
                double t = i * stepMs / 1000.0;
                float signal = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * t));
                float absSignal = (float)Math.Abs(signal);

                if (provider != null && provider.IsInitialized)
                {
                    provider.UpdateTorque(signal);
                    var haptic = new HapticData { VibrationIntensity = absSignal };
                    provider.SetHaptics(haptic);
                }
                else
                {
                    _deviceManager.SendConstantForce(signal);
                    _deviceManager.SetTargetVibration(absSignal);
                }

                await Task.Delay(stepMs);
            }

            if (provider != null && provider.IsInitialized)
            {
                provider.UpdateTorque(0f);
                provider.SetHaptics(new HapticData());
            }
            else
            {
                _deviceManager.SendConstantForce(0f);
                _deviceManager.SetTargetVibration(0f);
            }

            if (provider is FanatecProvider fanatec2)
                fanatec2.TriggerAbsRumble(false);

            StatusText = "Haptic test complete";
        }
        catch (Exception ex)
        {
            StatusText = $"Haptic test failed: {ex.Message}";
        }
        finally
        {
            IsHapticTestRunning = false;
            TestHapticsCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshProviderFeatures()
    {
        ActiveFeatures.Clear();
        var provider = _telemetryLoop.ActiveProvider;

        if (provider == null)
        {
            HalProviderName = "None";
            IsHalSdkConnected = false;
            IsHalHapticEngineActive = false;
            IsHalPeripheralSynced = false;
            return;
        }

        ActiveFeatures.Add(provider.ProviderName);
        HalProviderName = provider.ProviderName;
        IsHalSdkConnected = provider.IsInitialized;
        IsHalHapticEngineActive = provider.IsInitialized && provider.IsAvailable;
        IsHalPeripheralSynced = IsDeviceConnected && provider.IsInitialized;

        if (provider is FanatecProvider fp)
        {
            if (fp.IsFullForceAvailable) ActiveFeatures.Add("FullForce Active");
            if (fp.HasRimRevLeds) ActiveFeatures.Add("Rev LEDs");
            if (fp.HasRumbleMotors) ActiveFeatures.Add("Rim Rumble");
            if (fp.HasRimLedDisplay) ActiveFeatures.Add("Gear Display");
            if (fp.MaxTorqueNm > 0) ActiveFeatures.Add($"Torque Capped {fp.MaxTorqueNm}Nm");
            if (fp.IsMauriceDetected) ActiveFeatures.Add("Maurice");
        }
        else if (provider is GenericDirectInputProvider)
        {
            ActiveFeatures.Add("DirectInput Only");
        }
        else
        {
            ActiveFeatures.Add("SDK Pending");
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestBuzz))]
    private async Task TestBuzz()
    {
        if (IsTestBuzzRunning) return;
        IsTestBuzzRunning = true;
        TestBuzzCommand.NotifyCanExecuteChanged();

        try
        {
            var provider = _telemetryLoop.ActiveProvider;
            AddSystemLog("Test Buzz: 500ms diagnostic vibration...");

            const int durationMs = 500;
            const double frequencyHz = 60.0;
            const double amplitude = 0.6;
            int stepMs = 4;
            int steps = durationMs / stepMs;

            for (int i = 0; i < steps; i++)
            {
                double t = i * stepMs / 1000.0;
                float signal = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * t));
                float absSignal = (float)Math.Abs(signal);

                if (provider != null && provider.IsInitialized)
                {
                    provider.UpdateTorque(signal);
                    provider.SetHaptics(new HapticData { VibrationIntensity = absSignal });
                }
                else
                {
                    _deviceManager.SendConstantForce(signal);
                    _deviceManager.SetTargetVibration(absSignal);
                }

                await Task.Delay(stepMs);
            }

            if (provider != null && provider.IsInitialized)
            {
                provider.UpdateTorque(0f);
                provider.SetHaptics(new HapticData());
            }
            else
            {
                _deviceManager.SendConstantForce(0f);
                _deviceManager.SetTargetVibration(0f);
            }

            AddSystemLog("Test Buzz: complete");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Test Buzz: failed — {ex.Message}");
        }
        finally
        {
            IsTestBuzzRunning = false;
            TestBuzzCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void ToggleScreenRecording()
    {
        if (_gameRecordingService.IsRecording)
        {
            _gameRecordingService.StopRecording();
            IsScreenRecording = false;
        }
        else
        {
            _gameRecordingService.StartRecording();
        }
    }

    partial void OnVoiceEnabledChanged(bool value)
    {
        _voiceService.Enabled = value;
        _appSettings.VoiceEnabled = value;
        _appSettings.Save();
    }

    partial void OnVoiceVolumeChanged(int value)
    {
        _voiceService.Volume = value;
        _appSettings.VoiceVolume = value;
        _appSettings.Save();
    }

    [RelayCommand]
    private void OpenVoiceCacheFolder()
    {
        try
        {
            var dir = GetVoiceCachePath();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }

    private static string GetVoiceCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "voice-cache");
    }

    partial void OnSelectedVoiceChanged(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == _voiceService.SelectedVoice) return;
        _voiceService.SelectedVoice = value;
        _appSettings.VoiceName = value;
        _appSettings.Save();

        if (_voiceInitialized)
            _voiceService.Speak("This is {0}", value);
    }

    [RelayCommand]
    private void OpenVoiceSettings()
    {
        Services.VoiceService.OpenSpeechSettings();
    }

    [RelayCommand]
    private async Task InstallNaturalVoicesAsync()
    {
        if (IsInstallingVoices) return;
        IsInstallingVoices = true;
        try
        {
            await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("powershell.exe")
                {
                    Arguments = "-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.Speech.en-US~~~0.0.1.0' -LimitAccess -Source 'WindowsUpdate'\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(120000);
            });
            RefreshVoices();
            if (_voiceService.HasNaturalVoice)
                _voiceService.Speak("Natural voices installed");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Voice install failed: {ex.Message}");
        }
        finally
        {
            IsInstallingVoices = false;
        }
    }

    partial void OnSelectedRecordingDeviceIdChanged(string? value)
    {
        _appSettings.LastRecordingDeviceId = value;
        _appSettings.Save();
    }

    private void RefreshVoices()
    {
        var current = _voiceService.SelectedVoice;
        AvailableVoices.Clear();
        foreach (var v in _voiceService.AvailableVoices)
            AvailableVoices.Add(v);

        if (!string.IsNullOrEmpty(_appSettings.VoiceName) && AvailableVoices.Contains(_appSettings.VoiceName))
            SelectedVoice = _appSettings.VoiceName;
        else if (current != null && AvailableVoices.Contains(current))
            SelectedVoice = current;
        else if (AvailableVoices.Count > 0)
            SelectedVoice = AvailableVoices[0];
    }

    [RelayCommand]
    private void RefreshRecordingDevices()
    {
        try
        {
            AudioOutputDevices.Clear();
            _deviceNameToId.Clear();
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active);

            var savedId = _appSettings.LastRecordingDeviceId;

            foreach (var device in devices)
            {
                var name = device.FriendlyName;
                AudioOutputDevices.Add(name);
                _deviceNameToId[name] = device.ID;
                if (device.ID == savedId)
                    SelectedAudioOutputDevice = name;
            }

            if (string.IsNullOrEmpty(SelectedAudioOutputDevice) && AudioOutputDevices.Count > 0)
                SelectedAudioOutputDevice = AudioOutputDevices[0];
        }
        catch (Exception ex)
        {
            StatusText = $"Audio device enumeration failed: {ex.Message}";
        }
    }

    [RelayCommand]
#pragma warning disable CS1998
    private async Task StartRecording()
#pragma warning restore CS1998
    {
        var deviceId = GetSelectedOutputDeviceId();
        if (deviceId == null)
        {
            RecordingStatus = "No output device selected.";
            return;
        }

        NAudio.CoreAudioApi.MMDevice? device = null;
        try
        {
            var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Could not open device: {ex.Message}";
            return;
        }

        if (device == null)
        {
            RecordingStatus = "Device not found.";
            return;
        }

        try
        {
            var soundsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "Sounds");
            Directory.CreateDirectory(soundsDir);
            _recordingTempPath = Path.Combine(soundsDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

            _selectedOutputDeviceId = deviceId;
            _loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture(device);

            RecordingStatus = $"Capturing from: {device.FriendlyName} ({_loopbackCapture.WaveFormat})";

            _waveWriter = new NAudio.Wave.WaveFileWriter(
                _recordingTempPath, _loopbackCapture.WaveFormat);

            _loopbackCapture.DataAvailable += (s, e) =>
            {
                if (_waveWriter == null) return;
                _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                var duration = (double)_waveWriter.Position / _waveWriter.WaveFormat.AverageBytesPerSecond;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                    RecordingStatus = $"Recording... {duration:F1}s");
            };

            _loopbackCapture.RecordingStopped += (s, e) =>
            {
                _waveWriter?.Flush();
                _waveWriter?.Dispose();
                _waveWriter = null;

                _loopbackCapture?.Dispose();
                _loopbackCapture = null;

                if (_recordingTempPath != null && File.Exists(_recordingTempPath))
                {
                    var rawPath = _recordingTempPath;
                    _recordingTempPath = null;
                    var finalPath = rawPath.Replace(".wav", "_final.wav");

                    try
                    {
                        ConvertToHighQualityWav(rawPath, finalPath);
                        File.Delete(rawPath);
                        rawPath = finalPath;
                    }
                    catch
                    {
                        try { File.Delete(finalPath); } catch { }
                    }

                    var savedPath = rawPath;
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        CustomStartupSoundPath = savedPath;
                        RecordingStatus = $"Saved: {Path.GetFileName(savedPath)}";
                        IsRecording = false;
                    });
                }
                else
                {
                    _recordingTempPath = null;
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        RecordingStatus = "Recording saved.";
                        IsRecording = false;
                    });
                }
            };

            _loopbackCapture.StartRecording();
            IsRecording = true;
            RecordingStatus = $"Recording from: {device.FriendlyName} — play audio now!";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Error: {ex.Message}";
            CleanupRecording();
        }
    }

    [RelayCommand]
#pragma warning disable CS1998
    private async Task StopRecording()
#pragma warning restore CS1998
    {
        try
        {
            _loopbackCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Error stopping: {ex.Message}";
            IsRecording = false;
        }
    }

    [RelayCommand]
    private void PreviewStartupSound()
    {
        if (string.IsNullOrEmpty(CustomStartupSoundPath) || !File.Exists(CustomStartupSoundPath))
        {
            RecordingStatus = "No sound file to preview.";
            return;
        }

        try
        {
            var player = new System.Windows.Media.MediaPlayer();
            player.Open(new Uri(CustomStartupSoundPath));
            player.MediaOpened += (s, e) => player.Play();
            player.MediaFailed += (s, e) => RecordingStatus = "Failed to play sound.";
            RecordingStatus = "Playing preview...";
        }
        catch (Exception ex)
        {
            RecordingStatus = $"Preview error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearStartupSound()
    {
        CustomStartupSoundPath = null;
        RecordingStatus = "Custom sound cleared. Using default.";
    }

    [RelayCommand]
    private void BrowseStartupSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio Files|*.mp3;*.wav;*.wma;*.ogg|All Files|*.*",
            Title = "Select Startup Sound"
        };

        if (dialog.ShowDialog() == true)
        {
            var soundsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "Sounds");
            Directory.CreateDirectory(soundsDir);
            var destPath = Path.Combine(soundsDir, Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, destPath, true);
            CustomStartupSoundPath = destPath;
            RecordingStatus = $"Sound set: {Path.GetFileName(destPath)}";
        }
    }

    private static void ConvertToHighQualityWav(string sourcePath, string destPath)
    {
        using var reader = new NAudio.Wave.WaveFileReader(sourcePath);
        var targetFormat = new NAudio.Wave.WaveFormat(48000, 24, reader.WaveFormat.Channels);

        using var writer = new NAudio.Wave.WaveFileWriter(destPath, targetFormat);
        using var resampler = new NAudio.Wave.MediaFoundationResampler(reader, targetFormat);
        resampler.ResamplerQuality = 60;

        var buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
        int bytesRead;
        while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            writer.Write(buffer, 0, bytesRead);
        }
        writer.Flush();
    }

    private void CleanupRecording()
    {
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;
        _waveWriter?.Dispose();
        _waveWriter = null;
        IsRecording = false;
        _selectedOutputDeviceId = null;
        if (_recordingTempPath != null)
        {
            try { if (File.Exists(_recordingTempPath)) File.Delete(_recordingTempPath); } catch { }
            _recordingTempPath = null;
        }
    }

    [RelayCommand]
    private async Task SendDiagnosticPack()
    {
        var mainWin = Application.Current?.MainWindow;
        WriteDiagLog("START", $"MainWindow={mainWin?.GetType().Name ?? "null"}");
        if (mainWin == null) return;

        var dialog = new Views.FeedbackDialog { Owner = mainWin };
        if (dialog.ShowDialog() != true)
        {
            WriteDiagLog("CANCELLED", "User cancelled feedback dialog");
            return;
        }

        IsSendingDiagnosticPack = true;
        DiagnosticPackStatus = "Auto-saving...";
        StatusText = "Auto-saving profile and snapshot...";

        try
        {
            WriteDiagLog("STEP", "Auto-saving profile...");
            AutoSaveDiagnosticProfile();
            WriteDiagLog("STEP", "Auto-saving snapshot...");
            (mainWin as Views.MainWindow)?.AutoSaveSnapshot();

            StatusText = "Sending diagnostic pack...";
            DiagnosticPackStatus = "Sending...";
            var progress = new Progress<string>(msg =>
            {
                StatusText = msg;
                DiagnosticPackStatus = msg;
                WriteDiagLog("PROGRESS", msg);
            });
            WriteDiagLog("STEP", "Calling DiagnosticPackService.SendAsync...");
            var (success, message) = await DiagnosticPackService.SendAsync(dialog.Feedback, progress);
            StatusText = message;
            WriteDiagLog("RESULT", $"Success={success}, Message={message}");

            if (!success)
            {
                DiagnosticPackStatus = "Failed";
                MessageBox.Show(mainWin, $"{message}\n\nLog: {DiagLogDir()}", "Send Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                DiagnosticPackStatus = message;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            DiagnosticPackStatus = "Error";
            WriteDiagLog("ERROR", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show(mainWin, $"Failed to send diagnostics:\n\n{ex.Message}\n\nLog: {DiagLogDir()}", "Send Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSendingDiagnosticPack = false;
        }
    }

    private static string DiagLogDir()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "diag_send.log");
    }

    private static void WriteDiagLog(string category, string detail)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "diag_send.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {detail}\n");
        }
        catch { }
    }

    private static void LogUpdate(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        UpdateStatusText = "Checking for updates...";
        IsUpdateAvailable = false;

        try
        {
            var service = new GitHubUpdateService();
            LogUpdate($"CheckForUpdates: current version={service.CurrentVersion}, checking...");
            var update = await service.CheckForUpdateAsync();

            if (update != null)
            {
                _pendingUpdate = update;
                LatestVersionText = $"v{update.Version}";
                IsUpdateAvailable = true;
                UpdateStatusText = $"Update available: v{update.Version}";
                LogUpdate($"CheckForUpdates: update available v{update.Version}, url={update.DownloadUrl}");
            }
            else
            {
                UpdateStatusText = $"You're up to date (v{service.CurrentVersion.ToString(3)})";
                _pendingUpdate = null;
                LogUpdate("CheckForUpdates: up to date");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "Update check failed";
            LogUpdate($"CheckForUpdates FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_pendingUpdate == null || IsDownloadingUpdate) return;

        IsDownloadingUpdate = true;
        DownloadProgressPercent = 0;
        UpdateStatusText = "Downloading update...";
        LogUpdate($"DownloadAndInstall: starting for v{_pendingUpdate.Version}");

        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.State == DownloadState.Downloading)
                {
                    DownloadProgressPercent = p.Percent;
                    UpdateStatusText = $"Downloading update... {p.Percent}%";
                }
                else
                {
                    UpdateStatusText = "Launching installer...";
                }
            });

            await GitHubUpdateService.DownloadAndInstallAsync(_pendingUpdate, progress);

            LogUpdate("DownloadAndInstall: installer launched successfully, shutting down app");
            UpdateStatusText = "Installer launched — closing app...";
            Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Download failed: {ex.Message}";
            LogUpdate($"DownloadAndInstall FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            IsDownloadingUpdate = false;
            DownloadProgressPercent = 0;
        }
    }
}
