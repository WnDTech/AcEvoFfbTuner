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

    private const int TI_GEAR = 352;
    private const int TI_ENGINE_RPM = 356;
    private const int TI_VEHICLE_NAME = 32;
    private const int TI_TRACK_NAME = 96;
    private const int TI_LOCAL_VEL = 184;
    private const int TI_LOCAL_ACCEL = 208;
    private const int TI_LOCAL_ROT = 304;
    private const int TI_UNFILTERED_THROTTLE = 388;
    private const int TI_UNFILTERED_BRAKE = 396;
    private const int TI_UNFILTERED_STEERING = 404;
    private const int TI_STEERING_SHAFT_TORQUE = 452;
    private const int TI_LAP_NUMBER = 20;

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

            // ── Wheel data (mWheel[4] at TelemInfoV01 start + 848, each 260 bytes) ──
            const int wheelBaseOff = 848;
            const int wheelStride = 260;
            float[] tireLoads = new float[4];
            float[] tirePressures = new float[4];
            float[] tireRots = new float[4];
            float[] latForces = new float[4];
            float[] lonForces = new float[4];
            float[] tireTemps = new float[4];
            float[] tireWears = new float[4];
            float[] suspDef = new float[4];

            for (int wi = 0; wi < 4; wi++)
            {
                int wOff = t + wheelBaseOff + wi * wheelStride;
                if (wOff + 160 > readLen) continue;
                double rv = LmuFieldReader.ReadF64(_rawBuffer, wOff + 40);
                tireRots[wi] = IsFinite(rv) ? (float)rv : 0f;
                double lf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 88);
                latForces[wi] = IsFinite(lf) ? Math.Clamp((float)lf, -50000f, 50000f) : 0f;
                double lnf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 96);
                lonForces[wi] = IsFinite(lnf) ? Math.Clamp((float)lnf, -50000f, 50000f) : 0f;
                double tl = LmuFieldReader.ReadF64(_rawBuffer, wOff + 104);
                tireLoads[wi] = IsFinite(tl) ? Math.Max(0, (float)tl) : 0f;
                double gf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 112);
                _tireGrip[wi] = IsFinite(gf) ? (float)Math.Clamp(gf, -1f, 1f) : 0f;
                double pr = LmuFieldReader.ReadF64(_rawBuffer, wOff + 120);
                tirePressures[wi] = IsFinite(pr) ? Math.Max(0, (float)pr) : 0f;
                double t1 = LmuFieldReader.ReadF64(_rawBuffer, wOff + 128);
                double t2 = LmuFieldReader.ReadF64(_rawBuffer, wOff + 136);
                double t3 = LmuFieldReader.ReadF64(_rawBuffer, wOff + 144);
                tireTemps[wi] = IsFinite(t1) && IsFinite(t2) && IsFinite(t3) ? (float)(((t1 + t2 + t3) / 3.0) - 273.15) : 0f;
                double tw = LmuFieldReader.ReadF64(_rawBuffer, wOff + 152);
                tireWears[wi] = IsFinite(tw) ? Math.Clamp((float)tw, 0f, 1f) : 0f;
                double sd = LmuFieldReader.ReadF64(_rawBuffer, wOff);
                suspDef[wi] = IsFinite(sd) ? (float)sd : 0f;
            }

            // One-time wheel data dump for diagnostics
            if (!_loggedWheelDump && tirePressures[0] > 50f)
            {
                int wOff = t + wheelBaseOff;
                double sd = LmuFieldReader.ReadF64(_rawBuffer, wOff);
                double rv = LmuFieldReader.ReadF64(_rawBuffer, wOff + 40);
                double lf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 88);
                double lnf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 96);
                double tl = LmuFieldReader.ReadF64(_rawBuffer, wOff + 104);
                double gf = LmuFieldReader.ReadF64(_rawBuffer, wOff + 112);
                double pr = LmuFieldReader.ReadF64(_rawBuffer, wOff + 120);
                double t1 = LmuFieldReader.ReadF64(_rawBuffer, wOff + 128);
                double tw = LmuFieldReader.ReadF64(_rawBuffer, wOff + 152);
                Log($"WHEEL0: off={wOff} suspDef={sd:F4} rot={rv:F2} latF={lf:F1} lonF={lnf:F1} load={tl:F1} grip={gf:F4} press={pr:F1} t1={t1:F1} wear={tw:F4}");
                _loggedWheelDump = true;
            }

            float totalForce = (float)(Math.Abs(steeringTorque) > 0.0001 ? steeringTorque : ffbTorque);
            float absForce = Math.Abs(totalForce);
            float absSteer = (float)Math.Abs(steer);
            float sgn = (float)(steer > 0 ? 1 : -1);
            float blend = Math.Clamp(absSteer / 0.002f, 0f, 1f);
            float centerDeg = Math.Abs((float)steer * 450f);
            float cFactor = centerDeg < 3f ? MathF.Pow(centerDeg / 3f, 0.5f) : 1f;
            int mg = gear switch { -1 => 0, 0 => 1, _ => gear + 1 };
            float slipA = (float)(speedMs >= 0.5 ? Math.Clamp(Math.Atan2(vx, -vz), -0.30, 0.30) : 0);
            float speedKmh = (float)(speedMs * 3.6);

            // Synthesize per-wheel Mz from total steering shaft torque.
            // LMU/rF2 only provides mSteeringShaftTorque, not per-wheel Mz.
            float mzMagnitude = absForce * blend * cFactor;
            float mzSign = sgn;
            float mzFL = -mzMagnitude * mzSign * 18f;
            float mzFR = -mzMagnitude * mzSign * 18f;
            float mzRL = -mzMagnitude * mzSign * 12f;
            float mzRR = -mzMagnitude * mzSign * 12f;

            // Log every ~100ms for debugging
            if (_lastReadTicks % (Stopwatch.Frequency / 10) < Stopwatch.Frequency / 250)
            {
                string veh = LmuFieldReader.ReadStr(_rawBuffer, t + TI_VEHICLE_NAME, 64);
                Log($"Telem: veh='{Trunc(veh,20)}' gear={gear} rpm={rpm:F0} speed={speedKmh:F1}km/h tq={totalForce:F4} steer={steer:F4} thr={throttle:F3} brk={brake:F3} mz={-absForce * sgn * blend * cFactor:F4}");
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
                SlipRatio = [0, 0, 0, 0],
                SlipAngle = [slipA, slipA, slipA, slipA],
                Mz = [mzFL, mzFR, mzRL, mzRR],
                Fx = [lonForces[0], lonForces[1], lonForces[2], lonForces[3]],
                Fy = [latForces[0], latForces[1], latForces[2], latForces[3]],
                LocalAngularVel = [(float)0, (float)0, (float)0],
                KerbVibration = 0,
                SlipVibrations = 0,
                RoadVibrations = 0,
                TyreTemp = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempI = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempM = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                TyreTempO = [tireTemps[0], tireTemps[1], tireTemps[2], tireTemps[3]],
                LocalVelocity = [(float)vx, (float)vy, (float)vz],
                IsEngineRunning = 1,
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
                _telemInfoOffset = kTelemInfoOff;
                byte active = buf[kTelemHeaderOff];
                byte playerIdx = buf[kTelemHeaderOff + 1];
                _lockedSlotIndex = FindPlayerVehIndex(buf, len, new[] { v0 });
                if (_lockedSlotIndex < 0) _lockedSlotIndex = playerIdx;
                Log($"FindTelemSection: FIXED offset veh='{Trunc(v0,20)}' active={active} player={_lockedSlotIndex} rpm={r0:F0} gear={g0}");
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
                    _telemInfoOffset = telemHdr + 4;
                    byte playerIdx = buf[telemHdr + 1];
                    _lockedSlotIndex = FindPlayerVehIndex(buf, len, new[] { v0 });
                    if (_lockedSlotIndex < 0) _lockedSlotIndex = playerIdx;
                    Log($"FindTelemSection: scoring-fallback FOUND veh='{Trunc(v0,20)}' rpm={r0:F0} gear={g0}");
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
