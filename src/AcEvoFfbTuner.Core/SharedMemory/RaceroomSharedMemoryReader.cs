using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class RaceroomSharedMemoryReader : ISharedMemoryReader
{
    private const string SharedMemoryName = @"$R3E";

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;

    private int _lastTicks = -1;
    private byte[] _rawBuffer = new byte[65536];

    private R3eShared _lastData;

    private float[] _prevTireSpeed = new float[4];
    private double[] _prevSuspensionDeflection = new double[4];

    public float SteeringCenterTrimDeg { get; set; } = 0.0f;

    public bool IsConnected => _mmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    public float[] TireGrip => _mmf != null ? MapTireDataFloat(_lastData.TireGrip) : new float[4];

    public bool TryConnect()
    {
        if (_mmf != null) return true;

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            
            // DEBUG: Log connection success
            System.Diagnostics.Debug.WriteLine($"[R3E] Connected to shared memory '{SharedMemoryName}'");
            
            _lastTicks = -1;
            GameConnected?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[R3E] Connect failed: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _lastTicks = -1;
        GameDisconnected?.Invoke();
    }

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_view == null) return false;

        try
        {
            int size = Marshal.SizeOf<R3eShared>();
            byte[] buffer = new byte[size];
            _view.ReadArray(0, buffer, 0, size);

            System.Diagnostics.Debug.WriteLine($"[R3E] Read {size} bytes from shared memory");

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                _lastData = Marshal.PtrToStructure<R3eShared>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            if (_lastData.Player.GameSimulationTicks == _lastTicks)
                return false;

            _lastTicks = _lastData.Player.GameSimulationTicks;

            // DEBUG: Check version compatibility
            System.Diagnostics.Debug.WriteLine($"[R3E] Version: Major={_lastData.VersionMajor}, Minor={_lastData.VersionMinor}");

            physics = new SPageFilePhysicsEvo
            {
                PacketId = _lastData.Player.GameSimulationTicks,
                Gas = Clamp01(_lastData.Throttle),
                Brake = Clamp01(_lastData.Brake),
                Fuel = _lastData.FuelLeft,
                Gear = _lastData.Gear,
                Rpms = (int)(_lastData.EngineRps * 60f / (2f * MathF.PI)),
                SteerAngle = _lastData.SteerInputRaw,
                SpeedKmh = _lastData.CarSpeed * 3.6f,
                Velocity = new float[3],
                AccG = new float[3],
                WheelSlip = new float[4],
                WheelLoad = MapTireDataFloat(_lastData.TireLoad),
                WheelsPressure = MapTireDataFloat(_lastData.TirePressure),
                WheelAngularSpeed = MapTireDataFloat(_lastData.TireRps),
                TyreDirtyLevel = MapTireDataFloat(_lastData.TireDirt),
                SuspensionTravel = new float[4],
                Heading = _lastData.CarOrientation.Yaw,
                Pitch = _lastData.CarOrientation.Pitch,
                Roll = _lastData.CarOrientation.Roll,
                FinalFf = (float)_lastData.Player.SteeringForce,
                SlipRatio = new float[4],
                SlipAngle = new float[4],
                Mz = new float[4],
                Fx = new float[4],
                Fy = new float[4],
                LocalAngularVel = new float[3],
                KerbVibration = 0f,
                SlipVibrations = 0f,
                RoadVibrations = 0f,
                AbsVibrations = 0f,
                AirTemp = _lastData.EngineTemp,
                SuspensionDamage = new float[4],
                TyreTemp = new float[4],
                TyreWear = MapTireDataFloat(_lastData.TireWear),
                TyreContactPoint = new StructVector3[4],
                TyreContactNormal = new StructVector3[4],
                TyreContactHeading = new StructVector3[4],
                AbsInAction = 0,
                IsEngineRunning = 1,
            };

            var steeringForce = (float)_lastData.Player.SteeringForce;
            var steerNorm = _lastData.SteerInputRaw; // normalized -1..+1 (R3E API)
            var carSpeedMs = _lastData.CarSpeed;

            // Moza R5 convention: positive force = wheel LEFT (auto-detect confirmed).
            // -absForce * steerSign ensures centering pushes toward center.
            float absForce = Math.Abs(steeringForce);
            float steerSign = steerNorm > 0f ? 1f : -1f;
            float absSteer = Math.Abs(steerNorm);
            float blend = Math.Clamp(absSteer / 0.002f, 0f, 1f);

            // Center smooth zone: no deadzone — DD wheels don't need software
            // deadbands. Force scales linearly from 0°, eliminating the center notch.
            float steerDeg = steerNorm * _lastData.SteerLockDegrees * 0.5f;
            float absSteerDeg = Math.Abs(steerDeg);
            const float centerSmoothZoneDeg = 5.0f;
            float centeringFactor;
            if (absSteerDeg < centerSmoothZoneDeg)
            {
                float t = absSteerDeg / centerSmoothZoneDeg;
                centeringFactor = MathF.Pow(t, 0.5f); // sqrt: fast ramp-up from zero
            }
            else
            {
                centeringFactor = 1f;
            }

            physics.Mz[0] = -absForce * steerSign * blend * centeringFactor;
            physics.Mz[1] = -absForce * steerSign * blend * centeringFactor;

            const float minSpeedThreshold = 0.5f;

            if (carSpeedMs >= minSpeedThreshold)
            {
                physics.SlipRatio[0] = CalculateSlipRatio(_lastData.TireSpeed.FrontLeft, carSpeedMs);
                physics.SlipRatio[1] = CalculateSlipRatio(_lastData.TireSpeed.FrontRight, carSpeedMs);
                physics.SlipRatio[2] = CalculateSlipRatio(_lastData.TireSpeed.RearLeft, carSpeedMs);
                physics.SlipRatio[3] = CalculateSlipRatio(_lastData.TireSpeed.RearRight, carSpeedMs);

                // Synthesize slip vibration from front tyre slip ratio.
                // GT3 feel: subtle buzz through the wheel as tyres approach the limit.
                // Threshold 0.08 (~8% slip) — below this the tyre is gripping.
                float maxFrontSlip = Math.Max(physics.SlipRatio[0], physics.SlipRatio[1]);
                float slipVib = maxFrontSlip > 0.08f ? Math.Min((maxFrontSlip - 0.08f) * 5f, 1f) : 0f;
                physics.SlipVibrations = slipVib;

                // Synthesize slip angle from R3E's real TireGrip data using a nonlinear
                // mapping with deadband. The real relationship between grip utilization
                // and slip angle follows a Pacejka-like curve:
                //   - Linear region (TireGrip 0.85-1.0): slipAngle ≈ 0 (tyre grips normally)
                //   - Transition  (TireGrip 0.60-0.85): slipAngle builds slowly as peak Mz approaches
                //   - Post-peak   (TireGrip 0.00-0.60): slipAngle ramps to max as tyre fully slides
                // This prevents constant rumble during normal cornering — slip-sensitive effects
                // (scrub, rear slip, slip enhancer, grip guard) only activate when the tyre
                // is genuinely working near or past its grip limit.
                const float maxSyntheticSlipAngle = 0.20f;
                physics.SlipAngle[0] = SynthesizeSlipAngle(_lastData.TireGrip.FrontLeft, maxSyntheticSlipAngle);
                physics.SlipAngle[1] = SynthesizeSlipAngle(_lastData.TireGrip.FrontRight, maxSyntheticSlipAngle);
                physics.SlipAngle[2] = SynthesizeSlipAngle(_lastData.TireGrip.RearLeft, maxSyntheticSlipAngle);
                physics.SlipAngle[3] = SynthesizeSlipAngle(_lastData.TireGrip.RearRight, maxSyntheticSlipAngle);
            }

            physics.KerbVibration = SynthesizeKerbVibration();
            physics.RoadVibrations = SynthesizeRoadVibration();

            var absState = _lastData.AidSettings.Abs;
            physics.AbsInAction = (absState == 1 || absState == 5) ? 1 : 0;
            physics.AbsVibrations = SynthesizeAbsVibration(absState);

            var tireLoads = new float[4]
            {
                _lastData.TireLoad.FrontLeft,
                _lastData.TireLoad.FrontRight,
                _lastData.TireLoad.RearLeft,
                _lastData.TireLoad.RearRight
            };

            var localLatG = _lastData.LocalAcceleration.X;

            for (int i = 0; i < 4; i++)
            {
                physics.Fy[i] = localLatG * tireLoads[i] * 0.01f;
                physics.Fx[i] = physics.SlipRatio[i] * tireLoads[i] * 0.05f;
            }

            if (_lastData.Player.LocalGforce.X != 0 || _lastData.Player.LocalGforce.Y != 0 || _lastData.Player.LocalGforce.Z != 0)
            {
                physics.AccG[0] = (float)_lastData.Player.LocalGforce.X;
                physics.AccG[1] = (float)_lastData.Player.LocalGforce.Y;
                physics.AccG[2] = (float)_lastData.Player.LocalGforce.Z;
            }

            if (_lastData.Player.AngularVelocity.X != 0 || _lastData.Player.AngularVelocity.Y != 0 || _lastData.Player.AngularVelocity.Z != 0)
            {
                physics.LocalAngularVel[0] = (float)_lastData.Player.AngularVelocity.X;
                physics.LocalAngularVel[1] = (float)_lastData.Player.AngularVelocity.Y;
                physics.LocalAngularVel[2] = (float)_lastData.Player.AngularVelocity.Z;
            }

            if (_lastData.Player.Velocity.X != 0 || _lastData.Player.Velocity.Y != 0 || _lastData.Player.Velocity.Z != 0)
            {
                physics.Velocity[0] = (float)_lastData.Player.Velocity.X;
                physics.Velocity[1] = (float)_lastData.Player.Velocity.Y;
                physics.Velocity[2] = (float)_lastData.Player.Velocity.Z;
            }

            if (_lastData.Player.LocalVelocity.X != 0 || _lastData.Player.LocalVelocity.Y != 0 || _lastData.Player.LocalVelocity.Z != 0)
            {
                physics.LocalVelocity = new float[3]
                {
                    (float)_lastData.Player.LocalVelocity.X,
                    (float)_lastData.Player.LocalVelocity.Y,
                    (float)_lastData.Player.LocalVelocity.Z
                };
            }

            if (_lastData.Player.SuspensionDeflection.FrontLeft != 0 || _lastData.Player.SuspensionDeflection.FrontRight != 0)
            {
                physics.SuspensionTravel = new float[4]
                {
                    (float)_lastData.Player.SuspensionDeflection.FrontLeft,
                    (float)_lastData.Player.SuspensionDeflection.FrontRight,
                    (float)_lastData.Player.SuspensionDeflection.RearLeft,
                    (float)_lastData.Player.SuspensionDeflection.RearRight
                };
            }
 
            return true;
        }
        catch
        {
            var reason = _view == null ? "view is null" : "read failed";
            System.Diagnostics.Debug.WriteLine($"[R3E] ReadPhysics error: {reason}");
            return false;
        }
    }

    public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
    {
        graphics = default;
        if (_view == null) return false;

        try
        {
            var d = _lastData;
            float rpmPercent = d.MaxEngineRps > 0 ? d.EngineRps / d.MaxEngineRps : 0f;

            graphics = new SPageFileGraphicEvo
            {
                PacketId = d.Player.GameSimulationTicks,
                Status = d.GamePaused != 0 ? AcEvoStatus.AcPause : AcEvoStatus.AcLive,
                RpmPercent = rpmPercent,
                IsRpmLimiterOn = d.EngineRps >= d.MaxEngineRps && d.MaxEngineRps > 0,
                IsChangeUpRpm = d.UpshiftRps > 0 && d.EngineRps >= d.UpshiftRps * 0.95f,
                FfbStrength = 0f,
                CarFfbMultiplier = 0f,
                SteerDegrees = d.SteerLockDegrees > 0 ? d.SteerLockDegrees : 900,
                Npos = d.LapDistanceFraction >= 0 ? d.LapDistanceFraction : 0f,
                Flag = AcEvoFlagType.AcNoFlag,
                IsIgnitionOn = true,
                IsEngineRunning = true,
                EngineType = AcEvoEngineType.AcevoInternalCombustion,
                CarLocation = d.GameInMenus != 0 ? AcEvoCarLocation.AcevoPitbox : AcEvoCarLocation.AcevoTrack,
                CarCoordinates = new StructVector3[60],
                UseSingleCompound = false,
                CarModel = new byte[33],
                DriverName = new byte[33],
                DriverSurname = new byte[33],
                PerformanceModeName = new byte[33],
            };

            graphics.CarCoordinates[0] = new StructVector3
            {
                X = d.CarCgLocation.X,
                Y = d.CarCgLocation.Y,
                Z = d.CarCgLocation.Z
            };

            graphics.SessionState = new SmevoSessionState { CurrentLap = d.CompletedLaps + 1 };

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReadStatic(out SPageFileStaticEvo staticData)
    {
        staticData = default;
        if (_view == null) return false;

        try
        {
            var d = _lastData;

            staticData = new SPageFileStaticEvo
            {
                SmVersion = System.Text.Encoding.ASCII.GetBytes("R3E\0"),
                AcEvoVersion = System.Text.Encoding.ASCII.GetBytes($"{d.VersionMajor}.{d.VersionMinor}\0"),
                Session = AcEvoSessionType.AcPractice,
                EventId = 0,
                SessionId = 0,
                SessionName = new byte[33],
                NumberOfSessions = 1,
                Nation = new byte[33],
                Longitude = 0f,
                Latitude = 0f,
                Track = CopyUtf8ToFixed(d.TrackName, 33),
                TrackConfiguration = CopyUtf8ToFixed(d.LayoutName, 33),
                TrackLengthM = d.LayoutLength,
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));

    private static float[] MapTireDataFloat(R3eTireData<float> td) => new float[4]
    {
        td.FrontLeft < 0 ? 0f : td.FrontLeft,
        td.FrontRight < 0 ? 0f : td.FrontRight,
        td.RearLeft < 0 ? 0f : td.RearLeft,
        td.RearRight < 0 ? 0f : td.RearRight
    };

    private static float CalculateSlipRatio(float tireSpeedMs, float carSpeedMs)
    {
        if (carSpeedMs <= 0f) return 0f;
        return Math.Abs(tireSpeedMs - carSpeedMs) / carSpeedMs;
    }

    /// <summary>
    /// Nonlinear slip angle synthesis from R3E's TireGrip with deadband.
    /// TireGrip 1.0-0.85 (linear region): slipAngle ≈ 0 — no rumble in normal cornering.
    /// TireGrip 0.85-0.60 (transition): slipAngle builds quadratically — approaching peak Mz.
    /// TireGrip 0.60-0.00 (post-peak): slipAngle continues to max — full slide feel.
    /// This prevents slip-sensitive effects (scrub, rear slip, slip enhancer, grip guard)
    /// from activating during normal grip cornering.
    /// </summary>
    private static float SynthesizeSlipAngle(float tireGrip, float maxSlipAngle)
    {
        float gripLoss = 1.0f - Math.Clamp(tireGrip, 0f, 1f);
        // Deadband: gripLoss must exceed 0.15 (TireGrip < 0.85) before any signal.
        // This represents the linear elastic region of the tyre where grip is abundant.
        float deadbandLoss = Math.Max(gripLoss - 0.15f, 0f) / 0.85f; // normalized 0..1
        // Quadratic curve: signal builds slowly near deadband, accelerates as grip fades.
        // Matches the Pacejka Mz characteristic shape.
        return deadbandLoss * deadbandLoss * maxSlipAngle;
    }


    private float SynthesizeRoadVibration()
    {
        if (_lastData.CarSpeed < 1f) return 0f;

        float maxDelta = 0f;
        float[] currentSpeeds = { _lastData.TireSpeed.FrontLeft, _lastData.TireSpeed.FrontRight, _lastData.TireSpeed.RearLeft, _lastData.TireSpeed.RearRight };

        for (int i = 0; i < 4; i++)
        {
            float delta = Math.Abs(currentSpeeds[i] - _prevTireSpeed[i]);
            if (delta > maxDelta) maxDelta = delta;
            _prevTireSpeed[i] = currentSpeeds[i];
        }

        return Math.Min(maxDelta * 2f, 1f);
    }

    private float SynthesizeKerbVibration()
    {
        double[] currentDeflection = { _lastData.Player.SuspensionDeflection.FrontLeft, _lastData.Player.SuspensionDeflection.FrontRight, _lastData.Player.SuspensionDeflection.RearLeft, _lastData.Player.SuspensionDeflection.RearRight };

        float maxAcceleration = 0f;

        for (int i = 0; i < 4; i++)
        {
            double delta = Math.Abs(currentDeflection[i] - _prevSuspensionDeflection[i]);
            float accel = (float)(delta * 400d);
            if (accel > maxAcceleration) maxAcceleration = accel;
            _prevSuspensionDeflection[i] = currentDeflection[i];
        }

        return Math.Min(maxAcceleration * 0.3f, 1f);
    }

    private float SynthesizeAbsVibration(int absState)
    {
        if (absState != 5) return 0f;

        float frontAsymmetry = Math.Abs(_lastData.TireSpeed.FrontLeft - _lastData.TireSpeed.FrontRight);
        float rearAsymmetry = Math.Abs(_lastData.TireSpeed.RearLeft - _lastData.TireSpeed.RearRight);
        float maxAsymmetry = Math.Max(frontAsymmetry, rearAsymmetry);

        return Math.Min(maxAsymmetry * 0.1f + 0.02f, 1f);
    }

    private static byte[] CopyUtf8ToFixed(byte[] src, int dstLen)
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
