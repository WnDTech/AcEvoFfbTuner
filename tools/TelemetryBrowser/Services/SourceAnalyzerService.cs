using System.Text.RegularExpressions;

namespace TelemetryBrowser.Services;

public sealed class SourceAnalyzerService
{
    private readonly Dictionary<string, (HashSet<string> physics, HashSet<string> graphics, HashSet<string> staticFields)> _mappings = new();
    private readonly Dictionary<string, string> _analysisMethods = new();

    public SourceAnalyzerService()
    {
        // Locate the repo src/ directory by walking up from the output directory
        // Output is at {projectDir}\bin\Release\ → need 3 more parents to reach repo root
        var probe = AppContext.BaseDirectory;
        string? srcDir = null;

        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(probe, "src", "AcEvoFfbTuner.Core", "SharedMemory");
            if (Directory.Exists(candidate))
            {
                srcDir = Path.Combine(probe, "src");
                break;
            }
            var parent = Path.GetDirectoryName(probe);
            if (parent == null || parent == probe) break;
            probe = parent;
        }

        if (srcDir == null)
        {
            // Fallback: try using current directory (works when run from repo root)
            var curDir = Environment.CurrentDirectory;
            if (Directory.Exists(Path.Combine(curDir, "src", "AcEvoFfbTuner.Core", "SharedMemory")))
                srcDir = Path.Combine(curDir, "src");
        }

