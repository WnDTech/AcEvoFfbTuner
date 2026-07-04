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
    private void WireTelemetryLoopEvents(TelemetryLoop newLoop)
    {
        newLoop.StatusChanged += status => Application.Current?.Dispatcher.Invoke(() => StatusText = status);
        newLoop.GameConnectionChanged += () => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsGameConnected = newLoop.IsGameConnected;
            if (IsGameConnected)
                AddSystemLog($"Game connected ({GameDisplayName})");
            else
            {
                _gameRecordingService.StopRecording();
                IsScreenRecording = false;
                AddSystemLog("Game disconnected");
            }
        });
        newLoop.TrackMapCompleted += map => Application.Current?.Dispatcher.Invoke(() =>
        {
            IsTrackMapAvailable = true;
            IsTrackMapRecording = false;
            TrackLengthM = map.TrackLengthM;
            TrackWaypointCount = map.Waypoints.Count;
            CornerCount = map.Corners.Count;
            SectorCount = map.Sectors.Count;
            if (string.IsNullOrEmpty(map.TrackName) || map.TrackName.StartsWith("track_"))
                map.TrackName = newLoop.DetectedTrackName;
            DetectedTrackName = map.TrackName;
            IsPitDetected = map.PitLane.IsDetected;
            TrackMapStatus = $"{map.Waypoints.Count} pts | {map.TrackLengthM:F0}m | {map.Corners.Count} corners | {map.Sectors.Count} sectors";
        });
        newLoop.StaticDataReceived += (trackName, config, lengthM, latitude, longitude) => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedTrackName = trackName;
            if (!string.IsNullOrEmpty(trackName))
                StatusText = $"Connected — Track: {trackName} ({config}) {lengthM:F0}m";
        });
        newLoop.CarModelChanged += carModel => Application.Current?.Dispatcher.Invoke(() =>
        {
            DetectedCarModel = carModel;
            StatusText = $"Car detected: {carModel}";
        });
        newLoop.TrackChanged += newTrackName => Application.Current?.Dispatcher.Invoke(() =>
        {
            _mapClearedByUser = false;
            _lastAutoLoadedTrack = null;
            IsTrackMapAvailable = false;
            IsTrackMapRecording = false;
            StatusText = $"Track changed to: {newTrackName}";
        });
        _discordPresence.Attach(newLoop);
    }

    private void OnUiUpdate(object? sender, EventArgs e)
    {
        var raw = _telemetryLoop.LatestRaw;
        var processed = _telemetryLoop.LatestProcessed;

        if (processed != null && raw != null)
        {
            CurrentForceOutput = processed.MainForce;
            CurrentRawForce = processed.RawFinalFf;
            IsClipping = processed.IsClipping;
            SpeedKmh = processed.SpeedKmh;
            var g = raw.DisplayAccG?.Length > 0 && raw.DisplayAccG.Any(v => v != 0) ? raw.DisplayAccG : raw.AccG;
            LatG = g?.Length > 0 ? g[0] : 0f;
            LongG = g?.Length > 1 ? g[1] : 0f;
            ActiveLedCount = _deviceManager.ActiveLedCount;

            float highFreqHaptics = processed.VibrationForce;
            UpdateSignalMonitor(processed.MainForce, highFreqHaptics);

            _gameRecordingService.OnTelemetryTick(processed.SpeedKmh);
            IsScreenRecording = _gameRecordingService.IsRecording;

            if (IsGameConnected && raw.FfbStrength > 0.01f && !ShowGameFfbWarning && _ffbWarningDismissed != true)
                ShowGameFfbWarning = true;
            int lockDeg = SteeringLockDegrees;
            if (lockDeg <= 0) lockDeg = 900;
            SteerAngle = (float)raw.SteerAngle * (lockDeg / 2f);

            DebugSnapshot =
                $"=== RAW SHARED MEMORY ===\n" +
                $"Mz:  FL={raw.Mz[0]:F4}  FR={raw.Mz[1]:F4}  RL={raw.Mz[2]:F4}  RR={raw.Mz[3]:F4}\n" +
                $"Fx:  FL={raw.Fx[0]:F4}  FR={raw.Fx[1]:F4}  RL={raw.Fx[2]:F4}  RR={raw.Fx[3]:F4}\n" +
                $"Fy:  FL={raw.Fy[0]:F4}  FR={raw.Fy[1]:F4}  RL={raw.Fy[2]:F4}  RR={raw.Fy[3]:F4}\n" +
                $"FinalFf:       {raw.FinalFf:F6}\n" +
                $"WheelLoad: FL={raw.WheelLoad[0]:F1}  FR={raw.WheelLoad[1]:F1}  RL={raw.WheelLoad[2]:F1}  RR={raw.WheelLoad[3]:F1}\n" +
                $"SteerAngle:    {raw.SteerAngle:F4}  SteerDeg={raw.SteerDegrees}  Lock={SteeringLockDegrees}°\n" +
                $"Speed:         {raw.SpeedKmh:F1} km/h\n" +
                $"AccG:          X={raw.AccG[0]:F3}  Y={raw.AccG[1]:F3}  Z={raw.AccG[2]:F3}\n" +
                $"SlipRatio:     FL={raw.SlipRatio[0]:F4}  FR={raw.SlipRatio[1]:F4}  RL={raw.SlipRatio[2]:F4}  RR={raw.SlipRatio[3]:F4}\n" +
                $"SlipAngle:     FL={raw.SlipAngle[0]:F4}  FR={raw.SlipAngle[1]:F4}  RL={raw.SlipAngle[2]:F4}  RR={raw.SlipAngle[3]:F4}\n" +
                $"FfbStrength:   {raw.FfbStrength:F4}  CarMult={raw.CarFfbMultiplier:F4}\n" +
                $"\n=== VIBRATIONS ===\n" +
                $"Kerb: {raw.KerbVibration:F4}  Slip: {raw.SlipVibrations:F4}  Road: {raw.RoadVibrations:F4}  ABS: {raw.AbsVibrations:F4}\n" +
                $"VibForce: {processed.VibrationForce:F4}  MasterGain: {_pipeline.VibrationMixer.MasterGain:F2}  AbsGain: {_pipeline.VibrationMixer.AbsGain:F2}\n" +
                $"\n=== PIPELINE STAGES ===\n" +
                $"MzFront (norm): {processed.ChannelMzFront:F6}\n" +
                $"FxFront (norm): {processed.ChannelFxFront:F6}\n" +
                $"FyFront (norm): {processed.ChannelFyFront:F6}\n" +
                $"PostCompress:    {processed.PostCompressionForce:F6}\n" +
                $"PostLUT:         {processed.PostLutForce:F6}\n" +
                $"PostDamping:    {processed.PostDampingForce:F6}\n" +
                $"PostGainOut:    {processed.PostOutputGainForce:F6}\n" +
                $"PostDynamic:    {processed.PostDynamicForce:F6}\n" +
                $"OUTPUT:         {processed.MainForce:F6}   Clipping={processed.IsClipping}\n" +
                $"AutoGain:       {processed.AutoGainApplied:F4}\n" +
                $"\n=== FFB DEVICE ===\n" +
                $"Acquired:       {IsDeviceConnected}\n" +
                $"MasterGain:     {_pipeline.MasterGain:F2}\n" +
                $"OutputGain:     {_pipeline.OutputGain:F2}\n" +
                $"ClipThreshold:  {_pipeline.OutputClipper.SoftClipThreshold:F2}\n" +
                $"MzScale:        {_pipeline.ChannelMixer.MzScale:F0}\n" +
                $"FxScale:        {_pipeline.ChannelMixer.FxScale:F0}\n" +
                $"FyScale:        {_pipeline.ChannelMixer.FyScale:F0}\n" +
                $"LastError:      {_deviceManager.LastError ?? "none"}\n" +
                $"PeriodicFX:     {_deviceManager.SupportsPeriodicEffects}\n" +
                $"PacketId:       {raw.PacketId}  PPS: {_telemetryLoop.PacketsPerSecond}\n" +
                $"Latency:        {_telemetryLoop.LastLatencyMs:F2}ms (avg: {_telemetryLoop.AvgLatencyMs:F2}ms)\n" +
                $"\n=== LED CONTROLLER ===\n" +
                $"Connected:      {_deviceManager.IsLedControllerConnected}\n" +
                $"Vendor:         {_deviceManager.LedControllerVendor}\n" +
                $"RPM:            {raw.RpmPercent:F1}%  ShiftUp={raw.IsChangeUpRpm}  Limiter={raw.IsRpmLimiterOn}\n" +
                $"ABS:  InAction={raw.AbsInAction}  Level={raw.AbsLevel:F3}  Gfx={raw.AbsActiveGfx}  Vib={raw.AbsVibrations:F4}  Brake={raw.BrakeInput:F3}\n" +
                $"LED Config:     Brightness={LedBrightness}% FlashRate={LedFlashRate} AbsFlash={LedAbsFlashEnabled}\n" +
                $"{_deviceManager.LedDiagnosticInfo}";

            if (IsRunning && IsDeviceConnected)
            {
                if (_deviceManager.HasLostDeviceAccess)
                {
                    IsDeviceConnected = false;
                    DeviceName = "Lost exclusive access";
                    StatusText = "FFB device lost exclusive access — auto-reconnect in progress...";
                }
                else
                {
                    var err = _deviceManager.LastError;
                    if (!string.IsNullOrEmpty(err))
                        StatusText = err;
                }
            }

            _profilerMinOut = Math.Min(_profilerMinOut, processed.MainForce);
            _profilerMaxOut = Math.Max(_profilerMaxOut, processed.MainForce);
            _profilerSumOut += processed.MainForce;
            _profilerFrames++;
            if (processed.IsClipping) _profilerClips++;
            _profilerPeakMz = Math.Max(_profilerPeakMz, Math.Abs(processed.ChannelMzFront));
            _profilerPeakFx = Math.Max(_profilerPeakFx, Math.Abs(processed.ChannelFxFront));
            _profilerPeakFy = Math.Max(_profilerPeakFy, Math.Abs(processed.ChannelFyFront));

            if (_profilerFrames >= ProfilerStatsWindow)
            {
                if (_isLiveMonitoring)
                {
                    _profilerSamples.Add(new ProfilerSample(
                        _profilerMinOut, _profilerMaxOut, _profilerSumOut / _profilerFrames,
                        (float)_profilerClips / _profilerFrames * 100f,
                        _profilerPeakMz, _profilerPeakFx, _profilerPeakFy,
                        _profilerFrames));

                    if (_profilerSamples.Count >= MinMonitorSamples)
                        _ = ProcessMonitorSamplesAsync();
                }

                _profilerMinOut = float.MaxValue;
                _profilerMaxOut = float.MinValue;
                _profilerSumOut = 0f;
                _profilerFrames = 0;
                _profilerClips = 0;
                _profilerPeakMz = 0f;
                _profilerPeakFx = 0f;
                _profilerPeakFy = 0f;
            }

            if (Application.Current?.MainWindow is MainWindow mw)
            {
                mw.UpdateProfiler(
                    raw.SpeedKmh, raw.SteerAngle,
                    processed.MainForce, processed.RawFinalFf,
                    processed.PostCompressionForce, processed.PostDampingForce,
                    processed.PostOutputGainForce, processed.PostDynamicForce,
                    processed.ChannelMzFront, processed.ChannelFxFront, processed.ChannelFyFront,
                    processed.PostLutForce, processed.IsClipping,
                    raw.GasInput, raw.BrakeInput, raw, processed.WetnessFactor,
                    lockDeg);

                mw.UpdateCalibrationWizard(raw.SpeedKmh, processed.MainForce, processed.IsClipping);
                mw.UpdateSetupWizard(raw.SpeedKmh, processed.MainForce, raw.SteerAngle, processed.IsClipping, processed.ChannelMzFront, raw.BrakeInput, raw.GasInput);

                bool r3eAi = _telemetryLoop.IsR3eAiControlled;
                if (r3eAi)
                {
                    float physicalNorm = _telemetryLoop.LatestPhysicalWheelNormalized;
                    float physicalAngleDeg = physicalNorm * (lockDeg / 2f);
                    mw.UpdateWheelCenter(physicalNorm, physicalAngleDeg);
                    mw.ShowWheelCenterOverlay();
                }
                else
                {
                    mw.CloseWheelCenterOverlay();
                }
            }

            WetWeatherCurrentFactor = processed.WetnessFactor;
            CurrentTyreCompoundFront = processed.TyreCompoundFrontName;
            CurrentTyreCompoundRear = processed.TyreCompoundRearName;
            CurrentTyreCategoryName = processed.TyreCategory.ToString();

            CarPosX = raw.CarX;
            CarPosZ = raw.CarZ;
            CarHeading = raw.Heading;

            if (_telemetryLoop.PositionDetector.HasMap)
            {
                var pos = _telemetryLoop.PositionDetector.GetPosition(raw.CarX, raw.CarZ);
                if (pos.IsValid)
                {
                    IsOnTrackMap = pos.IsOnTrack;
                    TrackProgress = pos.TrackProgress;
                    TrackDistanceFromCenter = pos.DistanceFromCenterM;

                    if (pos.CurrentCorner != null)
                    {
                        _lastCurrentCorner = pos.CurrentCorner;
                        CurrentCornerName = pos.CurrentCorner.DisplayName;
                        CurrentCornerType = pos.CurrentCorner.TypeName;
                    }
                    else
                    {
                        _lastCurrentCorner = null;
                        CurrentCornerName = "Straight";
                        CurrentCornerType = "";
                    }

                    if (pos.CurrentSector != null)
                    {
                        _sectorStatsCounter++;
                        if (_sectorStatsCounter >= 30)
                        {
                            _sectorStatsCounter = 0;
                            var map = _telemetryLoop.MapBuilder.CurrentMap;
                            if (map != null)
                                map.UpdateSectorStats(_telemetryLoop.ForceHeatmap);
                        }

                        CurrentSectorNumber = pos.CurrentSector.SectorNumber;
                        var s = pos.CurrentSector;
                        if (s.SampleCount > 0)
                        {
                            SectorStats = $"AvgF={s.AvgOutputForce:F3} PeakF={s.PeakOutputForce:F3} Clip={s.ClippingPct:F1}% AvgMz={s.AvgMzFront:F3} AvgSpd={s.AvgSpeedKmh:F0}";
                        }
                    }
                }
            }

            IsTrackMapRecording = _telemetryLoop.MapBuilder.IsRecording;
            IsTrackMapAvailable = _telemetryLoop.MapBuilder.HasCompleteMap;
            TrackWaypointCount = _telemetryLoop.MapBuilder.WaypointCount;

            var currentMap = _telemetryLoop.MapBuilder.CurrentMap;
            if (currentMap != null)
            {
                CornerCount = currentMap.Corners.Count;
                SectorCount = currentMap.Sectors.Count;
                IsPitDetected = currentMap.PitLane.IsDetected;
            }

            IsInPit = raw.IsInPitLane;
            CompletedLapCount = _telemetryLoop.LapRecorder.CompletedLaps.Count;
            var laps = _telemetryLoop.LapRecorder.CompletedLaps;
            if (laps.Count >= 2)
            {
                var last = laps[^1];
                var prev = laps[^2];
                float forceDelta = last.AvgOutputForce - prev.AvgOutputForce;
                float clipDelta = last.ClippingPct - prev.ClippingPct;
                LapComparison = $"Lap{last.LapNumber} vs Lap{prev.LapNumber}: ΔForce={forceDelta:+F3;-F3} ΔClip={clipDelta:+F1;-F1}%";
            }

            var runningSummary = _telemetryLoop.DiagnosticHeatmap.GetRunningSummary();
            var coverage = _telemetryLoop.DiagnosticHeatmap.TrackCoveragePct;
            var sufficientCoverage = _telemetryLoop.DiagnosticHeatmap.HasSufficientCoverage;

            if (runningSummary != null && runningSummary.TotalEvents > 0)
            {
                DiagnosticSummary = $"Snaps:{runningSummary.TotalSnapEvents} Osc:{runningSummary.TotalOscillations} Clip:{runningSummary.TotalClippingEvents} Anomaly:{runningSummary.TotalForceAnomalies} | Corner:{runningSummary.CornerEventPct:F0}% Suspicious:{runningSummary.SuspiciousPct:F0}%";
                DiagnosticVerdict = runningSummary.Verdict;
            }

            DiagnosticCoverage = sufficientCoverage
                ? $"Coverage: {coverage:F0}% — ready for recommendations"
                : $"Coverage: {coverage:F0}% — keep driving ({60 - (int)coverage}% more needed)";

            var completedSummary = _telemetryLoop.DiagnosticHeatmap.LatestLapSummary;
            if (sufficientCoverage)
            {
                if (completedSummary != null && completedSummary.TotalEvents > 0)
                {
                    UpdateRecommendations(completedSummary);
                }
                else if (runningSummary != null && runningSummary.TotalEvents > 0)
                {
                    UpdateRecommendations(runningSummary);
                }
            }

            var lastEvt = _telemetryLoop.EventDetector.LastEvent;
            if (lastEvt != null)
            {
                LastEventInfo = $"{lastEvt.EventType} @ {lastEvt.CornerName ?? "straight"} ({lastEvt.Classification}) ΔF={lastEvt.ForceDelta:F3}";
            }

            if (Application.Current?.MainWindow is MainWindow mw2)
            {
                mw2.UpdateTrackMapDisplay(
                    raw.CarX, raw.CarZ, raw.Heading, raw.SpeedKmh,
                    IsOnTrackMap, raw.Npos, TrackProgress, TrackDistanceFromCenter,
                    _telemetryLoop.MapBuilder.CurrentMap?.TrackLengthM ?? 0f,
                    TrackWaypointCount, IsTrackMapRecording, IsTrackMapAvailable,
                    _telemetryLoop.MapBuilder.CurrentMap,
                    _telemetryLoop.ForceHeatmap.GetSnapshot(),
                    ShowForceHeatmap,
                    ShowTrackEdges,
                    _telemetryLoop.DiagnosticHeatmap.GetSnapshot(),
                    ShowDiagnostics,
                    TrackLatitude,
                    TrackLongitude,
                    TrackRotation);
            }
        }

        var racePhysics = _telemetryLoop.LatestPhysicsRaw;
        var raceGraphics = _telemetryLoop.LatestGraphicsRaw;
        if (racePhysics != null && raceGraphics != null && _raceInfoOverlay != null)
        {
            _raceInfoProcessor.Process(racePhysics.Value, raceGraphics.Value, out var raceInfo);
            _raceInfoOverlay.UpdateData(raceInfo, racePhysics.Value, raceGraphics.Value);
        }
        if (IsDeviceConnected || IsAssigningSnapshotButton || IsAssigningPanicButton)
            PollSnapshotButton();

        CheckConflictingApps();

        PacketsPerSecond = _telemetryLoop.PacketsPerSecond;
        IsGameConnected = _telemetryLoop.IsGameConnected;
    }

    private void UpdateSignalMonitor(float lowFreqValue, float highFreqValue)
    {
        _signalMonitorTickCounter++;
        if (_signalMonitorTickCounter % 3 != 0) return;

        _signalLowFreqBuffer.Add(lowFreqValue);
        _signalHighFreqBuffer.Add(highFreqValue);

        while (_signalLowFreqBuffer.Count > _signalMonitorMaxPoints)
            _signalLowFreqBuffer.RemoveAt(0);
        while (_signalHighFreqBuffer.Count > _signalMonitorMaxPoints)
            _signalHighFreqBuffer.RemoveAt(0);

        double canvasWidth = 260;
        double canvasHeight = 80;
        double midY = canvasHeight / 2.0;
        double stepX = canvasWidth / _signalMonitorMaxPoints;

        var low = new System.Windows.Media.PointCollection(_signalLowFreqBuffer.Count);
        for (int i = 0; i < _signalLowFreqBuffer.Count; i++)
        {
            double y = midY - Math.Clamp(_signalLowFreqBuffer[i], -1, 1) * midY;
            low.Add(new System.Windows.Point(i * stepX, y));
        }

        var high = new System.Windows.Media.PointCollection(_signalHighFreqBuffer.Count);
        for (int i = 0; i < _signalHighFreqBuffer.Count; i++)
        {
            double y = midY - Math.Clamp(_signalHighFreqBuffer[i], -1, 1) * midY;
            high.Add(new System.Windows.Point(i * stepX, y));
        }

        SignalMonitorLowFreq = low;
        SignalMonitorHighFreq = high;
    }

    private void AddSystemLog(string entry)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var formatted = $"[{timestamp}] {entry}";
        SystemLogEntries.Add(formatted);
        while (SystemLogEntries.Count > 500)
            SystemLogEntries.RemoveAt(0);

        RecentSystemLogEntries.Clear();
        if (SystemLogEntries.Count > 0)
            RecentSystemLogEntries.Add(SystemLogEntries[^1]);

        SaveSystemLog();
    }

    private void SaveSystemLog()
    {
        try
        {
            var dir = Path.GetDirectoryName(SystemLogFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(SystemLogEntries.ToList());
            File.WriteAllText(SystemLogFilePath, json);
        }
        catch { }
    }

    private void LoadSystemLog()
    {
        try
        {
            if (!File.Exists(SystemLogFilePath)) return;
            var json = File.ReadAllText(SystemLogFilePath);
            var entries = JsonSerializer.Deserialize<List<string>>(json);
            if (entries == null) return;
            SystemLogEntries.Clear();
            foreach (var e in entries)
                SystemLogEntries.Add(e);

            RecentSystemLogEntries.Clear();
            if (SystemLogEntries.Count > 0)
            {
                RecentSystemLogEntries.Add(SystemLogEntries[0]);
            }
        }
        catch { }
    }

    public void Initialize()
    {
        _profileManager.AutoMigrate = _appSettings.AutoProfileUpgrade;
        _profileManager.Initialize();
        RefreshProfiles();
        RefreshDevices();
        RestoreButtonSettings();

        if (_profileManager.ActiveProfile != null)
        {
            _profileManager.ActiveProfile.ApplyToPipeline(_pipeline);
            _profileManager.ActiveProfile.ApplyToStaticFriction(_telemetryLoop.StaticFriction);
            LoadProfileValues(_profileManager.ActiveProfile);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name == _profileManager.ActiveProfile.Name);
        }

        LoadSystemLog();

        _ = CheckForUpdatesAsync();
        _ = EnsureFfmpegAsync();
    }

    public void LoadAppSettings()
    {
        SplashScreenEnabled = _appSettings.SplashScreenEnabled;
        CustomStartupSoundPath = _appSettings.CustomStartupSoundPath;
        StartMinimised = _appSettings.StartMinimised;
        AutoConnect = _appSettings.AutoConnect;
        AutoStart = _appSettings.AutoStart;
        IsPerCarAutoLoadEnabled = _appSettings.PerCarAutoLoadEnabled;
        TooltipsEnabled = _appSettings.TooltipsEnabled;
        AutoProfileUpgrade = _appSettings.AutoProfileUpgrade;
        ThemeMode = _appSettings.ThemeName;
        VoiceEnabled = _appSettings.VoiceEnabled;
        VoiceVolume = _appSettings.VoiceVolume;
        if (Enum.TryParse<NavPage>(_appSettings.DefaultStartPage, out var startPage))
        {
            var pages = Enum.GetValues<NavPage>();
            DefaultStartPageIndex = Array.IndexOf(pages, startPage);
            CurrentPage = startPage;
        }
        else
        {
            DefaultStartPageIndex = 0;
            CurrentPage = NavPage.Home;
        }

        RefreshVoices();
        _voiceInitialized = true;
        RefreshRecordingDevices();
    }

    public void RestoreButtonSettings()
    {
        SnapshotButtonComboIndex = _appSettings.SnapshotButtonComboIndex;
        PanicButtonComboIndex = _appSettings.PanicButtonComboIndex;

        if (!string.IsNullOrEmpty(_appSettings.PanicDeviceInstanceId) && Guid.TryParse(_appSettings.PanicDeviceInstanceId, out var guid))
        {
            var match = PanicDevices.FirstOrDefault(d =>
                d.DeviceInstance != null && d.DeviceInstance.InstanceGuid == guid);
            if (match != null)
                SelectedPanicDevice = match;
        }
    }

    public void ApplyStartupActions()
    {
        if (AutoConnect && !string.IsNullOrEmpty(_appSettings.LastConnectedDeviceInstanceId)
            && Guid.TryParse(_appSettings.LastConnectedDeviceInstanceId, out var deviceGuid))
        {
            var match = AvailableDevices.FirstOrDefault(d =>
                d.DeviceInstance != null && d.DeviceInstance.InstanceGuid == deviceGuid);
            if (match != null)
            {
                SelectedDevice = match;
                ConnectDevice();
            }
        }

        if (AutoStart && IsDeviceConnected)
        {
            IsRunning = true;
            ToggleTelemetry();
        }
    }

    partial void OnCurrentCornerNameChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentCornerRealName));
    }

    public string? CurrentCornerRealName
    {
        get
        {
            return _lastCurrentCorner?.RealName;
        }
    }

    public void NotifyCornerNameChanged()
    {
        OnPropertyChanged(nameof(CurrentCornerRealName));
    }
}
