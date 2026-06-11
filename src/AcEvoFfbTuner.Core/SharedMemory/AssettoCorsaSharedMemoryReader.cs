using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class AssettoCorsaSharedMemoryReader : ISharedMemoryReader
{
    private const string PhysicsMapName = @"Local\acpmf_physics";
    private const string GraphicsMapName = @"Local\acpmf_graphic";
    private const string StaticMapName = @"Local\acpmf_static";

    private MemoryMappedFile? _physicsMmf;
    private MemoryMappedFile? _graphicsMmf;
    private MemoryMappedFile? _staticMmf;

    private MemoryMappedViewAccessor? _physicsView;
    private MemoryMappedViewAccessor? _graphicsView;
    private MemoryMappedViewAccessor? _staticView;

    private int _lastPhysicsPacketId = -1;
    private int _lastGraphicsPacketId = -1;
    private byte[] _rawBuffer = new byte[65536];
    private bool _gameConnectedFired;
    private bool _didLogZeroPkt;

    private SPageFilePhysicsEvo _lastPhysics;

    private float[] _prevTireSpeed = new float[4];
    private double[] _prevSuspensionDeflection = new double[4];

    public bool IsConnected => _physicsMmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    private readonly float[] _tireGrip = [1f, 1f, 1f, 1f];
    public float[] TireGrip => _tireGrip;

    public bool TryConnect()
    {
        if (_physicsMmf != null) return true;

        var log = new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] AC TryConnect: physics={PhysicsMapName} graphics={GraphicsMapName} static={StaticMapName}");

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

        // Graphics — try both possible names
        string[] gfxNames = { GraphicsMapName, @"Local\acpmf_graphics" };
        foreach (var name in gfxNames)
        {
            try
            {
                _graphicsMmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _graphicsView = _graphicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                log.AppendLine($"  graphics OK (name={name})");
                break;
            }
            catch { log.AppendLine($"  graphics FAILED for {name}"); }
        }

        // Static — optional
        try
        {
            _staticMmf = MemoryMappedFile.OpenExisting(StaticMapName, MemoryMappedFileRights.Read);
            _staticView = _staticMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            log.AppendLine($"  static OK");
        }
        catch { log.AppendLine($"  static FAILED (non-critical)"); }

        _lastPhysicsPacketId = -1;
        _lastGraphicsPacketId = -1;
        _gameConnectedFired = false;

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
                    LogErr($"TryReadPhysics call#{_callCount}: rawPkt={rawPktId} acPkt={ac.PacketId} lastPkt={_lastPhysicsPacketId} rawSteer={rawSteer:F6} acSteer={ac.SteerAngle:F6} speed={rawSpeed:F1}");
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

                if (ac.PacketId % 60 == 0)
                    DumpDiag(buffer, physics);
            }
            finally
            {
                handle.Free();
            }

            SynthesizeForces(ref physics);
            SynthesizeVibrations(ref physics);
            _lastPhysics = physics;

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
        catch { return false; }
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
                    _lastPhysics.CurrentMaxRpm = ac.MaxRpm;
                return true;
            }
            finally { handle.Free(); }
        }
        catch { return false; }
    }

    private static SPageFilePhysicsEvo MapPhysics(SPageFilePhysicsAC ac)
    {
        var p = new SPageFilePhysicsEvo();
        p.PacketId = ac.PacketId;
        p.Gas = ac.Gas;
        p.Brake = ac.Brake;
        p.Fuel = ac.Fuel;
        p.Gear = ac.Gear;
        p.Rpms = ac.Rpms;
        // Normalize incoming steer to -1..+1.
        float rawSteer = ac.SteerAngle;
        float steerNorm;
        if (Math.Abs(rawSteer) > 2f)
        {
            // Likely in degrees; use steering lock fallback (900°) to normalize
            float lockDeg = 900f;
            steerNorm = rawSteer / (lockDeg / 2f);
        }
        else if (rawSteer >= 0f && rawSteer <= 1f)
        {
            // Value in 0..1 range — map to -1..+1
            steerNorm = rawSteer * 2f - 1f;
        }
        else
        {
            // Assume already normalized -1..+1
            steerNorm = rawSteer;
        }
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
        p.FinalFf = ac.FinalFF;
        p.PerformanceMeter = ac.PerformanceMeter;
        p.EngineBrake = ac.EngineBrake;
        p.ErsRecoveryLevel = ac.ErsRecoveryLevel;
        p.IsAiControlled = ac.IsAiControlled;
        p.P2pActivations = ac.ErsDeploymentLevel;
        p.P2pStatus = ac.ErsHeatController;
        p.CurrentMaxRpm = 8000;
        p.SlipRatio = CopyArr(ac.SlipRatio, 4);
        p.SlipAngle = new float[4];
        p.TcinAction = ac.TcInAction;
        p.AbsInAction = ac.AbsInAction;
        p.SuspensionDamage = CopyArr(ac.SuspensionDamage, 4);
        p.TyreTemp = CopyArr(ac.TyreTempMqs, 4);
        p.TyreContactNormal = new StructVector3[4];
        p.TyreContactPoint = new StructVector3[4];
        p.TyreContactHeading = new StructVector3[4];
        p.BrakeTemp = new float[4];
        p.TyreTempI = new float[4];
        p.TyreTempM = new float[4];
        p.TyreTempO = new float[4];
        p.IgnitionOn = 1;
        p.IsEngineRunning = 1;
        return p;
    }

    private static SPageFileGraphicEvo MapGraphics(SPageFileGraphicAC ac)
    {
        return new SPageFileGraphicEvo
        {
            PacketId = ac.PacketId,
            Status = ac.Status switch
            {
                0 => AcEvoStatus.AcOff, 1 => AcEvoStatus.AcReplay,
                2 => AcEvoStatus.AcLive, 3 => AcEvoStatus.AcPause,
                _ => AcEvoStatus.AcLive
            },
            Rpm = 0, RpmPercent = 0, GasPercent = 0, BrakePercent = 0,
            IsIgnitionOn = true, IsEngineRunning = true,
            DisplaySpeedKmh = 0, SteeringPercent = 0,
            FfbStrength = 0f, CarFfbMultiplier = 1f, SteerDegrees = 900,
            Npos = ac.NormalizedCarPosition,
            CarLocation = ac.IsInPitLane != 0 ? AcEvoCarLocation.AcevoPitlane
                        : ac.IsInPit != 0 ? AcEvoCarLocation.AcevoPitbox
                        : AcEvoCarLocation.AcevoTrack,
            EngineType = AcEvoEngineType.AcevoInternalCombustion,
            CarCoordinates = new StructVector3[60],
            CarModel = new byte[33], DriverName = new byte[33],
            DriverSurname = new byte[33], PerformanceModeName = new byte[33],
            Flag = AcEvoFlagType.AcNoFlag, GlobalFlag = AcEvoFlagType.AcNoFlag,
            SessionState = new SmevoSessionState { CurrentLap = ac.CompletedLaps + 1, TotalLap = ac.NumberOfLaps },
            TimingState = new SmevoTimingState(),
            TyreLf = new SmevoTyreState(), TyreRf = new SmevoTyreState(),
            TyreLr = new SmevoTyreState(), TyreRr = new SmevoTyreState(),
            CarDamage = new SmevoDamageState(), PitInfo = new SmevoPitInfo(),
            Instrumentation = new SmevoInstrumentation(),
            Electronics = new SmevoElectronics(),
            AssistsState = new SmevoAssistsState(),
        };
    }

    private static SPageFileStaticEvo MapStatic(SPageFileStaticAC ac)
    {
        return new SPageFileStaticEvo
        {
            SmVersion = StrToBytes(ac.SmVersion ?? "", 15),
            AcEvoVersion = StrToBytes(ac.AcVersion ?? "", 15),
            Session = AcEvoSessionType.AcPractice,
            SessionName = new byte[33], Nation = new byte[33],
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
            sb.AppendLine($"  SlipRatio=[{p.SlipRatio[0]:F4} {p.SlipRatio[1]:F4} {p.SlipRatio[2]:F4} {p.SlipRatio[3]:F4}]");
            sb.AppendLine($"  SlipAngle=[{p.SlipAngle[0]:F4} {p.SlipAngle[1]:F4} {p.SlipAngle[2]:F4} {p.SlipAngle[3]:F4}]");
            sb.AppendLine($"  Mz=[{p.Mz[0]:F3} {p.Mz[1]:F3} {p.Mz[2]:F3} {p.Mz[3]:F3}]");
            sb.AppendLine($"  Fy=[{p.Fy[0]:F3} {p.Fy[1]:F3} {p.Fy[2]:F3} {p.Fy[3]:F3}]");
            sb.AppendLine($"  Load=[{p.WheelLoad[0]:F1} {p.WheelLoad[1]:F1} {p.WheelLoad[2]:F1} {p.WheelLoad[3]:F1}]");
            sb.AppendLine($"  LatG={p.AccG[0]:F4} LongG={p.AccG[1]:F4} AccG={p.AccG[2]:F4}");

            // Raw non-zero floats in range 0-400
            sb.AppendLine("--- Non-zero raw floats ---");
            for (int i = 0; i + 4 <= Math.Min(buf.Length, 400); i += 4)
            {
                float f = BitConverter.ToSingle(buf, i);
                if (MathF.Abs(f) > 0.005f)
                    sb.AppendLine($"  Off {i,3}: {f,14:F6}");
            }

            // Hex of first 40 bytes
            sb.AppendLine("--- Hex 0-40 ---");
            for (int row = 0; row < 40 && row + 16 <= buf.Length; row += 16)
            {
                sb.Append($"  Off {row,3}: ");
                for (int c = 0; c < 16; c++) sb.Append($"{buf[row + c]:X2} ");
                sb.AppendLine();
            }
            sb.AppendLine();

            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    private static SPageFilePhysicsEvo MapPhysicsFromBuffer(byte[] b)
    {
        return new SPageFilePhysicsEvo
        {
            PacketId = BitConverter.ToInt32(b, 0),
            Gas = BitConverter.ToSingle(b, 4),
            Brake = BitConverter.ToSingle(b, 8),
            Fuel = BitConverter.ToSingle(b, 12),
            Gear = BitConverter.ToInt32(b, 16),
            Rpms = BitConverter.ToInt32(b, 20),
            SteerAngle = BitConverter.ToSingle(b, 24),
            SpeedKmh = BitConverter.ToSingle(b, 28),
            Velocity = new[] { BitConverter.ToSingle(b, 32), BitConverter.ToSingle(b, 36), BitConverter.ToSingle(b, 40) },
            AccG = new[] { BitConverter.ToSingle(b, 44), BitConverter.ToSingle(b, 48), BitConverter.ToSingle(b, 52) },
            WheelSlip = new[] { BitConverter.ToSingle(b, 56), BitConverter.ToSingle(b, 60), BitConverter.ToSingle(b, 64), BitConverter.ToSingle(b, 68) },
            WheelLoad = new[] { BitConverter.ToSingle(b, 72), BitConverter.ToSingle(b, 76), BitConverter.ToSingle(b, 80), BitConverter.ToSingle(b, 84) },
            WheelsPressure = new[] { BitConverter.ToSingle(b, 88), BitConverter.ToSingle(b, 92), BitConverter.ToSingle(b, 96), BitConverter.ToSingle(b, 100) },
            WheelAngularSpeed = new[] { BitConverter.ToSingle(b, 104), BitConverter.ToSingle(b, 108), BitConverter.ToSingle(b, 112), BitConverter.ToSingle(b, 116) },
            TyreWear = new[] { BitConverter.ToSingle(b, 120), BitConverter.ToSingle(b, 124), BitConverter.ToSingle(b, 128), BitConverter.ToSingle(b, 132) },
            TyreDirtyLevel = new[] { BitConverter.ToSingle(b, 136), BitConverter.ToSingle(b, 140), BitConverter.ToSingle(b, 144), BitConverter.ToSingle(b, 148) },
            TyreCoreTemperature = new[] { BitConverter.ToSingle(b, 152), BitConverter.ToSingle(b, 156), BitConverter.ToSingle(b, 160), BitConverter.ToSingle(b, 164) },
            CamberRad = new[] { BitConverter.ToSingle(b, 168), BitConverter.ToSingle(b, 172), BitConverter.ToSingle(b, 176), BitConverter.ToSingle(b, 180) },
            SuspensionTravel = new[] { BitConverter.ToSingle(b, 184), BitConverter.ToSingle(b, 188), BitConverter.ToSingle(b, 192), BitConverter.ToSingle(b, 196) },
            PitLimiterOn = BitConverter.ToInt32(b, 220),
            Abs = BitConverter.ToSingle(b, 224),
            AutoShifterOn = BitConverter.ToInt32(b, 236),
            RideHeight = new[] { BitConverter.ToSingle(b, 240), BitConverter.ToSingle(b, 244) },
            TurboBoost = BitConverter.ToSingle(b, 248),
            Ballast = BitConverter.ToSingle(b, 252),
            AirDensity = BitConverter.ToSingle(b, 256),
            AirTemp = BitConverter.ToSingle(b, 260),
            RoadTemp = BitConverter.ToSingle(b, 264),
            LocalAngularVel = new[] { BitConverter.ToSingle(b, 268), BitConverter.ToSingle(b, 272), BitConverter.ToSingle(b, 276) },
            FinalFf = BitConverter.ToSingle(b, 280),
            PerformanceMeter = BitConverter.ToSingle(b, 284),
            EngineBrake = BitConverter.ToInt32(b, 288),
            ErsRecoveryLevel = BitConverter.ToInt32(b, 292),
            IsAiControlled = BitConverter.ToInt32(b, 304),
            P2pActivations = BitConverter.ToInt32(b, 296),
            P2pStatus = BitConverter.ToInt32(b, 300),
            CurrentMaxRpm = 8000,
            SlipRatio = new[] { BitConverter.ToSingle(b, 324), BitConverter.ToSingle(b, 328), BitConverter.ToSingle(b, 332), BitConverter.ToSingle(b, 336) },
            SlipAngle = new[] { BitConverter.ToSingle(b, 340), BitConverter.ToSingle(b, 344), BitConverter.ToSingle(b, 348), BitConverter.ToSingle(b, 352) },
            TcinAction = BitConverter.ToInt32(b, 356),
            AbsInAction = BitConverter.ToInt32(b, 360),
            SuspensionDamage = new[] { BitConverter.ToSingle(b, 364), BitConverter.ToSingle(b, 368), BitConverter.ToSingle(b, 372), BitConverter.ToSingle(b, 376) },
            TyreTemp = new[] { BitConverter.ToSingle(b, 380), BitConverter.ToSingle(b, 384), BitConverter.ToSingle(b, 388), BitConverter.ToSingle(b, 392) },
            IgnitionOn = 1,
            IsEngineRunning = 1,
        };
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

    private void SynthesizeVibrations(ref SPageFilePhysicsEvo physics)
    {
        var p = _lastPhysics;

        float speedMs = p.SpeedKmh / 3.6f;

        // ── Road vibration from wheel speed changes ────────────────
        float[] currentSpeeds = p.WheelAngularSpeed != null && p.WheelAngularSpeed.Length >= 4
            ? p.WheelAngularSpeed
            : new float[4];

        float maxDelta = 0f;
        for (int i = 0; i < 4; i++)
        {
            float delta = Math.Abs(currentSpeeds[i] - _prevTireSpeed[i]);
            if (delta > maxDelta) maxDelta = delta;
            _prevTireSpeed[i] = currentSpeeds[i];
        }
        // Scale up: normal road at 30km/h gives delta≈0.08 rad, target ~0.3 road feel
        physics.RoadVibrations = Math.Clamp(maxDelta * 5f, 0f, 1f);

        // ── Kerb vibration from suspension travel changes ──────────
        // Threshold: ignore deltas below 0.003m (normal road flex).
        // A real curb hit causes >0.01m per frame at 333Hz.
        if (p.SuspensionTravel != null && p.SuspensionTravel.Length >= 4)
        {
            float maxAccel = 0f;
            for (int i = 0; i < 4; i++)
            {
                float delta = (float)Math.Abs(p.SuspensionTravel[i] - _prevSuspensionDeflection[i]);
                _prevSuspensionDeflection[i] = p.SuspensionTravel[i];
                if (delta > 0.003f)
                {
                    float accel = (delta - 0.003f) * 30f;
                    if (accel > maxAccel) maxAccel = accel;
                }
            }
            physics.KerbVibration = Math.Clamp(maxAccel, 0f, 1f);
        }

        // ── Slip vibration ────────────────────────────────────────
        if (speedMs > 0.5f && physics.SlipRatio != null && physics.SlipRatio.Length >= 4)
        {
            float maxFrontSlip = Math.Max(physics.SlipRatio[0], physics.SlipRatio[1]);
            physics.SlipVibrations = maxFrontSlip > 0.08f ? Math.Min((maxFrontSlip - 0.08f) * 5f, 1f) : 0f;
        }

        physics.AbsVibrations = (physics.AbsInAction != 0 && speedMs > 5f) && p.WheelAngularSpeed != null && p.WheelAngularSpeed.Length >= 4
            ? Math.Clamp(Math.Abs(p.WheelAngularSpeed[0] - p.WheelAngularSpeed[1]) * 0.3f, 0f, 1f)
            : 0f;
    }

    private float _smLongG;
    private float _smLatG;
    private float _smFrontSlip;
    private float _smRearSlip;
    private float _smCenteringMz;
    private float _smLoadFL, _smLoadFR, _smLoadRL, _smLoadRR;
    private float _lastOutputMz;
    private int _lastGear;
    private int _gearChangeFramesRemaining;

    private void SynthesizeForces(ref SPageFilePhysicsEvo physics)
    {
        float speedMs = physics.SpeedKmh / 3.6f;
        float steerRad = physics.SteerAngle;

        // ── Smooth slip and longG for gear-change stability ────────
        float alpha = 0.25f;
        float frontSlip = physics.SlipRatio != null && physics.SlipRatio.Length >= 2
            ? (physics.SlipRatio[0] + physics.SlipRatio[1]) * 0.5f : 0f;
        float rearSlip = physics.SlipRatio != null && physics.SlipRatio.Length >= 4
            ? (physics.SlipRatio[2] + physics.SlipRatio[3]) * 0.5f : 0.5f;
        _smFrontSlip += (frontSlip - _smFrontSlip) * alpha;
        _smRearSlip += (rearSlip - _smRearSlip) * alpha;

        float latG = physics.AccG != null && physics.AccG.Length >= 1 ? physics.AccG[0] : 0f;
        float longG = physics.AccG != null && physics.AccG.Length >= 2 ? physics.AccG[1] : 0f;

        // ── Gear change detection: suppress forces for ~120ms ──────
        int currentGear = physics.Gear;
        if (currentGear != _lastGear && _lastGear != 0 && currentGear != 0)
        {
            _gearChangeFramesRemaining = 40; // ~120ms at 333Hz
        }
        _lastGear = currentGear;

        if (_gearChangeFramesRemaining > 0)
        {
            _gearChangeFramesRemaining--;
            // Full mute for first 20 frames (~60ms), then linear fade over next 20
            float muteGain = _gearChangeFramesRemaining > 20
                ? 0.05f
                : Math.Clamp(_gearChangeFramesRemaining / 20f, 0.05f, 1f);
            longG *= muteGain;
            latG *= muteGain;
        }

        _smLongG += (longG - _smLongG) * alpha;
        _smLatG += (latG - _smLatG) * 0.15f;

        // ── Tire forces from real telemetry ────────────────────────
        float[] rawLoad = physics.WheelLoad ?? new float[4];
        float loadAlpha = 0.15f;
        _smLoadFL += (rawLoad[0] - _smLoadFL) * loadAlpha;
        _smLoadFR += (rawLoad[1] - _smLoadFR) * loadAlpha;
        _smLoadRL += (rawLoad[2] - _smLoadRL) * loadAlpha;
        _smLoadRR += (rawLoad[3] - _smLoadRR) * loadAlpha;
        float[] load = { _smLoadFL, _smLoadFR, _smLoadRL, _smLoadRR };

        if (physics.Fy == null || physics.Fy.Length < 4)
            physics.Fy = new float[4];
        if (physics.Fx == null || physics.Fx.Length < 4)
            physics.Fx = new float[4];

        for (int i = 0; i < 4; i++)
        {
            // Clamp synthesized lateral/longitudinal forces to avoid extreme spikes
            physics.Fy[i] = Math.Clamp(_smLatG * load[i] * 0.5f, -600f, 600f);
            physics.Fx[i] = Math.Clamp(_smLongG * load[i] * 0.2f, -600f, 600f);
        }

        // ── Off-track / dirty tyres ────────────────────────────────
        float dirtFactor = 1f;
        if (physics.TyreDirtyLevel != null && physics.TyreDirtyLevel.Length >= 4)
        {
            float maxDirt = Math.Max(physics.TyreDirtyLevel[0],
                            Math.Max(physics.TyreDirtyLevel[1],
                            Math.Max(physics.TyreDirtyLevel[2], physics.TyreDirtyLevel[3])));
            dirtFactor = 1f - maxDirt * 0.7f;
        }

        // ── Pneumatic trail ────────────────────────────────────────
        float pneuTrailF = 0.025f * Math.Max(0f, 1f - _smFrontSlip * 0.5f);
        float pneuTrailR = 0.015f * Math.Max(0f, 1f - _smRearSlip * 0.5f);

        // ── Speed-dependent force scaling ──────────────────────────
        // Full force at 20 m/s (~72 km/h). Gradual ramp from 0.2 at very low speed.
        float speedFactor = Math.Clamp(speedMs / 20f, 0.2f, 1f);

        // ── Steer-based centering (dead simple proportional) ─────────
        // MOZA convention: positive force = LEFT, negative = RIGHT
        // Mz = +steer * k: steerNeg→MzNeg→RIGHT, steerPos→MzPos→LEFT
        float absSteer = Math.Abs(steerRad);
        float centeringForce;
        if (absSteer < 0.15f)
            centeringForce = 0f;
        else
            centeringForce = steerRad * speedFactor * 3f;

        if (physics.Mz == null || physics.Mz.Length < 4)
            physics.Mz = new float[4];
        physics.Mz[0] = centeringForce;
        physics.Mz[1] = centeringForce;
        physics.Mz[2] = centeringForce * 0.45f;
        physics.Mz[3] = centeringForce * 0.45f;

        // Zero Fx/Fy out completely during debug
        for (int i = 0; i < 4; i++)
        {
            physics.Fx[i] = 0f;
            physics.Fy[i] = 0f;
        }

        physics.FinalFf = physics.Mz[0];
    }

    private static byte[] CopyUtf8ToFixed(byte[]? src, int dstLen)
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
