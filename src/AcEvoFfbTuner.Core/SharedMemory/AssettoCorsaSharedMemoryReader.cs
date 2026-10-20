using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

/// <summary>
/// Assetto Corsa (AC1) shared-memory reader.
///
/// AC1 exposes three MMFs (Local\acpmf_physics / acpmf_graphics / acpmf_static)
/// with the classic Kunos layout (SPageFilePhysicsAC etc. in AcSharedStruct.cs).
/// Data is mapped onto the EVO-shaped structs (SPageFilePhysicsEvo/...) so the
/// rest of the app (TelemetryLoop, pipelines, snapshots) works unchanged.
///
/// Key AC1 facts (verified against live AC1 1.16.4 telemetry + Kunos docs):
/// - FinalFF is the game's own complete FFB column torque (-1..+1). It is
///   passed through untouched — the AcFfbPipeline uses it as its core force
///   (same column-force pattern as R3E/LMU). There is NO per-wheel Mz/Fx/Fy,
///   NO SlipRatio/SlipAngle arrays and NO TcInAction/AbsInAction flags in AC1.
/// - WheelSlip[4] is the slip source. It feeds grip + vibration synthesis.
/// - SteerAngle is -1..+1 normalized.
/// - Graphics CarCoordinates is the PLAYER car position (float[3]), not 60 cars.
/// - Static MaxRpm/MaxFuel sit behind SectorCount+MaxTorque+MaxPower.
/// </summary>
public sealed class AssettoCorsaSharedMemoryReader : ISharedMemoryReader
{
    private const string PhysicsMapName = @"Local\acpmf_physics";
    // Real AC1 graphics MMF is the plural "acpmf_graphics" (Kunos sim_info.py).
    // The singular variant is tried as a fallback for renamed installs.
    private static readonly string[] GraphicsMapNames = { @"Local\acpmf_graphics", @"Local\acpmf_graphic" };
    private const string StaticMapName = @"Local\acpmf_static";

    private MemoryMappedFile? _physicsMmf;
    private MemoryMappedFile? _graphicsMmf;
    private MemoryMappedFile? _staticMmf;

    private MemoryMappedViewAccessor? _physicsView;
    private MemoryMappedViewAccessor? _graphicsView;
    private MemoryMappedViewAccessor? _staticView;

    private int _lastPhysicsPacketId = -1;
    private int _lastGraphicsPacketId = -1;
    private bool _gameConnectedFired;

    private int _cachedMaxRpm;
    // Static identity (AC1 graphics carries no car/driver names).
    private byte[] _cachedCarModel = new byte[33];
    private byte[] _cachedDriverName = new byte[33];
    private byte[] _cachedDriverSurname = new byte[33];
    private int _cachedNumCars = 1;
    // Last physics values needed by the graphics mapping (graphics MMF has no
    // RPM/pedal fields of its own).
    private int _lastRpms;
    private float _lastGas;
    private float _lastBrake;
    private float _lastSteer;

    // Previous-frame state for vibration synthesis (current-frame deltas).
    private readonly float[] _prevWheelSpeed = new float[4];
    private readonly float[] _prevSuspTravel = new float[4];
    private bool _vibInit;

    public bool IsConnected => _physicsMmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    private readonly float[] _tireGrip = [1f, 1f, 1f, 1f];
    public float[] TireGrip => _tireGrip;

    public bool TryConnect()
    {
        if (_physicsMmf != null) return true;

        var log = new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] AC TryConnect: physics={PhysicsMapName} static={StaticMapName}");

