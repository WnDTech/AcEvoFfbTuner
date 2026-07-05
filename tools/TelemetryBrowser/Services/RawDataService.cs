using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace TelemetryBrowser.Services;

public sealed class RawDataService
{
    private const string R3E_MMF = @"$R3E";
    private const string EVO_PHYSICS = @"Local\acevo_pmf_physics";
    private const string EVO_GRAPHICS = @"Local\acevo_pmf_graphics";
    private const string EVO_STATIC = @"Local\acevo_pmf_static";
    private const string AC1_PHYSICS = @"Local\acpmf_physics";
    private const string AC1_GRAPHICS = @"Local\acpmf_graphic";
    private const string AC1_STATIC = @"Local\acpmf_static";
    private const string LMU_MMF = @"LMU_Data";
    private const string RF2_MMF = @"$rFactor2Telemetry$";
    private const string ACC_PHYSICS = @"Local\acpmf_physics";
    private const string ACC_GRAPHICS = @"Local\acpmf_graphics";
    private const string ACC_STATIC = @"Local\acpmf_static";
    private static readonly string[] ACC_OPPONENT_NAMES =
    [
        "acpmf_opponents",
        @"Local\acpmf_opponents",
        @"Global\acpmf_opponents",
        "acpmf_opponent",
        @"Local\acpmf_opponent",
        "acpmf_opponents_s",
        @"Local\acpmf_opponents_s",
        "acpmf_opponentsdata",
        @"Local\acpmf_opponentsdata",
    ];

    private static readonly HashSet<Type> PrimitiveTypes =
    [
        typeof(float), typeof(double), typeof(int), typeof(short), typeof(long),
        typeof(byte), typeof(bool), typeof(uint), typeof(ushort), typeof(ulong),
        typeof(sbyte), typeof(decimal), typeof(char), typeof(nint), typeof(nuint)
    ];

    public List<RawFieldInfo> GetRawFields(string gameId, string section)
    {
        var fields = new List<RawFieldInfo>();

        if (gameId == "raccoroom" && section == "physics")
            ExploreStructFields(typeof(R3eShared), "", fields);
        else if (gameId == "raccoroom" && section == "player")
            ExploreStructFields(typeof(R3ePlayerData), "Player", fields);
        else if (gameId == "raccoroom" && section == "tires")
            ExploreR3eTireFields(fields);
        else if (gameId == "acevo" && section == "physics")
            ExploreStructFields(typeof(SPageFilePhysicsEvo), "", fields);
        else if (gameId == "acevo" && section == "graphics")
            ExploreStructFields(typeof(SPageFileGraphicEvo), "", fields);
        else if (gameId == "acevo" && section == "static")
            ExploreStructFields(typeof(SPageFileStaticEvo), "", fields);
        else if (gameId == "assettocorsa" && section == "physics")
            ExploreStructFields(typeof(SPageFilePhysicsAC), "", fields);
        else if (gameId == "assettocorsa" && section == "graphics")
            ExploreStructFields(typeof(SPageFileGraphicAC), "", fields);
        else if (gameId == "assettocorsa" && section == "static")
            ExploreStructFields(typeof(SPageFileStaticAC), "", fields);
        else if (gameId == "lemansultimate" && section == "physics")
            ExploreLmuFields(fields);
        else if (gameId == "lemansultimate" && section == "header")
            ExploreLmuHeaderFields(fields);
        else if (gameId == "lemansultimate" && section == "scoring")
            ExploreLmuScoringFields(fields);
        else if (gameId == "assettocorsac" && section == "physics")
            ExploreAccPhysicsFields(fields);
        else if (gameId == "assettocorsac" && section == "graphics")
            ExploreAccGraphicsFields(fields);
        else if (gameId == "assettocorsac" && section == "static")
            ExploreAccStaticFields(fields);
        else if (gameId == "rfactor2" && section == "physics")
            ExploreRf2Fields(fields);

        return fields;
    }

    public Dictionary<string, object?> ReadRawValues(string gameId, string section)
    {
        Dictionary<string, object?> result = gameId switch
        {
            "raccoroom" => ReadR3eRaw(section),
            "acevo" => ReadEvoRaw(section),
            "assettocorsa" => ReadAc1Raw(section),
            "lemansultimate" => ReadLmuRaw(section),
            "assettocorsac" => ReadAccRaw(section),
            "rfactor2" => ReadRf2Raw(section),
            _ => new()
        };
        SanitizeFloats(result);
        return result;
    }

