using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class LmuSharedMemoryReader : ISharedMemoryReader
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private long _capacity;

    private long _lastReadTicks;
    private byte[] _rawBuffer = new byte[524288];
    private int _telemInfoOffset = -1;
    private int _stride = 1888;
        private int _lockedSlotIndex = -1;
        private int _connectAttempts;
        private bool _loggedHeader;


    public bool IsConnected => _mmf != null;

    public event Action? GameConnected;
    public event Action? GameDisconnected;

    private readonly float[] _tireGrip = new float[4];
    private bool _loggedWheelDump;
    public float[] TireGrip => _tireGrip;
    public float[] LocalAccelG { get; private set; } = new float[3];

    private float _prevAccY;
    private float _prevAccX;
    private bool _prevAccInitialized;

    // ── LMU-specific extended data for pipeline ──
    public float[] SuspForces { get; private set; } = new float[4];
    public float[] BrakePressures { get; private set; } = new float[4];
    public float[] PatchVelocities { get; private set; } = new float[4];
    public float[] VerticalTireDeflections { get; private set; } = new float[4];
    public float[] CamberRad { get; private set; } = new float[4];
    public float[] TireCarcassTemps { get; private set; } = new float[4];
    public float[] InnerLayerTemps { get; private set; } = new float[4];
    public float[] OptimalTemps { get; private set; } = new float[4];
    public string[] TerrainNames { get; private set; } = new string[4];

    public float FrontDownforce { get; private set; }
    public float RearDownforce { get; private set; }
    public float Drag { get; private set; }
    public float EngineTorque { get; private set; }
    public float LastImpactMagnitude { get; private set; }
    public float LastImpactET { get; private set; }
    public float DentSeverity { get; private set; }
    public float FrontRideHeight { get; private set; }
    public float RearRideHeight { get; private set; }
    public float FrontWingHeight { get; private set; }
    public float Front3rdDeflection { get; private set; }
    public float Rear3rdDeflection { get; private set; }
    public float PhysicalSteeringWheelRange { get; private set; }
    public float VisualSteeringWheelRange { get; private set; }
    public float RearBrakeBias { get; private set; }
    public float TurboBoostPressure { get; private set; }
    public float ElectricBoostMotorTorque { get; private set; }
    public float ElectricBoostMotorRPM { get; private set; }
    public float ElectricBoostMotorTemp { get; private set; }
    public float Regen { get; private set; }
    public float Soc { get; private set; }
    public float BatteryChargeFraction { get; private set; }
    public float DeltaBest { get; private set; }
    public int CurrentSector { get; private set; }
    public int FuelCapacity { get; private set; }
    public string? FrontTireCompoundName { get; private set; }
    public string? RearTireCompoundName { get; private set; }
    public string? VehicleName { get; private set; }

    private const int TI_VEHICLE_NAME = 32;
    private const int TI_TRACK_NAME = 96;
    private const int TI_LAP_NUMBER = 20;
    private const int TI_LAP_START_ET = 24;
    private const int TI_DELTA_TIME = 4;
    private const int TI_ELAPSED_TIME = 12;
    private const int TI_POS = 160;
    private const int TI_LOCAL_VEL = 184;
    private const int TI_LOCAL_ACCEL = 208;
    private const int TI_LOCAL_ROT = 304;
    private const int TI_GEAR = 352;
    private const int TI_ENGINE_RPM = 356;
    private const int TI_ENGINE_WATER_TEMP = 364;
    private const int TI_ENGINE_OIL_TEMP = 372;
    private const int TI_CLUTCH_RPM = 380;
    private const int TI_UNFILTERED_THROTTLE = 388;
    private const int TI_UNFILTERED_BRAKE = 396;
    private const int TI_UNFILTERED_STEERING = 404;
    private const int TI_UNFILTERED_CLUTCH = 412;
    private const int TI_STEERING_SHAFT_TORQUE = 452;
    private const int TI_FRONT_3RD_DEFLECTION = 460;
    private const int TI_REAR_3RD_DEFLECTION = 468;
    private const int TI_FRONT_WING_HEIGHT = 476;
    private const int TI_FRONT_RIDE_HEIGHT = 484;
    private const int TI_REAR_RIDE_HEIGHT = 492;
    private const int TI_DRAG = 500;
    private const int TI_FRONT_DOWNFORCE = 508;
    private const int TI_REAR_DOWNFORCE = 516;
    private const int TI_ENGINE_MAX_RPM = 532;
    private const int TI_DENT_SEVERITY = 544;
    private const int TI_LAST_IMPACT_ET = 552;
    private const int TI_LAST_IMPACT_MAGNITUDE = 560;
    private const int TI_ENGINE_TORQUE = 592;
    private const int TI_CURRENT_SECTOR = 600;
    private const int TI_FUEL_CAPACITY = 608;
    private const int TI_FRONT_TIRE_COMPOUND = 620;
    private const int TI_REAR_TIRE_COMPOUND = 638;
    private const int TI_VISUAL_STEER_RANGE = 660;
    private const int TI_REAR_BRAKE_BIAS = 664;
    private const int TI_TURBO_BOOST = 672;
    private const int TI_PHYSICAL_STEER_RANGE = 692;
    private const int TI_DELTA_BEST = 696;
    private const int TI_BATTERY_CHARGE = 704;
    private const int TI_ERS_MOTOR_TORQUE = 712;
    private const int TI_ERS_MOTOR_RPM = 720;
    private const int TI_ERS_MOTOR_TEMP = 728;
    private const int TI_REGEN = 768;
    private const int TI_SOC = 772;
    private const int TI_VIRTUAL_ENERGY = 776;

    // Wheel field offsets (from wheel struct start)
    private const int W_SUSP_DEFLECTION = 0;
    private const int W_SUSP_FORCE = 16;
    private const int W_BRAKE_PRESSURE = 32;
    private const int W_ROTATION = 40;
    private const int W_LAT_PATCH_VEL = 48;
    private const int W_LON_PATCH_VEL = 56;
    private const int W_LAT_GROUND_VEL = 64;
    private const int W_LON_GROUND_VEL = 72;
    private const int W_CAMBER = 80;
    private const int W_LAT_FORCE = 88;
    private const int W_LON_FORCE = 96;
    private const int W_TIRE_LOAD = 104;
    private const int W_GRIP_FRACT = 112;
    private const int W_PRESSURE = 120;
    private const int W_TEMP_INNER = 128;
    private const int W_TEMP_MID = 136;
    private const int W_TEMP_OUTER = 144;
    private const int W_WEAR = 152;
    private const int W_TERRAIN_NAME = 160;
    private const int W_VERT_TIRE_DEFLECTION = 180;
    private const int W_WHEEL_Y_LOCATION = 188;
    private const int W_TOE = 196;
    private const int W_TIRE_CARCASS_TEMP = 204;
    private const int W_INNER_LAYER_TEMP0 = 212;
    private const int W_INNER_LAYER_TEMP1 = 220;
    private const int W_INNER_LAYER_TEMP2 = 228;
    private const int W_OPTIMAL_TEMP = 236;

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "lmu_debug.log");

    private static void Log(string msg)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogPath);
            if (dir != null) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    public bool TryConnect()
    {
        if (_mmf != null) { Log("TryConnect: already connected"); return true; }
        _connectAttempts++;
        Log($"TryConnect attempt #{_connectAttempts} opening '{LmuNative.SmMapName}'...");
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(LmuNative.SmMapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _capacity = _view.Capacity;
            _lastReadTicks = 0;
            _telemInfoOffset = -1;
            _lockedSlotIndex = -1;
            _loggedHeader = false;
            Log($"SUCCESS: Connected to '{LmuNative.SmMapName}' ({_capacity} bytes)");
            GameConnected?.Invoke();
            return true;
        }
        catch (FileNotFoundException)
        {
            Log($"FAILED: Shared memory map '{LmuNative.SmMapName}' does not exist. Is LMU running?");
        }
        catch (Exception ex)
        {
            Log($"FAILED: {ex.GetType().Name}: {ex.Message}");
        }
        Disconnect();
        return false;
    }

    public void Disconnect()
    {
        _view?.Dispose();
        _mmf?.Dispose();
        _view = null;
        _mmf = null;
        _lastReadTicks = 0;
        _telemInfoOffset = -1;
        _lockedSlotIndex = -1;
        _loggedHeader = false;
        _loggedWheelDump = false;
        _prevAccInitialized = false;
        Log("Disconnected");
        GameDisconnected?.Invoke();
    }

    public bool TryReadPhysics(out SPageFilePhysicsEvo physics)
    {
        physics = default;
        if (_view == null) { Log("TryReadPhysics: view is null"); return false; }

        try
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastReadTicks < Stopwatch.Frequency / 250) return false;
            _lastReadTicks = now;

            int readLen = (int)Math.Min(_capacity, _rawBuffer.Length);
            _view.ReadArray(0, _rawBuffer, 0, readLen);

            if (!_loggedHeader)
            {
                int ev0 = LmuFieldReader.ReadI32(_rawBuffer, 0);
                float ffb = LmuFieldReader.ReadF32(_rawBuffer, 64);
                string s0 = LmuFieldReader.ReadStr(_rawBuffer, 328, 64);
                Log($"Buffer: {readLen} bytes, events[0]={ev0}, FFBTorque={ffb:F6}, pathStart='{s0}'");
                _loggedHeader = true;
            }

            float ffbTorque = LmuFieldReader.ReadF32(_rawBuffer, 64);

            int t = _telemInfoOffset;
            if (t < 0 || t + 450 > readLen)
            {
                if (!FindTelemSection(_rawBuffer, readLen))
                {
                    Log("TryReadPhysics: no telemetry section found");
                    return false;
                }
                t = _telemInfoOffset;
            }

            if (t < 0 || t + 450 > readLen)
            {
                Log($"TryReadPhysics: bad offset {t} (len={readLen})");
                return false;
            }

            double steeringTorque = LmuFieldReader.ReadF64(_rawBuffer, t + TI_STEERING_SHAFT_TORQUE);
            double throttle = LmuFieldReader.ReadF64(_rawBuffer, t + TI_UNFILTERED_THROTTLE);
            double brake = LmuFieldReader.ReadF64(_rawBuffer, t + TI_UNFILTERED_BRAKE);
            double steer = LmuFieldReader.ReadF64(_rawBuffer, t + TI_UNFILTERED_STEERING);
            double rpm = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ENGINE_RPM);
            int gear = LmuFieldReader.ReadI32(_rawBuffer, t + TI_GEAR);
            double vx = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_VEL);
            double vy = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_VEL + 8);
            double vz = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_VEL + 16);
            double speedMs = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            double accX = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ACCEL);
            double accY = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ACCEL + 8);
            double accZ = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ACCEL + 16);

            LocalAccelG = [(float)accX, -(float)accZ, -(float)accY];

            // ── Chassis-level fields ──
            double engineTorque = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ENGINE_TORQUE);
            double frontDownforce = LmuFieldReader.ReadF64(_rawBuffer, t + TI_FRONT_DOWNFORCE);
            double rearDownforce = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REAR_DOWNFORCE);
            double drag = LmuFieldReader.ReadF64(_rawBuffer, t + TI_DRAG);
            double lastImpactMag = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LAST_IMPACT_MAGNITUDE);
            double lastImpactET = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LAST_IMPACT_ET);
            double dentSeverity = LmuFieldReader.ReadF64(_rawBuffer, t + TI_DENT_SEVERITY);
            double frontRideH = LmuFieldReader.ReadF64(_rawBuffer, t + TI_FRONT_RIDE_HEIGHT);
            double rearRideH = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REAR_RIDE_HEIGHT);
            double frontWingH = LmuFieldReader.ReadF64(_rawBuffer, t + TI_FRONT_WING_HEIGHT);
            double front3rd = LmuFieldReader.ReadF64(_rawBuffer, t + TI_FRONT_3RD_DEFLECTION);
            double rear3rd = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REAR_3RD_DEFLECTION);
            double physSteerRange = LmuFieldReader.ReadF64(_rawBuffer, t + TI_PHYSICAL_STEER_RANGE);
            double visSteerRange = LmuFieldReader.ReadF64(_rawBuffer, t + TI_VISUAL_STEER_RANGE);
            double rearBias = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REAR_BRAKE_BIAS);
            double turboBoost = LmuFieldReader.ReadF64(_rawBuffer, t + TI_TURBO_BOOST);
            double ersTorque = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_TORQUE);
            double ersRPM = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_RPM);
            double ersTemp = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_TEMP);
            double regen = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REGEN);
            double soc = LmuFieldReader.ReadF64(_rawBuffer, t + TI_SOC);
            double battCharge = LmuFieldReader.ReadF64(_rawBuffer, t + TI_BATTERY_CHARGE);
            double virtEnergy = LmuFieldReader.ReadF64(_rawBuffer, t + TI_VIRTUAL_ENERGY);
            double deltaBest = LmuFieldReader.ReadF64(_rawBuffer, t + TI_DELTA_BEST);
            int currentSector = LmuFieldReader.ReadI32(_rawBuffer, t + TI_CURRENT_SECTOR);
            int fuelCapacity = LmuFieldReader.ReadI32(_rawBuffer, t + TI_FUEL_CAPACITY);
            string frontCompound = LmuFieldReader.ReadStr(_rawBuffer, t + TI_FRONT_TIRE_COMPOUND, 18);
            string rearCompound = LmuFieldReader.ReadStr(_rawBuffer, t + TI_REAR_TIRE_COMPOUND, 18);

            FrontDownforce = IsFinite(frontDownforce) ? (float)frontDownforce : 0f;
            RearDownforce = IsFinite(rearDownforce) ? (float)rearDownforce : 0f;
            Drag = IsFinite(drag) ? (float)drag : 0f;
            EngineTorque = IsFinite(engineTorque) ? (float)engineTorque : 0f;
            LastImpactMagnitude = IsFinite(lastImpactMag) ? (float)lastImpactMag : 0f;
            LastImpactET = IsFinite(lastImpactET) ? (float)lastImpactET : 0f;
            DentSeverity = IsFinite(dentSeverity) ? Math.Clamp((float)dentSeverity, 0f, 1f) : 0f;
            FrontRideHeight = IsFinite(frontRideH) ? (float)frontRideH : 0f;
            RearRideHeight = IsFinite(rearRideH) ? (float)rearRideH : 0f;
            FrontWingHeight = IsFinite(frontWingH) ? (float)frontWingH : 0f;
            Front3rdDeflection = IsFinite(front3rd) ? (float)front3rd : 0f;
            Rear3rdDeflection = IsFinite(rear3rd) ? (float)rear3rd : 0f;
            PhysicalSteeringWheelRange = IsFinite(physSteerRange) && physSteerRange > 100f ? (float)physSteerRange : 900f;
            VisualSteeringWheelRange = IsFinite(visSteerRange) && visSteerRange > 100f ? (float)visSteerRange : 900f;
            RearBrakeBias = IsFinite(rearBias) ? (float)rearBias : 0.5f;
            TurboBoostPressure = IsFinite(turboBoost) ? (float)turboBoost : 0f;
            ElectricBoostMotorTorque = IsFinite(ersTorque) ? (float)ersTorque : 0f;
            ElectricBoostMotorRPM = IsFinite(ersRPM) ? (float)ersRPM : 0f;
            ElectricBoostMotorTemp = IsFinite(ersTemp) ? (float)ersTemp : 0f;
            Regen = IsFinite(regen) ? (float)regen : 0f;
            Soc = IsFinite(soc) ? Math.Clamp((float)soc, 0f, 100f) : 0f;
            BatteryChargeFraction = IsFinite(battCharge) ? Math.Clamp((float)battCharge, 0f, 1f) : 0f;
            DeltaBest = IsFinite(deltaBest) ? (float)deltaBest : 0f;
            CurrentSector = currentSector;
            FuelCapacity = fuelCapacity;
            FrontTireCompoundName = string.IsNullOrEmpty(frontCompound) || frontCompound.Length < 2 ? null : frontCompound;
            RearTireCompoundName = string.IsNullOrEmpty(rearCompound) || rearCompound.Length < 2 ? null : rearCompound;
            string vehicleName = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
            VehicleName = string.IsNullOrEmpty(vehicleName) || vehicleName.Length < 3 ? null : vehicleName;

            // ── Wheel data (mWheel[4] at TelemInfoV01 start + 848, each 260 bytes) ──
            // Basic telemetry (steer, speed, torque) reads from the player's slot.
            // Wheel data (Fx, Fy, tireLoad, rotation, etc.) only populates at slot 0.
            const int wheelBaseOff = 848;
            const int wheelStride = 260;
            const int kTelemHeaderOff = 128464;
            const int kTelemInfoOff = kTelemHeaderOff + 4;
            int wheelSlot0Base = kTelemInfoOff;
            float[] tireLoads = new float[4];
            float[] tirePressures = new float[4];
            float[] tireRots = new float[4];
            float[] latForces = new float[4];
            float[] lonForces = new float[4];
            float[] tireTemps = new float[4];
            float[] tireWears = new float[4];
            float[] suspDef = new float[4];
            float[] suspForce = new float[4];
            float[] brakePressure = new float[4];
            float[] latPatchVel = new float[4];
            float[] lonPatchVel = new float[4];
            float[] latGroundVel = new float[4];
            float[] lonGroundVel = new float[4];
            float[] camberRad = new float[4];
            float[] vertTireDefl = new float[4];
            float[] tireCarcassTemp = new float[4];
            float[] innerLayerTemp = new float[4];
            float[] optimalTemp = new float[4];
            string[] terrainNames = new string[4];

            for (int wi = 0; wi < 4; wi++)
            {
                int wOff = wheelSlot0Base + wheelBaseOff + wi * wheelStride;
                if (wOff + 240 > readLen)
                {
                    wOff = t + wheelBaseOff + wi * wheelStride;
                    if (wOff + 240 > readLen) continue;
                }

                suspDef[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_SUSP_DEFLECTION)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_SUSP_DEFLECTION) : 0f;
                suspForce[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_SUSP_FORCE)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_SUSP_FORCE) : 0f;
                brakePressure[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_BRAKE_PRESSURE)) ? Math.Max(0, (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_BRAKE_PRESSURE)) : 0f;
                tireRots[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_ROTATION)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_ROTATION) : 0f;
                latPatchVel[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_PATCH_VEL)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_PATCH_VEL) : 0f;
                lonPatchVel[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_PATCH_VEL)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_PATCH_VEL) : 0f;
                latGroundVel[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_GROUND_VEL)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_GROUND_VEL) : 0f;
                lonGroundVel[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_GROUND_VEL)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_GROUND_VEL) : 0f;
                camberRad[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_CAMBER)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_CAMBER) : 0f;
                latForces[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_FORCE)) ? Math.Clamp((float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LAT_FORCE), -50000f, 50000f) : 0f;
                lonForces[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_FORCE)) ? Math.Clamp((float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_LON_FORCE), -50000f, 50000f) : 0f;
                tireLoads[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_LOAD)) ? Math.Max(0, (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_LOAD)) : 0f;
                _tireGrip[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_GRIP_FRACT)) ? Math.Clamp((float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_GRIP_FRACT), -1f, 1f) : 0f;
                tirePressures[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_PRESSURE)) ? Math.Max(0, (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_PRESSURE)) : 0f;

                double t1 = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_INNER);
                double t2 = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_MID);
                double t3 = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_OUTER);
                tireTemps[wi] = IsFinite(t1) && IsFinite(t2) && IsFinite(t3) ? (float)(((t1 + t2 + t3) / 3.0) - 273.15) : 0f;
                tireWears[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_WEAR)) ? Math.Clamp((float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_WEAR), 0f, 1f) : 0f;
                terrainNames[wi] = LmuFieldReader.ReadStr(_rawBuffer, wOff + W_TERRAIN_NAME, 16);
                vertTireDefl[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_VERT_TIRE_DEFLECTION)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_VERT_TIRE_DEFLECTION) : 0f;
                tireCarcassTemp[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_CARCASS_TEMP)) ? (float)(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_CARCASS_TEMP) - 273.15) : 0f;
                innerLayerTemp[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP0)) ? (float)(((LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP0) + LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP1) + LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP2)) / 3.0) - 273.15) : 0f;
                optimalTemp[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_OPTIMAL_TEMP)) ? (float)(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_OPTIMAL_TEMP) - 273.15) : 0f;

                // Copy to reader properties
                SuspForces[wi] = suspForce[wi];
                BrakePressures[wi] = brakePressure[wi];
                PatchVelocities[wi] = latPatchVel[wi];
                VerticalTireDeflections[wi] = vertTireDefl[wi];
                CamberRad[wi] = camberRad[wi];
                TireCarcassTemps[wi] = tireCarcassTemp[wi];
                InnerLayerTemps[wi] = innerLayerTemp[wi];
                OptimalTemps[wi] = optimalTemp[wi];
                TerrainNames[wi] = terrainNames[wi];
            }

            // One-time wheel data dump for diagnostics (unconditional)
            if (!_loggedWheelDump)
            {
                int wOff = wheelSlot0Base + wheelBaseOff;
                Log($"WHEEL0 slot0({wheelSlot0Base}): off={wOff} defl={suspDef[0]:F4} rot={tireRots[0]:F2} latF={latForces[0]:F1} lonF={lonForces[0]:F1} load={tireLoads[0]:F1} press={tirePressures[0]:F1} suspF={suspForce[0]:F1} brakeP={brakePressure[0]:F1}");
                _loggedWheelDump = true;
            }

            float totalForce = (float)(Math.Abs(steeringTorque) > 0.0001 ? steeringTorque : ffbTorque);

            // ── Mz synthesis from steering shaft torque ──
            float absForce = Math.Abs(totalForce);
            float absSteer = (float)Math.Abs(steer);
            float sgn = (float)(steer > 0 ? 1 : -1);
            float blend = Math.Clamp(absSteer / 0.002f, 0f, 1f);
            float centerDeg = Math.Abs((float)steer * 450f);
            float cFactor = centerDeg < 3f ? MathF.Pow(centerDeg / 3f, 0.5f) : 1f;
            int mg = gear switch { -1 => 0, 0 => 1, _ => gear + 1 };
            float slipA = (float)(speedMs >= 0.5 ? Math.Clamp(Math.Atan2(vx, -vz), -0.30, 0.30) : 0);
            float speedKmh = (float)(speedMs * 3.6);

            // ── Use real wheel data when available, fall back to synthesis ──
            // Estimate tire loads from physics when wheel data is zero
            if (tireLoads[0] < 1f && tireLoads[1] < 1f && tireLoads[2] < 1f && tireLoads[3] < 1f)
            {
                float speedSq = (float)(speedMs * speedMs);
                float aeroF = 0.8f * speedSq * 0.40f;
                float aeroR = 0.8f * speedSq * 0.60f;
                float latWT = (float)(accY * 1400f * 0.4f / 1.6f);
                float lonWT = (float)(-accX * 1400f * 0.4f / 2.7f);
                float staticL = 1400f * 9.81f / 4f;
                tireLoads[0] = Math.Max(0, staticL + aeroF / 2f - lonWT - latWT);
                tireLoads[1] = Math.Max(0, staticL + aeroF / 2f - lonWT + latWT);
                tireLoads[2] = Math.Max(0, staticL + aeroR / 2f + lonWT - latWT);
                tireLoads[3] = Math.Max(0, staticL + aeroR / 2f + lonWT + latWT);
            }

            // Smooth acceleration for Fx/Fy synthesis fallback
            float rawLatG = (float)accY;
            float rawLonG = (float)accX;
            if (!_prevAccInitialized)
            {
                _prevAccY = rawLatG;
                _prevAccX = rawLonG;
                _prevAccInitialized = true;
            }
            float smoothLatG = _prevAccY + (rawLatG - _prevAccY) * 0.25f;
            float smoothLonG = _prevAccX + (rawLonG - _prevAccX) * 0.25f;
            _prevAccY = smoothLatG;
            _prevAccX = smoothLonG;

            float[] synFx = new float[4];
            float[] synFy = new float[4];
            for (int wi = 0; wi < 4; wi++)
            {
                float load = tireLoads[wi];
                synFy[wi] = Math.Abs(latForces[wi]) < 0.001f ? smoothLatG * load * 0.002f : latForces[wi];
                synFx[wi] = Math.Abs(lonForces[wi]) < 0.001f ? smoothLonG * load * 0.002f : lonForces[wi];
            }

            // Slip ratio from wheel speed vs vehicle speed, or estimate from accel
            float[] slipRatio = new float[4];
            if (speedMs > 1f)
            {
                float radius = 0.31f;
                for (int wi = 0; wi < 4; wi++)
                {
                    float wSpeedMs = Math.Abs(tireRots[wi]) * radius;
                    if (wSpeedMs > 0.1f)
                        slipRatio[wi] = Math.Clamp(((float)speedMs - wSpeedMs) / Math.Max(wSpeedMs, 0.1f), -0.20f, 0.20f);
                    else
                        slipRatio[wi] = Math.Clamp(smoothLonG / 20f, -0.15f, 0.15f);
                }
            }

            // ── Vibrations from real data ──
            float kerbVib = 0f;
            float roadVib = 0f;
            float slipVib = 0f;
            float absVib = 0f;

            if (speedMs > 1f)
            {
                // Kerb vibration from suspension force spikes
                float maxSuspF = 0f;
                for (int wi = 0; wi < 4; wi++)
                    if (Math.Abs(suspForce[wi]) > maxSuspF) maxSuspF = Math.Abs(suspForce[wi]);
                kerbVib = Math.Min(maxSuspF * 0.0002f, 1f);

                // Road vibration from vertical tire deflection
                float maxVertDefl = 0f;
                for (int wi = 0; wi < 4; wi++)
                    if (Math.Abs(vertTireDefl[wi]) > maxVertDefl) maxVertDefl = Math.Abs(vertTireDefl[wi]);
                roadVib = Math.Min(maxVertDefl * 20f, 1f);

                // Impact vibration
                if (LastImpactMagnitude > 0.1f)
                    kerbVib = Math.Max(kerbVib, Math.Min(LastImpactMagnitude * 0.002f, 1f));

                // Slip vibration from grip fraction
                float minGrip = 1f;
                for (int wi = 0; wi < 4; wi++)
                    if (_tireGrip[wi] < minGrip && _tireGrip[wi] > 0.01f) minGrip = _tireGrip[wi];
                if (minGrip < 0.92f)
                    slipVib = Math.Min((0.92f - minGrip) * 12f, 1f);

                // ABS vibration from brake pressure asymmetry
                if (brakePressure[0] > 100f || brakePressure[1] > 100f)
                {
                    float fp = Math.Abs(brakePressure[0] - brakePressure[1]);
                    float rp = Math.Abs(brakePressure[2] - brakePressure[3]);
                    absVib = Math.Min(Math.Max(fp, rp) * 0.001f, 1f);
                }
            }

            // ── Mz synthesis ──
            float mzMagnitude = absForce * blend * cFactor;
            float mzFL = -mzMagnitude * sgn * 18f;
            float mzFR = -mzMagnitude * sgn * 18f;
            float mzRL = -mzMagnitude * sgn * 12f;
            float mzRR = -mzMagnitude * sgn * 12f;

            // Log every ~100ms
            if (_lastReadTicks % (Stopwatch.Frequency / 10) < Stopwatch.Frequency / 250)
            {
                string veh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                Log($"Telem: veh='{Trunc(veh,20)}' gear={gear} rpm={rpm:F0} speed={speedKmh:F1} tq={totalForce:F4} steer={steer:F4} thr={throttle:F3} brk={brake:F3} fDown={FrontDownforce:F1} rDown={RearDownforce:F1} impact={LastImpactMagnitude:F1}");
            }

            physics = new SPageFilePhysicsEvo
            {
                PacketId = (int)_lastReadTicks,
                Gas = Math.Clamp((float)throttle, 0, 1),
                Brake = Math.Clamp((float)brake, 0, 1),
                Gear = mg,
                Rpms = (int)rpm,
                SteerAngle = (float)steer,
                SpeedKmh = speedKmh,
                Velocity = [(float)vx, (float)vy, (float)vz],
                AccG = [(float)accX, (float)accY, (float)accZ],
                WheelLoad = [tireLoads[0], tireLoads[1], tireLoads[2], tireLoads[3]],
                WheelsPressure = [tirePressures[0], tirePressures[1], tirePressures[2], tirePressures[3]],
                WheelAngularSpeed = [tireRots[0], tireRots[1], tireRots[2], tireRots[3]],
                TyreWear = [tireWears[0], tireWears[1], tireWears[2], tireWears[3]],
                SuspensionTravel = [suspDef[0], suspDef[1], suspDef[2], suspDef[3]],
                FinalFf = totalForce,
                SlipRatio = slipRatio,
                SlipAngle = [slipA, slipA, slipA, slipA],
                Mz = [mzFL, mzFR, mzRL, mzRR],
                Fx = [synFx[0], synFx[1], synFx[2], synFx[3]],
                Fy = [synFy[0], synFy[1], synFy[2], synFy[3]],
                LocalAngularVel = [(float)0, (float)0, (float)0],
                KerbVibration = kerbVib,
                SlipVibrations = slipVib,
                RoadVibrations = roadVib,
                AbsVibrations = absVib,
                TyreTemp = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempI = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempM = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempO = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                LocalVelocity = [(float)vx, (float)vy, (float)vz],
                IsEngineRunning = 1,
                TurboBoost = TurboBoostPressure,
                KersCharge = Soc / 100f,
                KersInput = ElectricBoostMotorTorque,
                BrakeBias = RearBrakeBias,
                RideHeight = [FrontRideHeight, RearRideHeight],
                EngineBrake = (int)EngineTorque,
            };
            return true;
        }
        catch (Exception ex)
        {
            Log($"TryReadPhysics exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool FindTelemSection(byte[] buf, int len)
    {
        _telemInfoOffset = -1;

        // The MMF layout from SharedMemoryInterface.hpp is fixed:
        //   SharedMemoryGeneric (328) + SharedMemoryPathData (1300)
        //   + SharedMemoryScoringData (ScoringInfoV01~556 + streamSize(8)
        //     + 104*VehicleScoringInfoV01(584) + scoringStream[65536])
        //   = SharedMemoryTelemetryData header at 128464
        //
        // VehicleScoringInfoV01[0] is at 2192 (verified in old log).
        // 2192 - 104*584 = 62928 end of VSC array
        // 62928 + 65536 = 128464 = telemetry section header
        // First TelemInfoV01 at 128468.
        const int kTelemHeaderOff = 128464;
        const int kTelemInfoOff = kTelemHeaderOff + 4; // 128468

        if (kTelemInfoOff + 96 <= len)
        {
            string v0 = LmuFieldReader.ReadStr(buf, kTelemInfoOff + TI_VEHICLE_NAME, 64);
            double r0 = LmuFieldReader.ReadF64(buf, kTelemInfoOff + TI_ENGINE_RPM);
            int g0 = LmuFieldReader.ReadI32(buf, kTelemInfoOff + TI_GEAR);

            if (!string.IsNullOrEmpty(v0) && v0.Length >= 5 &&
                r0 >= 0 && r0 <= 15000 && g0 >= -1 && g0 <= 10)
            {
                _stride = 1888;
                byte active = buf[kTelemHeaderOff];
                byte playerIdx = buf[kTelemHeaderOff + 1];
                _lockedSlotIndex = FindPlayerVehIndex(buf, len, new[] { v0 });
                if (_lockedSlotIndex < 0) _lockedSlotIndex = playerIdx;
                _telemInfoOffset = kTelemInfoOff + playerIdx * _stride;
                Log($"FindTelemSection: FIXED offset veh='{Trunc(v0,20)}' active={active} telemSlot={playerIdx} scoringSlot={_lockedSlotIndex} telemOff={_telemInfoOffset} rpm={r0:F0} gear={g0}");
                return true;
            }
        }

        // Fallback: find the scoring section via PlayerVehIndex scan, then compute
        // telemetry offset from the known layout.
        Log("FindTelemSection: trying scoring-section fallback...");
        int vsBase = FindScoringSectionBase(buf, len);
        if (vsBase > 0)
        {
            // VehicleScoringInfoV01 array ends at: vsBase + 104 * 584
            // scoringStream[65536] starts there, and telemetry is right after.
            int scoringEnd = vsBase + 104 * 584;
            int telemHdr = scoringEnd + 65536;
            if (telemHdr + 100 <= len)
            {
                string v0 = LmuFieldReader.ReadStr(buf, telemHdr + 4 + TI_VEHICLE_NAME, 64);
                double r0 = LmuFieldReader.ReadF64(buf, telemHdr + 4 + TI_ENGINE_RPM);
                int g0 = LmuFieldReader.ReadI32(buf, telemHdr + 4 + TI_GEAR);
                if (!string.IsNullOrEmpty(v0) && v0.Length >= 5 &&
                    r0 >= 0 && r0 <= 15000 && g0 >= -1 && g0 <= 10)
                {
                    _stride = 1888;
                    byte playerIdx = buf[telemHdr + 1];
                    _lockedSlotIndex = FindPlayerVehIndex(buf, len, new[] { v0 });
                    if (_lockedSlotIndex < 0) _lockedSlotIndex = playerIdx;
                    _telemInfoOffset = telemHdr + 4 + playerIdx * _stride;
                    Log($"FindTelemSection: scoring-fallback FOUND veh='{Trunc(v0,20)}' telemSlot={playerIdx} scoringSlot={_lockedSlotIndex} telemOff={_telemInfoOffset} rpm={r0:F0} gear={g0}");
                    return true;
                }
            }
        }

        Log("FindTelemSection: FAILED");
        return false;
    }

    /// <summary>
    /// Find the offset of the first VehicleScoringInfoV01 entry by scanning
    /// for vehicle names.
    /// </summary>
    private int FindScoringSectionBase(byte[] buf, int len)
    {
        const int vsStride = 584;
        const int nameOff = 36;
        for (int off = 1500; off < len - 2000; off += 4)
        {
            string name = LmuFieldReader.ReadStr(buf, off, 64);
            if (name.Length < 10) continue;
            // Verify second entry exists
            int nextName = off - nameOff + vsStride + nameOff;
            if (nextName + 20 < len)
            {
                string next = LmuFieldReader.ReadStr(buf, nextName, 64);
                if (next.Length >= 10)
                    return off - nameOff;
            }
        }
        return -1;
    }

    /// <summary>
    /// Find the player's vehicle index by scanning VehicleScoringInfoV01 entries.
    /// Vehicle names at offset 36, stride 584. mIsPlayer at 196, mControl at 197.
    /// </summary>
    private int FindPlayerVehIndex(byte[] buf, int len, string[] knownNames)
    {
        const int vsStride = 584;
        const int nameOff = 36;

        // Find the VehicleScoringInfoV01 base by locating a known vehicle name
        int baseEntry = -1;
        for (int off = 1500; off < len - 2000; off += 4)
        {
            string name = LmuFieldReader.ReadStr(buf, off, 64);
            if (name.Length < 10) continue;
            foreach (var known in knownNames)
            {
                if (name == known || known.StartsWith(name) || name.StartsWith(known))
                {
                    int structStart = off - nameOff;
                    // Verify second entry
                    int nextName = structStart + vsStride + nameOff;
                    if (nextName + 20 < len)
                    {
                        string next = LmuFieldReader.ReadStr(buf, nextName, 64);
                        if (next.Length >= 10)
                        {
                            baseEntry = structStart;
                            break;
                        }
                    }
                }
            }
            if (baseEntry >= 0) break;
        }

        if (baseEntry < 0)
        {
            Log("FindPlayerVehIndex: could not find scoring section base via name match");
            return -1;
        }

        Log($"FindPlayerVehIndex: base={baseEntry}, scanning for player...");
        for (int i = 0; i < 104; i++)
        {
            int entry = baseEntry + i * vsStride;
            if (entry + 198 > len) break;

            byte isP = buf[entry + 196];
            byte ctrl = buf[entry + 197];
            string vn = LmuFieldReader.ReadStr(buf, entry + nameOff, 64);

            if (isP == 1 || ctrl == 1)
            {
                Log($"  -> PLAYER at index {i} (isP={isP} ctrl={ctrl}): '{Trunc(vn,25)}'");
                return i;
            }
        }

        Log("FindPlayerVehIndex: no player found");
        return -1;
    }

    private string[] GetKnownNames(byte[] buf, int t0, int stride, int count, int len)
    {
        var names = new List<string>();
        for (int i = 0; i < Math.Min(count, 30); i++)
        {
            int pt = t0 + i * stride;
            if (pt + 100 > len) break;
            string n = LmuFieldReader.ReadStr(buf, pt + TI_VEHICLE_NAME, 64);
            if (!string.IsNullOrEmpty(n) && n.Length >= 10)
                names.Add(n);
        }
        return names.ToArray();
    }

    private bool TryParseTelemetryHeader(byte[] buf, int len, int off)
    {
        if (off + 4 + 2000 > len) return false;

        // ── Telemetry section header (3 bytes + 1 pad) ──
        byte active = buf[off];
        byte headerPlayerIdx = buf[off + 1];
        byte hasVehicle = buf[off + 2];
        if (active < 1 || active > 40) return false;
        if (hasVehicle != 1) return false;

        int t0 = off + 4;

        // ── STRICT SANITY: vehicle 0 ──
        string v0 = LmuFieldReader.ReadStr(buf, t0 + TI_VEHICLE_NAME, 64);
        if (string.IsNullOrEmpty(v0) || v0.Length < 5) return false;
        // LMU/rF2 vehicle names use underscores (BMW_M4_GT3), not spaces.
        // Skip the space/# check — RPM/gear/speed are stronger sanity signals.

        double r0 = LmuFieldReader.ReadF64(buf, t0 + TI_ENGINE_RPM);
        int g0 = LmuFieldReader.ReadI32(buf, t0 + TI_GEAR);
        double vx0 = LmuFieldReader.ReadF64(buf, t0 + TI_LOCAL_VEL);
        double vy0 = LmuFieldReader.ReadF64(buf, t0 + TI_LOCAL_VEL + 8);
        double vz0 = LmuFieldReader.ReadF64(buf, t0 + TI_LOCAL_VEL + 16);
        double s0 = Math.Sqrt(vx0 * vx0 + vy0 * vy0 + vz0 * vz0);

        if (r0 < 0 || r0 > 15000) return false;
        if (g0 < -1 || g0 > 10) return false;
        if (double.IsNaN(s0) || double.IsInfinity(s0) || s0 > 150) return false;

        // ── Find stride ──
        int stride = -1;
        for (int s = 1500; s < 3000; s += 4)
        {
            int next = t0 + s;
            if (next + 128 > len) break;
            string vn = LmuFieldReader.ReadStr(buf, next + TI_VEHICLE_NAME, 64);
            if (string.IsNullOrEmpty(vn) || vn.Length < 5 || vn == v0) continue;
            // LMU/rF2 names use underscores (BMW_M4_GT3), not spaces. Skip space/# check.
            double rn = LmuFieldReader.ReadF64(buf, next + TI_ENGINE_RPM);
            int gn = LmuFieldReader.ReadI32(buf, next + TI_GEAR);
            if (gn < -1 || gn > 10 || rn < 0 || rn > 15000) continue;
            int third = t0 + s * 2;
            if (third + 128 > len) { stride = s; break; }
            string vt = LmuFieldReader.ReadStr(buf, third + TI_VEHICLE_NAME, 64);
            if (string.IsNullOrEmpty(vt) || vt.Length < 3) continue;
            if (vt == vn) continue;
            int gt = LmuFieldReader.ReadI32(buf, third + TI_GEAR);
            double rt = LmuFieldReader.ReadF64(buf, third + TI_ENGINE_RPM);
            if (gt >= -1 && gt <= 10 && rt >= 0 && rt <= 15000) { stride = s; break; }
        }
        if (stride < 0) return false;
        _stride = stride;

        // ── Find player index from VehicleScoringInfoV01 mIsPlayer flag ──
        var knownNames = GetKnownNames(buf, t0, stride, (int)active, len);
        int playerSlot = FindPlayerVehIndex(buf, len, knownNames);
        if (playerSlot < 0)
        {
            Log($"  @@{off}: player not found in scoring, trying header pIdx={headerPlayerIdx}");
            playerSlot = headerPlayerIdx;
        }
        _lockedSlotIndex = playerSlot;

        int pt = t0 + _lockedSlotIndex * stride;
        if (pt + 96 > len) return false;

        string vp = LmuFieldReader.ReadStr(buf, pt + TI_VEHICLE_NAME, 64);
        double rp = LmuFieldReader.ReadF64(buf, pt + TI_ENGINE_RPM);
        double sp = Math.Sqrt(
            Math.Pow(LmuFieldReader.ReadF64(buf, pt + TI_LOCAL_VEL), 2) +
            Math.Pow(LmuFieldReader.ReadF64(buf, pt + TI_LOCAL_VEL + 8), 2) +
            Math.Pow(LmuFieldReader.ReadF64(buf, pt + TI_LOCAL_VEL + 16), 2));

        // Don't reject if name is empty — player slot from scoring section
        // might have valid RPM/gear data even without the name in the telemInfo array.
        _telemInfoOffset = pt;

        // Dump all active telemInfo slots for debugging
        Log($"--- ALL TELEM SLOTS (active={active}) ---");
        int selIdx = _lockedSlotIndex;
        for (int i = 0; i < Math.Min((int)active, 30); i++)
        {
            int si = t0 + i * stride;
            string sn = LmuFieldReader.ReadStr(buf, si + TI_VEHICLE_NAME, 64);
            double sr = LmuFieldReader.ReadF64(buf, si + TI_ENGINE_RPM);
            int sg = LmuFieldReader.ReadI32(buf, si + TI_GEAR);
            double svx = LmuFieldReader.ReadF64(buf, si + TI_LOCAL_VEL);
            double svy = LmuFieldReader.ReadF64(buf, si + TI_LOCAL_VEL + 8);
            double svz = LmuFieldReader.ReadF64(buf, si + TI_LOCAL_VEL + 16);
            double ss = Math.Sqrt(svx * svx + svy * svy + svz * svz);
            if (!string.IsNullOrEmpty(sn) && sn.Length >= 5)
            {
                string marker = i == selIdx ? " <<< SELECTED" : "";
                Log($"  [SLOT {i,2}] '{Trunc(sn,30),-30}'  gear={sg,2}  rpm={sr,7:F0}  spd={ss,5:F1}{marker}");
            }
        }
        Log("--- END SLOTS ---");

        Log($"  OK @{off}: active={active} hdrPIdx={headerPlayerIdx} stride={stride} slot={selIdx} veh='{Trunc(vp,20)}' rpm={rp:F0} spd={sp:F1}");
        return true;
    }

    public bool TryReadGraphics(out SPageFileGraphicEvo graphics)
    {
        graphics = default;
        if (_view == null) return false;
        try
        {
            int t = _telemInfoOffset;
            if (t < 0 || t + 160 > _rawBuffer.Length)
            {
                graphics = new SPageFileGraphicEvo { SteerDegrees = 900, CarModel = new byte[33], CarCoordinates = new StructVector3[60] };
                return true;
            }
            double rpm = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ENGINE_RPM);
            var carName = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
            int lapNumber = LmuFieldReader.ReadI32(_rawBuffer, t + TI_LAP_NUMBER);
            graphics = new SPageFileGraphicEvo
            {
                SteerDegrees = 900, CarModel = new byte[33], CarCoordinates = new StructVector3[60],
                Status = AcEvoStatus.AcLive, RpmPercent = (float)(rpm / 8000),
                Flag = AcEvoFlagType.AcNoFlag, IsEngineRunning = true,
                EngineType = AcEvoEngineType.AcevoInternalCombustion,
                CarLocation = AcEvoCarLocation.AcevoTrack, UseSingleCompound = false,
            };
            if (!string.IsNullOrEmpty(carName))
            {
                var b = System.Text.Encoding.ASCII.GetBytes(carName);
                int c = Math.Min(b.Length, 32); Array.Copy(b, graphics.CarModel, c);
            }
            graphics.GlobalFlag = graphics.Flag;
            graphics.SessionState = new SmevoSessionState { CurrentLap = lapNumber + 1, TotalLap = lapNumber + 1, TimeLeft = new byte[16] };
            return true;
        }
        catch { return false; }
    }

    public bool TryReadStatic(out SPageFileStaticEvo staticData)
    {
        staticData = default;
        if (_view == null) return false;
        try
        {
            string track = "";
            if (_telemInfoOffset > 0)
                track = LmuFieldReader.ReadStr(_rawBuffer, _telemInfoOffset + TI_TRACK_NAME, 64);
            staticData = new SPageFileStaticEvo
            {
                SmVersion = "LMU\0"u8.ToArray(),
                Track = CopyStr(track, 33),
                TrackConfiguration = new byte[33],
            };
            return true;
        }
        catch { return false; }
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    private static string Trunc(string s, int n) => s == null || s.Length <= n ? s : s[..n] + "…";
    private static byte[] CopyStr(string src, int d)
    {
        var dst = new byte[d];
        if (!string.IsNullOrEmpty(src))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(src);
            int c = Math.Min(b.Length, d); Array.Copy(b, dst, c);
        }
        return dst;
    }

    public void Dispose() => Disconnect();
}