        try
        {
            // Physics is required
            _physicsMmf = MemoryMappedFile.OpenExisting(PhysicsMapName, MemoryMappedFileRights.Read);
            _physicsView = _physicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            log.AppendLine($"  physics OK, capacity={_physicsView.Capacity}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"  physics FAILED: {ex.Message}");
            LogConn(log);
            Disconnect();
            return false;
        }

        // Graphics — try plural (real AC1 name) first, then singular fallback
        foreach (var name in GraphicsMapNames)
        {
            try
            {
                _graphicsMmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _graphicsView = _graphicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                log.AppendLine($"  graphics OK (name={name}, capacity={_graphicsView.Capacity})");
                break;
            }
            catch { log.AppendLine($"  graphics FAILED for {name}"); }
        }

        // Static — optional
        try
        {
            _staticMmf = MemoryMappedFile.OpenExisting(StaticMapName, MemoryMappedFileRights.Read);
            _staticView = _staticMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            log.AppendLine($"  static OK, capacity={_staticView.Capacity}");
        }
        catch { log.AppendLine($"  static FAILED (non-critical)"); }

        _lastPhysicsPacketId = -1;
        _lastGraphicsPacketId = -1;
        _gameConnectedFired = false;
        _vibInit = false;
        Array.Clear(_prevWheelSpeed);
        Array.Clear(_prevSuspTravel);

        log.AppendLine("  connection complete");
        LogConn(log);
        return true;
    }

    public void Disconnect()
    {
        _physicsView?.Dispose();
        _graphicsView?.Dispose();
        _staticView?.Dispose();
        _physicsMmf?.Dispose();
        _graphicsMmf?.Dispose();
        _staticMmf?.Dispose();

        _physicsView = null;
        _graphicsView = null;
        _staticView = null;
        _physicsMmf = null;
        _graphicsMmf = null;
        _staticMmf = null;

        _lastPhysicsPacketId = -1;
        _lastGraphicsPacketId = -1;
        _gameConnectedFired = false;

        GameDisconnected?.Invoke();
    }

