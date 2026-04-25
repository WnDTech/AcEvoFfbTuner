using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.DirectInput;
using AcEvoFfbTuner.Core.FfbProcessing;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.Profiles;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.SharedMemory.Structs;
using AcEvoFfbTuner.Core.TrackMapping;

namespace AcEvoFfbTuner.Core;

public sealed class TelemetryLoop : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private const int GameTickIntervalMs = 3;

    private readonly SharedMemoryReader _reader;
    private readonly FfbPipeline _pipeline;
    private readonly FfbDeviceManager _deviceManager;

    public readonly TrackMapBuilder MapBuilder = new();
    public readonly TrackPositionDetector PositionDetector = new();
    public readonly TrackForceHeatmap ForceHeatmap = new();
    public readonly LapDataRecorder LapRecorder = new();
    public readonly TrackDiagnosticHeatmap DiagnosticHeatmap = new();
    public readonly FfbEventDetector EventDetector = new();

    private float _posX, _posZ;
    private int _lastPosPacketId = -1;
    private bool _posInitialized;
    private volatile bool _realignRequested;
    private volatile float _latestNpos;

    private Thread? _loopThread;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _suppressOutput;
    private bool _timerResolutionSet;

    private FfbRawData? _latestRaw;
    private FfbProcessedData? _latestProcessed;
    private SPageFilePhysicsEvo _latestPhysicsRaw;
    private SPageFileGraphicEvo _latestGraphicsRaw;
    private SPageFileStaticEvo _latestStaticRaw;
    private readonly object _dataLock = new();

    private int _packetsPerSecond;
    private int _packetCount;
    private long _ppsResetTicks;
    private static readonly long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly System.Diagnostics.Stopwatch _watchdog = new();
    private const int WatchdogTimeoutMs = 2000;
    private const int LogIntervalFrames = 30;

    private int _logFrameCounter;
    private bool _logHeaderWritten;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "ffb_debug.log");

    // Latency measurement
    private readonly System.Diagnostics.Stopwatch _latencyStopwatch = new();
    private float _lastLatencyMs;
    private float _avgLatencyMs;
    private const int LatencyAvgFrames = 60;
    private float _latencyAccumulator;
    private int _latencyFrameCount;

    private string _lastDetectedTrackName = "";
    private bool _staticDataRead;

    public TelemetryLoop(SharedMemoryReader reader, FfbPipeline pipeline, FfbDeviceManager deviceManager)
    {
        _reader = reader;
        _pipeline = pipeline;
        _deviceManager = deviceManager;
    }

    public bool IsRunning => _running;
    public bool IsGameConnected => _reader.IsConnected;
    public bool IsDeviceConnected => _deviceManager.IsDeviceAcquired;
    public int PacketsPerSecond => _packetsPerSecond;
    public bool SuppressOutput { get => _suppressOutput; set => _suppressOutput = value; }
    public float LastLatencyMs => _lastLatencyMs;
    public float AvgLatencyMs => _avgLatencyMs;

    public string DetectedTrackName => _lastDetectedTrackName;

    private static string DecodeString(byte[] data)
    {
        if (data == null) return "";
        int nullIdx = Array.IndexOf(data, (byte)0);
        int len = nullIdx >= 0 ? nullIdx : data.Length;
        return System.Text.Encoding.ASCII.GetString(data, 0, len).Trim();
    }

    public FfbRawData? LatestRaw
    {
        get { lock (_dataLock) return _latestRaw; }
    }

    public FfbProcessedData? LatestProcessed
    {
        get { lock (_dataLock) return _latestProcessed; }
    }

    public TelemetrySnapshotDto? CaptureTelemetrySnapshot()
    {
        lock (_dataLock)
        {
            if (_latestRaw == null) return null;
            return TelemetrySnapshotDto.Capture(_latestPhysicsRaw, _latestGraphicsRaw, _latestStaticRaw);
        }
    }

    public event Action<FfbRawData, FfbProcessedData>? DataUpdated;
    public event Action? GameConnectionChanged;
    public event Action<string>? StatusChanged;
    public event Action<TrackMap>? TrackMapCompleted;
    public event Action<string, string, float>? StaticDataReceived;

    public void Start()
    {
        if (_running) return;
        _running = true;

        _loopThread = new Thread(Loop)
        {
            Name = "AC Evo FFB Telemetry",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _loopThread.Start();

        StatusChanged?.Invoke("Telemetry loop started");
    }

    public void Stop()
    {
        _running = false;
        _loopThread?.Join(2000);
        _loopThread = null;

        _deviceManager.ZeroForce();

        if (_timerResolutionSet)
        {
            timeEndPeriod(1);
            _timerResolutionSet = false;
        }

        StatusChanged?.Invoke("Telemetry loop stopped");
    }

    private void Loop()
    {
        timeBeginPeriod(1);
        _timerResolutionSet = true;

        bool wasConnected = false;
        int idleCount = 0;

        _ppsResetTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        while (_running)
        {
            try
            {
                if (!_reader.IsConnected)
                {
                    if (_reader.TryConnect())
                    {
                        if (!wasConnected)
                        {
                            wasConnected = true;
                            _pipeline.Reset();
                            _staticDataRead = false;
                            if (!MapBuilder.HasCompleteMap)
                            {
                                MapBuilder.Reset();
                                PositionDetector.ClearMap();
                            }
                            _posInitialized = false;
                            _lastPosPacketId = -1;
                            _watchdog.Restart();
                            _ppsResetTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            _packetCount = 0;
                            _packetsPerSecond = 0;
                            idleCount = 0;
                            GameConnectionChanged?.Invoke();
                            StatusChanged?.Invoke("Connected to AC Evo");
                        }

                        if (!_staticDataRead && _reader.TryReadStatic(out var staticData))
                        {
                            _staticDataRead = true;
                            _lastDetectedTrackName = DecodeString(staticData.Track);
                            var trackConfig = DecodeString(staticData.TrackConfiguration);
                            var nation = DecodeString(staticData.Nation);
                            var sessionName = DecodeString(staticData.SessionName);
                            float officialLength = staticData.TrackLengthM;

                            _latestStaticRaw = staticData;

                            StatusChanged?.Invoke($"Track: '{_lastDetectedTrackName}' | Config: '{trackConfig}' | Nation: '{nation}' | Session: '{sessionName}' | Length: {officialLength:F0}m");
                            StaticDataReceived?.Invoke(_lastDetectedTrackName, trackConfig, officialLength);
                        }
                    }
                    else
                    {
                        if (wasConnected)
                        {
                            wasConnected = false;
                            _deviceManager.ZeroForce();
                            GameConnectionChanged?.Invoke();
                            StatusChanged?.Invoke("Disconnected from AC Evo");
                        }
                        Thread.Sleep(500);
                        continue;
                    }
                }

                if (_reader.TryReadPhysics(out SPageFilePhysicsEvo physics))
                {
                    idleCount = 0;
                    _watchdog.Restart();
                    _latencyStopwatch.Restart();

                    _reader.TryReadGraphics(out SPageFileGraphicEvo graphics);

                    var raw = MapRawData(physics, graphics);

                    IntegratePosition(raw, physics);

                    var processed = _pipeline.Process(raw);

                    UpdateTrackPosition(raw);

                    _latestNpos = raw.Npos;

                    lock (_dataLock)
                    {
                        _latestRaw = raw;
                        _latestProcessed = processed;
                        _latestPhysicsRaw = physics;
                        _latestGraphicsRaw = graphics;
                    }

                    _packetCount++;

                    if (_deviceManager.IsDeviceAcquired && !_suppressOutput)
                    {
                        if (raw.SpeedKmh < 2.0f)
                        {
                            _deviceManager.SendConstantForce(0f);
                            _deviceManager.SetTargetVibration(0f);
                        }
                        else
                        {
                            _deviceManager.SendConstantForce(processed.MainForce);
                            _deviceManager.SetTargetVibration(processed.VibrationForce);
                        }

                        _deviceManager.UpdateWheelLeds(raw.RpmPercent, raw.IsChangeUpRpm, raw.IsRpmLimiterOn, raw.Flag, raw.AbsVibrations > 0.001f);
                    }

                    // Measure round-trip latency: shared memory read → process → device command
                    _latencyStopwatch.Stop();
                    _lastLatencyMs = (float)_latencyStopwatch.Elapsed.TotalMilliseconds;
                    _latencyAccumulator += _lastLatencyMs;
                    _latencyFrameCount++;
                    if (_latencyFrameCount >= LatencyAvgFrames)
                    {
                        _avgLatencyMs = _latencyAccumulator / _latencyFrameCount;
                        _latencyAccumulator = 0f;
                        _latencyFrameCount = 0;
                    }

                    DataUpdated?.Invoke(raw, processed);

                    _logFrameCounter++;
                    if (_logFrameCounter >= LogIntervalFrames)
                    {
                        _logFrameCounter = 0;
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                            if (!_logHeaderWritten)
                            {
                            File.AppendAllText(LogPath,
                                "Timestamp,SpeedKmh,SteerAngle,Gear,Mz_FL,Mz_FR,Fx_FL,Fx_FR,Fy_FL,Fy_FR," +
                                "ChMzFront,ChFxFront,ChFyFront,PostCompress,PostLUT,PostSlip,PostDamping,PostDynamic,Output,Clipping,WL_FL,WL_FR,LatencyMs," +
                                "KerbVib,SlipVib,RoadVib,AbsVib,VibForce,AbsGain\n");
                                _logHeaderWritten = true;
                            }
                            File.AppendAllText(LogPath,
                                $"{DateTime.Now:HH:mm:ss.fff}," +
                                $"{raw.SpeedKmh:F1},{raw.SteerAngle:F4},{raw.Gear}," +
                                $"{raw.Mz[0]:F4},{raw.Mz[1]:F4},{raw.Fx[0]:F2},{raw.Fx[1]:F2},{raw.Fy[0]:F2},{raw.Fy[1]:F2}," +
                                $"{processed.ChannelMzFront:F6},{processed.ChannelFxFront:F6},{processed.ChannelFyFront:F6}," +
                                $"{processed.PostCompressionForce:F6},{processed.PostLutForce:F6},{processed.PostSlipForce:F6}," +
                                $"{processed.PostDampingForce:F6},{processed.PostDynamicForce:F6}," +
                                $"{processed.MainForce:F6},{processed.IsClipping},{raw.WheelLoad[0]:F1},{raw.WheelLoad[1]:F1},{_lastLatencyMs:F2}," +
                                $"{raw.KerbVibration:F4},{raw.SlipVibrations:F4},{raw.RoadVibrations:F4},{raw.AbsVibrations:F4},{processed.VibrationForce:F6},{_pipeline.VibrationMixer.AbsGain:F2}\n");
                        }
                        catch { }
                    }

                    Thread.Sleep(0);
                }
                else
                {
                    idleCount++;
                    if (idleCount < 3)
                        Thread.Sleep(0);
                    else
                        Thread.Sleep(1);
                }

                UpdatePps();

                if (_watchdog.ElapsedMilliseconds > WatchdogTimeoutMs)
                {
                    _deviceManager.ZeroForce();
                    StatusChanged?.Invoke("Watchdog timeout - zeroing FFB");
                    _watchdog.Restart();
                }
            }
            catch (Exception)
            {
                Thread.Sleep(100);
            }
        }
    }

    private int _pitEntryWaypointIdx = -1;
    private int _pitExitWaypointIdx = -1;
    private bool _inPit;

    private void UpdateTrackPosition(FfbRawData raw)
    {
        if (MapBuilder.IsRecording)
        {
            MapBuilder.Update(raw.CarX, raw.CarZ, raw.CarY, raw.SpeedKmh, raw.CurrentLap, raw.IsOnTrack, raw.Npos);

            if (MapBuilder.HasCompleteMap && MapBuilder.CurrentMap != null)
            {
                var map = MapBuilder.CurrentMap;
                map.Analyze();

                var matched = TrackMap.TryAutoDetect(map);
                if (matched != null)
                {
                    map.TrackName = matched.TrackName;
                    if (matched.PitLane.IsDetected)
                        map.PitLane = matched.PitLane;
                    if (matched.Sectors.Count > 0)
                        map.Sectors = matched.Sectors;
                }
                else if (!string.IsNullOrEmpty(_lastDetectedTrackName))
                {
                    map.TrackName = _lastDetectedTrackName;
                }

                PositionDetector.SetMap(map);
                ForceHeatmap.Initialize(map.Waypoints.Count);
                LapRecorder.Initialize(map.Waypoints.Count);
                DiagnosticHeatmap.Initialize(map.Waypoints.Count);

                int nearIdx = FindNearestWaypoint(map, _posX, _posZ);
                var nearWp = map.Waypoints[nearIdx];
                _posX = nearWp.X;
                _posZ = nearWp.Z;
                _lastMapNearestIdx = nearIdx;

                TrackMapCompleted?.Invoke(map);
                StatusChanged?.Invoke($"Track map: {map.TrackName} ({map.Waypoints.Count} pts, {map.TrackLengthM:F0}m, {map.Corners.Count} corners, {map.Sectors.Count} sectors)");

                _pitEntryWaypointIdx = -1;
                _pitExitWaypointIdx = -1;
                _inPit = false;
            }
        }

        if (PositionDetector.HasMap && !MapBuilder.IsRecording)
        {
            var pos = PositionDetector.GetPosition(raw.CarX, raw.CarZ);
            if (pos.IsValid && raw.SpeedKmh > 5f)
            {
                var processed = LatestProcessed;
                if (processed != null)
                {
                    ForceHeatmap.Record(
                        pos.NearestWaypointIndex,
                        processed.MainForce,
                        processed.ChannelMzFront,
                        processed.ChannelFxFront,
                        processed.ChannelFyFront,
                        raw.SpeedKmh,
                        processed.IsClipping);

                    LapRecorder.Update(
                        raw.CurrentLap,
                        pos.NearestWaypointIndex,
                        processed.MainForce,
                        processed.ChannelMzFront,
                        processed.ChannelFxFront,
                        processed.ChannelFyFront,
                        raw.SpeedKmh,
                        processed.IsClipping);

                    DetectPit(raw, pos.NearestWaypointIndex);

                    UpdateDiagnostics(raw, processed, pos);
                }
            }
        }
    }

    private void DetectPit(FfbRawData raw, int nearestWaypoint)
    {
        var map = MapBuilder.CurrentMap;
        if (map == null) return;
        if (map.PitLane.IsDetected) return;

        if (raw.IsInPitLane || raw.IsPitEntry)
        {
            if (!_inPit)
            {
                _inPit = true;
                _pitEntryWaypointIdx = nearestWaypoint;
            }
        }
        else if (_inPit && (raw.IsPitExit || raw.CarLocationRaw == (int)AcEvoCarLocation.AcevoTrack))
        {
            _inPit = false;
            _pitExitWaypointIdx = nearestWaypoint;

            if (_pitEntryWaypointIdx > 0 && _pitExitWaypointIdx > 0)
            {
                map.SetPitLocation(_pitEntryWaypointIdx, _pitExitWaypointIdx);
                StatusChanged?.Invoke($"Pit lane detected: entry=T{_pitEntryWaypointIdx}, exit=T{_pitExitWaypointIdx}");
            }
        }
    }

    private int _diagLastLapNumber = -1;
    private int _diagEventLogCount;

    private void UpdateDiagnostics(FfbRawData raw, FfbProcessedData processed, TrackPositionResult pos)
    {
        if (!DiagnosticHeatmap.HasData)
        {
            if (PositionDetector.HasMap && !MapBuilder.IsRecording && raw.SpeedKmh > 5f)
            {
                LogDiag($"DIAG: Heatmap not initialized, wpCount={MapBuilder.CurrentMap?.Waypoints.Count ?? 0}");
            }
            return;
        }

        if (_diagEventLogCount == 0 && raw.SpeedKmh > 10f)
        {
            LogDiag($"DIAG: First tick — force={processed.MainForce:F6} steer={raw.SteerAngle:F4} " +
                    $"spd={raw.SpeedKmh:F1} clip={processed.IsClipping} mzF={processed.ChannelMzFront:F6} " +
                    $"lap={raw.CurrentLap} wp={pos.NearestWaypointIndex}");
        }

        if (_diagLastLapNumber < 0 && raw.SpeedKmh > 5f)
        {
            _diagLastLapNumber = raw.CurrentLap;
            DiagnosticHeatmap.OnNewLap(raw.CurrentLap);
            LogDiag($"DIAG: Lap tracking started, lap={raw.CurrentLap}");
        }
        else if (_diagLastLapNumber >= 0 && raw.CurrentLap != _diagLastLapNumber)
        {
            LogDiag($"DIAG: Lap transition {_diagLastLapNumber} -> {raw.CurrentLap}, events={DiagnosticHeatmap.RunningTotalEvents}");
            DiagnosticHeatmap.OnNewLap(raw.CurrentLap);
            _diagLastLapNumber = raw.CurrentLap;
        }

            float slipAngleFront = (MathF.Abs(raw.SlipAngle[0]) + MathF.Abs(raw.SlipAngle[1])) * 0.5f;
            float slipRatioFront = (MathF.Abs(raw.SlipRatio[0]) + MathF.Abs(raw.SlipRatio[1])) * 0.5f;
            float lateralG = MathF.Abs(raw.AccG[1]);

            bool inCorner = pos.CurrentCorner != null;
            string? cornerName = pos.CurrentCorner?.DisplayName;

            DiagnosticHeatmap.RecordDrivingState(
                pos.NearestWaypointIndex,
                slipAngleFront, slipRatioFront,
                raw.SteerAngle, lateralG);

            var events = EventDetector.DetectEvents(
                processed.MainForce,
                raw.SteerAngle,
                raw.SpeedKmh,
                slipAngleFront,
                slipRatioFront,
                lateralG,
                raw.BrakeInput,
                raw.GasInput,
                inCorner,
                cornerName,
                pos.NearestWaypointIndex,
                processed.IsClipping,
                processed.ChannelMzFront,
                processed.ChannelFxFront,
                processed.ChannelFyFront,
                processed.PostCompressionForce,
                processed.PostSlipForce,
                processed.PostDampingForce,
                processed.PostDynamicForce,
                _pipeline.VibrationMixer.RoadForceModulation);

            foreach (var evt in events)
            {
                DiagnosticHeatmap.RecordEvent(evt);
                _diagEventLogCount++;
                if (_diagEventLogCount <= 20)
                {
                    LogDiag($"EVENT #{_diagEventLogCount}: {evt.EventType} @ {evt.CornerName ?? "straight"} " +
                            $"cls={evt.Classification} ΔF={evt.ForceDelta:F4} F={evt.OutputForce:F4} " +
                            $"steer={evt.SteerAngle:F3} spd={evt.SpeedKmh:F0} inCorner={evt.InCorner}");
                }
            }
    }

    public void RealignToMap()
    {
        _realignRequested = true;
    }

    private void PerformRealign()
    {
        var map = MapBuilder.CurrentMap;
        if (map == null || map.Waypoints.Count < 3) return;

        int targetIdx;

        float npos = _latestNpos;
        if (npos > 0.001f && npos < 0.999f && map.TrackLengthM > 1f)
        {
            float targetDist = npos * map.TrackLengthM;
            var cumDist = map.GetCumulativeDistances();
            targetIdx = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < cumDist.Length; i++)
            {
                float delta = MathF.Abs(cumDist[i] - targetDist);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    targetIdx = i;
                }
            }
            TrackMapBuilder.Log($"REALIGN via Npos: npos={npos:F4} targetDist={targetDist:F1}m -> wp{targetIdx} ({map.Waypoints[targetIdx].X:F1},{map.Waypoints[targetIdx].Z:F1})");
        }
        else
        {
            targetIdx = FindNearestWaypoint(map, _posX, _posZ);
            TrackMapBuilder.Log($"REALIGN via nearest: pos=({_posX:F1},{_posZ:F1}) -> wp{targetIdx}");
        }

        var wp = map.Waypoints[targetIdx];
        _posX = wp.X;
        _posZ = wp.Z;
        _lastMapNearestIdx = targetIdx;

        TrackMapBuilder.Log($"  REALIGN result: pos=({_posX:F1},{_posZ:F1})");
    }

    private void IntegratePosition(FfbRawData raw, SPageFilePhysicsEvo physics)
    {
        if (physics.PacketId == _lastPosPacketId) return;
        _lastPosPacketId = physics.PacketId;

        bool hasWorldCoords = raw.CarX != 0f || raw.CarZ != 0f;

        if (hasWorldCoords)
        {
            _posX = raw.CarX;
            _posZ = raw.CarZ;
            _posInitialized = true;
        }
        else if (!_posInitialized)
        {
            _posX = 0f;
            _posZ = 0f;
            _posInitialized = true;
        }
        else
        {
            float dt = 1f / 333f;
            _posX += physics.Velocity[0] * dt;
            _posZ += physics.Velocity[2] * dt;
        }

        if (_realignRequested)
        {
            _realignRequested = false;
            PerformRealign();
        }

        if (PositionDetector.HasMap && !MapBuilder.IsRecording && raw.SpeedKmh > 2f)
        {
            var map = MapBuilder.CurrentMap;
            if (map != null && map.Waypoints.Count > 3 && map.TrackLengthM > 1f)
            {
                int nearest = FindNearestWaypoint(map, _posX, _posZ);
                if (nearest >= 0)
                {
                    _lastMapNearestIdx = nearest;
                    var wp = map.Waypoints[nearest];
                    float errX = wp.X - _posX;
                    float errZ = wp.Z - _posZ;
                    float error = MathF.Sqrt(errX * errX + errZ * errZ);

                    if (error > 2f)
                    {
                        float alpha = hasWorldCoords ? 0.05f : 0.02f;
                        _posX += errX * alpha;
                        _posZ += errZ * alpha;
                    }
                }
            }
        }

        raw.CarX = _posX;
        raw.CarZ = _posZ;
        raw.CarY = 0f;
    }

    private int _lastMapNearestIdx;
    private int FindNearestWaypoint(TrackMap map, float x, float z)
    {
        var wps = map.Waypoints;
        int count = wps.Count;
        int searchRadius = 60;

        int start = Math.Max(0, _lastMapNearestIdx - searchRadius);
        int end = Math.Min(count - 1, _lastMapNearestIdx + searchRadius);

        float bestDist = float.MaxValue;
        int bestIdx = _lastMapNearestIdx;

        for (int i = start; i <= end; i++)
        {
            float dx = wps[i].X - x;
            float dz = wps[i].Z - z;
            float d = dx * dx + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

        if (bestIdx == start && start > 0)
        {
            for (int i = 0; i < start; i++)
            {
                float dx = wps[i].X - x;
                float dz = wps[i].Z - z;
                float d = dx * dx + dz * dz;
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
        }

        if (bestIdx == end && end < count - 1)
        {
            for (int i = end + 1; i < count; i++)
            {
                float dx = wps[i].X - x;
                float dz = wps[i].Z - z;
                float d = dx * dx + dz * dz;
                if (d < bestDist) { bestDist = d; bestIdx = i; }
            }
        }

        _lastMapNearestIdx = bestIdx;
        return bestIdx;
    }

    private static FfbRawData MapRawData(SPageFilePhysicsEvo physics, SPageFileGraphicEvo graphics)
    {
        return new FfbRawData
        {
            PacketId = physics.PacketId,
            FinalFf = physics.FinalFf,
            SteerAngle = (float)physics.SteerAngle,
            SpeedKmh = physics.SpeedKmh,
            KerbVibration = physics.KerbVibration,
            SlipVibrations = physics.SlipVibrations,
            RoadVibrations = physics.RoadVibrations,
            AbsVibrations = physics.AbsVibrations,
            AbsInAction = graphics.AbsActive ? 1 : 0,
            AbsLevel = physics.Abs,
            AbsActiveGfx = graphics.AbsActive,
            BrakeInput = physics.Brake,
            GasInput = physics.Gas,
            FfbStrength = graphics.FfbStrength,
            CarFfbMultiplier = graphics.CarFfbMultiplier,
            SteerDegrees = graphics.SteerDegrees,
            Mz = new float[] { physics.Mz[0], physics.Mz[1], physics.Mz[2], physics.Mz[3] },
            Fx = new float[] { physics.Fx[0], physics.Fx[1], physics.Fx[2], physics.Fx[3] },
            Fy = new float[] { physics.Fy[0], physics.Fy[1], physics.Fy[2], physics.Fy[3] },
            WheelLoad = new float[] { physics.WheelLoad[0], physics.WheelLoad[1], physics.WheelLoad[2], physics.WheelLoad[3] },
            SlipRatio = new float[] { physics.SlipRatio[0], physics.SlipRatio[1], physics.SlipRatio[2], physics.SlipRatio[3] },
            SlipAngle = new float[] { physics.SlipAngle[0], physics.SlipAngle[1], physics.SlipAngle[2], physics.SlipAngle[3] },
            SuspensionTravel = new float[] { physics.SuspensionTravel[0], physics.SuspensionTravel[1], physics.SuspensionTravel[2], physics.SuspensionTravel[3] },
            AccG = new float[] { physics.AccG[0], physics.AccG[1], physics.AccG[2] },
            LocalAngularVel = new float[] { physics.LocalAngularVel[0], physics.LocalAngularVel[1], physics.LocalAngularVel[2] },
            Gear = physics.Gear,
            RpmPercent = graphics.RpmPercent,
            IsRpmLimiterOn = graphics.IsRpmLimiterOn,
            IsChangeUpRpm = graphics.IsChangeUpRpm,
            Flag = (int)graphics.Flag,
            CarX = GetCarX(graphics, physics),
            CarY = GetCarY(graphics, physics),
            CarZ = GetCarZ(graphics, physics),
            Npos = graphics.Npos,
            CurrentLap = graphics.SessionState.CurrentLap,
            IsOnTrack = graphics.CarLocation == AcEvoCarLocation.AcevoTrack,
            Heading = physics.Heading,
            CarLocationRaw = (int)graphics.CarLocation,
            IsInPitLane = graphics.CarLocation == AcEvoCarLocation.AcevoPitlane,
            IsPitEntry = false,
            IsPitExit = false
        };
    }

    private static float GetCarX(SPageFileGraphicEvo graphics, SPageFilePhysicsEvo physics)
    {
        if (graphics.CarCoordinates is { Length: > 0 })
            return graphics.CarCoordinates[0].X;
        return (physics.TyreContactPoint[0].X + physics.TyreContactPoint[1].X +
                physics.TyreContactPoint[2].X + physics.TyreContactPoint[3].X) * 0.25f;
    }

    private static float GetCarY(SPageFileGraphicEvo graphics, SPageFilePhysicsEvo physics)
    {
        if (graphics.CarCoordinates is { Length: > 0 })
            return graphics.CarCoordinates[0].Y;
        return (physics.TyreContactPoint[0].Y + physics.TyreContactPoint[1].Y +
                physics.TyreContactPoint[2].Y + physics.TyreContactPoint[3].Y) * 0.25f;
    }

    private static float GetCarZ(SPageFileGraphicEvo graphics, SPageFilePhysicsEvo physics)
    {
        if (graphics.CarCoordinates is { Length: > 0 })
            return graphics.CarCoordinates[0].Z;
        return (physics.TyreContactPoint[0].Z + physics.TyreContactPoint[1].Z +
                physics.TyreContactPoint[2].Z + physics.TyreContactPoint[3].Z) * 0.25f;
    }

    private void UpdatePps()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _ppsResetTicks >= TicksPerSecond)
        {
            _packetsPerSecond = _packetCount;
            _packetCount = 0;
            _ppsResetTicks = now;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static void LogDiag(string msg)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "diagnostic_debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
