using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.SharedMemory;

public sealed class LmuSharedMemoryReader : ISharedMemoryReader, ICarPositionSource
{
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private long _capacity;

    private long _lastReadTicks;
    private byte[] _rawBuffer = new byte[524288];
    private int _telemInfoOffset = -1;
    private const int SlotDebounceFrames = 15;
    private int _pendingSlotOff = -1;
    private int _pendingSlotFrames = 0;
    // New-frame gate cache (see TryReadPhysics).
    private SPageFilePhysicsEvo _cachedPhysics;
    private bool _hasCachedPhysics;
    // Telemetry-slot identity counters (session/car-change detection;
    // the name itself lives in _expectedVehicleName, set at every resolve).
    private int _identityCheckCounter;
    private int _identityMismatchStreak;
    private float _prevTotalForce;
    private bool _torqueSlewSeeded;
    private long _torqueFlipStartTicks;
    private int _gateSlot = -1;
    private double _gateTorque, _gateSteer, _gateSpeed, _gateRpm, _gateBrake, _gateThrottle;
    private int _gateGear;
    private int _stride = 1888;
        private int _lockedSlotIndex = -1;
        private int _connectAttempts;
        private bool _loggedHeader;
        private string _expectedVehicleName = "";
        private long _lastReResolveTicks;

    // ── LMU world-position / lap-distance state (drives the track-map car dot) ──
    // Telemetry mPos (TI_POS) is world position in meters; scoring mLapDist is
    // meters around track from the SF line. Lap length comes from the scoring
    // header (ScoringInfoV01.mLapDist); SF-wrap learning is the fallback only.
    private float _lmuCarX, _lmuCarY, _lmuCarZ;
    private bool _lmuPosValid;
    private float _lmuNpos;
    private double _lmuLapLength;              // learned at a genuine SF wrap (fallback)
    private double _lmuScoringTrackLength;     // ScoringInfoV01.mLapDist (authoritative)
    private int _lmuScoringInfoBase = -1;      // ScoringInfoV01 start, located by track name
    private int _lmuTrackLenCounter;
    private string _lmuTrackLenLogKey = "";
    private int _framesSinceTeleport;
    private double _prevLapDist = -1;
    private string _lmuPosTrack = "";
    private float _lmuHeading;
    private bool _lmuHeadingInit;
    private int _lmuScoringBase = -1;
    private int _lmuPlayerScoringSlot = -1;
    private bool _lmuInPits;


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

    // DC-free vibration synthesis state (per-frame deltas strip static baselines)
    private readonly float[] _prevSuspDef = new float[4];
    private readonly float[] _prevVertDefl = new float[4];
    private bool _vibInitialized;
    private double _lastImpactSeenET = -1;

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
    // Player's scoring-entry mVehicleClass (e.g. the class string LMU shows in
    // the session UI). VehicleName is a livery/entry string ("Team Virage
    // #8:ELMS25"), so per-car-TYPE profiles match on this instead.
    public string? VehicleClass { get; private set; }
    private int _vehicleClassCounter;

    private const int TI_VEHICLE_NAME = 32;
    private const int TI_TRACK_NAME = 96;
    private const int TI_LAP_NUMBER = 20;
    private const int TI_LAP_START_ET = 24;
    private const int TI_DELTA_TIME = 4;
    private const int TI_ELAPSED_TIME = 12;
    private const int TI_POS = 160;
    // mOri: 3x3 orientation matrix rows (9 doubles) right after mLocalAccel.
    // Cross-checked: TI_LOCAL_ACCEL(208) + 24 = 232, + 72 = TI_LOCAL_ROT(304). ✓
    private const int TI_ORI = 232;
    // VehicleScoringInfoV01 (stride 584, Pack=4 — verified against CrewChief
    // rF2Data.cs which reproduces our known-good name@36/isPlayer@196/control@197):
    // mID(0) + mDriverName[32](4) + mVehicleName[64](36) + mTotalLaps(100) +
    // mSector(102) + mFinishStatus(103) => mLapDist double @104 ("current distance around track").
    private const int SC_VS_STRIDE = 584;
    private const int SC_NAME_OFF = 36;
    private const int SC_IS_PLAYER_OFF = 196;
    private const int SC_LAP_DIST_OFF = 104;
    // mInPits byte @198 (mInPits; 0 = on track, non-zero = pitlane).
    // Cross-checked: isPlayer@196 ✓ + control@197 ✓ => 198/199 = mInPits/mPlace. ✓
    private const int SC_IN_PITS_OFF = 198;
    // mVehicleClass char[32] @200, directly after mPlace@199 (rF2
    // VehicleScoringInfoV01; same offset as TelemetryBrowser RawDataService
    // and the LmuSharedStruct field order).
    private const int SC_VEHICLE_CLASS_OFF = 200;
    // ScoringInfoV01 sits directly before streamSize(8) + the vehicle array. VERIFIED
    // LIVE 2026-10-20: the name search found it at 1632 = 2192 - 8 - 552 (not the
    // 1628 the generic 328 + paths 1300 sizes suggest). mLapDist ("distance around
    // track" = lap length) is a double @+88: trackName[64] + mSession(4) + mCurrentET(8)
    // + mEndET(8) + mMaxLaps(4); read 13560.9 m at Le Mans Mulsanne. The search stays
    // primary; this size is only the fallback.
    private const int SC_INFO_SIZE = 552;
    private const int SC_INFO_LAP_DIST_OFF = 88;
    // ScoringInfoV01.mNumVehicles @+104 (after the 8-byte mResultsStream pointer).
    private const int SC_INFO_NUM_VEHICLES_OFF = 104;
    // VehicleScoringInfoV01 per-car fields (same Pack=4 layout as above):
    // mID @0, mDriverName[32] @4, mPlace @199, mPos (3 doubles) @264.
    private const int SC_ID_OFF = 0;
    private const int SC_DRIVER_NAME_OFF = 4;
    private const int SC_PLACE_OFF = 199;
    private const int SC_POS_OFF = 264;
    // Flag-related fields (verified in a live 20-car dump 2026-10-20: mFlag 0, mInGarageStall
    // = 1 exactly for the cars parked in their garages, so this block's alignment holds):
    // mFinishStatus (sbyte) @103, mNumPenalties (short) @194, mFlag @504, mUnderYellow @505.
    private const int SC_FINISH_STATUS_OFF = 103;
    private const int SC_NUM_PENALTIES_OFF = 194;
    private const int SC_FLAG_OFF = 504;
    private const int SC_UNDER_YELLOW_OFF = 505;
    // Telemetry section: header (active count @+0) then TelemInfoV01[] (mID @0, mPos @160).
    private const int TELEM_HEADER_OFF = 128464;
    private const int TELEM_INFO_OFF = TELEM_HEADER_OFF + 4;
    private readonly Dictionary<int, (double x, double z)> _telemPosById = new();
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
    private const int TI_REGEN = 768;          // float (NOT double)
    private const int TI_SOC = 772;            // float (NOT double)
    private const int TI_VIRTUAL_ENERGY = 776; // float (NOT double)
    // Driver-aid LIVE state bytes (TelemInfoV01, pack=4 — verified against
    // pyLMUSharedMemory lmu_data.py field order): 0=off/inactive, 1=active.
    // mABSActive/mTCActive are the real intervention flags — no synthesis needed.
    private const int TI_ABS_ACTIVE = 746;
    private const int TI_TC_ACTIVE = 747;

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
    // UNUSABLE for FFB (verified Oct 2026): reads ~1.0 on loaded
    // straight-line driving. TireGrip/slip derive from slip ratio instead.
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
            // Full date stamp: dateless HH:mm:ss lines made multi-day log
            // forensics (slot-flap timing) unreliable — same format family
            // as connection_debug.log.
            System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n");
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
            _pendingSlotOff = -1;
            _pendingSlotFrames = 0;
            _hasCachedPhysics = false;
            _identityCheckCounter = 0;
            _identityMismatchStreak = 0;
            _torqueSlewSeeded = false;
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
        _pendingSlotOff = -1;
        _pendingSlotFrames = 0;
        _hasCachedPhysics = false;
        _identityCheckCounter = 0;
        _identityMismatchStreak = 0;
        _torqueSlewSeeded = false;
        _lockedSlotIndex = -1;
        _loggedHeader = false;
        _loggedWheelDump = false;
        _prevAccInitialized = false;
        _vibInitialized = false;
        _lastImpactSeenET = -1;
        _lmuPosValid = false;
        _lmuHeadingInit = false;
        _lmuNpos = 0;
        _prevLapDist = -1;
        _lmuScoringTrackLength = 0;
        _lmuScoringInfoBase = -1;
        _lmuTrackLenLogKey = "";
        _framesSinceTeleport = 0;
        _lmuScoringBase = -1;
        _lmuPlayerScoringSlot = -1;
        VehicleClass = null;
        _vehicleClassCounter = 0;
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