    private int _callCount;

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_physicsView == null) return false;

        try
        {
            int acSize = Marshal.SizeOf<SPageFilePhysicsAC>();
            int cap = (int)_physicsView.Capacity;
            byte[] buffer = new byte[Math.Min(acSize, cap)];
            _physicsView.ReadArray(0, buffer, 0, buffer.Length);

            int rawPktId = BitConverter.ToInt32(buffer, 0);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var ac = Marshal.PtrToStructure<SPageFilePhysicsAC>(handle.AddrOfPinnedObject());

                _callCount++;
                if (_callCount % 100 == 1)
                {
                    float rawSteer = BitConverter.ToSingle(buffer, 24);
                    float rawSpeed = BitConverter.ToSingle(buffer, 28);
                    float rawFinalFf = buffer.Length >= 312 ? BitConverter.ToSingle(buffer, 308) : 0f;
                    LogErr($"TryReadPhysics call#{_callCount}: rawPkt={rawPktId} acPkt={ac.PacketId} lastPkt={_lastPhysicsPacketId} rawSteer={rawSteer:F6} speed={rawSpeed:F1} finalFF={rawFinalFf:F4}");
                }

                if (ac.PacketId == _lastPhysicsPacketId)
                    return false;

                _lastPhysicsPacketId = ac.PacketId;

                if (!_gameConnectedFired)
                {
                    _gameConnectedFired = true;
                    GameConnected?.Invoke();
                }

                physics = MapPhysics(ac);
                SynthesizeGripAndAbsTc(ref physics, ac);
                SynthesizeVibrations(ref physics, ac);

                _lastRpms = ac.Rpms;
                _lastGas = ac.Gas;
                _lastBrake = ac.Brake;
                _lastSteer = physics.SteerAngle;

                if (ac.PacketId % 60 == 0)
                    DumpDiag(buffer, physics);
            }
            finally
            {
                handle.Free();
            }

            return true;
        }
        catch (Exception ex)
        {
            LogErr($"TryReadPhysics: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
    {
        graphics = default;
        if (_graphicsView == null) return false;

        try
        {
            int size = Marshal.SizeOf<SPageFileGraphicAC>();
            byte[] buffer = new byte[Math.Min(size, (int)_graphicsView.Capacity)];
            _graphicsView.ReadArray(0, buffer, 0, buffer.Length);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var ac = Marshal.PtrToStructure<SPageFileGraphicAC>(handle.AddrOfPinnedObject());
                if (ac.PacketId == _lastGraphicsPacketId)
                    return false;
                _lastGraphicsPacketId = ac.PacketId;

                graphics = MapGraphics(ac);
                return true;
            }
            finally { handle.Free(); }
        }
        catch (Exception ex)
        {
            LogErr($"TryReadGraphics: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool TryReadStatic(out SPageFileStaticEvo staticData)
    {
        staticData = default;
        if (_staticView == null) return false;

        try
        {
            int size = Marshal.SizeOf<SPageFileStaticAC>();
            byte[] buffer = new byte[Math.Min(size, (int)_staticView.Capacity)];
            _staticView.ReadArray(0, buffer, 0, buffer.Length);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var ac = Marshal.PtrToStructure<SPageFileStaticAC>(handle.AddrOfPinnedObject());
                staticData = MapStatic(ac);
                if (ac.MaxRpm > 0)
                {
                    _cachedMaxRpm = ac.MaxRpm;
                }
                if (!string.IsNullOrEmpty(ac.CarModel))
                    _cachedCarModel = StrToBytes(ac.CarModel, 33);
                if (!string.IsNullOrEmpty(ac.PlayerName))
                    _cachedDriverName = StrToBytes(ac.PlayerName, 33);
                if (!string.IsNullOrEmpty(ac.PlayerSurname))
                    _cachedDriverSurname = StrToBytes(ac.PlayerSurname, 33);
                if (ac.NumCars > 0)
                    _cachedNumCars = ac.NumCars;
                return true;
            }
            finally { handle.Free(); }
        }
        catch (Exception ex)
        {
            LogErr($"TryReadStatic: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private SPageFilePhysicsEvo MapPhysics(SPageFilePhysicsAC ac)
    {
        var p = new SPageFilePhysicsEvo();
        p.PacketId = ac.PacketId;
        p.Gas = ac.Gas;
        p.Brake = ac.Brake;
        p.Fuel = ac.Fuel;
        p.Gear = ac.Gear;
        p.Rpms = ac.Rpms;
        // AC1 SteerAngle is -1..+1 normalized. Only rescale when clearly in
        // degrees (|v| > 2); otherwise clamp. (The old 0..1 → *2-1 branch
        // corrupted valid centered inputs: 0.0 → -1.0 full lock.)
        float rawSteer = ac.SteerAngle;
        float steerNorm = Math.Abs(rawSteer) > 2f ? rawSteer / 450f : rawSteer;
        p.SteerAngle = Math.Clamp(steerNorm, -1f, 1f);
        p.SpeedKmh = ac.SpeedKmh;
        p.Velocity = CopyArr(ac.Velocity, 3);
        p.AccG = CopyArr(ac.AccG, 3);
        p.WheelSlip = CopyArr(ac.WheelSlip, 4);
        p.WheelLoad = CopyArr(ac.WheelLoad, 4);
        p.WheelsPressure = CopyArr(ac.WheelsPressure, 4);
        p.WheelAngularSpeed = CopyArr(ac.WheelAngularSpeed, 4);
        p.TyreWear = CopyArr(ac.TyreWear, 4);
        p.TyreDirtyLevel = CopyArr(ac.TyreDirtyLevel, 4);
        p.TyreCoreTemperature = CopyArr(ac.TyreCoreTemperature, 4);
        p.CamberRad = CopyArr(ac.CamberRad, 4);
        p.SuspensionTravel = CopyArr(ac.SuspensionTravel, 4);
        p.Drs = ac.Drs;
        p.Tc = ac.Tc;
        p.Heading = ac.Heading;
        p.Pitch = ac.Pitch;
        p.Roll = ac.Roll;
        p.CgHeight = ac.CgHeight;
        p.CarDamage = CopyArr(ac.CarDamage, 5);
        p.Abs = ac.Abs;
        p.PitLimiterOn = ac.PitLimiterOn;
        p.AutoShifterOn = ac.AutoShifterOn;
        p.RideHeight = CopyArr(ac.RideHeight, 2);
        p.TurboBoost = ac.TurboBoost;
        p.Ballast = ac.Ballast;
        p.AirDensity = ac.AirDensity;
        p.AirTemp = ac.AirTemp;
        p.RoadTemp = ac.RoadTemp;
        p.LocalAngularVel = CopyArr(ac.LocalAngularVelocity, 3);
        p.LocalVelocity = CopyArr(ac.LocalVelocity, 3);
        // Game-computed FFB column torque — the AcFfbPipeline core force.
        // NEVER overwritten by synthesis (unlike the old debug synth).
        p.FinalFf = ac.FinalFF;
        p.PerformanceMeter = ac.PerformanceMeter;
        p.EngineBrake = ac.EngineBrake;
        p.ErsRecoveryLevel = ac.ErsRecoveryLevel;
        p.IsAiControlled = ac.IsAIControlled;
        p.KersCharge = ac.KersCharge;
        p.KersInput = ac.KersInput;
        p.Clutch = ac.Clutch;
        p.BrakeBias = ac.BrakeBias;
        p.BrakeTemp = CopyArr(ac.BrakeTemp, 4);
        p.TyreTempI = CopyArr(ac.TyreTempI, 4);
        p.TyreTempM = CopyArr(ac.TyreTempM, 4);
        p.TyreTempO = CopyArr(ac.TyreTempO, 4);
        // Middle temps drive the shared TyreTemp channel (temp effects).
        p.TyreTemp = CopyArr(ac.TyreTempM, 4);
        p.TyreContactPoint = CopyContacts(ac.TyreContactPoint);
        p.TyreContactNormal = CopyContacts(ac.TyreContactNormal);
        p.TyreContactHeading = CopyContacts(ac.TyreContactHeading);
        p.DrsAvailable = ac.DrsAvailable;
        p.DrsEnabled = ac.DrsEnabled;
        p.KersCurrentKj = ac.KersCurrentKJ;
        p.CurrentMaxRpm = _cachedMaxRpm;

        // AC1 exposes none of these — explicit zeros keep consumers honest:
        // the column-force pipeline never reads the mixer channels, the slip
        // enhancer stays inert, and GripGuard passes through at zero slip.
        p.Mz = new float[4];
        p.Fx = new float[4];
        p.Fy = new float[4];
        p.SlipRatio = new float[4];
        p.SlipAngle = new float[4];
        p.SuspensionDamage = new float[4];
        p.TcinAction = 0;
        p.AbsInAction = 0;
        p.IgnitionOn = 1;
        p.IsEngineRunning = 1;
        return p;
    }

    /// <summary>
    /// Grip + ABS/TC synthesis from real AC1 signals. AC1 reports no grip
    /// fraction and no intervention flags, so both are derived conservatively:
    /// grip falls only when a wheel's |slip| clearly exceeds the standstill
    /// noise floor (~0.085 observed); ABS/TC need pedal + wheel-speed evidence.
    /// </summary>
    private void SynthesizeGripAndAbsTc(ref SPageFilePhysicsEvo physics, SPageFilePhysicsAC ac)
    {
        float[] slip = ac.WheelSlip ?? Array.Empty<float>();
        float[] wspd = ac.WheelAngularSpeed ?? Array.Empty<float>();
        const float tyreRadius = 0.33f;

        for (int i = 0; i < 4; i++)
        {
            float s = (slip.Length > i) ? Math.Abs(slip[i]) : 0f;
            float grip = 1f - Math.Clamp((s - 0.06f) * 4f, 0f, 0.9f);
            _tireGrip[i] = grip;
        }

        float speedMs = physics.SpeedKmh / 3.6f;
        if (speedMs > 3f && wspd.Length >= 4)
        {
            // ABS: hard braking + front-axle wheel-speed dispersion (lockup).
            float frontDisp = Math.Abs(wspd[0] - wspd[1]) * tyreRadius;
            float rearDisp = Math.Abs(wspd[2] - wspd[3]) * tyreRadius;
            if (physics.Brake > 0.6f && Math.Max(frontDisp, rearDisp) > 1.5f)
                physics.AbsInAction = 1;

            // TC: throttle + rears out-spinning fronts and car speed (spin).
            float frontAvg = (wspd[0] + wspd[1]) * 0.5f * tyreRadius;
            float rearAvg = (wspd[2] + wspd[3]) * 0.5f * tyreRadius;
            if (physics.Gas > 0.5f && rearAvg - Math.Max(frontAvg, speedMs) > 2f)
                physics.TcinAction = 1;
        }
    }

    /// <summary>
    /// Vibration envelopes from current-frame deltas (previous frame stored in
    /// _prevWheelSpeed/_prevSuspTravel). AC1 MMF carries no vibration channels.
    /// Thresholds reject standstill noise: road needs wheel-speed jitter,
    /// kerbs need >3mm/frame suspension velocity, slip needs front WheelSlip
    /// above the observed 0.085 standstill floor.
    /// </summary>
    private void SynthesizeVibrations(ref SPageFilePhysicsEvo physics, SPageFilePhysicsAC ac)
    {
        float[] speeds = ac.WheelAngularSpeed ?? Array.Empty<float>();
        float[] susp = ac.SuspensionTravel ?? Array.Empty<float>();
        float[] slip = ac.WheelSlip ?? Array.Empty<float>();

        if (!_vibInit)
        {
            for (int i = 0; i < 4; i++)
            {
                _prevWheelSpeed[i] = speeds.Length > i ? speeds[i] : 0f;
                _prevSuspTravel[i] = susp.Length > i ? susp[i] : 0f;
            }
            _vibInit = true;
        }

        float maxDelta = 0f;
        for (int i = 0; i < 4; i++)
        {
            float cur = speeds.Length > i ? speeds[i] : 0f;
            float delta = Math.Abs(cur - _prevWheelSpeed[i]);
            if (delta > maxDelta) maxDelta = delta;
            _prevWheelSpeed[i] = cur;
        }
        physics.RoadVibrations = Math.Clamp(maxDelta * 5f, 0f, 1f);

        float maxAccel = 0f;
        for (int i = 0; i < 4; i++)
        {
            float cur = susp.Length > i ? susp[i] : 0f;
            float delta = Math.Abs(cur - _prevSuspTravel[i]);
            _prevSuspTravel[i] = cur;
            if (delta > 0.003f)
            {
                float accel = (delta - 0.003f) * 30f;
                if (accel > maxAccel) maxAccel = accel;
            }
        }
        physics.KerbVibration = Math.Clamp(maxAccel, 0f, 1f);

        float speedMs = physics.SpeedKmh / 3.6f;
        if (speedMs > 0.5f && slip.Length >= 2)
        {
            float maxFrontSlip = Math.Max(Math.Abs(slip[0]), Math.Abs(slip[1]));
            physics.SlipVibrations = maxFrontSlip > 0.10f ? Math.Min((maxFrontSlip - 0.10f) * 5f, 1f) : 0f;
        }
        else
        {
            physics.SlipVibrations = 0f;
        }

        physics.AbsVibrations = (physics.AbsInAction != 0 && speedMs > 5f && speeds.Length >= 2)
            ? Math.Clamp(Math.Abs(speeds[0] - speeds[1]) * 0.3f, 0f, 1f)
            : 0f;
    }

    private SPageFileGraphicEvo MapGraphics(SPageFileGraphicAC ac)
    {
        var coords = new StructVector3[60];
        if (ac.CarCoordinates is { Length: >= 3 })
            coords[0] = new StructVector3 { X = ac.CarCoordinates[0], Y = ac.CarCoordinates[1], Z = ac.CarCoordinates[2] };

        byte[] compound = StrToBytes(ac.TyreCompound ?? "", 33);
        float rpmPct = _cachedMaxRpm > 0 ? Math.Clamp(_lastRpms / (float)_cachedMaxRpm, 0f, 1.2f) : 0f;

        var g = new SPageFileGraphicEvo
        {
            PacketId = ac.PacketId,
            Status = ac.Status switch
            {
                0 => AcEvoStatus.AcOff,
                1 => AcEvoStatus.AcReplay,
                2 => AcEvoStatus.AcLive,
                3 => AcEvoStatus.AcPause,
                _ => AcEvoStatus.AcLive
            },
            DisplaySpeedKmh = 0,
            Rpm = (ushort)Math.Clamp(_lastRpms, 0, 65535),
            RpmPercent = rpmPct,
            GasPercent = _lastGas * 100f,
            BrakePercent = _lastBrake * 100f,
            SteeringPercent = _lastSteer * 100f,
            // AC1 reports no game FFB strength (its gain lives in the game's
            // settings files, not shared memory). Report 0 like the ACC/R3E
            // readers: anything else trips the in-game-FFB warning forever.
            FfbStrength = 0f,
            CarFfbMultiplier = 1f,
            // AC1 reports no steering lock; 900° matches the old default and
            // keeps SoftLock/centering math in a sane range.
            SteerDegrees = 900,
            Npos = ac.NormalizedCarPosition,
            CarLocation = ac.IsInPitLane != 0 ? AcEvoCarLocation.AcevoPitlane
                        : ac.IsInPit != 0 ? AcEvoCarLocation.AcevoPitbox
                        : AcEvoCarLocation.AcevoTrack,
            IsInPitLane = ac.IsInPitLane != 0,
            IsInPitBox = ac.IsInPit != 0,
            EngineType = AcEvoEngineType.AcevoInternalCombustion,
            CarCoordinates = coords,
            CarModel = (byte[])_cachedCarModel.Clone(),
            DriverName = (byte[])_cachedDriverName.Clone(),
            DriverSurname = (byte[])_cachedDriverSurname.Clone(),
            PerformanceModeName = new byte[33],
            Flag = ac.Flag switch
            {
                1 => AcEvoFlagType.AcBlueFlag,
                2 => AcEvoFlagType.AcYellowFlag,
                3 => AcEvoFlagType.AcBlackFlag,
                4 => AcEvoFlagType.AcWhiteFlag,
                5 => AcEvoFlagType.AcCheckeredFlag,
                6 => AcEvoFlagType.AcPenaltyFlag,
                _ => AcEvoFlagType.AcNoFlag,
            },
            GlobalFlag = AcEvoFlagType.AcNoFlag,
            SessionState = new SmevoSessionState
            {
                CurrentLap = ac.CompletedLaps + 1,
                TotalLap = ac.NumberOfLaps,
                TimeLeftMs = (int)(ac.SessionTimeLeft * 1000f),
            },
            TimingState = new SmevoTimingState(),
            TyreLf = new SmevoTyreState(),
            TyreRf = new SmevoTyreState(),
            TyreLr = new SmevoTyreState(),
            TyreRr = new SmevoTyreState(),
            CarDamage = new SmevoDamageState(),
            PitInfo = new SmevoPitInfo(),
            Instrumentation = new SmevoInstrumentation(),
            Electronics = new SmevoElectronics(),
            AssistsState = new SmevoAssistsState(),
            CurrentLapTimeMs = ac.ICurrentTime,
            LastLaptimeMs = ac.ILastTime,
            BestLaptimeMs = ac.IBestTime,
            TotalLapCount = ac.CompletedLaps,
            CurrentPos = (uint)Math.Max(ac.Position, 0),
            TotalDrivers = (uint)Math.Max(_cachedNumCars, 1),
            GapAhead = 0f,
            GapBehind = 0f,
            IsIgnitionOn = true,
            IsEngineRunning = true,
        };
        g.TyreLf.TyreCompoundFront = CopyBytes(compound, g.TyreLf.TyreCompoundFront?.Length ?? 33);
        g.TyreRf.TyreCompoundFront = CopyBytes(compound, g.TyreRf.TyreCompoundFront?.Length ?? 33);
        g.TyreLr.TyreCompoundRear = CopyBytes(compound, g.TyreLr.TyreCompoundRear?.Length ?? 33);
        g.TyreRr.TyreCompoundRear = CopyBytes(compound, g.TyreRr.TyreCompoundRear?.Length ?? 33);
        return g;
    }

    private SPageFileStaticEvo MapStatic(SPageFileStaticAC ac)
    {
        return new SPageFileStaticEvo
        {
            SmVersion = StrToBytes(ac.SmVersion ?? "", 15),
            AcEvoVersion = StrToBytes(ac.AcVersion ?? "", 15),
            // AC1 static carries no session type (it lives in graphics.Session,
            // mapped in MapGraphics); Practice is a neutral default.
            Session = AcEvoSessionType.AcPractice,
            SessionName = new byte[33],
            Nation = new byte[33],
            StartingGrip = AcEvoStartingGrip.AcevoOptimum,
            IsTimedRace = ac.IsTimedRace != 0,
            NumberOfSessions = ac.NumberOfSessions,
            Track = StrToBytes(ac.Track ?? "", 33),
            TrackConfiguration = StrToBytes(ac.TrackConfiguration ?? "", 33),
            TrackLengthM = ac.TrackSplineLength,
        };
    }

    private static float[] CopyArr(float[]? src, int count)
    {
        var dst = new float[count];
        if (src != null) Array.Copy(src, dst, Math.Min(src.Length, count));
        return dst;
    }

    private static StructVector3[] CopyContacts(StructVector3[]? src)
    {
        var dst = new StructVector3[4];
        if (src != null) Array.Copy(src, dst, Math.Min(src.Length, 4));
        return dst;
    }

    private static void DumpDiag(byte[] buf, SPageFilePhysicsEvo p)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "ac_physics_hex.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== AC1 Diag PacketId={p.PacketId} BufLen={buf.Length} ===");
            sb.AppendLine($"  SpeedKmh={p.SpeedKmh:F2} Steer={p.SteerAngle:F6} FinalFf={p.FinalFf:F6}");
            sb.AppendLine($"  Gas={p.Gas:F3} Brake={p.Brake:F3} Gear={p.Gear} Rpm={p.Rpms} PitLimiterOn={p.PitLimiterOn}");
            sb.AppendLine($"  WheelSlip=[{p.WheelSlip[0]:F4} {p.WheelSlip[1]:F4} {p.WheelSlip[2]:F4} {p.WheelSlip[3]:F4}]");
            sb.AppendLine($"  Load=[{p.WheelLoad[0]:F1} {p.WheelLoad[1]:F1} {p.WheelLoad[2]:F1} {p.WheelLoad[3]:F1}]");
            sb.AppendLine($"  LatG={p.AccG[0]:F4} LongG={p.AccG[1]:F4} VertG={p.AccG[2]:F4}");
            sb.AppendLine($"  Heading={p.Heading:F3} Tc={p.Tc:F2} Abs={p.Abs:F2} BrakeBias={p.BrakeBias:F3}");
            sb.AppendLine($"  TyreTempM=[{p.TyreTempM[0]:F1} {p.TyreTempM[1]:F1} {p.TyreTempM[2]:F1} {p.TyreTempM[3]:F1}]");
            sb.AppendLine($"  Vib: kerb={p.KerbVibration:F3} slip={p.SlipVibrations:F3} road={p.RoadVibrations:F3} abs={p.AbsVibrations:F3}");
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    private static void LogErr(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "ac_error.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private static void LogConn(System.Text.StringBuilder sb)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "ac_connect.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    private static byte[] StrToBytes(string s, int len)
    {
        byte[] dst = new byte[len];
        if (!string.IsNullOrEmpty(s))
        {
            byte[] src = System.Text.Encoding.UTF8.GetBytes(s);
            int copyLen = Math.Min(src.Length, len);
            Array.Copy(src, dst, copyLen);
        }
        return dst;
    }

    private static byte[] CopyBytes(byte[]? src, int dstLen)
    {
        byte[] dst = new byte[dstLen];
        if (src != null && src.Length > 0)
        {
            int copyLen = Math.Min(src.Length, dstLen);
            Array.Copy(src, dst, copyLen);
        }
        return dst;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
