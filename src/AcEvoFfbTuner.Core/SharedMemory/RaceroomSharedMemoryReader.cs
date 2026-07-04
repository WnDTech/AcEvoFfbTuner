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

    private R3eShared _lastData;

    private float[] _prevTireSpeed = new float[4];
    private double[] _prevSuspensionDeflection = new double[4];

    public float SteeringCenterTrimDeg { get; set; } = 0.0f;

    public bool IsConnected => _mmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    public float[] TireGrip => _mmf != null ? MapTireDataFloat(_lastData.TireGrip) : new float[4];

    public float[] LocalAccelG => _mmf != null
        ? new[] { (float)_lastData.LocalAcceleration.X, (float)_lastData.LocalAcceleration.Z, (float)_lastData.LocalAcceleration.Y }
        : new float[3];

    public float EngineTorque => _mmf != null ? (float)_lastData.Player.EngineTorque : 0f;

    public float[] TireFlatspot => _mmf != null
        ? new[] {
            _lastData.TireFlatspot.FrontLeft == 1 ? 1f : 0f,
            _lastData.TireFlatspot.FrontRight == 1 ? 1f : 0f,
            _lastData.TireFlatspot.RearLeft == 1 ? 1f : 0f,
            _lastData.TireFlatspot.RearRight == 1 ? 1f : 0f }
        : new float[4];

    public int[] TireOnMtrl => _mmf != null
        ? new[] { _lastData.TireOnMtrl.FrontLeft, _lastData.TireOnMtrl.FrontRight, _lastData.TireOnMtrl.RearLeft, _lastData.TireOnMtrl.RearRight }
        : new int[4];

    public float[] BrakePressure => _mmf != null
        ? new[] { _lastData.BrakePressure.FrontLeft, _lastData.BrakePressure.FrontRight, _lastData.BrakePressure.RearLeft, _lastData.BrakePressure.RearRight }
        : new float[4];

    public float TractionControlPercent => _mmf != null ? _lastData.TractionControlPercent : 0f;

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
                FinalFf = (float)_lastData.Player.SteeringForcePercentage,
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
                AirTemp = 0f, // R3E shared memory does not expose ambient air temperature
                RoadTemp = 0f, // R3E shared memory does not expose track temperature
                TyreTemp = new float[4],
                TyreWear = MapTireDataFloat(_lastData.TireWear),
                TyreTempI = new float[4]
                {
                    _lastData.TireTemp.FrontLeft.CurrentTemp.Left,
                    _lastData.TireTemp.FrontRight.CurrentTemp.Left,
                    _lastData.TireTemp.RearLeft.CurrentTemp.Left,
                    _lastData.TireTemp.RearRight.CurrentTemp.Left,
                },
                TyreTempM = new float[4]
                {
                    _lastData.TireTemp.FrontLeft.CurrentTemp.Center,
                    _lastData.TireTemp.FrontRight.CurrentTemp.Center,
                    _lastData.TireTemp.RearLeft.CurrentTemp.Center,
                    _lastData.TireTemp.RearRight.CurrentTemp.Center,
                },
                TyreTempO = new float[4]
                {
                    _lastData.TireTemp.FrontLeft.CurrentTemp.Right,
                    _lastData.TireTemp.FrontRight.CurrentTemp.Right,
                    _lastData.TireTemp.RearLeft.CurrentTemp.Right,
                    _lastData.TireTemp.RearRight.CurrentTemp.Right,
                },
                BrakeTemp = new float[4]
                {
                    _lastData.BrakeTemp.FrontLeft.CurrentTemp,
                    _lastData.BrakeTemp.FrontRight.CurrentTemp,
                    _lastData.BrakeTemp.RearLeft.CurrentTemp,
                    _lastData.BrakeTemp.RearRight.CurrentTemp,
                },
                WaterTemp = _lastData.EngineTemp,
                SuspensionDamage = new float[4]
                {
                    _lastData.CarDamage.Suspension,
                    _lastData.CarDamage.Suspension,
                    _lastData.CarDamage.Suspension,
                    _lastData.CarDamage.Suspension,
                },
                CarDamage = new float[5]
                {
                    _lastData.CarDamage.Engine,
                    _lastData.CarDamage.Transmission,
                    _lastData.CarDamage.Aerodynamics,
                    _lastData.CarDamage.Suspension,
                    0f,
                },
                TyreContactPoint = new StructVector3[4],
                TyreContactNormal = new StructVector3[4],
                TyreContactHeading = new StructVector3[4],
                AbsInAction = 0,
                IsEngineRunning = 1,
                IsAiControlled = _lastData.ControlType == 1 ? 1 : 0,
            };

            var steeringForcePct = (float)_lastData.Player.SteeringForcePercentage;
            var steerNorm = _lastData.SteerInputRaw; // normalized -1..+1 (R3E API)
            var carSpeedMs = _lastData.CarSpeed;

            // Raw centering force: use percentage magnitude, derive direction
            // from steer angle. R3E SteeringForcePercentage is 0-100%.
            // Pipeline's ApplyCenteringOverride handles all on-center shaping.
            float steerSign = steerNorm >= 0f ? -1f : 1f;
            float centeringForce = steerSign * Math.Abs(steeringForcePct);

            physics.Mz[0] = centeringForce;
            physics.Mz[1] = centeringForce;

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
                float maxFrontSlip = Math.Max(Math.Abs(physics.SlipRatio[0]), Math.Abs(physics.SlipRatio[1]));
                float slipVib = maxFrontSlip > 0.08f ? Math.Min((maxFrontSlip - 0.08f) * 5f, 1f) : 0f;
                physics.SlipVibrations = slipVib;

                // Chassis slip angle from local-space velocity vectors.
                // R3E doesn't expose per-wheel slip angle, but calculates chassis-level
                // sideslip from LocalVelocity (X=left, Z=back in car local space).
                // β = atan2(Vx, -Vz) gives the angle between heading and travel direction.
                // This is real physics data — more accurate than TireGrip-based synthesis.
                float localVx = (float)_lastData.Player.LocalVelocity.X;
                float localVz = (float)_lastData.Player.LocalVelocity.Z;
                float chassisSlipAngle = (float)Math.Atan2(localVx, -localVz);
                chassisSlipAngle = Math.Clamp(chassisSlipAngle, -0.30f, 0.30f);
                physics.SlipAngle[0] = chassisSlipAngle;
                physics.SlipAngle[1] = chassisSlipAngle;
                physics.SlipAngle[2] = chassisSlipAngle;
                physics.SlipAngle[3] = chassisSlipAngle;
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


            if (_lastData.ControlType == 1)
            {
                // AI: R3E zeros SteeringForce during AI control, so synthesize
                // centering from steerNorm * 200x (matches typical SteeringForce magnitude).
                float aiSign = steerNorm >= 0f ? -1f : 1f;
                float aiMagnitude = Math.Abs(steerNorm * 200f);
                float aiCentering = aiSign * aiMagnitude;
                physics.Mz[0] = aiCentering;
                physics.Mz[1] = aiCentering;
                physics.Fx[0] = physics.Fx[1] = physics.Fx[2] = physics.Fx[3] = 0f;
                physics.Fy[0] = physics.Fy[1] = physics.Fy[2] = physics.Fy[3] = 0f;
            }
            if (_lastData.Player.GForce.X != 0 || _lastData.Player.GForce.Y != 0 || _lastData.Player.GForce.Z != 0)
            {
                physics.AccG[0] = (float)_lastData.Player.GForce.X;
                physics.AccG[1] = (float)_lastData.Player.GForce.Z;
                physics.AccG[2] = (float)_lastData.Player.GForce.Y;
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

            // ── Overlay fields (race info) ──
            graphics.GapAhead = d.TimeDeltaFront;
            graphics.GapBehind = d.TimeDeltaBehind;
            graphics.CurrentPos = (uint)d.Position;
            graphics.TotalDrivers = (uint)d.NumCars;
            graphics.FuelLiterCurrentQuantity = d.FuelLeft;
            graphics.FuelLiterPerLap = d.FuelPerLap;
            graphics.LapsPossibleWithFuel = d.FuelPerLap > 0 ? d.FuelLeft / d.FuelPerLap : 0;
            graphics.TotalLapCount = d.NumberOfLaps;
            graphics.IsInPitLane = d.InPitlane != 0;
            graphics.IsLastLap = d.NumberOfLaps > 0 && d.CompletedLaps >= d.NumberOfLaps;
            graphics.IsWrongWay = false;

            // Map R3E flags to AcEvoFlagType
            if (d.Flags.Checkered != 0)
                graphics.Flag = AcEvoFlagType.AcCheckeredFlag;
            else if (d.Flags.Yellow != 0)
                graphics.Flag = AcEvoFlagType.AcYellowFlag;
            else if (d.Flags.Blue != 0)
                graphics.Flag = AcEvoFlagType.AcBlueFlag;
            else if (d.Flags.Black != 0)
                graphics.Flag = AcEvoFlagType.AcBlackFlag;
            else if (d.Flags.White != 0)
                graphics.Flag = AcEvoFlagType.AcWhiteFlag;
            else if (d.Flags.Green != 0)
                graphics.Flag = AcEvoFlagType.AcGreenFlag;
            else
                graphics.Flag = AcEvoFlagType.AcNoFlag;

            graphics.GlobalFlag = graphics.Flag;

            // Timing
            graphics.SessionState = new SmevoSessionState
            {
                CurrentLap = d.CompletedLaps + 1,
                TotalLap = d.NumberOfLaps,
                TimeLeft = System.Text.Encoding.ASCII.GetBytes(d.SessionTimeRemaining > 0 ? $"{d.SessionTimeRemaining:F0}" : ""),
            };

            graphics.LastLaptimeMs = (int)(d.LapTimePreviousSelf * 1000f);
            graphics.BestLaptimeMs = (int)(d.LapTimeBestSelf * 1000f);

            graphics.CarCoordinates[0] = new StructVector3
            {
                X = d.CarCgLocation.X,
                Y = d.CarCgLocation.Y,
                Z = d.CarCgLocation.Z
            };

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
        return (tireSpeedMs - carSpeedMs) / carSpeedMs;
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