    public Dictionary<string, object?> ReadR3eOpponents()
    {
        var result = new Dictionary<string, object?>();
        var buf = ReadMmf(R3E_MMF, 65536);
        if (buf == null)
        {
            result["_error"] = "MMF not found";
            return result;
        }

        try
        {
            int driverOff = BitConverter.ToInt32(buf, 8);
            int driverEntrySize = BitConverter.ToInt32(buf, 12);

            result["_driverOff"] = driverOff;
            result["_driverEntrySize"] = driverEntrySize;

            if (driverOff <= 0 || driverEntrySize <= 0 || driverOff + 128 * driverEntrySize > buf.Length)
            {
                result["_error"] = $"Invalid offsets: off={driverOff} size={driverEntrySize}";
                return result;
            }

            // Count drivers with non-empty name bytes
            int numCars = 0;
            for (int i = 0; i < 128; i++)
            {
                int entryOff = driverOff + i * driverEntrySize;
                if (entryOff + 4 > buf.Length) break;
                bool hasName = false;
                for (int j = 0; j < 64; j++)
                {
                    if (buf[entryOff + j] != 0) { hasName = true; break; }
                }
                if (hasName) numCars++;
            }

            result["NumCars"] = numCars;

            // Dump raw hex of first 3 driver entries for diagnosis
            var hexDump = new System.Text.StringBuilder();
            for (int dumpI = 0; dumpI < 3 && dumpI < numCars; dumpI++)
            {
                int entryBase = driverOff + dumpI * driverEntrySize;
                hexDump.AppendLine($"--- Driver[{dumpI}] raw bytes ({driverEntrySize}B @{entryBase}) ---");
                for (int row = 0; row < driverEntrySize; row += 16)
                {
                    hexDump.Append($"  {entryBase + row:X4}  ");
                    for (int col = 0; col < 16 && row + col < driverEntrySize; col++)
                        hexDump.Append($"{buf[entryBase + row + col]:X2} ");
                    hexDump.Append("  ");
                    for (int col = 0; col < 16 && row + col < driverEntrySize; col++)
                    {
                        byte b = buf[entryBase + row + col];
                        hexDump.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    hexDump.AppendLine();
                }
            }
            result["_driverRawHex"] = hexDump.ToString();

            // Game's actual R3eDriverData has a 4-byte header (driver ID) before DriverInfo,
            // shifting all C# struct offsets by +4. Verified from raw hex dump.
            const int HEADER = 4;
            const int OFF_Name = HEADER + 0;                    // 4
            const int OFF_CarNumber = HEADER + 64;              // 68
            const int OFF_FinishStatus = HEADER + 128;          // 132
            const int OFF_Place = HEADER + 132;                 // 136
            const int OFF_PlaceClass = HEADER + 136;            // 140
            const int OFF_TrackSector = HEADER + 160;           // 164
            const int OFF_CompletedLaps = HEADER + 164;         // 168
            const int OFF_LapTimeCurrentSelf = HEADER + 172;    // 176
            const int OFF_TimeDeltaFront = HEADER + 212;        // 216
            const int OFF_TimeDeltaBehind = HEADER + 216;       // 220
            const int OFF_PitStopStatus = HEADER + 220;         // 224
            const int OFF_InPitlane = HEADER + 224;             // 228
            const int OFF_NumPitstops = HEADER + 228;           // 232
            const int OFF_CarSpeed = HEADER + 252;              // 256
            const int OFF_TireTypeFront = HEADER + 256;         // 260
            const int OFF_TireTypeRear = HEADER + 260;          // 264
            const int OFF_DrsState = HEADER + 280;              // 284
            const int OFF_PtpState = HEADER + 284;              // 288
            const int OFF_VirtualEnergy = HEADER + 288;         // 292

            for (int i = 0; i < numCars && i < 128; i++)
            {
                string p = $"DriverData[{i}]";
                int off = driverOff + i * driverEntrySize;

                result[$"{p}_Name"] = ReadFixedString(buf, off + OFF_Name, 64);
                result[$"{p}_CarNumber"] = BitConverter.ToInt32(buf, off + OFF_CarNumber);
                result[$"{p}_Place"] = BitConverter.ToInt32(buf, off + OFF_Place);
                result[$"{p}_PlaceClass"] = BitConverter.ToInt32(buf, off + OFF_PlaceClass);
                result[$"{p}_CompletedLaps"] = BitConverter.ToInt32(buf, off + OFF_CompletedLaps);
                result[$"{p}_LapTimeCurrentSelf"] = SafeFloat(buf, off + OFF_LapTimeCurrentSelf);
                result[$"{p}_TrackSector"] = BitConverter.ToInt32(buf, off + OFF_TrackSector);
                result[$"{p}_TimeDeltaFront"] = SafeFloat(buf, off + OFF_TimeDeltaFront);
                result[$"{p}_TimeDeltaBehind"] = SafeFloat(buf, off + OFF_TimeDeltaBehind);
                result[$"{p}_PitStopStatus"] = BitConverter.ToInt32(buf, off + OFF_PitStopStatus);
                result[$"{p}_InPitlane"] = BitConverter.ToInt32(buf, off + OFF_InPitlane);
                result[$"{p}_NumPitstops"] = BitConverter.ToInt32(buf, off + OFF_NumPitstops);
                result[$"{p}_CarSpeed"] = SafeFloat(buf, off + OFF_CarSpeed);
                result[$"{p}_TireTypeFront"] = BitConverter.ToInt32(buf, off + OFF_TireTypeFront);
                result[$"{p}_TireTypeRear"] = BitConverter.ToInt32(buf, off + OFF_TireTypeRear);
                result[$"{p}_DrsState"] = BitConverter.ToInt32(buf, off + OFF_DrsState);
                result[$"{p}_PtpState"] = BitConverter.ToInt32(buf, off + OFF_PtpState);
                result[$"{p}_VirtualEnergy"] = SafeFloat(buf, off + OFF_VirtualEnergy);
                result[$"{p}_FinishStatus"] = BitConverter.ToInt32(buf, off + OFF_FinishStatus);
            }
        }
        catch (Exception ex)
        {
            result["_error"] = $"ReadR3eOpponents: {ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private static void ExploreLmuHeaderFields(List<RawFieldInfo> fields)
    {
        fields.Add(new(RawFieldType.Primitive, "H_gameVersion", "", "", 60));
        fields.Add(new(RawFieldType.Primitive, "H_ffbTorque", "Nm", "", 64));
        fields.Add(new(RawFieldType.Primitive, "H_hwnd", "", "", 68));
        fields.Add(new(RawFieldType.Primitive, "H_width", "px", "", 76));
        fields.Add(new(RawFieldType.Primitive, "H_height", "px", "", 80));
        fields.Add(new(RawFieldType.Primitive, "H_refreshRate", "Hz", "", 84));
        fields.Add(new(RawFieldType.Primitive, "H_windowed", "", "", 88));
    }

    private static void ExploreLmuScoringFields(List<RawFieldInfo> fields)
    {
        fields.Add(new(RawFieldType.String, "S_trackName", "", "", 0));
        fields.Add(new(RawFieldType.Primitive, "S_session", "", "", 64));
        fields.Add(new(RawFieldType.Primitive, "S_currentET", "s", "", 68));
        fields.Add(new(RawFieldType.Primitive, "S_endET", "s", "", 76));
        fields.Add(new(RawFieldType.Primitive, "S_maxLaps", "", "", 84));
        fields.Add(new(RawFieldType.Primitive, "S_lapDist", "m", "", 88));
        fields.Add(new(RawFieldType.Primitive, "S_numVehicles", "", "", 104));
        fields.Add(new(RawFieldType.Primitive, "S_gamePhase", "", "", 108));
        fields.Add(new(RawFieldType.Primitive, "S_yellowFlagState", "", "", 109));
        fields.Add(new(RawFieldType.Primitive, "S_startLight", "", "", 113));
        fields.Add(new(RawFieldType.Primitive, "S_numRedLights", "", "", 114));
        fields.Add(new(RawFieldType.String, "S_playerName", "", "", 116));
        fields.Add(new(RawFieldType.Primitive, "S_darkCloud", "0-1", "", 212));
        fields.Add(new(RawFieldType.Primitive, "S_raining", "0-1", "", 220));
        fields.Add(new(RawFieldType.Primitive, "S_ambientTemp", "C", "", 228));
        fields.Add(new(RawFieldType.Primitive, "S_trackTemp", "C", "", 236));
        fields.Add(new(RawFieldType.Primitive, "S_wind", "m/s", "", 244));
        fields.Add(new(RawFieldType.Primitive, "S_minPathWetness", "0-1", "", 268));
        fields.Add(new(RawFieldType.Primitive, "S_maxPathWetness", "0-1", "", 276));
        fields.Add(new(RawFieldType.Primitive, "S_gameMode", "", "", 284));
        fields.Add(new(RawFieldType.String, "S_serverName", "", "", 296));
        fields.Add(new(RawFieldType.Primitive, "S_startET", "s", "", 328));
        fields.Add(new(RawFieldType.Primitive, "S_avgPathWetness", "0-1", "", 332));
        fields.Add(new(RawFieldType.Primitive, "S_sessionTimeRemaining", "s", "", 340));
        fields.Add(new(RawFieldType.Primitive, "S_timeOfDay", "h", "", 344));
        fields.Add(new(RawFieldType.Primitive, "S_trackGripLevel", "0-1", "", 349));
        fields.Add(new(RawFieldType.Primitive, "S_cloudCoverage", "0-1", "", 350));

        // Vehicle scoring fields (pattern, repeated for each vehicle)
        fields.Add(new(RawFieldType.Nested, "VehicleScoring", "", "", 2192));
    }

    private static void ExploreLmuFields(List<RawFieldInfo> fields)
    {
        const int wheelBaseOff = 848;
        const int wheelStride = 260;

        // Offsets are from start of TelemInfoV01 (player telemetry entry)
        fields.Add(new(RawFieldType.Primitive, "deltaTime", "s", "", 4));
        fields.Add(new(RawFieldType.Primitive, "elapsedTime", "s", "", 12));
        fields.Add(new(RawFieldType.Primitive, "lapNumber", "", "", 20));
        fields.Add(new(RawFieldType.Primitive, "lapStartET", "s", "", 24));
        fields.Add(new(RawFieldType.String, "vehicleName", "", "", 32));
        fields.Add(new(RawFieldType.String, "trackName", "", "", 96));
        fields.Add(new(RawFieldType.Primitive, "pos", "", "", 160));
        fields.Add(new(RawFieldType.Primitive, "localVel", "m/s", "", 184));
        fields.Add(new(RawFieldType.Primitive, "localAccel", "m/s2", "", 208));
        fields.Add(new(RawFieldType.Primitive, "localRot", "rad/s", "", 304));
        fields.Add(new(RawFieldType.Primitive, "gear", "", "", 352));
        fields.Add(new(RawFieldType.Primitive, "engineRPM", "rpm", "", 356));
        fields.Add(new(RawFieldType.Primitive, "engineWaterTemp", "C", "", 364));
        fields.Add(new(RawFieldType.Primitive, "engineOilTemp", "C", "", 372));
        fields.Add(new(RawFieldType.Primitive, "clutchRPM", "rpm", "", 380));
        fields.Add(new(RawFieldType.Primitive, "unfilteredThrottle", "%", "", 388));
        fields.Add(new(RawFieldType.Primitive, "unfilteredBrake", "%", "", 396));
        fields.Add(new(RawFieldType.Primitive, "unfilteredSteering", "-1..+1", "", 404));
        fields.Add(new(RawFieldType.Primitive, "unfilteredClutch", "%", "", 412));
        fields.Add(new(RawFieldType.Primitive, "steeringShaftTorque", "Nm", "", 452));
        fields.Add(new(RawFieldType.Primitive, "front3rdDeflection", "m", "", 460));
        fields.Add(new(RawFieldType.Primitive, "rear3rdDeflection", "m", "", 468));
        fields.Add(new(RawFieldType.Primitive, "frontWingHeight", "m", "", 476));
        fields.Add(new(RawFieldType.Primitive, "frontRideHeight", "m", "", 484));
        fields.Add(new(RawFieldType.Primitive, "rearRideHeight", "m", "", 492));
        fields.Add(new(RawFieldType.Primitive, "drag", "N", "", 500));
        fields.Add(new(RawFieldType.Primitive, "frontDownforce", "N", "", 508));
        fields.Add(new(RawFieldType.Primitive, "rearDownforce", "N", "", 516));
        fields.Add(new(RawFieldType.Primitive, "fuel", "L", "", 524));
        fields.Add(new(RawFieldType.Primitive, "engineMaxRPM", "rpm", "", 532));
        fields.Add(new(RawFieldType.Primitive, "dentSeverity", "", "", 544));
        fields.Add(new(RawFieldType.Primitive, "lastImpactET", "s", "", 552));
        fields.Add(new(RawFieldType.Primitive, "lastImpactMagnitude", "", "", 560));
        fields.Add(new(RawFieldType.Primitive, "engineTorque", "Nm", "", 592));
        fields.Add(new(RawFieldType.Primitive, "currentSector", "", "", 600));
        fields.Add(new(RawFieldType.Primitive, "fuelCapacity", "L", "", 608));
        fields.Add(new(RawFieldType.String, "frontTireCompoundName", "", "", 620));
        fields.Add(new(RawFieldType.String, "rearTireCompoundName", "", "", 638));
        fields.Add(new(RawFieldType.Primitive, "visualSteeringWheelRange", "deg", "", 660));
        fields.Add(new(RawFieldType.Primitive, "rearBrakeBias", "%", "", 664));
        fields.Add(new(RawFieldType.Primitive, "turboBoostPressure", "bar", "", 672));
        fields.Add(new(RawFieldType.Primitive, "physicalSteeringWheelRange", "deg", "", 692));
        fields.Add(new(RawFieldType.Primitive, "deltaBest", "s", "", 696));
        fields.Add(new(RawFieldType.Primitive, "batteryChargeFraction", "%", "", 704));
        fields.Add(new(RawFieldType.Primitive, "electricBoostMotorTorque", "Nm", "", 712));
        fields.Add(new(RawFieldType.Primitive, "electricBoostMotorRPM", "rpm", "", 720));
        fields.Add(new(RawFieldType.Primitive, "electricBoostMotorTemperature", "C", "", 728));
        fields.Add(new(RawFieldType.Primitive, "regen", "%", "", 768));
        fields.Add(new(RawFieldType.Primitive, "soc", "%", "", 772));
        fields.Add(new(RawFieldType.Primitive, "virtualEnergy", "J", "", 776));
        fields.Add(new(RawFieldType.Primitive, "timeGapCarAhead", "s", "", 780));
        fields.Add(new(RawFieldType.Primitive, "timeGapCarBehind", "s", "", 784));
        fields.Add(new(RawFieldType.Primitive, "timeGapPlaceAhead", "s", "", 788));
        fields.Add(new(RawFieldType.Primitive, "timeGapPlaceBehind", "s", "", 792));

        string[] wLabels = ["FL", "FR", "RL", "RR"];
        for (int wi = 0; wi < 4; wi++)
        {
            var w = wLabels[wi];
            int wOff = wheelBaseOff + wi * wheelStride;
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.suspensionDeflection", "m", "", wOff));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.rideHeight", "m", "", wOff + 8));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.suspForce", "N", "", wOff + 16));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.brakeTemp", "C", "", wOff + 24));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.brakePressure", "bar", "", wOff + 32));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.rotation", "rad/s", "", wOff + 40));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.lateralPatchVel", "m/s", "", wOff + 48));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.longitudinalPatchVel", "m/s", "", wOff + 56));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.lateralGroundVel", "m/s", "", wOff + 64));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.longitudinalGroundVel", "m/s", "", wOff + 72));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.camber", "rad", "", wOff + 80));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.lateralForce", "N", "", wOff + 88));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.longitudinalForce", "N", "", wOff + 96));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.tireLoad", "N", "", wOff + 104));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.gripFract", "0-1", "", wOff + 112));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.pressure", "bar", "", wOff + 120));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.temperatureInner", "K", "", wOff + 128));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.temperatureMid", "K", "", wOff + 136));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.temperatureOuter", "K", "", wOff + 144));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.wear", "0-1", "", wOff + 152));
            fields.Add(new(RawFieldType.String, $"Wheel.{w}.terrainName", "", "", wOff + 160));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.verticalTireDeflection", "m", "", wOff + 180));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.wheelYLocation", "m", "", wOff + 188));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.toe", "deg", "", wOff + 196));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.tireCarcassTemperature", "K", "", wOff + 204));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.tireInnerLayerTemp0", "K", "", wOff + 212));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.tireInnerLayerTemp1", "K", "", wOff + 220));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.tireInnerLayerTemp2", "K", "", wOff + 228));
            fields.Add(new(RawFieldType.Primitive, $"Wheel.{w}.optimalTemp", "K", "", wOff + 236));
        }
    }

    // ===== R3E Reader =====
    private Dictionary<string, object?> ReadR3eRaw(string section)
    {
        var result = new Dictionary<string, object?>();
        var buf = ReadMmf(R3E_MMF, 65536);
        if (buf == null) return result;

        try
        {
            var shared = MarshalBuffer<R3eShared>(buf);

            if (section == "physics" || section == "all")
            {
                // AddStructFields can fail when it hits R3eDriverData[] (Marshal may not
                // have populated it at the correct offset). We guard it so the rest
                // of the method — including raw-buffer DriverData — still works.
                try { AddStructFields(shared, "", result); }
                catch (Exception ex) { Console.Error.WriteLine($"[R3E] AddStructFields failed: {ex.Message}"); }

                AddStructFields(shared.Player, "Player", result);
                AddR3eTireValues(shared, result);

                // Read DriverData from raw buffer using game's AllDriversOffset + DriverDataSize
                int numCars = shared.NumCars;
                int driverOff = shared.AllDriversOffset;
                int driverEntrySize = shared.DriverDataSize;

                Console.Error.WriteLine($"[R3E] NumCars={numCars} AllDriversOffset={driverOff} DriverDataSize={driverEntrySize} bufLen={buf.Length}");

                if (numCars > 0 && driverOff > 0 && driverEntrySize > 0
                    && driverOff + numCars * driverEntrySize <= buf.Length)
                {
                    // R3eDriverData — game has 4-byte header (driver ID) before DriverInfo.
                    // All offsets from R3eSharedStruct.cs are +4. Verified from raw hex dump.
                    const int H = 4;
                    // Name[64] @ H+0 = 4
                    // CarNumber @ H+64 = 68
                    // FinishStatus @ H+128 = 132, Place@H+132=136, PlaceClass@H+136=140
                    // LapDistance @ H+140 = 144, TrackSector @ H+160 = 164, CompletedLaps @ H+164 = 168
                    // LapTimeCurrentSelf @ H+172 = 176
                    // SectorTimeCurrentSelf (3 floats) @ H+176 = 180
                    // SectorTimePreviousSelf (3 floats) @ H+188 = 192
                    // SectorTimeBestSelf (3 floats) @ H+200 = 204
                    // TimeDeltaFront @ H+212 = 216, TimeDeltaBehind @ H+216 = 220
                    // PitStopStatus @ H+220 = 224, InPitlane @ H+224 = 228, NumPitstops @ H+228 = 232
                    // Penalties (5 floats) @ H+232 = 236
                    // CarSpeed @ H+252 = 256
                    // TireTypeFront @ H+256 = 260, TireTypeRear @ H+260 = 264
                    // DrsState @ H+280 = 284, PtpState @ H+284 = 288, VirtualEnergy @ H+288 = 292

                    for (int i = 0; i < numCars && i < 128; i++)
                    {
                        int off = driverOff + i * driverEntrySize;
                        string p = $"DriverData[{i}]";
                        result[$"{p}_Name"] = ReadFixedString(buf, off + H + 0, 64);
                        result[$"{p}_CarNumber"] = BitConverter.ToInt32(buf, off + H + 64);
                        result[$"{p}_FinishStatus"] = BitConverter.ToInt32(buf, off + H + 128);
                        result[$"{p}_Place"] = BitConverter.ToInt32(buf, off + H + 132);
                        result[$"{p}_PlaceClass"] = BitConverter.ToInt32(buf, off + H + 136);
                        result[$"{p}_LapDistance"] = SafeFloat(buf, off + H + 140);
                        result[$"{p}_TrackSector"] = BitConverter.ToInt32(buf, off + H + 160);
                        result[$"{p}_CompletedLaps"] = BitConverter.ToInt32(buf, off + H + 164);
                        result[$"{p}_LapTimeCurrentSelf"] = SafeFloat(buf, off + H + 172);
                        result[$"{p}_SectorCurrent_S1"] = SafeFloat(buf, off + H + 176);
                        result[$"{p}_SectorCurrent_S2"] = SafeFloat(buf, off + H + 180);
                        result[$"{p}_SectorCurrent_S3"] = SafeFloat(buf, off + H + 184);
                        result[$"{p}_SectorPrevious_S1"] = SafeFloat(buf, off + H + 188);
                        result[$"{p}_SectorPrevious_S2"] = SafeFloat(buf, off + H + 192);
                        result[$"{p}_SectorPrevious_S3"] = SafeFloat(buf, off + H + 196);
                        result[$"{p}_SectorBest_S1"] = SafeFloat(buf, off + H + 200);
                        result[$"{p}_SectorBest_S2"] = SafeFloat(buf, off + H + 204);
                        result[$"{p}_SectorBest_S3"] = SafeFloat(buf, off + H + 208);
                        result[$"{p}_TimeDeltaFront"] = SafeFloat(buf, off + H + 212);
                        result[$"{p}_TimeDeltaBehind"] = SafeFloat(buf, off + H + 216);
                        result[$"{p}_PitStopStatus"] = BitConverter.ToInt32(buf, off + H + 220);
                        result[$"{p}_InPitlane"] = BitConverter.ToInt32(buf, off + H + 224);
                        result[$"{p}_NumPitstops"] = BitConverter.ToInt32(buf, off + H + 228);
                        result[$"{p}_Penalty_DT"] = SafeFloat(buf, off + H + 232);
                        result[$"{p}_Penalty_SG"] = SafeFloat(buf, off + H + 236);
                        result[$"{p}_Penalty_PS"] = SafeFloat(buf, off + H + 240);
                        result[$"{p}_Penalty_TD"] = SafeFloat(buf, off + H + 244);
                        result[$"{p}_Penalty_SD"] = SafeFloat(buf, off + H + 248);
                        result[$"{p}_CarSpeed"] = SafeFloat(buf, off + H + 252);
                        result[$"{p}_TireTypeFront"] = BitConverter.ToInt32(buf, off + H + 256);
                        result[$"{p}_TireTypeRear"] = BitConverter.ToInt32(buf, off + H + 260);
                        result[$"{p}_DrsState"] = BitConverter.ToInt32(buf, off + H + 280);
                        result[$"{p}_PtpState"] = BitConverter.ToInt32(buf, off + H + 284);
                        result[$"{p}_VirtualEnergy"] = SafeFloat(buf, off + H + 288);
                    }
                }
            }
            if (section == "graphics" || section == "all")
            {
                result["TrackName"] = ByteStr(shared.TrackName);
                result["LayoutName"] = ByteStr(shared.LayoutName);
                result["LayoutLength"] = shared.LayoutLength;
                result["SessionType"] = shared.SessionType;
                result["SessionPhase"] = shared.SessionPhase;
                result["Position"] = shared.Position;
                result["CompletedLaps"] = shared.CompletedLaps;
                result["LapTimeBestSelf"] = shared.LapTimeBestSelf;
                result["LapTimeCurrentSelf"] = shared.LapTimeCurrentSelf;
                result["LapTimePreviousSelf"] = shared.LapTimePreviousSelf;
                result["TimeDeltaFront"] = shared.TimeDeltaFront;
                result["TimeDeltaBehind"] = shared.TimeDeltaBehind;
                result["Flags_Yellow"] = shared.Flags.Yellow;
                result["Flags_Blue"] = shared.Flags.Blue;
                result["Flags_Green"] = shared.Flags.Green;
                result["Flags_Checkered"] = shared.Flags.Checkered;
            }
            if (section == "static" || section == "all")
            {
                result["VersionMajor"] = shared.VersionMajor;
                result["VersionMinor"] = shared.VersionMinor;
                result["GameMode"] = shared.GameMode;
                result["TrackName"] = ByteStr(shared.TrackName);
                result["LayoutName"] = ByteStr(shared.LayoutName);
                result["LayoutLength"] = shared.LayoutLength;
                result["PlayerName"] = ByteStr(shared.PlayerName);
                result["VehicleInfo_Name"] = ByteStr(shared.VehicleInfo.Name);
                result["VehicleInfo_CarNumber"] = shared.VehicleInfo.CarNumber;
                result["VehicleInfo_ClassId"] = shared.VehicleInfo.ClassId;
                result["VehicleInfo_ModelId"] = shared.VehicleInfo.ModelId;
                result["VehicleInfo_ManufacturerId"] = shared.VehicleInfo.ManufacturerId;
                result["VehicleInfo_EngineType"] = shared.VehicleInfo.EngineType;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[R3E Raw] Error reading shared memory: {ex.GetType().Name}: {ex.Message}");
        result["_error_"] = $"ReadR3eRaw exception: {ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private void AddR3eTireValues(R3eShared shared, Dictionary<string, object?> result)
    {
        // Tire data
        result["TireRps_FL"] = shared.TireRps.FrontLeft;
        result["TireRps_FR"] = shared.TireRps.FrontRight;
        result["TireRps_RL"] = shared.TireRps.RearLeft;
        result["TireRps_RR"] = shared.TireRps.RearRight;
        result["TireSpeed_FL"] = shared.TireSpeed.FrontLeft;
        result["TireSpeed_FR"] = shared.TireSpeed.FrontRight;
        result["TireSpeed_RL"] = shared.TireSpeed.RearLeft;
        result["TireSpeed_RR"] = shared.TireSpeed.RearRight;
        result["TireGrip_FL"] = shared.TireGrip.FrontLeft;
        result["TireGrip_FR"] = shared.TireGrip.FrontRight;
        result["TireGrip_RL"] = shared.TireGrip.RearLeft;
        result["TireGrip_RR"] = shared.TireGrip.RearRight;
        result["TireWear_FL"] = shared.TireWear.FrontLeft;
        result["TireWear_FR"] = shared.TireWear.FrontRight;
        result["TireWear_RL"] = shared.TireWear.RearLeft;
        result["TireWear_RR"] = shared.TireWear.RearRight;
        result["TirePressure_FL"] = shared.TirePressure.FrontLeft;
        result["TirePressure_FR"] = shared.TirePressure.FrontRight;
        result["TirePressure_RL"] = shared.TirePressure.RearLeft;
        result["TirePressure_RR"] = shared.TirePressure.RearRight;
        result["TireDirt_FL"] = shared.TireDirt.FrontLeft;
        result["TireDirt_FR"] = shared.TireDirt.FrontRight;
        result["TireDirt_RL"] = shared.TireDirt.RearLeft;
        result["TireDirt_RR"] = shared.TireDirt.RearRight;
        result["TireLoad_FL"] = shared.TireLoad.FrontLeft;
        result["TireLoad_FR"] = shared.TireLoad.FrontRight;
        result["TireLoad_RL"] = shared.TireLoad.RearLeft;
        result["TireLoad_RR"] = shared.TireLoad.RearRight;

        // Tire temps
        result["TireTemp_FL_Current"] = shared.TireTemp.FrontLeft.CurrentTemp;
        result["TireTemp_FL_Optimal"] = shared.TireTemp.FrontLeft.OptimalTemp;
        result["TireTemp_FR_Current"] = shared.TireTemp.FrontRight.CurrentTemp;
        result["TireTemp_FR_Optimal"] = shared.TireTemp.FrontRight.OptimalTemp;
        result["TireTemp_RL_Current"] = shared.TireTemp.RearLeft.CurrentTemp;
        result["TireTemp_RL_Optimal"] = shared.TireTemp.RearLeft.OptimalTemp;
        result["TireTemp_RR_Current"] = shared.TireTemp.RearRight.CurrentTemp;
        result["TireTemp_RR_Optimal"] = shared.TireTemp.RearRight.OptimalTemp;

        // Brake temps
        result["BrakeTemp_FL_Current"] = shared.BrakeTemp.FrontLeft.CurrentTemp;
        result["BrakeTemp_FR_Current"] = shared.BrakeTemp.FrontRight.CurrentTemp;
        result["BrakeTemp_RL_Current"] = shared.BrakeTemp.RearLeft.CurrentTemp;
        result["BrakeTemp_RR_Current"] = shared.BrakeTemp.RearRight.CurrentTemp;

        // Brake pressures
        result["BrakePressure_FL"] = shared.BrakePressure.FrontLeft;
        result["BrakePressure_FR"] = shared.BrakePressure.FrontRight;
        result["BrakePressure_RL"] = shared.BrakePressure.RearLeft;
        result["BrakePressure_RR"] = shared.BrakePressure.RearRight;
    }

    private static void ExploreR3eTireFields(List<RawFieldInfo> fields)
    {
        // R3eTireData<float> has FL, FR, RL, RR — each float (4 bytes)
        // R3eTireData<R3eTireTempInformation> has 4 × sizeof(R3eTireTempInformation)
        // R3eTireData<R3eBrakeTemp> has 4 × sizeof(R3eBrakeTemp)
        // These are nested inside R3eShared — calculate offsets via CalcOffset

        string[] wheelLabels = ["FL", "FR", "RL", "RR"];
        var root = typeof(R3eShared);

        string[] tireScalars = ["TireRps", "TireSpeed", "TireGrip", "TireWear",
            "TirePressure", "TireDirt", "TireLoad", "BrakePressure"];
        foreach (var t in tireScalars)
            foreach (var w in wheelLabels)
                fields.Add(new(RawFieldType.Float, $"{t}_{w}", "", $"{t}.{w}",
                    CalcOffset(root, $"{t}.{w}")));

        foreach (var w in wheelLabels)
        {
            fields.Add(new(RawFieldType.Nested, $"TireTemp_{w}", "", $"TireTemp.{w}",
                CalcOffset(root, $"TireTemp.{w}")));
            fields.Add(new(RawFieldType.Float, $"TireTemp_{w}_Current", "C", $"TireTemp.{w}.CurrentTemp",
                CalcOffset(root, $"TireTemp.{w}.CurrentTemp")));
            fields.Add(new(RawFieldType.Float, $"TireTemp_{w}_Optimal", "C", $"TireTemp.{w}.OptimalTemp",
                CalcOffset(root, $"TireTemp.{w}.OptimalTemp")));
            fields.Add(new(RawFieldType.Float, $"BrakeTemp_{w}_Current", "C", $"BrakeTemp.{w}.CurrentTemp",
                CalcOffset(root, $"BrakeTemp.{w}.CurrentTemp")));
        }
    }

    // ===== EVO Reader =====
    private Dictionary<string, object?> ReadEvoRaw(string section)
    {
        var result = new Dictionary<string, object?>();

        if (section == "physics" || section == "all")
        {
            var buf = ReadMmf(EVO_PHYSICS, 4096);
            if (buf != null)
            {
                try { AddStructFields(MarshalBuffer<SPageFilePhysicsEvo>(buf), "", result); }
                catch (Exception ex) { Console.Error.WriteLine($"[EVO Phys] AddStructFields: {ex.GetType().Name}: {ex.Message}"); result["_phys_error"] = ex.Message; }
            }
        }
        if (section == "graphics" || section == "all")
        {
            var buf = ReadMmf(EVO_GRAPHICS, 65536);
            if (buf != null)
            {
                // Raw-buffer diagnosis: read key fields directly from the MMF buffer
                try
                {
                    // SPageFileGraphicEvo fields and their known offsets (verify by reading raw bytes)
                    var gfxType = typeof(SPageFileGraphicEvo);
                    int offAC = (int)Marshal.OffsetOf(gfxType, "ActiveCars");
                    int offTD = (int)Marshal.OffsetOf(gfxType, "TotalDrivers");
                    int offCP = (int)Marshal.OffsetOf(gfxType, "CurrentPos");
                    int offCC = (int)Marshal.OffsetOf(gfxType, "CarCoordinates");
                    result["_off_ActiveCars"] = offAC;
                    result["_off_TotalDrivers"] = offTD;
                    result["_off_CurrentPos"] = offCP;
                    result["_off_CarCoords"] = offCC;
                    if (buf.Length > offAC + 4)
                        result["_raw_ActiveCars"] = buf[offAC];
                    if (buf.Length > offTD + 4)
                        result["_raw_TotalDrivers"] = BitConverter.ToUInt32(buf, offTD);
                    if (buf.Length > offCP + 4)
                        result["_raw_CurrentPos"] = BitConverter.ToUInt32(buf, offCP);
                    if (buf.Length > offCC + 20)
                    {
                        result["_raw_CC0_X"] = BitConverter.ToSingle(buf, offCC);
                        result["_raw_CC0_Y"] = BitConverter.ToSingle(buf, offCC + 4);
                        result["_raw_CC0_Z"] = BitConverter.ToSingle(buf, offCC + 8);
                    }
                }
                catch (Exception ex) { result["_off_error"] = $"{ex.GetType().Name}: {ex.Message}"; }

                try { AddStructFields(MarshalBuffer<SPageFileGraphicEvo>(buf), "", result); }
                catch (Exception ex) { Console.Error.WriteLine($"[EVO Gfx] AddStructFields: {ex.GetType().Name}: {ex.Message}"); result["_gfx_error"] = ex.Message; }
            }
            else
            {
                result["_gfx_error"] = "MMF null";
            }
        }
        if (section == "static" || section == "all")
        {
            var buf = ReadMmf(EVO_STATIC, 4096);
            if (buf != null)
            {
                try { AddStructFields(MarshalBuffer<SPageFileStaticEvo>(buf), "", result); }
                catch (Exception ex) { Console.Error.WriteLine($"[EVO St] AddStructFields: {ex.GetType().Name}: {ex.Message}"); result["_st_error"] = ex.Message; }
            }
        }

        return result;
    }

    // ===== AC1 Reader =====
    private Dictionary<string, object?> ReadAc1Raw(string section)
    {
        var result = new Dictionary<string, object?>();

        if (section == "physics" || section == "all")
        {
            var buf = ReadMmf(AC1_PHYSICS, 4096);
            if (buf != null)
            {
                try { AddStructFields(MarshalBuffer<SPageFilePhysicsAC>(buf), "", result); }
                catch { }
            }
        }
        if (section == "graphics" || section == "all")
        {
            var buf = ReadMmf(AC1_GRAPHICS, 4096);
            if (buf != null)
            {
                try { AddStructFields(MarshalBuffer<SPageFileGraphicAC>(buf), "", result); }
                catch { }
            }
        }
        if (section == "static" || section == "all")
        {
            var buf = ReadMmf(AC1_STATIC, 4096);
            if (buf != null)
            {
                try { AddStructFields(MarshalBuffer<SPageFileStaticAC>(buf), "", result); }
                catch { }
            }
        }

        return result;
    }

    // ===== LMU Reader =====
    private const int LmuTelemHeaderOff = 128464;
    private const int LmuTelemInfoOff = 128468;
    private const int LmuTelemStride = 1888;
    private const int LmuWheelBaseOff = 848;
    private const int LmuWheelStride = 260;
    private const int LmuScoringOff = 1628;   // LmuScoringInfoV01
    private const int LmuVehScoringOff = 2192; // LmuVehScoringInfoV01[0]
    private const int LmuVehStride = 584;
    private const int LmuMaxVehicles = 104;

    // ===== ACC Reader =====
    private Dictionary<string, object?> ReadAccRaw(string section)
    {
        var result = new Dictionary<string, object?>();

        // ACC uses its own struct layout — completely different from AC1.
        // Read each field from the raw MMF buffer at documented ACC offsets.

        if (section == "physics" || section == "all")
        {
            var buf = ReadMmf(ACC_PHYSICS, 4096);
            if (buf != null) ReadAccPhysics(buf, result);
            else result["_phys_missing"] = ACC_PHYSICS;
        }
        if (section == "graphics" || section == "all")
        {
            var buf = ReadMmf(ACC_GRAPHICS, 131072);
            if (buf != null) ReadAccGraphics(buf, result);
            else result["_gfx_missing"] = ACC_GRAPHICS;
        }
        if (section == "static" || section == "all")
        {
            var buf = ReadMmf(ACC_STATIC, 4096);
            if (buf != null) ReadAccStatic(buf, result);
            else result["_st_missing"] = ACC_STATIC;
        }

        return result;
    }

    private void ReadAccPhysics(byte[] buf, Dictionary<string, object?> r)
    {
        // ACC physics struct — offsets verified from Rust parser (acc_shared_memory_rs)
        r["PacketId"] = BitConverter.ToInt32(buf, 0);
        r["Gas"] = SafeFloat(buf, 4);
        r["Brake"] = SafeFloat(buf, 8);
        r["Fuel"] = SafeFloat(buf, 12);
        r["Gear"] = BitConverter.ToInt32(buf, 16);
        r["Rpms"] = BitConverter.ToInt32(buf, 20);
        r["SteerAngle"] = SafeFloat(buf, 24);
        r["SpeedKmh"] = SafeFloat(buf, 28);
        for (int i = 0; i < 3; i++) r[$"Velocity_{i}"] = SafeFloat(buf, 32 + i * 4);
        for (int i = 0; i < 3; i++) r[$"AccG_{i}"] = SafeFloat(buf, 44 + i * 4);
        for (int i = 0; i < 4; i++) r[$"WheelSlip_{i}"] = SafeFloat(buf, 56 + i * 4);
        for (int i = 0; i < 4; i++) r[$"WheelLoad_{i}"] = SafeFloat(buf, 72 + i * 4);
        for (int i = 0; i < 4; i++) r[$"WheelsPressure_{i}"] = SafeFloat(buf, 88 + i * 4);
        for (int i = 0; i < 4; i++) r[$"WheelAngularSpeed_{i}"] = SafeFloat(buf, 104 + i * 4);
        for (int i = 0; i < 4; i++) r[$"TyreWear_{i}"] = SafeFloat(buf, 120 + i * 4);
        for (int i = 0; i < 4; i++) r[$"TyreDirtyLevel_{i}"] = SafeFloat(buf, 136 + i * 4);
        for (int i = 0; i < 4; i++) r[$"TyreCoreTemperature_{i}"] = SafeFloat(buf, 152 + i * 4);
        for (int i = 0; i < 4; i++) r[$"CamberRad_{i}"] = SafeFloat(buf, 168 + i * 4);
        for (int i = 0; i < 4; i++) r[$"SuspensionTravel_{i}"] = SafeFloat(buf, 184 + i * 4);
        r["Drs"] = BitConverter.ToInt32(buf, 200);
        r["Tc"] = SafeFloat(buf, 204);
        r["Heading"] = SafeFloat(buf, 208);
        r["Pitch"] = SafeFloat(buf, 212);
        r["Roll"] = SafeFloat(buf, 216);
        r["CgHeight"] = SafeFloat(buf, 220);
        for (int i = 0; i < 5; i++) r[$"CarDamage_{i}"] = SafeFloat(buf, 224 + i * 4);
        r["NumberOfTyresOut"] = BitConverter.ToInt32(buf, 244);
        r["PitLimiterOn"] = BitConverter.ToInt32(buf, 248);
        r["Abs"] = SafeFloat(buf, 252);
        r["KersCharge"] = SafeFloat(buf, 256);
        r["KersInput"] = SafeFloat(buf, 260);
        r["AutoShifterOn"] = BitConverter.ToInt32(buf, 264);
        for (int i = 0; i < 2; i++) r[$"RideHeight_{i}"] = SafeFloat(buf, 268 + i * 4);
        r["TurboBoost"] = SafeFloat(buf, 276);
        r["Ballast"] = SafeFloat(buf, 280);
        r["AirDensity"] = SafeFloat(buf, 284);
        r["AirTemp"] = SafeFloat(buf, 288);
        r["RoadTemp"] = SafeFloat(buf, 292);
        for (int i = 0; i < 3; i++) r[$"LocalAngularVelocity_{i}"] = SafeFloat(buf, 296 + i * 4);
        r["FinalFF"] = SafeFloat(buf, 308);
        r["PerformanceMeter"] = SafeFloat(buf, 312);
        r["EngineBrake"] = BitConverter.ToInt32(buf, 316);
        r["ErsRecoveryLevel"] = BitConverter.ToInt32(buf, 320);
        r["ErsPowerLevel"] = BitConverter.ToInt32(buf, 324);
        r["ErsHeatCharging"] = BitConverter.ToInt32(buf, 328);
        r["ErsIsCharging"] = BitConverter.ToInt32(buf, 332);
        r["KersCurrentKJ"] = SafeFloat(buf, 336);
        r["DrsAvailable"] = BitConverter.ToInt32(buf, 340);
        r["DrsEnabled"] = BitConverter.ToInt32(buf, 344);
        for (int i = 0; i < 4; i++) r[$"BrakeTemp_{i}"] = SafeFloat(buf, 348 + i * 4);
        r["Clutch"] = SafeFloat(buf, 364);
        for (int i = 0; i < 4; i++) r[$"TyreTempI_{i}"] = SafeFloat(buf, 368 + i * 4);
        for (int i = 0; i < 4; i++) r[$"TyreTempM_{i}"] = SafeFloat(buf, 384 + i * 4);
        for (int i = 0; i < 4; i++) r[$"TyreTempO_{i}"] = SafeFloat(buf, 400 + i * 4);
        r["IsAiControlled"] = BitConverter.ToInt32(buf, 416);
        r["BrakeBias"] = SafeFloat(buf, 564);
        for (int i = 0; i < 4; i++) r[$"SlipRatio_{i}"] = SafeFloat(buf, 640 + i * 4);
        for (int i = 0; i < 4; i++) r[$"SlipAngle_{i}"] = SafeFloat(buf, 656 + i * 4);
        for (int i = 0; i < 4; i++) r[$"SuspensionDamage_{i}"] = SafeFloat(buf, 680 + i * 4);
        r["WaterTemp"] = SafeFloat(buf, 712);
        for (int i = 0; i < 4; i++) r[$"BrakePressure_{i}"] = SafeFloat(buf, 716 + i * 4);
        for (int i = 0; i < 4; i++) r[$"PadLife_{i}"] = SafeFloat(buf, 740 + i * 4);
        for (int i = 0; i < 4; i++) r[$"DiscLife_{i}"] = SafeFloat(buf, 756 + i * 4);
        r["IgnitionOn"] = BitConverter.ToInt32(buf, 772);
        r["StarterEngineOn"] = BitConverter.ToInt32(buf, 776);
        r["IsEngineRunning"] = BitConverter.ToInt32(buf, 780);
        r["KerbVibration"] = SafeFloat(buf, 784);
        r["SlipVibrations"] = SafeFloat(buf, 788);
        r["RoadVibrations"] = SafeFloat(buf, 792);
        r["AbsVibrations"] = SafeFloat(buf, 796);
    }

    private void ReadAccGraphics(byte[] buf, Dictionary<string, object?> r)
    {
        // Use Marshal.PtrToStructure with correct ACC struct definitions
        var gfx = MarshalBuffer<AccGraphics>(buf);
        r["PacketId"] = gfx.PacketId;
        r["Status"] = gfx.Status;
        r["SessionType"] = gfx.Session;
        r["CompletedLaps"] = gfx.CompletedLaps;
        r["Position"] = gfx.Position;
        r["CurrentTime"] = gfx.CurrentTime;
        r["LastTime"] = gfx.LastTime;
        r["BestTime"] = gfx.BestTime;
        r["SessionTimeLeft"] = gfx.SessionTimeLeft;
        r["DistanceTraveled"] = gfx.DistanceTraveled;
        r["IsInPit"] = gfx.IsInPit;
        r["NumberOfLaps"] = gfx.NumberOfLaps;
        r["TyreCompound"] = gfx.TyreCompound;
        r["NormalizedCarPosition"] = gfx.NormalizedCarPosition;
        r["ActiveCars"] = gfx.ActiveCars;

        for (int ci = 0; ci < 60; ci++)
        {
            var c = gfx.CarCoordinates[ci];
            r[$"CarCoord_{ci}_X"] = c.X;
            r[$"CarCoord_{ci}_Y"] = c.Y;
            r[$"CarCoord_{ci}_Z"] = c.Z;
            r[$"AccCarId_{ci}"] = gfx.CarIDs[ci];
        }

        r["PlayerCarId"] = gfx.PlayerCarID;
        r["PenaltyTime"] = gfx.PenaltyTime;
        r["Flag"] = gfx.Flag;
        r["WindSpeed"] = gfx.WindSpeed;
        r["WindDirection"] = gfx.WindDirection;
        r["TrackStatus"] = gfx.TrackStatus;
        r["RainTyres"] = gfx.RainTyres;
        r["FuelEstimatedLaps"] = gfx.FuelEstimatedLaps;
        r["SessionIndex"] = gfx.SessionIndex;
        r["Clock"] = gfx.Clock;
    }

    private void ReadAccStatic(byte[] buf, Dictionary<string, object?> r)
    {
        // ACC static struct — read from raw buffer
        r["SmVersion"] = ReadFixedString(buf, 0, 15);
        r["AcVersion"] = ReadFixedString(buf, 15, 15);
        r["NumberOfSessions"] = BitConverter.ToInt32(buf, 60);
        r["NumCars"] = BitConverter.ToInt32(buf, 64);
        r["CarModel"] = ReadFixedString(buf, 68, 33);
        r["Track"] = ReadFixedString(buf, 101, 33);
        r["PlayerName"] = ReadFixedString(buf, 134, 33);
        r["PlayerSurname"] = ReadFixedString(buf, 167, 33);
        r["PlayerNick"] = ReadFixedString(buf, 200, 33);
        r["MaxRpm"] = BitConverter.ToInt32(buf, 268);
        r["MaxFuel"] = SafeFloat(buf, 272);
        for (int i = 0; i < 4; i++) { r[$"SuspensionMaxTravel_{i}"] = SafeFloat(buf, 276 + i * 4); }
        for (int i = 0; i < 4; i++) { r[$"TyreRadius_{i}"] = SafeFloat(buf, 292 + i * 4); }
        r["MaxTurboBoost"] = SafeFloat(buf, 308);
        r["TrackConfiguration"] = ReadFixedString(buf, 340, 33);
    }

    private static void ExploreAccPhysicsFields(List<RawFieldInfo> fields)
    {
        int o = 0;
        fields.Add(new(RawFieldType.Primitive, "PacketId", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Gas", "%", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Brake", "%", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Fuel", "L", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Gear", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Rpms", "rpm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SteerAngle", "rad", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SpeedKmh", "km/h", "", o)); o += 4;
        fields.Add(new(RawFieldType.Array, "Velocity", "m/s", "", o)); o += 12;
        fields.Add(new(RawFieldType.Array, "AccG", "m/s2", "", o)); o += 12;
        fields.Add(new(RawFieldType.Array, "WheelSlip", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "WheelLoad", "N", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "WheelsPressure", "psi", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "WheelAngularSpeed", "rad/s", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "TyreWear", "0-1", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "TyreDirtyLevel", "0-1", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "TyreCoreTemperature", "C", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "CamberRad", "rad", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "SuspensionTravel", "m", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "CarDamage", "0-1", "", o)); o += 16;
        fields.Add(new(RawFieldType.Primitive, "NumberOfTyresOut", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "PitLimiterOn", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Abs", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "KersCharge", "J", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "KersInput", "J", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "AutoShifterOn", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Array, "RideHeight", "m", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "TurboBoost", "bar", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Ballast", "kg", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "AirDensity", "kg/m3", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "AirTemp", "C", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "RoadTemp", "C", "", o)); o += 4;
        fields.Add(new(RawFieldType.Array, "LocalAngularVelocity", "rad/s", "", o)); o += 12;
        fields.Add(new(RawFieldType.Primitive, "FinalFF", "Nm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "PerformanceMeter", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "EngineBrake", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "ErsRecoveryLevel", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "ErsPowerLevel", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "KersCurrentKJ", "kJ", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Drs", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "TcLevel", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "AbsLevel", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "FuelMix", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "FuelPerLap", "L", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "DrsAllowed", "", "", o)); o += 4;
    }

    private static void ExploreAccGraphicsFields(List<RawFieldInfo> fields)
    {
        int o = 0;
        fields.Add(new(RawFieldType.Primitive, "PacketId", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Status", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Session", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.String, "CurrentTime", "", "", o)); o += 15;
        fields.Add(new(RawFieldType.String, "LastTime", "", "", o)); o += 15;
        fields.Add(new(RawFieldType.String, "BestTime", "", "", o)); o += 15;
        fields.Add(new(RawFieldType.String, "Split", "", "", o)); o += 15;
        fields.Add(new(RawFieldType.Primitive, "CompletedLaps", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Position", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SessionTimeLeft", "s", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "DistanceTraveled", "m", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "IsInPit", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Gas", "%", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Brake", "%", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Gear", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Rpms", "rpm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SteerAngle", "rad", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SpeedKmh", "km/h", "", o)); o += 4;
        fields.Add(new(RawFieldType.Array, "Velocity", "", "", o)); o += 12;
        fields.Add(new(RawFieldType.Array, "AccG", "", "", o)); o += 12;
        fields.Add(new(RawFieldType.Array, "WheelSlip", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "WheelLoad", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "WheelsPressure", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "TyreWear", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "TyreCoreTemperature", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "SuspensionTravel", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Array, "CarDamage", "", "", o)); o += 16;
        fields.Add(new(RawFieldType.Primitive, "NumberOfTyresOut", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Nested, "CarCoordinates[60]", "", "", 1344));
    }

    private static void ExploreAccStaticFields(List<RawFieldInfo> fields)
    {
        fields.Add(new(RawFieldType.String, "SmVersion", "", "", 0));
        fields.Add(new(RawFieldType.String, "AcVersion", "", "", 15));
        fields.Add(new(RawFieldType.Primitive, "NumberOfSessions", "", "", 60));
        fields.Add(new(RawFieldType.Primitive, "NumCars", "", "", 64));
        fields.Add(new(RawFieldType.String, "CarModel", "", "", 68));
        fields.Add(new(RawFieldType.String, "Track", "", "", 101));
        fields.Add(new(RawFieldType.String, "PlayerName", "", "", 134));
        fields.Add(new(RawFieldType.String, "PlayerSurname", "", "", 167));
        fields.Add(new(RawFieldType.String, "PlayerNick", "", "", 200));
        fields.Add(new(RawFieldType.Primitive, "MaxRpm", "rpm", "", 268));
        fields.Add(new(RawFieldType.Primitive, "MaxFuel", "L", "", 272));
        fields.Add(new(RawFieldType.Array, "SuspensionMaxTravel", "m", "", 276));
        fields.Add(new(RawFieldType.Array, "TyreRadius", "m", "", 292));
        fields.Add(new(RawFieldType.Primitive, "MaxTurboBoost", "bar", "", 308));
        fields.Add(new(RawFieldType.String, "TrackConfiguration", "", "", 340));
    }

    // ===== rFactor 2 =====
    private static void ExploreRf2Fields(List<RawFieldInfo> fields)
    {
        int o = 0;
        fields.Add(new(RawFieldType.Primitive, "DeltaTime", "s", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "ElapsedTime", "s", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "LapNumber", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "LapStartET", "s", "", o)); o += 4;
        fields.Add(new(RawFieldType.String, "VehicleName", "", "", o)); o += 64;
        fields.Add(new(RawFieldType.String, "TrackName", "", "", o)); o += 64;
        fields.Add(new(RawFieldType.Primitive, "PosX", "m", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "PosY", "m", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "PosZ", "m", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalVelX", "m/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalVelY", "m/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalVelZ", "m/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalAccelX", "m/s2", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalAccelY", "m/s2", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalAccelZ", "m/s2", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalRotX", "rad/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalRotY", "rad/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "LocalRotZ", "rad/s", "", o)); o += 8;
        fields.Add(new(RawFieldType.Primitive, "Gear", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "EngineRPM", "rpm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "EngineWaterTemp", "C", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "EngineOilTemp", "C", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "ClutchRPM", "rpm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "UnfilteredThrottle", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "UnfilteredBrake", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "UnfilteredSteering", "", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "SteeringShaftTorque", "Nm", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "FrontRideHeight", "m", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "RearRideHeight", "m", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "Fuel", "L", "", o)); o += 4;
        fields.Add(new(RawFieldType.Primitive, "EngineMaxRPM", "rpm", "", o)); o += 4;
    }

    private Dictionary<string, object?> ReadRf2Raw(string section)
    {
        var result = new Dictionary<string, object?>();
        var buf = ReadMmf(RF2_MMF, 65536);
        if (buf == null) { result["_error"] = "MMF not found"; return result; }

        // rFactor 2 TelemInfoV1 layout — read key fields from raw buffer
        result["DeltaTime"] = SafeFloat(buf, 0);
        result["ElapsedTime"] = SafeFloat(buf, 4);
        result["LapNumber"] = BitConverter.ToInt32(buf, 8);
        result["VehicleName"] = ReadFixedString(buf, 16, 64);
        result["TrackName"] = ReadFixedString(buf, 80, 64);
        for (int i = 0; i < 3; i++)
        {
            result[$"Pos{i}"] = BitConverter.ToDouble(buf, 144 + i * 8);
            result[$"LocalVel{i}"] = BitConverter.ToDouble(buf, 168 + i * 8);
            result[$"LocalAccel{i}"] = BitConverter.ToDouble(buf, 192 + i * 8);
            result[$"LocalRot{i}"] = BitConverter.ToDouble(buf, 216 + i * 8);
        }
        result["Gear"] = BitConverter.ToInt32(buf, 240);
        result["EngineRPM"] = SafeFloat(buf, 244);
        result["EngineWaterTemp"] = SafeFloat(buf, 248);
        result["UnfilteredThrottle"] = SafeFloat(buf, 260);
        result["UnfilteredBrake"] = SafeFloat(buf, 264);
        result["UnfilteredSteering"] = SafeFloat(buf, 268);
        result["SteeringShaftTorque"] = SafeFloat(buf, 276);
        result["FrontRideHeight"] = SafeFloat(buf, 288);
        result["RearRideHeight"] = SafeFloat(buf, 292);
        result["Fuel"] = SafeFloat(buf, 312);
        result["EngineMaxRPM"] = SafeFloat(buf, 316);

        return result;
    }

    public Dictionary<string, object?> ReadRf2Opponents()
    {
        var result = new Dictionary<string, object?>();
        var buf = ReadMmf(RF2_MMF, 65536);
        if (buf == null) { result["_error"] = "MMF not found"; return result; }

        // rF2 has a scoring section starting at a known offset (similar to LMU)
        // Player telemetry is at offset 0 (single struct, not an array like LMU)
        result["VehicleName"] = ReadFixedString(buf, 16, 64);
        result["TrackName"] = ReadFixedString(buf, 80, 64);
        result["LapNumber"] = BitConverter.ToInt32(buf, 8);
        result["Gear"] = BitConverter.ToInt32(buf, 240);
        result["EngineRPM"] = SafeFloat(buf, 244);
        result["Speed"] = Math.Sqrt(
            BitConverter.ToDouble(buf, 168) * BitConverter.ToDouble(buf, 168) +
            BitConverter.ToDouble(buf, 176) * BitConverter.ToDouble(buf, 176) +
            BitConverter.ToDouble(buf, 184) * BitConverter.ToDouble(buf, 184));
        result["Fuel"] = SafeFloat(buf, 312);
        result["SteeringTorque"] = SafeFloat(buf, 276);

        // Car position
        result["PosX"] = BitConverter.ToDouble(buf, 144);
        result["PosY"] = BitConverter.ToDouble(buf, 152);
        result["PosZ"] = BitConverter.ToDouble(buf, 160);

        var entries = new List<Dictionary<string, object?>>();
        entries.Add(new Dictionary<string, object?>
        {
            ["posX"] = BitConverter.ToDouble(buf, 144),
            ["posY"] = BitConverter.ToDouble(buf, 152),
            ["posZ"] = BitConverter.ToDouble(buf, 160),
            ["gear"] = BitConverter.ToInt32(buf, 240),
            ["rpm"] = SafeFloat(buf, 244),
            ["name"] = ReadFixedString(buf, 16, 64),
            ["trackName"] = ReadFixedString(buf, 80, 64),
            ["laps"] = BitConverter.ToInt32(buf, 8),
            ["index"] = 0
        });

        result["entries"] = entries;
        result["_count"] = entries.Count;
        return result;
    }

    public Dictionary<string, object?>? ReadAccGraphicsOpponents()
    {
        var gfxValues = ReadRawValues("assettocorsac", "graphics");
        var entries = new List<Dictionary<string, object?>>();
        int oppEntryOff = gfxValues.TryGetValue("_oppEntryOff", out var oeo) ? Convert.ToInt32(oeo) : 0;

        // Strategy data from graphics struct
        var metaFields = new Dictionary<string, string>
        {
            ["ActiveCars"] = "activeCars",
            ["PlayerCarId"] = "playerCarId",
            ["SessionType"] = "sessionType",
            ["CompletedLaps"] = "completedLaps",
            ["NumberOfLaps"] = "totalLaps",
            ["Position"] = "playerPosition",
            ["TyreCompound"] = "tyreCompound",
            ["SessionTimeLeft"] = "sessionTimeLeft",
            ["NormalizedCarPosition"] = "trackPos",
            ["PenaltyTime"] = "penaltyTime",
            ["FuelEstimatedLaps"] = "fuelEstLaps",
            ["UsedFuel"] = "usedFuel",
            ["FuelXLap"] = "fuelPerLap",
            ["WindSpeed"] = "windSpeed",
            ["WindDirection"] = "windDirection",
            ["RainTyres"] = "rainTyres",
            ["TrackStatus"] = "trackStatus",
            ["GapAhead"] = "gapAhead",
            ["GapBehind"] = "gapBehind",
            ["Clock"] = "clock",
        };
        foreach (var (k, v) in metaFields)
            if (gfxValues.TryGetValue(k, out var val)) gfxValues[v] = val;

        // Build per-car entries from coordinates
        for (int i = 0; i < 60; i++)
        {
            bool hasX = gfxValues.TryGetValue($"CarCoord_{i}_X", out var x);
            gfxValues.TryGetValue($"CarCoord_{i}_Y", out var y);
            gfxValues.TryGetValue($"CarCoord_{i}_Z", out var z);
            if (!hasX) break;
            float fx = x is float f ? f : 0f;
            float fy = y is float ff ? ff : 0f;
            float fz = z is float fff ? fff : 0f;
            if (Math.Abs(fx) > 0.1f || Math.Abs(fy) > 0.1f || Math.Abs(fz) > 0.1f)
            {
                gfxValues.TryGetValue($"AccCarId_{i}", out var cid);
                var entry = new Dictionary<string, object?>
                {
                    ["posX"] = fx, ["posY"] = fy, ["posZ"] = fz,
                    ["index"] = i,
                    ["carId"] = cid ?? i
                };
                entries.Add(entry);
            }
        }
        gfxValues["_count"] = entries.Count;
        gfxValues["entries"] = entries;
        return gfxValues;
    }

    public Dictionary<string, object?> ReadAccOpponents()
    {
        var result = new Dictionary<string, object?>();
        byte[]? buf = null;
        string foundName = "";
        string lastError = "";
        foreach (var name in ACC_OPPONENT_NAMES)
        {
            try
            {
                using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(name, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                using var view = mmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                int size = (int)Math.Min(view.Capacity, 131072L);
                buf = new byte[size];
                view.ReadArray(0, buf, 0, size);
                foundName = name;
                break;
            }
            catch (Exception ex) { lastError = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        if (buf == null)
        {
            result["_error"] = $"MMF not found (tried {ACC_OPPONENT_NAMES.Length} names, last: {lastError})";
            return result;
        }
        result["_mmfName"] = foundName;

        try
        {
            int packetId = BitConverter.ToInt32(buf, 0);
            result["_packetId"] = packetId;
            // ACC opponent entry size ~340 bytes per public API
            const int oppStride = 340;
            const int maxOpps = 60;
            const int headerSize = 4;

            // Also dump hex of first entry for diagnosis
            var hexDump = new System.Text.StringBuilder();
            for (int dumpI = 0; dumpI < 1 && dumpI < maxOpps; dumpI++)
            {
                int entryBase = headerSize + dumpI * oppStride;
                hexDump.AppendLine($"--- Opponent[{dumpI}] raw bytes ({oppStride}B @{entryBase}) ---");
                for (int row = 0; row < Math.Min(128, oppStride); row += 16)
                {
                    hexDump.Append($"  {entryBase + row:X4}  ");
                    for (int col = 0; col < 16 && row + col < oppStride; col++)
                        hexDump.Append($"{buf[entryBase + row + col]:X2} ");
                    hexDump.Append("  ");
                    for (int col = 0; col < 16 && row + col < oppStride; col++)
                    {
                        byte b = buf[entryBase + row + col];
                        hexDump.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    hexDump.AppendLine();
                }
            }
            result["_oppHexDump"] = hexDump.ToString();

            int oppCount = 0;
            for (int i = 0; i < maxOpps; i++)
            {
                int baseOff = headerSize + i * oppStride;
                if (baseOff + 4 > buf.Length) break;

                // Check if this slot is occupied — look for non-zero carModel string
                bool occupied = false;
                for (int j = 0; j < 32; j++)
                {
                    if (buf[baseOff + j] != 0) { occupied = true; break; }
                }
                if (!occupied) continue;
                oppCount++;

                string p = $"Opp[{i}]";
                result[$"{p}_carModel"] = ReadFixedString(buf, baseOff + 0, 33);
                result[$"{p}_driverName"] = ReadFixedString(buf, baseOff + 33, 33);
                result[$"{p}_driverSurname"] = ReadFixedString(buf, baseOff + 66, 33);
                result[$"{p}_team"] = ReadFixedString(buf, baseOff + 99, 33);
                result[$"{p}_nationality"] = ReadFixedString(buf, baseOff + 132, 17);
                result[$"{p}_currentSector"] = BitConverter.ToInt32(buf, baseOff + 152);
                result[$"{p}_totalLaps"] = BitConverter.ToInt32(buf, baseOff + 156);
                result[$"{p}_bestLapTime"] = SafeFloat(buf, baseOff + 164);
                result[$"{p}_lastLapTime"] = SafeFloat(buf, baseOff + 168);
                result[$"{p}_curLapTime"] = SafeFloat(buf, baseOff + 172);
                result[$"{p}_sector1Time"] = SafeFloat(buf, baseOff + 176);
                result[$"{p}_sector2Time"] = SafeFloat(buf, baseOff + 180);
                result[$"{p}_carPosX"] = SafeFloat(buf, baseOff + 184);
                result[$"{p}_carPosY"] = SafeFloat(buf, baseOff + 188);
                result[$"{p}_carPosZ"] = SafeFloat(buf, baseOff + 192);
                result[$"{p}_timeBehindLeader"] = SafeFloat(buf, baseOff + 280);
                result[$"{p}_timeBehindNext"] = SafeFloat(buf, baseOff + 284);
            }
            result["_oppCount"] = oppCount;
        }
        catch (Exception ex)
        {
            result["_error"] = $"ReadAccOpponents: {ex.GetType().Name}: {ex.Message}";
        }

        return result;
    }

    private Dictionary<string, object?> ReadLmuRaw(string section)
    {
        var result = new Dictionary<string, object?>();

        var buf = ReadMmf(LMU_MMF, 320000);
        if (buf == null) return result;

        // --- Header section (LmuSharedMemoryGeneric at offset 0) ---
        if (section == "header" || section == "all")
        {
            result["H_gameVersion"] = BitConverter.ToInt32(buf, 60);
            result["H_ffbTorque"] = BitConverter.ToSingle(buf, 64);
            result["H_hwnd"] = BitConverter.ToInt64(buf, 68);
            result["H_width"] = BitConverter.ToUInt32(buf, 76);
            result["H_height"] = BitConverter.ToUInt32(buf, 80);
            result["H_refreshRate"] = BitConverter.ToUInt32(buf, 84);
            result["H_windowed"] = BitConverter.ToUInt32(buf, 88);
        }

        // --- Scoring section (LmuScoringInfoV01 + veh array) ---
        if (section == "scoring" || section == "all")
        {
            int sOff = LmuScoringOff;
            result["S_trackName"] = ReadFixedString(buf, sOff, 64);
            result["S_session"] = BitConverter.ToInt32(buf, sOff + 64);
            result["S_currentET"] = BitConverter.ToDouble(buf, sOff + 68);
            result["S_endET"] = BitConverter.ToDouble(buf, sOff + 76);
            result["S_maxLaps"] = BitConverter.ToInt32(buf, sOff + 84);
            result["S_lapDist"] = BitConverter.ToDouble(buf, sOff + 88);
            result["S_numVehicles"] = BitConverter.ToInt32(buf, sOff + 104);
            result["S_gamePhase"] = buf[sOff + 108];
            result["S_yellowFlagState"] = (sbyte)buf[sOff + 109];
            result["S_startLight"] = buf[sOff + 113];
            result["S_numRedLights"] = buf[sOff + 114];
            result["S_playerName"] = ReadFixedString(buf, sOff + 116, 32);
            result["S_darkCloud"] = BitConverter.ToDouble(buf, sOff + 212);
            result["S_raining"] = BitConverter.ToDouble(buf, sOff + 220);
            result["S_ambientTemp"] = BitConverter.ToDouble(buf, sOff + 228);
            result["S_trackTemp"] = BitConverter.ToDouble(buf, sOff + 236);
            result["S_windX"] = BitConverter.ToDouble(buf, sOff + 244);
            result["S_windY"] = BitConverter.ToDouble(buf, sOff + 252);
            result["S_windZ"] = BitConverter.ToDouble(buf, sOff + 260);
            result["S_minPathWetness"] = BitConverter.ToDouble(buf, sOff + 268);
            result["S_maxPathWetness"] = BitConverter.ToDouble(buf, sOff + 276);
            result["S_gameMode"] = buf[sOff + 284];
            result["S_serverName"] = ReadFixedString(buf, sOff + 296, 32);
            result["S_startET"] = BitConverter.ToSingle(buf, sOff + 328);
            result["S_avgPathWetness"] = BitConverter.ToDouble(buf, sOff + 332);
            result["S_sessionTimeRemaining"] = BitConverter.ToSingle(buf, sOff + 340);
            result["S_timeOfDay"] = BitConverter.ToSingle(buf, sOff + 344);
            result["S_trackGripLevel"] = buf[sOff + 349];
            result["S_cloudCoverage"] = buf[sOff + 350];

            // Vehicle scoring array
            for (int vi = 0; vi < LmuMaxVehicles; vi++)
            {
                int vOff = LmuVehScoringOff + vi * LmuVehStride;
                if (vOff + LmuVehStride > buf.Length) break;

                var name = ReadFixedString(buf, vOff + 36, 64);
                if (string.IsNullOrEmpty(name) && vi > 0) continue; // skip empty slots after first

                string prefix = $"V{vi}";
                result[$"{prefix}_name"] = name;
                result[$"{prefix}_driver"] = ReadFixedString(buf, vOff + 4, 32); // driverName starts at +4 (after int32 id)
                result[$"{prefix}_totalLaps"] = BitConverter.ToInt16(buf, vOff + 100);
                result[$"{prefix}_finishStatus"] = (sbyte)buf[vOff + 103];
                result[$"{prefix}_lapDist"] = BitConverter.ToDouble(buf, vOff + 104);
                result[$"{prefix}_bestLapTime"] = BitConverter.ToDouble(buf, vOff + 144);
                result[$"{prefix}_lastLapTime"] = BitConverter.ToDouble(buf, vOff + 168);
                result[$"{prefix}_isPlayer"] = buf[vOff + 196];
                result[$"{prefix}_control"] = (sbyte)buf[vOff + 197];
                result[$"{prefix}_inPits"] = buf[vOff + 198];
                result[$"{prefix}_place"] = buf[vOff + 199];
                result[$"{prefix}_vehicleClass"] = ReadFixedString(buf, vOff + 200, 32);
                result[$"{prefix}_timeBehindNext"] = BitConverter.ToDouble(buf, vOff + 232);
                result[$"{prefix}_lapsBehindNext"] = BitConverter.ToInt32(buf, vOff + 240);
                result[$"{prefix}_timeBehindLeader"] = BitConverter.ToDouble(buf, vOff + 244);
                result[$"{prefix}_lapsBehindLeader"] = BitConverter.ToInt32(buf, vOff + 252);
                result[$"{prefix}_posX"] = BitConverter.ToDouble(buf, vOff + 264);
                result[$"{prefix}_posY"] = BitConverter.ToDouble(buf, vOff + 272);
                result[$"{prefix}_posZ"] = BitConverter.ToDouble(buf, vOff + 280);
                result[$"{prefix}_localVelX"] = BitConverter.ToDouble(buf, vOff + 288);
                result[$"{prefix}_localVelY"] = BitConverter.ToDouble(buf, vOff + 296);
                result[$"{prefix}_localVelZ"] = BitConverter.ToDouble(buf, vOff + 304);
                result[$"{prefix}_localAccelX"] = BitConverter.ToDouble(buf, vOff + 312);
                result[$"{prefix}_localAccelY"] = BitConverter.ToDouble(buf, vOff + 320);
                result[$"{prefix}_localAccelZ"] = BitConverter.ToDouble(buf, vOff + 328);
                result[$"{prefix}_timeIntoLap"] = BitConverter.ToDouble(buf, vOff + 464);
                result[$"{prefix}_estimatedLapTime"] = BitConverter.ToDouble(buf, vOff + 472);
                result[$"{prefix}_flag"] = buf[vOff + 504];
                result[$"{prefix}_underYellow"] = buf[vOff + 505];
                result[$"{prefix}_fuelFraction"] = buf[vOff + 578];
                result[$"{prefix}_drsState"] = buf[vOff + 579];
                result[$"{prefix}_vehFile"] = ReadFixedString(buf, vOff + 544, 32);
            }
        }

        // --- Physics / Telemetry section ---
        byte playerIdx = buf.Length > LmuTelemHeaderOff + 2 ? buf[LmuTelemHeaderOff + 1] : (byte)0;
        int telemOff = LmuTelemInfoOff + playerIdx * LmuTelemStride;

        if (section == "physics" || section == "all")
        {
            ReadLmuField(result, buf, telemOff, 4, "deltaTime", "double");
            ReadLmuField(result, buf, telemOff, 12, "elapsedTime", "double");
            ReadLmuField(result, buf, telemOff, 20, "lapNumber", "int32");
            ReadLmuField(result, buf, telemOff, 32, "vehicleName", "string", 64);
            ReadLmuField(result, buf, telemOff, 96, "trackName", "string", 64);
            ReadLmuFieldVec3(result, buf, telemOff, 184, "localVel");
            ReadLmuFieldVec3(result, buf, telemOff, 208, "localAccel");
            ReadLmuFieldVec3(result, buf, telemOff, 304, "localRot");
            ReadLmuField(result, buf, telemOff, 352, "gear", "int32");
            ReadLmuField(result, buf, telemOff, 356, "engineRPM", "double");
            ReadLmuField(result, buf, telemOff, 364, "engineWaterTemp", "double");
            ReadLmuField(result, buf, telemOff, 372, "engineOilTemp", "double");
            ReadLmuField(result, buf, telemOff, 380, "clutchRPM", "double");
            ReadLmuField(result, buf, telemOff, 388, "unfilteredThrottle", "double");
            ReadLmuField(result, buf, telemOff, 396, "unfilteredBrake", "double");
            ReadLmuField(result, buf, telemOff, 404, "unfilteredSteering", "double");
            ReadLmuField(result, buf, telemOff, 412, "unfilteredClutch", "double");
            ReadLmuField(result, buf, telemOff, 452, "steeringShaftTorque", "double");
            ReadLmuField(result, buf, telemOff, 460, "front3rdDeflection", "double");
            ReadLmuField(result, buf, telemOff, 468, "rear3rdDeflection", "double");
            ReadLmuField(result, buf, telemOff, 476, "frontWingHeight", "double");
            ReadLmuField(result, buf, telemOff, 484, "frontRideHeight", "double");
            ReadLmuField(result, buf, telemOff, 492, "rearRideHeight", "double");
            ReadLmuField(result, buf, telemOff, 500, "drag", "double");
            ReadLmuField(result, buf, telemOff, 508, "frontDownforce", "double");
            ReadLmuField(result, buf, telemOff, 516, "rearDownforce", "double");
            ReadLmuField(result, buf, telemOff, 524, "fuel", "double");
            ReadLmuField(result, buf, telemOff, 532, "engineMaxRPM", "double");
            ReadLmuField(result, buf, telemOff, 544, "dentSeverity", "bytes", 8);
            ReadLmuField(result, buf, telemOff, 552, "lastImpactET", "double");
            ReadLmuField(result, buf, telemOff, 560, "lastImpactMagnitude", "double");
            ReadLmuField(result, buf, telemOff, 568, "lastImpactPos", "vec3");
            ReadLmuField(result, buf, telemOff, 592, "engineTorque", "double");
            ReadLmuField(result, buf, telemOff, 600, "currentSector", "int32");
            ReadLmuField(result, buf, telemOff, 608, "fuelCapacity", "double");
            ReadLmuField(result, buf, telemOff, 620, "frontTireCompoundName", "string", 18);
            ReadLmuField(result, buf, telemOff, 638, "rearTireCompoundName", "string", 18);
            ReadLmuField(result, buf, telemOff, 660, "visualSteeringWheelRange", "float");
            ReadLmuField(result, buf, telemOff, 664, "rearBrakeBias", "double");
            ReadLmuField(result, buf, telemOff, 672, "turboBoostPressure", "double");
            ReadLmuField(result, buf, telemOff, 692, "physicalSteeringWheelRange", "float");
            ReadLmuField(result, buf, telemOff, 696, "deltaBest", "double");
            ReadLmuField(result, buf, telemOff, 704, "batteryChargeFraction", "double");
            ReadLmuField(result, buf, telemOff, 712, "electricBoostMotorTorque", "double");
            ReadLmuField(result, buf, telemOff, 720, "electricBoostMotorRPM", "double");
            ReadLmuField(result, buf, telemOff, 728, "electricBoostMotorTemperature", "double");
            ReadLmuField(result, buf, telemOff, 768, "regen", "float");
            ReadLmuField(result, buf, telemOff, 772, "soc", "float");
            ReadLmuField(result, buf, telemOff, 776, "virtualEnergy", "float");
            ReadLmuField(result, buf, telemOff, 780, "timeGapCarAhead", "float");
            ReadLmuField(result, buf, telemOff, 784, "timeGapCarBehind", "float");
            ReadLmuField(result, buf, telemOff, 788, "timeGapPlaceAhead", "float");
            ReadLmuField(result, buf, telemOff, 792, "timeGapPlaceBehind", "float");

            string[] wLabels = ["FL", "FR", "RL", "RR"];
            for (int wi = 0; wi < 4; wi++)
            {
                int wOff = telemOff + LmuWheelBaseOff + wi * LmuWheelStride;
                string wl = wLabels[wi];
                ReadLmuField(result, buf, wOff, 0, $"Wheel_{wl}_suspensionDeflection", "double");
                ReadLmuField(result, buf, wOff, 8, $"Wheel_{wl}_rideHeight", "double");
                ReadLmuField(result, buf, wOff, 16, $"Wheel_{wl}_suspForce", "double");
                ReadLmuField(result, buf, wOff, 24, $"Wheel_{wl}_brakeTemp", "double");
                ReadLmuField(result, buf, wOff, 32, $"Wheel_{wl}_brakePressure", "double");
                ReadLmuField(result, buf, wOff, 40, $"Wheel_{wl}_rotation", "double");
                ReadLmuField(result, buf, wOff, 48, $"Wheel_{wl}_lateralPatchVel", "double");
                ReadLmuField(result, buf, wOff, 56, $"Wheel_{wl}_longitudinalPatchVel", "double");
                ReadLmuField(result, buf, wOff, 64, $"Wheel_{wl}_lateralGroundVel", "double");
                ReadLmuField(result, buf, wOff, 72, $"Wheel_{wl}_longitudinalGroundVel", "double");
                ReadLmuField(result, buf, wOff, 80, $"Wheel_{wl}_camber", "double");
                ReadLmuField(result, buf, wOff, 88, $"Wheel_{wl}_lateralForce", "double");
                ReadLmuField(result, buf, wOff, 96, $"Wheel_{wl}_longitudinalForce", "double");
                ReadLmuField(result, buf, wOff, 104, $"Wheel_{wl}_tireLoad", "double");
                ReadLmuField(result, buf, wOff, 112, $"Wheel_{wl}_gripFract", "double");
                ReadLmuField(result, buf, wOff, 120, $"Wheel_{wl}_pressure", "double");
                ReadLmuField(result, buf, wOff, 128, $"Wheel_{wl}_temperatureInner", "double");
                ReadLmuField(result, buf, wOff, 136, $"Wheel_{wl}_temperatureMid", "double");
                ReadLmuField(result, buf, wOff, 144, $"Wheel_{wl}_temperatureOuter", "double");
                ReadLmuField(result, buf, wOff, 152, $"Wheel_{wl}_wear", "double");
                ReadLmuField(result, buf, wOff, 160, $"Wheel_{wl}_terrainName", "string", 16);
                ReadLmuField(result, buf, wOff, 180, $"Wheel_{wl}_verticalTireDeflection", "double");
                ReadLmuField(result, buf, wOff, 188, $"Wheel_{wl}_wheelYLocation", "double");
                ReadLmuField(result, buf, wOff, 196, $"Wheel_{wl}_toe", "double");
                ReadLmuField(result, buf, wOff, 204, $"Wheel_{wl}_tireCarcassTemperature", "double");
                ReadLmuField(result, buf, wOff, 212, $"Wheel_{wl}_tireInnerLayerTemp0", "double");
                ReadLmuField(result, buf, wOff, 220, $"Wheel_{wl}_tireInnerLayerTemp1", "double");
                ReadLmuField(result, buf, wOff, 228, $"Wheel_{wl}_tireInnerLayerTemp2", "double");
                ReadLmuField(result, buf, wOff, 236, $"Wheel_{wl}_optimalTemp", "float");
            }
        }

        return result;
    }

    private static void ReadLmuField(Dictionary<string, object?> result, byte[] buf, int baseOff, int fieldOff, string name, string type, int? strLen = null)
    {
        int off = baseOff + fieldOff;
        if (off + SizeOfType(type) > buf.Length) return;

        object? val = type switch
        {
            "double" => BitConverter.ToDouble(buf, off),
            "float" => BitConverter.ToSingle(buf, off),
            "int32" => BitConverter.ToInt32(buf, off),
            "string" when strLen.HasValue => ReadFixedString(buf, off, strLen.Value),
            "bytes" when strLen.HasValue => buf.Skip(off).Take(strLen.Value).ToArray(),
            "vec3" => new {
                X = BitConverter.ToDouble(buf, off),
                Y = BitConverter.ToDouble(buf, off + 8),
                Z = BitConverter.ToDouble(buf, off + 16)
            },
            _ => null
        };

        result[name] = val;
    }

    private static int SizeOfType(string type) => type switch
    {
        "double" => 8, "float" => 4, "int32" => 4, "string" => 64, "bytes" => 8, "vec3" => 24,
        _ => 8
    };

    private static void ReadLmuFieldVec3(Dictionary<string, object?> result, byte[] buf, int baseOff, int fieldOff, string name)
    {
        int off = baseOff + fieldOff;
        result[$"{name}_X"] = BitConverter.ToDouble(buf, off);
        result[$"{name}_Y"] = BitConverter.ToDouble(buf, off + 8);
        result[$"{name}_Z"] = BitConverter.ToDouble(buf, off + 16);
    }

    // ===== Struct Reflection Utilities =====

    public static void ExploreStructFields(Type type, string prefix, List<RawFieldInfo> fields, Type? rootType = null)
    {
        rootType ??= type;
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var fType = field.FieldType;
            var fullName = string.IsNullOrEmpty(prefix) ? field.Name : $"{prefix}.{field.Name}";
            var offset = CalcOffset(rootType, fullName);

            if (PrimitiveTypes.Contains(fType) || fType.IsEnum)
            {
                fields.Add(new(RawFieldType.Primitive, fullName, GetUnitHint(fullName), fullName, offset));
            }
            else if (fType.IsArray)
            {
                var elemType = fType.GetElementType();
                if (elemType != null && elemType == typeof(byte))
                    fields.Add(new(RawFieldType.String, fullName, "", fullName, offset));
                else if (elemType != null && (PrimitiveTypes.Contains(elemType) || elemType.IsEnum))
                    fields.Add(new(RawFieldType.Array, fullName, "", fullName, offset));
                else if (elemType == typeof(StructVector3))
                    fields.Add(new(RawFieldType.Nested, fullName, "", fullName, offset));
                else if (elemType != null && elemType.IsValueType && !PrimitiveTypes.Contains(elemType))
                    ExploreStructFields(elemType, fullName, fields, rootType);
            }
            else if (fType.IsValueType && !fType.IsPrimitive && !IsNestedStructType(fType))
            {
                ExploreStructFields(fType, fullName, fields, rootType);
            }
            else
            {
                fields.Add(new(RawFieldType.Other, fullName, "", fullName, offset));
            }
        }
    }

    public static int CalcOffset(Type rootType, string fieldPath)
    {
        try
        {
            var parts = fieldPath.Split('.');
            int cumulative = 0;
            Type currentType = rootType;

            for (int i = 0; i < parts.Length; i++)
            {
                var fi = currentType.GetField(parts[i], BindingFlags.Public | BindingFlags.Instance);
                if (fi == null) return -1;

                int fieldOff = (int)Marshal.OffsetOf(currentType, parts[i]);
                cumulative += fieldOff;
                // If the field is an array of value types, dive into the element type
                currentType = fi.FieldType.IsArray && fi.FieldType.GetElementType() is Type et && et.IsValueType
                    ? et : fi.FieldType;
            }

            return cumulative;
        }
        catch { return -1; }
    }

    private static bool IsNestedStructType(Type type)
    {
        string n = type.Name;
        if (n is "StructVector3" or "R3eVector3d" or "R3eVector3f") return false; // leaf types
        if (n.StartsWith("R3eTireData`") || n.StartsWith("R3eSectors`") ||
            n.StartsWith("R3eOrientation`") || n.StartsWith("R3eRaceDuration`") ||
            n.StartsWith("R3eTireTemp3`") || n.StartsWith("R3eTireData`"))
            return false; // generic data containers
        if (n is "R3eFlags" or "R3eAidSettings" or "R3eDRS" or "R3ePushToPass")
            return false; // leaf types
        return true; // recurse
    }

    public static void AddStructFields(object obj, string prefix, Dictionary<string, object?> result)
    {
        if (obj == null) return;
        var type = obj.GetType();

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var val = field.GetValue(obj);
            var fullName = string.IsNullOrEmpty(prefix) ? field.Name : $"{prefix}_{field.Name}";

            if (val is byte[] bytes)
            {
                int len = Array.IndexOf(bytes, (byte)0);
                len = len < 0 ? bytes.Length : len;
                result[fullName] = Encoding.UTF8.GetString(bytes, 0, len);
            }
            else if (val is float[] fa)
            {
                for (int i = 0; i < fa.Length; i++)
                    result[$"{fullName}_{i}"] = fa[i];
            }
            else if (val is int[] ia)
            {
                for (int i = 0; i < ia.Length; i++)
                    result[$"{fullName}_{i}"] = ia[i];
            }
            else if (val is double[] da)
            {
                for (int i = 0; i < da.Length; i++)
                    result[$"{fullName}_{i}"] = da[i];
            }
            else if (val is StructVector3 sv)
            {
                result[$"{fullName}_X"] = sv.X;
                result[$"{fullName}_Y"] = sv.Y;
                result[$"{fullName}_Z"] = sv.Z;
            }
            else if (val is StructVector3[] sva)
            {
                for (int i = 0; i < sva.Length; i++)
                {
                    result[$"{fullName}_{i}_X"] = sva[i].X;
                    result[$"{fullName}_{i}_Y"] = sva[i].Y;
                    result[$"{fullName}_{i}_Z"] = sva[i].Z;
                }
            }
            else if (val is Array structArr && structArr.GetType().GetElementType() is Type arrElem
                     && arrElem.IsValueType && !PrimitiveTypes.Contains(arrElem)
                     && arrElem != typeof(StructVector3))
            {
                for (int i = 0; i < structArr.Length; i++)
                {
                    var elemVal = structArr.GetValue(i);
                    if (elemVal != null)
                        AddStructFields(elemVal, $"{fullName}[{i}]", result);
                }
            }
            else if (val != null && val.GetType().IsValueType && !PrimitiveTypes.Contains(val.GetType()) && !val.GetType().IsEnum)
            {
                AddStructFields(val, fullName, result);
            }
            else
            {
                result[fullName] = val;
            }
        }
    }

    // ===== Utilities =====
    private static byte[]? ReadMmf(string mmfName, int maxSize)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(mmfName, MemoryMappedFileRights.Read);
            // Use capacity 0 to get the entire MMF (the game may allocate less than maxSize)
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            long actualSize = view.Capacity;
            int size = (int)Math.Min(actualSize, maxSize);
            var buf = new byte[size];
            view.ReadArray(0, buf, 0, size);
            return buf;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MMF] Failed to read '{mmfName}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static T MarshalBuffer<T>(byte[] buf)
    {
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject())!;
        }
        finally
        {
            handle.Free();
        }
    }

    private static string ByteStr(byte[]? buf)
    {
        if (buf == null || buf.Length == 0) return "";
        int len = Array.IndexOf(buf, (byte)0);
        len = len < 0 ? buf.Length : len;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    private static string ReadFixedString(byte[] buf, int offset, int maxLen)
    {
        int end = Math.Min(offset + maxLen, buf.Length);
        int nullIdx = Array.IndexOf(buf, (byte)0, offset, end - offset);
        int len = nullIdx < 0 ? end - offset : nullIdx - offset;
        return Encoding.UTF8.GetString(buf, offset, len);
    }

    private static string GetUnitHint(string fieldName)
    {
        var lower = fieldName.ToLowerInvariant();
        if (lower.Contains("speed") || lower.Contains("velocity")) return "m/s";
        if (lower.Contains("temp")) return "C";
        if (lower.Contains("force") || lower.Contains("load")) return "N";
        if (lower.Contains("torque")) return "Nm";
        if (lower.Contains("pressure")) return "bar";
        if (lower.Contains("angle") || lower.Contains("camber") || lower.Contains("toe")) return "rad";
        if (lower.Contains("rpm")) return "rpm";
        if (lower.Contains("fuel")) return "L";
        if (lower.Contains("wear")) return "0-1";
        if (lower.Contains("deflection") || lower.Contains("travel") || lower.Contains("height")) return "m";
        return "";
    }

    private static float SafeFloat(byte[] buf, int offset)
    {
        float val = BitConverter.ToSingle(buf, offset);
        if (float.IsNaN(val) || float.IsInfinity(val)) return 0f;
        return val;
    }

    private static void SanitizeFloats(Dictionary<string, object?> d)
    {
        foreach (var k in d.Keys.ToList())
        {
            var v = d[k];
            if (v is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                d[k] = 0f;
            else if (v is double dv && (double.IsNaN(dv) || double.IsInfinity(dv)))
                d[k] = 0.0;
        }
    }
}

public enum RawFieldType { Primitive, Array, Nested, String, Float, Other }

public record RawFieldInfo(RawFieldType Type, string Name, string Unit, string Path, int Offset = -1);

// ACC shared memory structs (from Thomsen.AccTools, verified against Kunos API)
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AccCoordinates { public float X; public float Y; public float Z; }

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AccGraphics
{
    public int PacketId;
    public int Status;
    public int Session;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string CurrentTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string LastTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string BestTimeString;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string SplitString;
    public int CompletedLaps;
    public int Position;
    public int CurrentTime;
    public int LastTime;
    public int BestTime;
    public float SessionTimeLeft;
    public float DistanceTraveled;
    public int IsInPit;
    public int CurrentSectorIndex;
    public int LastSectorTime;
    public int NumberOfLaps;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TyreCompound;
    public float ReplayTimeMultiplier;
    public float NormalizedCarPosition;
    public int ActiveCars;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)] public AccCoordinates[] CarCoordinates;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 60)] public int[] CarIDs;
    public int PlayerCarID;
    public float PenaltyTime;
    public int Flag;
    public int Penalty;
    public int IdealLineOn;
    public int IsInPitLane;
    public float SurfaceGrip;
    public int MandatoryPitDone;
    public float WindSpeed;
    public float WindDirection;
    public int IsSetupMenuVisible;
    public int MainDisplayIndex;
    public int SecondaryDisplayIndex;
    public int TC;
    public int TCCUT;
    public int EngineMap;
    public int ABS;
    public float FuelXLap;
    public int RainLights;
    public int FlashingLights;
    public int LightsStage;
    public float ExhaustTemperature;
    public int WiperLV;
    public int DriverStintTotalTimeLeft;
    public int DriverStintTimeLeft;
    public int RainTyres;
    public int SessionIndex;
    public float UsedFuel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string DeltaLapTimeString;
    public int DeltaLapTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string EstimatedLapTimeString;
    public int EstimatedLapTime;
    public int IsDeltaPositive;
    public int Split;
    public int IsValidLap;
    public float FuelEstimatedLaps;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TrackStatus;
    public int MissingMandatoryPits;
    public float Clock;
    public int DirectionLightsLeft;
    public int DirectionLightsRight;
    public int GlobalYellow;
    public int GlobalYellow1;
    public int GlobalYellow2;
    public int GlobalYellow3;
    public int GlobalWhite;
    public int GlobalGreen;
    public int GlobalChequered;
    public int GlobalRed;
    public int MfdTyreSet;
    public float MfdFuelToAdd;
    public float MfdTyrePressureLF;
    public float MfdTyrePressureRF;
    public float MfdTyrePressureLR;
    public float MfdTyrePressureRR;
    public int TrackGripStatus;
    public int RainIntensity;
    public int RainIntensityIn10min;
    public int RainIntensityIn30min;
    public int CurrentTyreSet;
    public int StrategyTyreSet;
    public int GapAhead;
    public int GapBehind;
}