            // Track the telemetry header's player slot on every frame — LMU compacts
            // the telemetry array when cars leave, shifting the player's slot downward.
            // Re-reading the header every frame keeps us in sync without re-scanning.
            const int HdrOff = 128464;
            const int InfoOff = HdrOff + 4;
            int t = _telemInfoOffset;
            if (readLen > HdrOff + 2)
            {
                int headerSlot = _rawBuffer[HdrOff + 1];
                int headerOff = InfoOff + headerSlot * _stride;
                if (_telemInfoOffset < 0)
                {
                    if (!FindTelemSection(_rawBuffer, readLen))
                    {
                        Log("TryReadPhysics: no telemetry section found");
                        return false;
                    }
                    t = _telemInfoOffset;
                }
                else if (headerOff != t && headerOff >= InfoOff && headerOff + 450 <= readLen)
                {
                    // Debounce slot switches: content validation alone cannot
                    // stop flapping, because the impostor slot is a REAL car
                    // with sane data (verified 2026-10-14: repeated 10↔0 flips
                    // between two 'Iron Lynx 2026 #79' cars, ~1.6s per stay).
                    // A torn header byte lasts a frame or two; a genuine slot
                    // change (telemetry compaction) persists. Require 15
                    // consecutive agreeing frames (~60ms at 250Hz) before the
                    // FFB follows the header anywhere.
                    if (headerOff == _pendingSlotOff)
                        _pendingSlotFrames++;
                    else
                    {
                        _pendingSlotOff = headerOff;
                        _pendingSlotFrames = 1;
                    }
                    if (_pendingSlotFrames >= SlotDebounceFrames
                        && IsSaneTelemetrySlot(_rawBuffer, readLen, headerOff))
                    {
                        string oldVeh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                        Log($"TryReadPhysics: slot {(_telemInfoOffset - InfoOff) / _stride} → {headerSlot} (old='{Trunc(oldVeh, 20)}', debounced {_pendingSlotFrames}f)");
                        t = headerOff;
                        _telemInfoOffset = t;
                        _pendingSlotOff = -1;
                        _pendingSlotFrames = 0;
                        // Re-anchor identity to the new slot, or the check
                        // below would instantly "detect" a car change.
                        string newVeh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                        if (!string.IsNullOrEmpty(newVeh) && newVeh.Length >= 5)
                            _expectedVehicleName = newVeh;
                        _identityMismatchStreak = 0;
                        _hasCachedPhysics = false;
                        _torqueSlewSeeded = false;
                    }
                }
                else if (headerOff == t)
                {
                    _pendingSlotOff = -1;
                    _pendingSlotFrames = 0;
                }
            }
            else
            {
                if (_telemInfoOffset < 0)
                {
                    Log("TryReadPhysics: buffer too small");
                    return false;
                }
            }

            if (t < 0 || t + 450 > readLen)
            {
                Log($"TryReadPhysics: bad offset {t} (len={readLen})");
                return false;
            }

