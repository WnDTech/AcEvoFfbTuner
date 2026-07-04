using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class AccSharedMemoryReader : ISharedMemoryReader
{
    private const string PhysicsMapName = @"Local\acpmf_physics";
    private const string GraphicsMapName = @"Local\acpmf_graphics";
    private const string StaticMapName = @"Local\acpmf_static";

    private MemoryMappedFile? _physicsMmf;
    private MemoryMappedFile? _graphicsMmf;
    private MemoryMappedFile? _staticMmf;

    private MemoryMappedViewAccessor? _physicsView;
    private MemoryMappedViewAccessor? _graphicsView;
    private MemoryMappedViewAccessor? _staticView;

    private int _lastPhysicsPacketId = -1;
    private int _lastGraphicsPacketId = -1;
    private int _accDiagFrames;
    private byte[] _rawBuffer = new byte[65536];
    private bool _gameConnectedFired;

    private SPageFilePhysicsEvo _lastPhysics;

    public bool IsConnected => _physicsMmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    private readonly float[] _tireGrip = [1f, 1f, 1f, 1f];
    public float[] TireGrip => _tireGrip;

    public bool TryConnect()
    {
        if (_physicsMmf != null) return true;

        var log = new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] ACC TryConnect: physics={PhysicsMapName} graphics={GraphicsMapName} static={StaticMapName}");

        try
        {
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

        try
        {
            _graphicsMmf = MemoryMappedFile.OpenExisting(GraphicsMapName, MemoryMappedFileRights.Read);
            _graphicsView = _graphicsMmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            log.AppendLine($"  graphics OK, capacity={_graphicsView.Capacity}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"  graphics FAILED: {ex.Message}");
        }

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

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_physicsView == null) return false;

        try
        {
            int cap = (int)_physicsView.Capacity;
            if (cap < 800) return false;

            byte[] buffer = _rawBuffer;
            if (buffer.Length < cap)
                buffer = new byte[cap];
            _physicsView.ReadArray(0, buffer, 0, Math.Min(cap, buffer.Length));

            int rawPktId = BitConverter.ToInt32(buffer, 0);
            if (rawPktId == _lastPhysicsPacketId)
                return false;

            _lastPhysicsPacketId = rawPktId;

            if (!_gameConnectedFired)
            {
                _gameConnectedFired = true;
                GameConnected?.Invoke();
            }

            physics = MapPhysicsFromBuffer(buffer);

            // ACC doesn't populate WheelLoad in shared memory — set a
            // reasonable default so the mixer's loadFactor doesn't collapse to 0.1×.
            if (physics.WheelLoad is { Length: >= 4 } && physics.WheelLoad[0] < 1f)
            {
                float staticLoad = 4000f;
                physics.WheelLoad[0] = staticLoad;
                physics.WheelLoad[1] = staticLoad;
                physics.WheelLoad[2] = staticLoad;
                physics.WheelLoad[3] = staticLoad;
            }

            BuildForcesFromTelemetry(ref physics);
            if (++_accDiagFrames % 60 == 0)
                LogErr($"ACC: mz0={physics.Mz[0]:F3} mz1={physics.Mz[1]:F3} steer={physics.SteerAngle:F4} spd={physics.SpeedKmh:F1}");
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
            int size = Marshal.SizeOf<AccGraphics>();
            byte[] buffer = new byte[Math.Min(size, (int)_graphicsView.Capacity)];
            _graphicsView.ReadArray(0, buffer, 0, buffer.Length);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var acc = Marshal.PtrToStructure<AccGraphics>(handle.AddrOfPinnedObject());
                if (acc.PacketId == _lastGraphicsPacketId)
                    return false;
                _lastGraphicsPacketId = acc.PacketId;

                graphics = MapGraphics(acc);
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
            int cap = (int)_staticView.Capacity;
            byte[] buffer = new byte[Math.Min(512, cap)];
            _staticView.ReadArray(0, buffer, 0, buffer.Length);

            staticData = new SPageFileStaticEvo
            {
                SmVersion = ReadFixedString(buffer, 0, 15),
                AcEvoVersion = ReadFixedString(buffer, 15, 15),
                NumberOfSessions = BitConverter.ToInt32(buffer, 60),
                Track = ReadFixedString(buffer, 101, 33),
                TrackConfiguration = ReadFixedString(buffer, 340, 33),
                TrackLengthM = 0f,
            };

            int maxRpm = BitConverter.ToInt32(buffer, 268);
            if (maxRpm > 0)
                _lastPhysics.CurrentMaxRpm = maxRpm;

            return true;
        }
        catch { return false; }
    }

    private static SPageFilePhysicsEvo MapPhysicsFromBuffer(byte[] b)
    {
        var p = new SPageFilePhysicsEvo();

        p.PacketId = BitConverter.ToInt32(b, 0);
        p.Gas = BitConverter.ToSingle(b, 4);
        p.Brake = BitConverter.ToSingle(b, 8);
        p.Fuel = BitConverter.ToSingle(b, 12);
        p.Gear = BitConverter.ToInt32(b, 16);
        p.Rpms = BitConverter.ToInt32(b, 20);

        float steerRaw = BitConverter.ToSingle(b, 24);
        p.SteerAngle = Math.Clamp(steerRaw, -1f, 1f);

        p.SpeedKmh = BitConverter.ToSingle(b, 28);
        p.Velocity = ReadFloatArray(b, 32, 3);
        p.AccG = ReadFloatArray(b, 44, 3);

        p.WheelSlip = ReadFloatArray(b, 56, 4);
        p.WheelLoad = ReadFloatArray(b, 72, 4);
        p.WheelsPressure = ReadFloatArray(b, 88, 4);
        p.WheelAngularSpeed = ReadFloatArray(b, 104, 4);
        p.TyreWear = ReadFloatArray(b, 120, 4);
        p.TyreDirtyLevel = ReadFloatArray(b, 136, 4);
        p.TyreCoreTemperature = ReadFloatArray(b, 152, 4);
        p.CamberRad = ReadFloatArray(b, 168, 4);
        p.SuspensionTravel = ReadFloatArray(b, 184, 4);

        p.Drs = BitConverter.ToInt32(b, 200);
        p.Tc = BitConverter.ToSingle(b, 204);
        p.Heading = BitConverter.ToSingle(b, 208);
        p.Pitch = BitConverter.ToSingle(b, 212);
        p.Roll = BitConverter.ToSingle(b, 216);
        p.CgHeight = BitConverter.ToSingle(b, 220);
        p.CarDamage = ReadFloatArray(b, 224, 5);

        p.NumberOfTyresOut = ReadInt(b, 244);
        p.PitLimiterOn = ReadInt(b, 248);
        p.Abs = BitConverter.ToSingle(b, 252);
        p.KersCharge = BitConverter.ToSingle(b, 256);
        p.KersInput = BitConverter.ToSingle(b, 260);
        p.AutoShifterOn = ReadInt(b, 264);
        p.RideHeight = ReadFloatArray(b, 268, 2);
        p.TurboBoost = BitConverter.ToSingle(b, 276);
        p.Ballast = BitConverter.ToSingle(b, 280);
        p.AirDensity = BitConverter.ToSingle(b, 284);
        p.AirTemp = BitConverter.ToSingle(b, 288);
        p.RoadTemp = BitConverter.ToSingle(b, 292);
        p.LocalAngularVel = ReadFloatArray(b, 296, 3);

        p.FinalFf = BitConverter.ToSingle(b, 308);
        p.PerformanceMeter = BitConverter.ToSingle(b, 312);
        p.EngineBrake = ReadInt(b, 316);
        p.ErsRecoveryLevel = ReadInt(b, 320);
        p.ErsPowerLevel = ReadInt(b, 324);
        p.ErsHeatCharging = ReadInt(b, 328);
        p.ErsIsCharging = ReadInt(b, 332);
        p.KersCurrentKj = BitConverter.ToSingle(b, 336);
        p.DrsAvailable = ReadInt(b, 340);
        p.DrsEnabled = ReadInt(b, 344);

        p.BrakeTemp = ReadFloatArray(b, 348, 4);
        p.Clutch = BitConverter.ToSingle(b, 364);
        p.TyreTempI = ReadFloatArray(b, 368, 4);
        p.TyreTempM = ReadFloatArray(b, 384, 4);
        p.TyreTempO = ReadFloatArray(b, 400, 4);

        p.IsAiControlled = ReadInt(b, 416);
        p.BrakeBias = BitConverter.ToSingle(b, 564);

        p.SlipRatio = ReadFloatArray(b, 640, 4);
        p.SlipAngle = ReadFloatArray(b, 656, 4);
        p.SuspensionDamage = ReadFloatArray(b, 680, 4);

        p.WaterTemp = BitConverter.ToSingle(b, 712);
        p.BrakeTorque = ReadFloatArray(b, 716, 4);
        p.PadLife = ReadFloatArray(b, 740, 4);
        p.DiscLife = ReadFloatArray(b, 756, 4);

        p.IgnitionOn = ReadInt(b, 772);
        p.StarterEngineOn = ReadInt(b, 776);
        p.IsEngineRunning = ReadInt(b, 780);

        p.KerbVibration = BitConverter.ToSingle(b, 784);
        p.SlipVibrations = BitConverter.ToSingle(b, 788);
        p.RoadVibrations = BitConverter.ToSingle(b, 792);
        p.AbsVibrations = BitConverter.ToSingle(b, 796);

        p.Mz = new float[4];
        p.Fx = new float[4];
        p.Fy = new float[4];
        p.CurrentMaxRpm = 8000;
        p.TyreContactNormal = new StructVector3[4];
        p.TyreContactPoint = new StructVector3[4];
        p.TyreContactHeading = new StructVector3[4];
        p.TyreTemp = new float[4];
        p.SlipAngle ??= new float[4];
        p.SlipRatio ??= new float[4];

        return p;
    }

    private static SPageFileGraphicEvo MapGraphics(AccGraphics acc)
    {
        return new SPageFileGraphicEvo
        {
            PacketId = acc.PacketId,
            Status = acc.Status switch
            {
                0 => AcEvoStatus.AcOff,
                1 => AcEvoStatus.AcReplay,
                2 => AcEvoStatus.AcLive,
                3 => AcEvoStatus.AcPause,
                _ => AcEvoStatus.AcLive
            },
            Rpm = 0,
            RpmPercent = 0,
            GasPercent = 0,
            BrakePercent = 0,
            IsIgnitionOn = true,
            IsEngineRunning = true,
            DisplaySpeedKmh = 0,
            SteeringPercent = 0,
            FfbStrength = 0f,
            CarFfbMultiplier = 1f,
            SteerDegrees = 900,
            Npos = acc.NormalizedCarPosition,
            CarLocation = acc.IsInPit != 0 ? AcEvoCarLocation.AcevoPitbox
                        : acc.IsInPitLane != 0 ? AcEvoCarLocation.AcevoPitlane
                        : AcEvoCarLocation.AcevoTrack,
            EngineType = AcEvoEngineType.AcevoInternalCombustion,
            CarCoordinates = new StructVector3[60],
            CarModel = new byte[33],
            DriverName = new byte[33],
            DriverSurname = new byte[33],
            PerformanceModeName = new byte[33],
            Flag = acc.Flag switch
            {
                0 => AcEvoFlagType.AcNoFlag,
                1 => AcEvoFlagType.AcBlueFlag,
                2 => AcEvoFlagType.AcYellowFlag,
                3 => AcEvoFlagType.AcBlackFlag,
                4 => AcEvoFlagType.AcWhiteFlag,
                5 => AcEvoFlagType.AcCheckeredFlag,
                6 => AcEvoFlagType.AcPenaltyFlag,
                7 => AcEvoFlagType.AcGreenFlag,
                8 => AcEvoFlagType.AcOrangeFlag,
                _ => AcEvoFlagType.AcNoFlag
            },
            GlobalFlag = AcEvoFlagType.AcNoFlag,
            SessionState = new SmevoSessionState
            {
                CurrentLap = acc.CompletedLaps + 1,
                TotalLap = acc.NumberOfLaps
            },
            TimingState = new SmevoTimingState(),
            TyreLf = new SmevoTyreState(),
            TyreRf = new SmevoTyreState(),
            TyreLr = new SmevoTyreState(),
            TyreRr = new SmevoTyreState(),
            CarDamage = new SmevoDamageState(),
            PitInfo = new SmevoPitInfo(),
            Instrumentation = new SmevoInstrumentation(),
            Electronics = new SmevoElectronics()
            {
                TcLevel = (sbyte)acc.TC,
                AbsLevel = (sbyte)acc.ABS,
                EngineMapLevel = (sbyte)acc.EngineMap,
            },
            AssistsState = new SmevoAssistsState(),
        };
    }

    private void BuildForcesFromTelemetry(ref SPageFilePhysicsEvo physics)
    {
        float steerRad = physics.SteerAngle;
        float speedKmh = physics.SpeedKmh;
        float speedMs = speedKmh / 3.6f;

        bool isValid = speedMs > 0.5f;
        float[] slipRatio = physics.SlipRatio ?? [];
        float[] slipAngle = physics.SlipAngle ?? [];
        float[] wheelLoad = physics.WheelLoad ?? [];

        // ── Mz from steer-based centering (primary) ──
        // ACC's FinalFF at offset 308 reads as zero in practice, so we use a
        // steer-based synthesis similar to the AC1 reader, but scaled for
        // the EVO mixer defaults (MzScale=30, MzFrontGain=0.42).
        // Base multiplier 50f gives ~26% max output at 36 km/h full lock,
        // comparable to AC1's feel. Clamped to ±40 to prevent runaway.
        float rawMz = 0f;
        if (isValid && Math.Abs(steerRad) > 0.001f)
        {
            float speedFactor = Math.Clamp(speedMs / 20f, 0.2f, 1f);
            float latG = (physics.AccG is { Length: >= 1 }) ? physics.AccG[0] : 0f;
            float latMod = 1f + Math.Abs(latG) * 4.0f;
            rawMz = -steerRad * speedFactor * latMod * 80f;
        }

        if (physics.Mz == null || physics.Mz.Length < 4)
            physics.Mz = new float[4];

        if (isValid && Math.Abs(rawMz) > 0.001f)
        {
            physics.Mz[0] = rawMz;
            physics.Mz[1] = rawMz * 0.55f;
            physics.Mz[2] = rawMz * 0.5f;
            physics.Mz[3] = rawMz * 0.5f;
        }
        else
        {
            physics.Mz[0] = 0f;
            physics.Mz[1] = 0f;
            physics.Mz[2] = 0f;
            physics.Mz[3] = 0f;
        }

        // ── Fx/Fy zeroed — ACC doesn't populate WheelLoad (our 4000N default
        //     is static, not dynamic), so slip-based lateral/longitudinal forces
        //     would be wrong. Rely on Mz (steer-based) as primary centering force.
        if (physics.Fx == null || physics.Fx.Length < 4)
            physics.Fx = new float[4];
        for (int i = 0; i < 4 && i < slipRatio.Length; i++)
            physics.Fx[i] = isValid ? slipRatio[i] * 40000f : 0f;

        if (physics.Fy == null || physics.Fy.Length < 4)
            physics.Fy = new float[4];
        float latG2 = (physics.AccG is { Length: >= 1 }) ? physics.AccG[0] : 0f;
        for (int i = 0; i < 4; i++)
            physics.Fy[i] = isValid ? latG2 * 20000f : 0f;

        // ── AbsInAction inferred from ACC's native AbsVibrations ──
        physics.AbsInAction = physics.AbsVibrations > 0.01f && isValid ? 1 : 0;

        // ── TcinAction inferred from TC level + slip ratio ──
        if (physics.Tc > 0.01f && isValid && slipRatio.Length >= 2)
        {
            float avgFrontSlip = (Math.Abs(slipRatio[0]) + Math.Abs(slipRatio[1])) * 0.5f;
            physics.TcinAction = avgFrontSlip > 0.03f ? 1 : 0;
        }
        else
        {
            physics.TcinAction = 0;
        }

        // ── Clamp stationary vibration to zero ──
        if (!isValid)
        {
            physics.KerbVibration = 0f;
            physics.SlipVibrations = 0f;
            physics.RoadVibrations = 0f;
            physics.AbsVibrations = 0f;
        }
    }

    private static float[] ReadFloatArray(byte[] buf, int offset, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
            result[i] = BitConverter.ToSingle(buf, offset + i * 4);
        return result;
    }

    private static int ReadInt(byte[] buf, int offset)
    {
        if (offset + 4 > buf.Length) return 0;
        return BitConverter.ToInt32(buf, offset);
    }

    private static byte[] ReadFixedString(byte[] buf, int offset, int maxLen)
    {
        byte[] dst = new byte[maxLen];
        if (offset + maxLen <= buf.Length)
            Array.Copy(buf, offset, dst, 0, maxLen);
        return dst;
    }

    private static void LogErr(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "acc_error.log");
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
                "AcEvoFfbTuner", "acc_connect.log");
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

    public void Dispose()
    {
        Disconnect();
    }
}