        AnalyzeAllReaders(srcDir ?? "");
    }

    public IReadOnlySet<string> GetMappedPhysicsFields(string gameId)
        => _mappings.TryGetValue(gameId, out var m) ? m.physics : new HashSet<string>();

    public IReadOnlySet<string> GetMappedGraphicsFields(string gameId)
        => _mappings.TryGetValue(gameId, out var m) ? m.graphics : new HashSet<string>();

    public IReadOnlySet<string> GetMappedStaticFields(string gameId)
        => _mappings.TryGetValue(gameId, out var m) ? m.staticFields : new HashSet<string>();

    public string GetAnalysisMethod(string gameId)
        => _analysisMethods.TryGetValue(gameId, out var m) ? m : "hashSetName-fallback";

    public (int totalMapped, string[] fields) GetMappedSummary(string gameId, string section)
    {
        var set = section switch
        {
            "physics" => GetMappedPhysicsFields(gameId),
            "graphics" => GetMappedGraphicsFields(gameId),
            "static" => GetMappedStaticFields(gameId),
            _ => new HashSet<string>()
        };
        return (set.Count, set.OrderBy(x => x).ToArray());
    }

    private void AnalyzeAllReaders(string srcDir)
    {
        var readerFiles = new (string gameId, string file)[]
        {
            ("raccoroom", Path.Combine(srcDir, "AcEvoFfbTuner.Core", "SharedMemory", "RaceroomSharedMemoryReader.cs")),
            ("acevo", Path.Combine(srcDir, "AcEvoFfbTuner.Core", "SharedMemory", "SharedMemoryReader.cs")),
            ("lemansultimate", Path.Combine(srcDir, "AcEvoFfbTuner.Core", "SharedMemory", "LmuSharedMemoryReader.cs")),
            ("assettocorsa", Path.Combine(srcDir, "AcEvoFfbTuner.Core", "SharedMemory", "AssettoCorsaSharedMemoryReader.cs")),
        };

        foreach (var (gameId, filePath) in readerFiles)
        {
            if (File.Exists(filePath))
            {
                var source = File.ReadAllText(filePath);
                var (physAnalysis, gfxAnalysis, stAnalysis, method) = AnalyzeReader(gameId, source, filePath);
                _mappings[gameId] = (physAnalysis, gfxAnalysis, stAnalysis);
                _analysisMethods[gameId] = method;

                // Cross-check against known mapping to validate
                var known = GetKnownMapping(gameId);
                var combined = new HashSet<string>(physAnalysis, StringComparer.OrdinalIgnoreCase);
                combined.UnionWith(gfxAnalysis);
                combined.UnionWith(stAnalysis);
                var missingInAnalysis = known.Except(combined).ToList();
                if (missingInAnalysis.Count > 0 && known.Count > combined.Count)
                {
                    // Analysis missed some fields — supplement with known mapping
                    foreach (var f in known)
                    {
                        if (typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFilePhysicsEvo)
                            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Any(fi => fi.Name.Equals(f, StringComparison.OrdinalIgnoreCase)))
                            physAnalysis.Add(f);
                        else if (typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileGraphicEvo)
                            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Any(fi => fi.Name.Equals(f, StringComparison.OrdinalIgnoreCase)))
                            gfxAnalysis.Add(f);
                        else if (typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileStaticEvo)
                            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                            .Any(fi => fi.Name.Equals(f, StringComparison.OrdinalIgnoreCase)))
                            stAnalysis.Add(f);
                    }
                    _mappings[gameId] = (physAnalysis, gfxAnalysis, stAnalysis);
                    _analysisMethods[gameId] = "source-analysis+hashSetName-supplement";
                }
            }
            else
            {
                _mappings[gameId] = GetGameMapping(gameId);
                _analysisMethods[gameId] = "hashSetName-fallback";
            }
        }
    }

    private (HashSet<string> physics, HashSet<string> graphics, HashSet<string> staticFields, string method) AnalyzeReader(
        string gameId, string source, string filePath)
    {
        var physics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var graphics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staticFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string method = "regex-assignment";

        // Check for Marshal.PtrToStructure — indicates ALL struct fields are populated
        bool usesMarshalPhysics = source.Contains("Marshal.PtrToStructure<SPageFilePhysicsEvo>");
        bool usesMarshalGraphics = source.Contains("Marshal.PtrToStructure<SPageFileGraphicEvo>");
        bool usesMarshalStatic = source.Contains("Marshal.PtrToStructure<SPageFileStaticEvo>");

        if (usesMarshalPhysics || usesMarshalGraphics || usesMarshalStatic)
        {
            method = "marshal-deserialization";
            if (usesMarshalPhysics)
                foreach (var f in typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFilePhysicsEvo)
                             .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    physics.Add(f.Name);
            if (usesMarshalGraphics)
                foreach (var f in typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileGraphicEvo)
                             .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    graphics.Add(f.Name);
            if (usesMarshalStatic)
                foreach (var f in typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileStaticEvo)
                             .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    staticFields.Add(f.Name);
        }

        // Extract explicit assignment targets: physics.FieldName = or +=
        var assignRegex = new Regex(
            @"(physics|graphics|staticData)\s*\.\s*(\w+)(?:\s*\[?\s*\w*\s*\]?)?\s*(?:\+?=|\?=)",
            RegexOptions.Multiline);
        foreach (Match m in assignRegex.Matches(source))
        {
            var target = m.Groups[1].Value;
            var field = m.Groups[2].Value;
            if (!string.IsNullOrEmpty(field))
            {
                if (target == "physics") physics.Add(field);
                else if (target == "graphics") graphics.Add(field);
                else if (target == "staticData") staticFields.Add(field);
            }
        }

        return (physics, graphics, staticFields, method);
    }

    /// <summary>Known field mappings verified by reading each reader's source code.</summary>
    private static HashSet<string> GetKnownMapping(string gameId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (gameId)
        {
            case "raccoroom":
                set.UnionWith(["PacketId", "Gas", "Brake", "Fuel", "Gear", "Rpms", "SteerAngle",
                    "SpeedKmh", "Velocity", "AccG", "WheelSlip", "WheelLoad", "WheelsPressure",
                    "WheelAngularSpeed", "TyreDirtyLevel", "SuspensionTravel", "Heading", "Pitch",
                    "Roll", "FinalFf", "SlipRatio", "SlipAngle", "Mz", "Fx", "Fy", "LocalAngularVel",
                    "KerbVibration", "SlipVibrations", "RoadVibrations", "AbsVibrations", "AirTemp",
                    "RoadTemp", "TyreTemp", "TyreWear", "TyreTempI", "TyreTempM", "TyreTempO",
                    "BrakeTemp", "WaterTemp", "SuspensionDamage", "CarDamage", "TyreContactPoint",
                    "TyreContactNormal", "TyreContactHeading", "AbsInAction", "IsEngineRunning",
                    "IsAiControlled", "LocalVelocity", "Status", "RpmPercent", "IsRpmLimiterOn",
                    "IsChangeUpRpm", "FfbStrength", "CarFfbMultiplier", "SteerDegrees", "Npos",
                    "Flag", "IsIgnitionOn", "EngineType", "CarLocation", "CarCoordinates",
                    "UseSingleCompound", "CarModel", "DriverName", "DriverSurname",
                    "PerformanceModeName", "GapAhead", "GapBehind", "CurrentPos", "TotalDrivers",
                    "FuelLiterCurrentQuantity", "FuelLiterPerLap", "LapsPossibleWithFuel",
                    "TotalLapCount", "IsInPitLane", "IsLastLap", "IsWrongWay", "GlobalFlag",
                    "SessionState", "LastLaptimeMs", "BestLaptimeMs", "SmVersion", "AcEvoVersion",
                    "Session", "EventId", "SessionId", "SessionName", "NumberOfSessions", "Nation",
                    "Longitude", "Latitude", "Track", "TrackConfiguration", "TrackLengthM"]);
                break;

            case "lemansultimate":
                set.UnionWith(["PacketId", "Gas", "Brake", "Gear", "Rpms", "SteerAngle", "SpeedKmh",
                    "Velocity", "AccG", "WheelLoad", "WheelsPressure", "WheelAngularSpeed",
                    "TyreWear", "SuspensionTravel", "FinalFf", "SlipRatio", "SlipAngle", "Mz", "Fx",
                    "Fy", "LocalAngularVel", "KerbVibration", "SlipVibrations", "RoadVibrations",
                    "AbsVibrations", "TyreTemp", "TyreTempI", "TyreTempM", "TyreTempO", "LocalVelocity",
                    "IsEngineRunning", "TurboBoost", "KersCharge", "KersInput", "BrakeBias",
                    "RideHeight", "EngineBrake", "SteerDegrees", "CarModel", "CarCoordinates",
                    "Status", "RpmPercent", "Flag", "EngineType", "CarLocation", "UseSingleCompound",
                    "GlobalFlag", "SessionState", "SmVersion", "Track", "TrackConfiguration"]);
                break;

            case "assettocorsa":
                set.UnionWith(["PacketId", "Gas", "Brake", "Fuel", "Gear", "Rpms", "SteerAngle",
                    "SpeedKmh", "Velocity", "AccG", "WheelSlip", "WheelLoad", "WheelsPressure",
                    "WheelAngularSpeed", "TyreWear", "TyreDirtyLevel", "TyreCoreTemperature",
                    "CamberRad", "SuspensionTravel", "Abs", "PitLimiterOn", "AutoShifterOn",
                    "RideHeight", "TurboBoost", "Ballast", "AirDensity", "AirTemp", "RoadTemp",
                    "LocalAngularVel", "FinalFf", "PerformanceMeter", "EngineBrake",
                    "ErsRecoveryLevel", "IsAiControlled", "P2pActivations", "P2pStatus",
                    "CurrentMaxRpm", "SlipRatio", "SlipAngle", "TcinAction", "AbsInAction",
                    "SuspensionDamage", "TyreTemp", "TyreContactNormal", "TyreContactPoint",
                    "TyreContactHeading", "BrakeTemp", "TyreTempI", "TyreTempM", "TyreTempO",
                    "IgnitionOn", "IsEngineRunning", "Fy", "Fx", "Mz", "KerbVibration",
                    "RoadVibrations", "SlipVibrations", "AbsVibrations", "Rpm", "RpmPercent",
                    "GasPercent", "BrakePercent", "IsIgnitionOn", "DisplaySpeedKmh",
                    "SteeringPercent", "FfbStrength", "CarFfbMultiplier", "SteerDegrees", "Npos",
                    "CarLocation", "EngineType", "CarCoordinates", "CarModel", "DriverName",
                    "DriverSurname", "PerformanceModeName", "Flag", "GlobalFlag", "SessionState",
                    "TimingState", "TyreLf", "TyreRf", "TyreLr", "TyreRr", "CarDamage", "PitInfo",
                    "Instrumentation", "Electronics", "AssistsState", "SmVersion", "AcEvoVersion",
                    "Session", "SessionName", "Nation", "StartingGrip", "IsTimedRace",
                    "NumberOfSessions", "Track", "TrackConfiguration", "TrackLengthM"]);
                break;
        }
        return set;
    }

    private static (HashSet<string> physics, HashSet<string> graphics, HashSet<string> staticFields) GetGameMapping(string gameId)
    {
        var phys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gfx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var st = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var all = GetKnownMapping(gameId);
        var physFields = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFilePhysicsEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gfxFields = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileGraphicEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stFields = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileStaticEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var f in all)
        {
            if (physFields.Contains(f)) phys.Add(f);
            else if (gfxFields.Contains(f)) gfx.Add(f);
            else if (stFields.Contains(f)) st.Add(f);
        }

        return (phys, gfx, st);
    }

    public Dictionary<string, object> GetGameCoverageReport(string gameId, List<RawFieldInfo> allRawFields)
    {
        var mappedPhys = GetMappedPhysicsFields(gameId);
        var mappedGfx = GetMappedGraphicsFields(gameId);
        var mappedSt = GetMappedStaticFields(gameId);
        var analysisMethod = GetAnalysisMethod(gameId);

        var totalPossiblePhys = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFilePhysicsEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length;
        var totalPossibleGfx = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileGraphicEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length;
        var totalPossibleSt = typeof(AcEvoFfbTuner.Core.SharedMemory.Structs.SPageFileStaticEvo)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Length;
        var totalPossible = totalPossiblePhys + totalPossibleGfx + totalPossibleSt;
        var totalMapped = mappedPhys.Count + mappedGfx.Count + mappedSt.Count;

        return new Dictionary<string, object>
        {
            ["totalRaw"] = allRawFields.Count,
            ["totalUniversalStructFields"] = totalPossible,
            ["mappedInMainApp"] = totalMapped,
            ["mappedPhysFields"] = mappedPhys.OrderBy(x => x).ToList(),
            ["mappedGfxFields"] = mappedGfx.OrderBy(x => x).ToList(),
            ["mappedStFields"] = mappedSt.OrderBy(x => x).ToList(),
            ["unmappedPhysCount"] = totalPossiblePhys - mappedPhys.Count,
            ["unmappedGfxCount"] = totalPossibleGfx - mappedGfx.Count,
            ["unmappedStCount"] = totalPossibleSt - mappedSt.Count,
            ["coverage"] = totalPossible > 0 ? $"{totalMapped * 100 / totalPossible}%" : "0%",
            ["analysisSource"] = analysisMethod,
            ["rawNativeFields"] = allRawFields.OrderBy(x => x.Name)
                .Select(f => new Dictionary<string, object?>
                {
                    ["name"] = f.Name,
                    ["type"] = f.Type.ToString(),
                    ["unit"] = f.Unit,
                    ["offset"] = f.Offset,
                    ["offsetHex"] = f.Offset >= 0 ? $"0x{f.Offset:X}" : ""
                }).ToList()
        };
    }
}