            // Identity check: the telemetry array reshuffles under a stable
            // header on session/car changes (verified Oct 2026: slot 24 went
            // 13 Autosport → Garage 59 with no header move and no log), which
            // silently retargets torque/wheels/grip at a different car.
            // _expectedVehicleName was written at every resolve but never
            // READ — the guard was stubbed, never implemented. Every 30th
            // fresh frame, confirm the expected name still sits at this slot;
            // two consecutive mismatches trigger full re-resolve.
            // Short/empty reads are torn bytes, not a car change — skipped.
            if ((_identityCheckCounter++ % 30) == 0 && !string.IsNullOrEmpty(_expectedVehicleName))
            {
                string currentVeh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                if (!string.IsNullOrEmpty(currentVeh) && currentVeh.Length >= 5)
                {
                    if (!string.Equals(currentVeh, _expectedVehicleName, StringComparison.Ordinal))
                    {
                        if (++_identityMismatchStreak >= 2)
                        {
                            Log($"TryReadPhysics: CAR CHANGED at slot {(_telemInfoOffset - 128468) / _stride} '{Trunc(_expectedVehicleName, 20)}' → '{Trunc(currentVeh, 20)}' — re-resolving");
                            _expectedVehicleName = "";
                            _identityMismatchStreak = 0;
                            _vibInitialized = false;
                            _torqueSlewSeeded = false;
                            if (FindTelemSection(_rawBuffer, readLen) && _telemInfoOffset >= 0)
                            {
                                t = _telemInfoOffset;
                                if (t < 0 || t + 450 > readLen)
                                {
                                    Log($"TryReadPhysics: re-resolved bad offset {t}");
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                    }
                    else
                    {
                        _identityMismatchStreak = 0;
                    }
                }
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

            // New-frame gate: the telemetry loop free-runs far faster than the
            // game writes frames. Reprocessing identical bytes corrupts every
            // per-call derivative (kerb/road/susp deltas read 0,0,…,spike) and
            // makes per-call decays (peak tracker, smoothers) machine-speed
            // dependent. If all live channels are bitwise-identical at the same
            // slot, no new game frame exists — return the cached physics
            // untouched (still true, so the watchdog stays fed and pause
            // detection behaves exactly as before).
            if (_hasCachedPhysics && t == _gateSlot
                && steeringTorque == _gateTorque && steer == _gateSteer
                && speedMs == _gateSpeed && rpm == _gateRpm
                && brake == _gateBrake && throttle == _gateThrottle && gear == _gateGear)
            {
                physics = _cachedPhysics;
                return true;
            }
            double accX = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ACCEL);
            double accY = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ACCEL + 8);
            double rotX = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ROT);
            double rotY = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ROT + 8);
            double rotZ = LmuFieldReader.ReadF64(_rawBuffer, t + TI_LOCAL_ROT + 16);
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
            float physSteerRange = LmuFieldReader.ReadF32(_rawBuffer, t + TI_PHYSICAL_STEER_RANGE);
            float visSteerRange = LmuFieldReader.ReadF32(_rawBuffer, t + TI_VISUAL_STEER_RANGE);
            double rearBias = LmuFieldReader.ReadF64(_rawBuffer, t + TI_REAR_BRAKE_BIAS);
            double turboBoost = LmuFieldReader.ReadF64(_rawBuffer, t + TI_TURBO_BOOST);
            double ersTorque = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_TORQUE);
            double ersRPM = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_RPM);
            double ersTemp = LmuFieldReader.ReadF64(_rawBuffer, t + TI_ERS_MOTOR_TEMP);
            float regen = LmuFieldReader.ReadF32(_rawBuffer, t + TI_REGEN);
            float soc = LmuFieldReader.ReadF32(_rawBuffer, t + TI_SOC);
            double battCharge = LmuFieldReader.ReadF64(_rawBuffer, t + TI_BATTERY_CHARGE);
            float virtEnergy = LmuFieldReader.ReadF32(_rawBuffer, t + TI_VIRTUAL_ENERGY);
            double deltaBest = LmuFieldReader.ReadF64(_rawBuffer, t + TI_DELTA_BEST);
            int currentSector = LmuFieldReader.ReadI32(_rawBuffer, t + TI_CURRENT_SECTOR);
            double fuelCapacityD = LmuFieldReader.ReadF64(_rawBuffer, t + TI_FUEL_CAPACITY);
            int fuelCapacity = IsFinite(fuelCapacityD) ? (int)Math.Clamp(fuelCapacityD, 0, 500) : 0;
            bool absActive = t + TI_ABS_ACTIVE < readLen && _rawBuffer[t + TI_ABS_ACTIVE] != 0;
            bool tcActive = t + TI_TC_ACTIVE < readLen && _rawBuffer[t + TI_TC_ACTIVE] != 0;
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

            // ── World position (mPos @ TI_POS, 3 doubles, meters, y-up) ──
            // This is what the track-map car dot, auto-record and Npos all consume.
            // Previously never read: CarX/CarZ stayed 0 and no LMU map could form.
            UpdateLmuPosition(_rawBuffer, t, readLen, vehicleName, vx, vy, vz, speedMs);

            // ── Wheel data (mWheel[4] at TelemInfoV01 start + 848, each 260 bytes) ──
            // Read wheel data from the player's telemetry slot (each car has its own
            // mWheel[] in LMU/rF2). Fall back to slot 0 if player slot is out of range.
            const int wheelBaseOff = 848;
            const int wheelStride = 260;
            const int kTelemHeaderOff = 128464;
            const int kTelemInfoOff = kTelemHeaderOff + 4;
            int wheelPlayerBase = t;
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
            float[] tireTempInner = new float[4];
            float[] tireTempMid = new float[4];
            float[] tireTempOuter = new float[4];

            for (int wi = 0; wi < 4; wi++)
            {
                int wOff = wheelPlayerBase + wheelBaseOff + wi * wheelStride;
                if (wOff + 240 > readLen)
                {
                    wOff = wheelSlot0Base + wheelBaseOff + wi * wheelStride;
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
                // mGripFract (+112) is NOT consumed: it reads ~1.0 ("fully
                // sliding") on loaded straight-line driving and mPressure
                // (+120) reads constant 0 (verified Oct 2026 snapshots:
                // fronts 0.99 sliding at 300 km/h under 7 kN load —
                // physically impossible). TireGrip is derived from the
                // verified-live wheel-speed slip ratio below instead.
                // Default to full grip; overwritten by the ratio mapping.
                _tireGrip[wi] = 1f;
                tirePressures[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_PRESSURE)) ? Math.Max(0, (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_PRESSURE)) : 0f;

                double tempInner = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_INNER);
                double tempMid = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_MID);
                double tempOuter = LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TEMP_OUTER);
                tireTempInner[wi] = IsFinite(tempInner) ? (float)(tempInner - 273.15) : 0f;
                tireTempMid[wi] = IsFinite(tempMid) ? (float)(tempMid - 273.15) : 0f;
                tireTempOuter[wi] = IsFinite(tempOuter) ? (float)(tempOuter - 273.15) : 0f;
                tireTemps[wi] = (tireTempInner[wi] + tireTempMid[wi] + tireTempOuter[wi]) / 3f;
                tireWears[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_WEAR)) ? Math.Clamp((float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_WEAR), 0f, 1f) : 0f;
                terrainNames[wi] = LmuFieldReader.ReadStr(_rawBuffer, wOff + W_TERRAIN_NAME, 16);
                vertTireDefl[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_VERT_TIRE_DEFLECTION)) ? (float)LmuFieldReader.ReadF64(_rawBuffer, wOff + W_VERT_TIRE_DEFLECTION) : 0f;
                tireCarcassTemp[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_CARCASS_TEMP)) ? (float)(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_TIRE_CARCASS_TEMP) - 273.15) : 0f;
                innerLayerTemp[wi] = IsFinite(LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP0)) ? (float)(((LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP0) + LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP1) + LmuFieldReader.ReadF64(_rawBuffer, wOff + W_INNER_LAYER_TEMP2)) / 3.0) - 273.15) : 0f;
                // mOptimalTemp is a float (Celsius, no Kelvin offset) — NOT a double.
                float optTempRaw = LmuFieldReader.ReadF32(_rawBuffer, wOff + W_OPTIMAL_TEMP);
                optimalTemp[wi] = float.IsFinite(optTempRaw) ? Math.Clamp(optTempRaw, -50f, 250f) : 0f;

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
                int wpOff = wheelPlayerBase + wheelBaseOff;
                int ws0Off = wheelSlot0Base + wheelBaseOff;
                Log($"WHEEL0 playerSlot({wheelPlayerBase}): off={wpOff} defl={suspDef[0]:F4} rot={tireRots[0]:F2} latF={latForces[0]:F1} lonF={lonForces[0]:F1} load={tireLoads[0]:F1} press={tirePressures[0]:F1}");
                if (wpOff != ws0Off)
                {
                    double s0Lat = LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_LAT_FORCE);
                    double s0Lon = LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_LON_FORCE);
                    double s0Load = LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_TIRE_LOAD);
                    double s0Press = LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_PRESSURE);
                    double s0Rot = LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_ROTATION);
                    Log($"WHEEL0 slot0  ({wheelSlot0Base}): off={ws0Off} defl={LmuFieldReader.ReadF64(_rawBuffer, ws0Off + W_SUSP_DEFLECTION):F4} rot={s0Rot:F2} latF={s0Lat:F1} lonF={s0Lon:F1} load={s0Load:F1} press={s0Press:F1}");
                }
                _loggedWheelDump = true;
            }

            float totalForce = (float)(Math.Abs(steeringTorque) > 0.0001 ? steeringTorque : ffbTorque);

            // Torque edge slew: cap per-game-frame change at 20 Nm. Under
            // hard braking the native column torque whipsaws ±20-100 Nm
            // frame-to-frame (verified Oct 2026: -124 Nm spikes at fixed
            // steer), shaking the core full-range. Real turn-in slews
            // <300 Nm/s — at any plausible game rate (90-400 Hz) 20 Nm/frame
            // allows 1800+ Nm/s, so only violent edges bind. Runs per game
            // frame (after the new-frame gate), hence time-consistent unlike
            // the loop-rate-dependent pipeline smoothers. LMU-local.
            const float TorqueSlewPerFrame = 20f;
            // Sign-flip spike hold: under braking the torque briefly reverses
            // (verified 22 Oct 2026 snapshots: 340 isolated samples such as
            // -43.6 → +1.2 → -42.0 Nm). With the pipeline consuming the native
            // torque sign these would be felt as reversals, not dropouts, so
            // a large sign flip away from a loaded value is held for up to
            // TorqueFlipHoldSec. A flip that persists past the window is real
            // (oversteer self-steer builds over 100+ ms) and is then accepted,
            // still bounded by the slew below. Small-value flips (on-centre
            // texture, |prev| ≤ 5 Nm) never trigger.
            const float TorqueFlipMinDelta = 15f;
            const float TorqueFlipMinPrev = 5f;
            const double TorqueFlipHoldSec = 0.030;
            if (!_torqueSlewSeeded)
            {
                _prevTotalForce = totalForce;
                _torqueFlipStartTicks = 0;
                _torqueSlewSeeded = true;
            }
            else
            {
                bool bigFlip = totalForce * _prevTotalForce < 0f
                    && Math.Abs(_prevTotalForce) > TorqueFlipMinPrev
                    && Math.Abs(totalForce - _prevTotalForce) > TorqueFlipMinDelta;
                if (bigFlip)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (_torqueFlipStartTicks == 0) _torqueFlipStartTicks = nowTicks;
                    if (nowTicks - _torqueFlipStartTicks < (long)(TorqueFlipHoldSec * Stopwatch.Frequency))
                        totalForce = _prevTotalForce;
                }
                else
                {
                    _torqueFlipStartTicks = 0;
                }

                float torqueDelta = totalForce - _prevTotalForce;
                if (torqueDelta > TorqueSlewPerFrame) totalForce = _prevTotalForce + TorqueSlewPerFrame;
                else if (torqueDelta < -TorqueSlewPerFrame) totalForce = _prevTotalForce - TorqueSlewPerFrame;
                _prevTotalForce = totalForce;
            }

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

            // Slip ratio from wheel speed vs vehicle speed, or estimate from accel.
            // Per-axle tire radii (front 0.33 / rear 0.35 m, typical LMU slick):
            // a single 0.31 m radius underestimated wheel speed and baked a
            // +6% front / +12% rear phantom slide into every ratio (verified
            // Oct 2026: median |ratio| 8-11% on clean laps, axle-split exactly
            // matching the radius error), permanently softening grip and
            // parking slip vibration at its trigger threshold.
            float[] slipRatio = new float[4];
            if (speedMs > 1f)
            {
                for (int wi = 0; wi < 4; wi++)
                {
                    float radius = wi < 2 ? 0.33f : 0.35f;
                    float wSpeedMs = Math.Abs(tireRots[wi]) * radius;
                    if (wSpeedMs > 0.1f)
                        slipRatio[wi] = Math.Clamp(((float)speedMs - wSpeedMs) / Math.Max(wSpeedMs, 0.1f), -0.20f, 0.20f);
                    else
                        slipRatio[wi] = Math.Clamp(smoothLonG / 20f, -0.15f, 0.15f);
                }
            }

            // TireGrip + slide magnitude from wheel-speed slip ratio (rotation
            // and vehicle speed both verified live; the native slide fraction
            // is unusable — see wheel-loop note). Grip semantics: 1 = rolling
            // cleanly, falling toward 0 at ±0.25 slip (hard lockup/spin).
            // Below ~1 m/s the ratios are zeroed above, so grip rests at 1.
            float maxRatioSlide = 0f;
            for (int wi = 0; wi < 4; wi++)
            {
                float rSlide = Math.Abs(slipRatio[wi]);
                _tireGrip[wi] = 1f - Math.Clamp(rSlide / 0.25f, 0f, 1f);
                if (rSlide > maxRatioSlide) maxRatioSlide = rSlide;
            }

            // ── Vibrations from real data ──
            float kerbVib = 0f;
            float roadVib = 0f;
            float slipVib = 0f;
            float absVib = 0f;

            if (speedMs > 1f)
            {
                // Kerb vibration: differentiate suspension deflection (per-frame delta).
                // Absolute suspension force carries the static load baseline (several kN
                // per wheel) which pinned kerb=1.000 permanently on straights
                // (lmu_debug.log). Same DC-free approach as R3E's SynthesizeKerbVibration.
                if (!_vibInitialized)
                {
                    Array.Copy(suspDef, _prevSuspDef, 4);
                    Array.Copy(vertTireDefl, _prevVertDefl, 4);
                    _vibInitialized = true;
                }

                float maxSuspDelta = 0f;
                for (int wi = 0; wi < 4; wi++)
                {
                    float d = Math.Abs(suspDef[wi] - _prevSuspDef[wi]);
                    if (d > maxSuspDelta) maxSuspDelta = d;
                    _prevSuspDef[wi] = suspDef[wi];
                }
                kerbVib = Math.Min(maxSuspDelta * 400f * 0.3f, 1f);

                // Road vibration: differentiate vertical tire deflection. Absolute
                // deflection includes static tire squash (~0.02 m -> road=0.4 constant).
                float maxVertDelta = 0f;
                for (int wi = 0; wi < 4; wi++)
                {
                    float d = Math.Abs(vertTireDefl[wi] - _prevVertDefl[wi]);
                    if (d > maxVertDelta) maxVertDelta = d;
                    _prevVertDefl[wi] = vertTireDefl[wi];
                }
                roadVib = Math.Min(maxVertDelta * 60f, 1f);

                // Impact vibration — apply only the frame a NEW impact registers
                // (event-driven via ET change, never sticky)
                if (LastImpactMagnitude > 0.1f && lastImpactET > _lastImpactSeenET)
                {
                    _lastImpactSeenET = lastImpactET;
                    kerbVib = Math.Max(kerbVib, Math.Min(LastImpactMagnitude * 0.002f, 1f));
                }

                // Slip vibration from wheel-speed slip ratio (lockup reads
                // positive under braking, spin negative under throttle —
                // magnitude only matters here). Threshold 0.08 ≈ 8% slip:
                // normal rolling stays silent, real lockup/spin fires
                // progressively. Never driven by mGripFract (unusable).
                if (maxRatioSlide > 0.08f)
                    slipVib = Math.Min((maxRatioSlide - 0.08f) * 12f, 1f);

                // ABS vibration from front lockup: per-wheel mBrakePressure is
                // normalized 0..1 (never exceeds 100), so synthesize from
                // brake input + front longitudinal slip instead. SlipRatio
                // here is (v - ωr)/ωr, so lockup (wheel slower than car)
                // reads POSITIVE under braking. The live mABSActive byte
                // (absActive) confirms real intervention.
                float frontLock = 0f;
                if (slipRatio.Length > 1)
                    frontLock = Math.Max(Math.Max(slipRatio[0], slipRatio[1]), 0f);
                if (brake > 0.3)
                {
                    if (absActive)
                        absVib = Math.Clamp(0.35f + frontLock * 4f, 0f, 1f);
                    else if (frontLock > 0.08f)
                        absVib = Math.Min((frontLock - 0.08f) * 8f, 1f);
                }
            }

            // Log every ~100ms
            if (_lastReadTicks % (Stopwatch.Frequency / 10) < Stopwatch.Frequency / 250)
            {
                string veh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                Log($"Telem: veh='{Trunc(veh,20)}' gear={gear} rpm={rpm:F0} spd={speedKmh:F1} tq={totalForce:F4} steer={steer:F4} thr={throttle:F3} brk={brake:F3} yaw={rotY:F3} kerb={kerbVib:F3} road={roadVib:F3} slip={slipVib:F3} abs={absVib:F3}");
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
                Heading = _lmuHeading,
                SlipRatio = slipRatio,
                SlipAngle = [slipA, slipA, slipA, slipA],
                Mz = [totalForce, totalForce, 0f, 0f],
                Fx = [synFx[0], synFx[1], synFx[2], synFx[3]],
                Fy = [synFy[0], synFy[1], synFy[2], synFy[3]],
                LocalAngularVel = [(float)rotX, (float)rotY, (float)rotZ],
                KerbVibration = kerbVib,
                SlipVibrations = slipVib,
                RoadVibrations = roadVib,
                AbsVibrations = absVib,
                TyreTemp = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempI = [tireTempInner[0], tireTempInner[1], tireTempInner[2], tireTempInner[3]],
                TyreTempM = [tireTempMid[0], tireTempMid[1], tireTempMid[2], tireTempMid[3]],
                TyreTempO = [tireTempOuter[0], tireTempOuter[1], tireTempOuter[2], tireTempOuter[3]],
                LocalVelocity = [(float)vx, (float)vy, (float)vz],
                IsEngineRunning = 1,
                // Real driver-aid intervention flags (mABSActive/mTCActive
                // bytes) — replaces the disabled-by-default slip synthesis.
                AbsInAction = absActive ? 1 : 0,
                TcinAction = tcActive ? 1 : 0,
                TurboBoost = TurboBoostPressure,
                KersCharge = Soc / 100f,
                KersInput = ElectricBoostMotorTorque,
                BrakeBias = RearBrakeBias,
                RideHeight = [FrontRideHeight, RearRideHeight],
                EngineBrake = (int)EngineTorque,
            };
            // Cache for the new-frame gate above.
            _cachedPhysics = physics;
            _gateSlot = t;
            _gateTorque = steeringTorque;
            _gateSteer = steer;
            _gateSpeed = speedMs;
            _gateRpm = rpm;
            _gateBrake = brake;
            _gateThrottle = throttle;
            _gateGear = gear;
            _hasCachedPhysics = true;
            return true;
        }
        catch (Exception ex)
        {
            Log($"TryReadPhysics exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Update world position + lap fraction from telemetry mPos and scoring mLapDist.
    /// Hot path (250 Hz): no logging, no full-buffer scans — the player scoring slot
    /// is cached and validated by name, re-resolved by cheap 104-entry scan on mismatch.
    /// </summary>
    private void UpdateLmuPosition(byte[] buf, int t, int len, string vehicleName,
        double vx, double vy, double vz, double speedMs)
    {
        if (t + TI_POS + 24 <= len)
        {
            double px = LmuFieldReader.ReadF64(buf, t + TI_POS);
            double py = LmuFieldReader.ReadF64(buf, t + TI_POS + 8);
            double pz = LmuFieldReader.ReadF64(buf, t + TI_POS + 16);
            if (IsFinite(px) && IsFinite(py) && IsFinite(pz)
                && Math.Abs(px) < 50000 && Math.Abs(py) < 50000 && Math.Abs(pz) < 50000)
            {
                // A >30 m jump between 250 Hz samples is a teleport (garage reset,
                // pit-box warp, session restart) — never a real SF crossing.
                if (_lmuPosValid)
                {
                    double jx = px - _lmuCarX, jz = pz - _lmuCarZ;
                    if (jx * jx + jz * jz > 900) _framesSinceTeleport = 0;
                }
                _lmuCarX = (float)px;
                _lmuCarY = (float)py;
                _lmuCarZ = (float)pz;
                _lmuPosValid = true;
            }
        }

        // Heading from world-frame velocity (mOri rows × local vel), smoothed.
        // atan2(x, z): 0 = facing +Z, clockwise-positive — same convention as R3E yaw.
        if (_lmuPosValid && speedMs > 3 && t + TI_ORI + 72 <= len)
        {
            double lvx = vx, lvy = vy, lvz = vz;
            double w0 = 0, w2 = 0;
            bool oriOk = true;
            for (int r = 0; r < 3; r++)
            {
                double rx = LmuFieldReader.ReadF64(buf, t + TI_ORI + r * 24);
                double ry = LmuFieldReader.ReadF64(buf, t + TI_ORI + r * 24 + 8);
                double rz = LmuFieldReader.ReadF64(buf, t + TI_ORI + r * 24 + 16);
                if (!IsFinite(rx) || !IsFinite(ry) || !IsFinite(rz)) { oriOk = false; break; }
                double w = rx * lvx + ry * lvy + rz * lvz;
                if (r == 0) w0 = w;
                else if (r == 2) w2 = w;
            }
            if (oriOk)
            {
                float h = (float)Math.Atan2(w0, w2);
                if (!_lmuHeadingInit) { _lmuHeading = h; _lmuHeadingInit = true; }
                else
                {
                    float d = h - _lmuHeading;
                    while (d > MathF.PI) d -= MathF.PI * 2;
                    while (d < -MathF.PI) d += MathF.PI * 2;
                    _lmuHeading += d * 0.3f;
                }
            }
        }

        if (_framesSinceTeleport < int.MaxValue) _framesSinceTeleport++;

        // Lap fraction from the player's scoring-entry mLapDist (meters from SF)
        // divided by the scoring header's lap length. Re-validated at 5 Hz until
        // found, then every ~5 s (cheap: two 64-byte string reads + one double).
        int slot = ResolvePlayerScoringSlot(buf, len, vehicleName);
        if (_lmuScoringBase > 0)
        {
            int every = _lmuScoringTrackLength > 0 ? 1250 : 50;
            if (_lmuTrackLenCounter++ % every == 0)
                RefreshScoringTrackLength(buf, t, len);
        }
        _lmuInPits = false;
        if (slot >= 0 && _lmuScoringBase > 0)
        {
            int pitEntry = _lmuScoringBase + slot * SC_VS_STRIDE;
            if (pitEntry + SC_IN_PITS_OFF + 1 <= len)
                _lmuInPits = buf[pitEntry + SC_IN_PITS_OFF] != 0;
            // Class only changes on a car swap — refresh ~1 Hz, not 250 Hz.
            if ((_vehicleClassCounter++ % 250) == 0 && pitEntry + SC_VEHICLE_CLASS_OFF + 32 <= len)
            {
                string cls = LmuFieldReader.ReadStr(buf, pitEntry + SC_VEHICLE_CLASS_OFF, 32).Trim();
                if (cls.Length >= 2 && cls != VehicleClass)
                {
                    VehicleClass = cls;
                    Log($"Vehicle class: '{cls}' (scoring slot {slot})");
                }
            }
        }
        if (slot >= 0 && _lmuScoringBase > 0)
        {
            int entry = _lmuScoringBase + slot * SC_VS_STRIDE;
            if (entry + SC_LAP_DIST_OFF + 8 <= len)
            {
                double lapDist = LmuFieldReader.ReadF64(buf, entry + SC_LAP_DIST_OFF);
                if (IsFinite(lapDist) && lapDist >= 0 && lapDist < 60000)
                {
                    // Fallback length (scoring header unavailable): a genuine SF wrap is a
                    // large backward lapDist jump WITHOUT a recent world-position teleport
                    // (scoring updates slower than telemetry, so allow a 2 s settle window).
                    // NOTE: never "grow" the length toward the current lapDist — that pinned
                    // Npos at 1.0 for the whole first lap (lapDist / lapDist).
                    if (_prevLapDist > 500 && lapDist < _prevLapDist - 500 && _framesSinceTeleport > 500)
                        _lmuLapLength = _prevLapDist;
                    _prevLapDist = lapDist;
                    double trackLen = _lmuScoringTrackLength > 500 ? _lmuScoringTrackLength : _lmuLapLength;
                    _lmuNpos = trackLen > 500 ? Math.Clamp((float)(lapDist / trackLen), 0f, 0.9999f) : 0f;
                }
            }
        }
    }

    /// <summary>
    /// Read the authoritative lap length from ScoringInfoV01.mLapDist. Accepted only
    /// when the info block's track name matches the telemetry track name (proves the
    /// offset) and the length is sane and not shorter than the player's lapDist.
    /// Logs on every validated/rejected state change so a live dump can confirm it.
    /// </summary>
    private void RefreshScoringTrackLength(byte[] buf, int t, int len)
    {
        if (t < 0 || t + TI_TRACK_NAME + 64 > len) return;
        string telemTrack = LmuFieldReader.ReadStr(buf, t + TI_TRACK_NAME, 64).Trim();

        // ScoringInfoV01 begins with mTrackName, so locate it by content. The computed
        // start (generic 328 + paths 1300 = 1628) read name='' / lapDist=-2.0 live on
        // 2026-10-19 — the real start differs by a few bytes from the assumed sizes.
        if (telemTrack.Length >= 3
            && (_lmuScoringInfoBase < 0 || LmuFieldReader.ReadStr(buf, _lmuScoringInfoBase, 64).Trim() != telemTrack))
        {
            int found = FindScoringInfoBase(buf, len, telemTrack);
            if (found != _lmuScoringInfoBase && found >= 0)
                Log($"ScoringInfoV01 located at {found} (vehicle array {_lmuScoringBase}, gap {_lmuScoringBase - found})");
            _lmuScoringInfoBase = found;
        }
        int infoBase = _lmuScoringInfoBase >= 0 ? _lmuScoringInfoBase : _lmuScoringBase - 8 - SC_INFO_SIZE;
        if (infoBase < 0 || infoBase + SC_INFO_LAP_DIST_OFF + 8 > len) return;

        string infoTrack = LmuFieldReader.ReadStr(buf, infoBase, 64).Trim();
        double trackLen = LmuFieldReader.ReadF64(buf, infoBase + SC_INFO_LAP_DIST_OFF);

        bool nameOk = telemTrack.Length >= 3 && infoTrack.Length >= 3
            && (telemTrack.Equals(infoTrack, StringComparison.OrdinalIgnoreCase)
                || telemTrack.Contains(infoTrack, StringComparison.OrdinalIgnoreCase)
                || infoTrack.Contains(telemTrack, StringComparison.OrdinalIgnoreCase));
        bool lenOk = IsFinite(trackLen) && trackLen > 500 && trackLen < 30000
            && (_prevLapDist < 0 || _prevLapDist <= trackLen + 50);
        bool ok = nameOk && lenOk;
        _lmuScoringTrackLength = ok ? trackLen : 0;

        string key = $"{telemTrack}|{ok}";
        if (key != _lmuTrackLenLogKey)
        {
            _lmuTrackLenLogKey = key;
            Log(ok
                ? $"Scoring track length: {trackLen:F1} m (scoringInfo@{infoBase} name='{Trunc(infoTrack, 32)}')"
                : $"Scoring track length REJECTED: scoringInfo@{infoBase} ({(_lmuScoringInfoBase >= 0 ? "located by name" : "track name NOT found in scoring window — computed offset")}) " +
                  $"name='{Trunc(infoTrack, 32)}' telem='{Trunc(telemTrack, 32)}' " +
                  $"lapDist={trackLen:F1} playerLapDist={_prevLapDist:F1} — using SF-wrap calibration fallback");
        }
    }

    /// <summary>
    /// Find ScoringInfoV01 by its mTrackName: the telemetry track name, null-terminated,
    /// in the window where the header must sit (400–700 bytes before streamSize + the
    /// vehicle array). Returns -1 when absent.
    /// </summary>
    private int FindScoringInfoBase(byte[] buf, int len, string telemTrack)
    {
        if (_lmuScoringBase <= 0) return -1;
        var needle = System.Text.Encoding.ASCII.GetBytes(telemTrack);
        int lo = Math.Max(0, _lmuScoringBase - 8 - 700);
        int hi = Math.Min(len - needle.Length - 1, _lmuScoringBase - 8 - 400);
        for (int off = lo; off <= hi; off++)
        {
            if (buf[off + needle.Length] != 0) continue;
            bool match = true;
            for (int k = 0; k < needle.Length && match; k++)
                match = buf[off + k] == needle[k];
            if (match) return off;
        }
        return -1;
    }

    /// <summary>
    /// Every car in the session. Positions come from the telemetry array (physics rate)
    /// matched by mID to the scoring entries (lapDist / place / pits / name, ~5 Hz);
    /// scoring mPos is the fallback for cars without a telemetry entry. Same telemetry
    /// thread as TryReadPhysics, so _rawBuffer is this tick's full MMF copy.
    /// </summary>
    public void ReadCarPositions(List<CarPosition> cars)
    {
        cars.Clear();
        if (_view == null || _lmuScoringBase <= 0) return;
        var buf = _rawBuffer;
        int len = (int)Math.Min(_capacity, buf.Length);

        // mNumVehicles from the located scoring header; without it, scan all 104 slots
        // and keep the ones carrying a vehicle name.
        int numVehicles = 104;
        bool countKnown = false;
        if (_lmuScoringInfoBase >= 0 && _lmuScoringInfoBase + SC_INFO_NUM_VEHICLES_OFF + 4 <= len)
        {
            int n = LmuFieldReader.ReadI32(buf, _lmuScoringInfoBase + SC_INFO_NUM_VEHICLES_OFF);
            if (n > 0 && n <= 104) { numVehicles = n; countKnown = true; }
        }
        double trackLen = _lmuScoringTrackLength > 500 ? _lmuScoringTrackLength : _lmuLapLength;

        _telemPosById.Clear();
        if (TELEM_HEADER_OFF < len)
        {
            int active = buf[TELEM_HEADER_OFF];
            for (int i = 0; i < active && i < 104; i++)
            {
                int off = TELEM_INFO_OFF + i * _stride;
                if (off + TI_POS + 24 > len) break;
                double x = LmuFieldReader.ReadF64(buf, off + TI_POS);
                double z = LmuFieldReader.ReadF64(buf, off + TI_POS + 16);
                if (IsFinite(x) && IsFinite(z) && Math.Abs(x) < 50000 && Math.Abs(z) < 50000)
                    _telemPosById[LmuFieldReader.ReadI32(buf, off)] = (x, z);
            }
        }

        for (int j = 0; j < numVehicles; j++)
        {
            int e = _lmuScoringBase + j * SC_VS_STRIDE;
            if (e + SC_POS_OFF + 24 > len) break;
            if (!countKnown && LmuFieldReader.ReadStr(buf, e + SC_NAME_OFF, 64).Length < 3) continue;
            int id = LmuFieldReader.ReadI32(buf, e + SC_ID_OFF);
            double x, z;
            if (_telemPosById.TryGetValue(id, out var tp))
            {
                (x, z) = tp;
            }
            else
            {
                x = LmuFieldReader.ReadF64(buf, e + SC_POS_OFF);
                z = LmuFieldReader.ReadF64(buf, e + SC_POS_OFF + 16);
                if (!IsFinite(x) || !IsFinite(z) || Math.Abs(x) > 50000 || Math.Abs(z) > 50000) continue;
            }
            if (x == 0 && z == 0) continue;

            double lapDist = LmuFieldReader.ReadF64(buf, e + SC_LAP_DIST_OFF);
            float npos = trackLen > 500 && IsFinite(lapDist) && lapDist >= 0
                ? Math.Clamp((float)(lapDist / trackLen), 0f, 0.9999f)
                : 0f;
            var flag = CarFlags.FromLmu(
                finishStatus: (sbyte)buf[e + SC_FINISH_STATUS_OFF],
                flag: buf[e + SC_FLAG_OFF],
                underYellow: buf[e + SC_UNDER_YELLOW_OFF] != 0,
                penalties: BitConverter.ToInt16(buf, e + SC_NUM_PENALTIES_OFF));
            cars.Add(new CarPosition(
                id, (float)x, (float)z, npos,
                IsPlayer: buf[e + SC_IS_PLAYER_OFF] != 0,
                InPits: buf[e + SC_IN_PITS_OFF] != 0,
                Place: buf[e + SC_PLACE_OFF],
                Name: LmuFieldReader.ReadStr(buf, e + SC_DRIVER_NAME_OFF, 32).Trim(),
                CarClass: LmuFieldReader.ReadStr(buf, e + SC_VEHICLE_CLASS_OFF, 32).Trim(),
                Number: CarFlags.LmuCarNumber(LmuFieldReader.ReadStr(buf, e + SC_NAME_OFF, 64)),
                Flag: flag));
        }
    }

    /// <summary>
    /// Find/validate the player's scoring entry index (hot-path safe: 104-entry scan,
    /// no logging, no full-buffer search unless the base is unknown or stale).
    /// </summary>
    private int ResolvePlayerScoringSlot(byte[] buf, int len, string vehicleName)
    {
        if (_lmuScoringBase <= 0)
        {
            _lmuScoringBase = FindScoringSectionBase(buf, len);
            _lmuPlayerScoringSlot = -1;
            if (_lmuScoringBase <= 0) return -1;
        }

        // Validate cached slot by name.
        if (_lmuPlayerScoringSlot >= 0)
        {
            int entry = _lmuScoringBase + _lmuPlayerScoringSlot * SC_VS_STRIDE;
            if (entry + SC_IS_PLAYER_OFF + 1 <= len && buf[entry + SC_IS_PLAYER_OFF] == 1)
                return _lmuPlayerScoringSlot;
            string nm = LmuFieldReader.ReadStr(buf, entry + SC_NAME_OFF, 64);
            if (!string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(vehicleName)
                && (nm == vehicleName || vehicleName.StartsWith(nm) || nm.StartsWith(vehicleName)))
                return _lmuPlayerScoringSlot;
        }

        // Cheap re-resolve: isP==1 flag scan (player slot order can differ from telemetry).
        for (int i = 0; i < 104; i++)
        {
            int entry = _lmuScoringBase + i * SC_VS_STRIDE;
            if (entry + SC_IS_PLAYER_OFF + 1 > len) break;
            if (buf[entry + SC_IS_PLAYER_OFF] == 1)
            {
                _lmuPlayerScoringSlot = i;
                return i;
            }
        }

        // Fallback: name match against the telemetry vehicle.
        if (!string.IsNullOrEmpty(vehicleName))
        {
            for (int i = 0; i < 104; i++)
            {
                int entry = _lmuScoringBase + i * SC_VS_STRIDE;
                if (entry + SC_NAME_OFF + 64 > len) break;
                string nm = LmuFieldReader.ReadStr(buf, entry + SC_NAME_OFF, 64);
                if (nm == vehicleName || vehicleName.StartsWith(nm) || nm.StartsWith(vehicleName))
                {
                    _lmuPlayerScoringSlot = i;
                    return i;
                }
                if (string.IsNullOrEmpty(nm)) break;
            }
        }

        return _lmuPlayerScoringSlot;
    }

    private bool FindTelemSection(byte[] buf, int len)
    {
        _telemInfoOffset = -1;
        _pendingSlotOff = -1;
        _pendingSlotFrames = 0;
        _hasCachedPhysics = false;
        _identityCheckCounter = 0;
        _identityMismatchStreak = 0;
        _torqueSlewSeeded = false;

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
                int scoringSlot = FindPlayerVehIndex(buf, len, new[] { v0 }, out int _);
                _lockedSlotIndex = scoringSlot >= 0 ? scoringSlot : buf[kTelemHeaderOff + 1];

                int selectedSlot = ResolvePlayerSlot(buf, kTelemInfoOff, kTelemHeaderOff, active);

                _telemInfoOffset = kTelemInfoOff + selectedSlot * _stride;
                string resolvedName = LmuFieldReader.ReadStr(buf, _telemInfoOffset + TI_VEHICLE_NAME, 64);
                if (!string.IsNullOrEmpty(resolvedName) && resolvedName.Length >= 5)
                    _expectedVehicleName = resolvedName;
                _lastReResolveTicks = Stopwatch.GetTimestamp();
                Log($"FindTelemSection: FIXED offset veh='{Trunc(v0,20)}' active={active} telemSlot={selectedSlot} (hdr={buf[kTelemHeaderOff + 1]} scoring={scoringSlot}) telemOff={_telemInfoOffset} rpm={r0:F0} gear={g0}");
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
                    int scoringSlot = FindPlayerVehIndex(buf, len, new[] { v0 }, out int _);
                    _lockedSlotIndex = scoringSlot >= 0 ? scoringSlot : buf[telemHdr + 1];

                    int selectedSlot = ResolvePlayerSlot(buf, telemHdr + 4, telemHdr, buf[telemHdr]);

                    _telemInfoOffset = telemHdr + 4 + selectedSlot * _stride;
                    string resolvedName = LmuFieldReader.ReadStr(buf, _telemInfoOffset + TI_VEHICLE_NAME, 64);
                    if (!string.IsNullOrEmpty(resolvedName) && resolvedName.Length >= 5)
                        _expectedVehicleName = resolvedName;
                    _lastReResolveTicks = Stopwatch.GetTimestamp();
                    Log($"FindTelemSection: scoring-fallback FOUND veh='{Trunc(v0,20)}' telemSlot={selectedSlot} (hdr={buf[telemHdr + 1]} scoring={scoringSlot}) telemOff={_telemInfoOffset} rpm={r0:F0} gear={g0}");
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
        // Fixed layout first: generic(328) + paths(1300) = scoring info @1628,
        // ScoringInfoV01(~556) + streamSize(8) → vehicle array @2192
        // (verified in logs). Name-scan below is fallback only.
        const int fixedBase = 2192;
        if (fixedBase + vsStride + nameOff + 20 <= len)
        {
            string n0 = LmuFieldReader.ReadStr(buf, fixedBase + nameOff, 64);
            string n1 = LmuFieldReader.ReadStr(buf, fixedBase + vsStride + nameOff, 64);
            if (n0.Length >= 10 && n1.Length >= 10)
                return fixedBase;
        }
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
    private int FindPlayerVehIndex(byte[] buf, int len, string[] knownNames, out int scoringBase)
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
            scoringBase = -1;
            return -1;
        }

        scoringBase = baseEntry;
        Log($"FindPlayerVehIndex: base={baseEntry}, scanning for player...");

        // First pass: find isP == 1 (local human player)
        for (int i = 0; i < 104; i++)
        {
            int entry = baseEntry + i * vsStride;
            if (entry + 198 > len) break;
            if (buf[entry + 196] == 1)
            {
                string vn = LmuFieldReader.ReadStr(buf, entry + nameOff, 64);
                Log($"  -> PLAYER at index {i} (isP=1): '{Trunc(vn,25)}'");
                return i;
            }
        }

        // Second pass: find ctrl == 1 (any human, in case isP is not set)
        for (int i = 0; i < 104; i++)
        {
            int entry = baseEntry + i * vsStride;
            if (entry + 198 > len) break;
            if (buf[entry + 197] == 1)
            {
                string vn = LmuFieldReader.ReadStr(buf, entry + nameOff, 64);
                Log($"  -> HUMAN at index {i} (ctrl=1): '{Trunc(vn,25)}'");
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

    private int FindTelemetrySlotByName(byte[] buf, int telemBase, int activeCount, string targetName)
    {
        for (int i = 0; i < Math.Min(activeCount, 104); i++)
        {
            int off = telemBase + i * _stride;
            if (off + 96 > buf.Length) break;
            string name = LmuFieldReader.ReadStr(buf, off + TI_VEHICLE_NAME, 64);
            if (string.IsNullOrEmpty(name) || name.Length < 5) continue;
            if (name == targetName || targetName.StartsWith(name) || name.StartsWith(targetName))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Content sanity for a TelemInfoV01 entry: non-empty vehicle name plus
    /// plausible RPM/gear. Rejects torn reads without logging (hot path).
    /// </summary>
    private static bool IsSaneTelemetrySlot(byte[] buf, int len, int entryOff)
    {
        if (entryOff + TI_GEAR + 4 > len) return false;
        string name = LmuFieldReader.ReadStr(buf, entryOff + TI_VEHICLE_NAME, 64);
        if (string.IsNullOrEmpty(name) || name.Length < 5) return false;
        double rpm = LmuFieldReader.ReadF64(buf, entryOff + TI_ENGINE_RPM);
        int gear = LmuFieldReader.ReadI32(buf, entryOff + TI_GEAR);
        return rpm >= 0 && rpm <= 15000 && gear >= -1 && gear <= 10;
    }

    private int ResolvePlayerSlot(byte[] buf, int telemBase, int telemHeaderOff, int activeCount)
    {
        byte headerSlot = buf[telemHeaderOff + 1];

        // Prefer the telemetry header's own player index: it addresses the
        // telemInfo array directly. Name matching picks the first entry with
        // an identical vehicle string — in multiclass fields with duplicate
        // cars that resolves to an AI car, not the player.
        int headerEntry = telemBase + headerSlot * _stride;
        if (headerSlot < activeCount && IsSaneTelemetrySlot(buf, buf.Length, headerEntry))
        {
            Log($"ResolvePlayerSlot: using header slot {headerSlot} (validated)");
            return headerSlot;
        }

        int vsBase = FindScoringSectionBase(buf, buf.Length);
        if (vsBase > 0)
        {
            // First pass: find isP == 1 (local player)
            for (int i = 0; i < 104; i++)
            {
                int entry = vsBase + i * 584;
                if (entry + 200 > buf.Length) break;
                if (buf[entry + 196] == 1)
                {
                    string playerVeh = LmuFieldReader.ReadStr(buf, entry + 36, 64);
                    if (!string.IsNullOrEmpty(playerVeh) && playerVeh.Length >= 5)
                    {
                        _expectedVehicleName = playerVeh;
                        int matched = FindTelemetrySlotByName(buf, telemBase, activeCount, playerVeh);
                        if (matched >= 0)
                        {
                            Log($"ResolvePlayerSlot: scoring slot {i} (isP=1) veh='{Trunc(playerVeh,20)}' matched telemetry slot {matched} (hdr={headerSlot})");
                            return matched;
                        }
                        if (i < activeCount)
                        {
                            Log($"ResolvePlayerSlot: scoring slot {i} (isP=1), no name match, using index (hdr={headerSlot})");
                            return i;
                        }
                    }
                    break;
                }
            }
            // Second pass: find ctrl == 1 (any human, fallback)
            for (int i = 0; i < 104; i++)
            {
                int entry = vsBase + i * 584;
                if (entry + 200 > buf.Length) break;
                if (buf[entry + 197] == 1)
                {
                    string playerVeh = LmuFieldReader.ReadStr(buf, entry + 36, 64);
                    if (!string.IsNullOrEmpty(playerVeh) && playerVeh.Length >= 5)
                    {
                        _expectedVehicleName = playerVeh;
                        int matched = FindTelemetrySlotByName(buf, telemBase, activeCount, playerVeh);
                        if (matched >= 0)
                        {
                            Log($"ResolvePlayerSlot: scoring slot {i} (ctrl=1) veh='{Trunc(playerVeh,20)}' matched telemetry slot {matched} (hdr={headerSlot})");
                            return matched;
                        }
                        if (i < activeCount)
                        {
                            Log($"ResolvePlayerSlot: scoring slot {i} (ctrl=1), no name match, using index (hdr={headerSlot})");
                            return i;
                        }
                    }
                    break;
                }
            }
        }

        Log($"ResolvePlayerSlot: unresolved, using header slot {headerSlot}");
        return headerSlot;
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
        int playerSlot = FindPlayerVehIndex(buf, len, knownNames, out _);
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
            // Player world position (filled by UpdateLmuPosition during the physics
            // read). Index 0 = player — this is what TelemetryLoop.GetCarX/Y/Z reads.
            var carCoords = new StructVector3[60];
            if (_lmuPosValid)
                carCoords[0] = new StructVector3 { X = _lmuCarX, Y = _lmuCarY, Z = _lmuCarZ };
            graphics = new SPageFileGraphicEvo
            {
                SteerDegrees = 900, CarModel = new byte[33], CarCoordinates = carCoords,
                Status = AcEvoStatus.AcLive, RpmPercent = (float)(rpm / 8000),
                Flag = AcEvoFlagType.AcNoFlag, IsEngineRunning = true,
                EngineType = AcEvoEngineType.AcevoInternalCombustion,
                CarLocation = _lmuInPits ? AcEvoCarLocation.AcevoPitlane : AcEvoCarLocation.AcevoTrack,
                UseSingleCompound = false,
                Npos = _lmuNpos,
                IsInPitLane = _lmuInPits,
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
            // New track: reset lap-length calibration and scoring-slot cache so the
            // car dot, Npos and learned length re-learn cleanly for the new venue.
            // A blank read (telemetry slot not resolved yet) is NOT a track change —
            // treating it as one wiped the calibration on every re-resolve.
            if (track.Length > 0 && track != _lmuPosTrack)
            {
                _lmuPosTrack = track;
                _lmuLapLength = 0;
                _lmuScoringTrackLength = 0;
                _lmuTrackLenCounter = 0;
                _prevLapDist = -1;
                _lmuNpos = 0;
                _lmuPlayerScoringSlot = -1;
            }
            double lapLength = _lmuScoringTrackLength > 500 ? _lmuScoringTrackLength : _lmuLapLength;
            staticData = new SPageFileStaticEvo
            {
                SmVersion = "LMU\0"u8.ToArray(),
                Track = CopyStr(track, 33),
                TrackConfiguration = new byte[33],
                // Scoring-header lap length (meters), or the SF-wrap learned length.
                // Feeds OSM candidate validation + length display like other games.
                TrackLengthM = lapLength > 500 ? (float)lapLength : 0f,
            };
            return true;
        }
        catch { return false; }
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    private static string Trunc(string? s, int n) => s == null || s.Length <= n ? s ?? "" : s[..n] + "…";
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
