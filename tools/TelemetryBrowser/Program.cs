using System.Text.Json;
using System.Threading.Channels;
using TelemetryBrowser.Services;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5200");

builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryService>());
builder.Services.AddSingleton<RawDataService>();
builder.Services.AddSingleton<DataLoggerService>();
builder.Services.AddSingleton<SourceAnalyzerService>();
builder.Services.AddSingleton<AccBroadcastService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/games", () => Results.Json(new[]
{
    new { id = "acevo", name = "Assetto Corsa EVO", mmf = "acevo_pmf_physics" },
    new { id = "assettocorsa", name = "Assetto Corsa (AC1)", mmf = "acpmf_physics" },
    new { id = "assettocorsac", name = "Assetto Corsa Competizione", mmf = "acpmf_physics (*)" },
    new { id = "raccoroom", name = "RaceRoom Racing Experience", mmf = "$R3E" },
    new { id = "lemansultimate", name = "Le Mans Ultimate", mmf = "LMU_Data" },
    new { id = "rfactor2", name = "rFactor 2", mmf = "$rFactor2Telemetry$" }
}));

api.MapGet("/status", (TelemetryService ts) =>
{
    return Results.Json(new
    {
        connected = ts.IsConnected,
        game = ts.CurrentGame,
        readCount = ts.ReadCount,
        lastActivity = ts.LastActivity
    });
});

api.MapPost("/connect/{game}", (string game, TelemetryService ts) =>
{
    var (success, message) = ts.Connect(game);
    return Results.Json(new { success, message });
});

api.MapPost("/disconnect", (TelemetryService ts) =>
{
    ts.Disconnect();
    return Results.Json(new { success = true, message = "Disconnected" });
});

api.MapGet("/stream", async (HttpContext ctx, TelemetryService ts, CancellationToken ct) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers["Cache-Control"] = "no-cache";
    ctx.Response.Headers["Connection"] = "keep-alive";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.DropOldest
    });

    var id = ts.Subscribe(channel);

    try
    {
        await foreach (var data in channel.Reader.ReadAllAsync(ct))
        {
            await ctx.Response.WriteAsync($"data: {data}\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        ts.Unsubscribe(id);
    }
});

api.MapGet("/read-once", (TelemetryService ts) =>
{
    var snap = ts.TryRead();
    if (snap == null)
        return Results.Json(new { success = false, message = "Not connected or read failed" });

    return Results.Json(snap, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 64 });
});

// ===== RAW DATA ENDPOINTS =====

api.MapGet("/raw/{game}/fields", (string game, RawDataService raw) =>
{
    var sections = new[] { "physics", "graphics", "static" };
    if (game == "lemansultimate")
        sections = ["header", "scoring", "physics"];

    var result = new Dictionary<string, object>();
    foreach (var section in sections)
    {
        var fields = raw.GetRawFields(game, section);
        if (fields.Count > 0)
            result[section] = fields.Select(f => new
            {
                name = f.Name,
                type = f.Type.ToString(),
                unit = f.Unit,
                offset = f.Offset,
                offsetHex = f.Offset >= 0 ? $"0x{f.Offset:X}" : ""
            }).ToList();
    }

    return Results.Json(result, new JsonSerializerOptions { WriteIndented = true });
});

api.MapGet("/raw/{game}/values", (string game, RawDataService raw) =>
{
    var sections = new[] { "physics", "graphics", "static" };
    if (game == "lemansultimate")
        sections = ["header", "scoring", "physics"];

    var result = new Dictionary<string, object?>();
    foreach (var section in sections)
    {
        var values = raw.ReadRawValues(game, section);
        foreach (var (k, v) in values)
            result[k] = v;
    }

    // Sanitize NaN/infinity floats which crash System.Text.Json
    var nanKeys = result.Where(kv => (kv.Value is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                                  || (kv.Value is double d && (double.IsNaN(d) || double.IsInfinity(d))))
                       .Select(kv => kv.Key).ToList();
    foreach (var k in nanKeys)
        result[k] = 0f;

    var hasValues = result.Any(kvp => !kvp.Key.StartsWith("_"));
    if (!hasValues)
        return Results.Json(new { success = false, message = "Cannot read raw data. Is the game running?" });

    return Results.Json(result, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 64 });
});

app.MapGet("/broadcast/status", (AccBroadcastService bc) =>
{
    var oppData = bc.GetOpponentsData();
    return Results.Json(new
    {
        connected = bc.IsConnected,
        boundPort = bc.BoundPort,
        messageCount = bc.MessageCount,
        lastError = bc.LastError,
        registerResult = bc.RegisterResult,
        cars = bc.GetAllCars().Length,
        entries = oppData
    }, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 64 });
});

api.MapGet("/raw/raccoroom/driverhex", (RawDataService raw) =>
{
    try
    {
        var values = raw.ReadR3eOpponents();
        if (values.TryGetValue("_driverRawHex", out var hex) && hex is string h)
            return Results.Content(h, "text/plain");
        return Results.Content("No hex dump available", "text/plain");
    }
    catch (Exception ex) { return Results.Content($"Error: {ex.Message}", "text/plain"); }
});

api.MapGet("/opponents/{game}", (string game, RawDataService raw, AccBroadcastService broadcastSvc) =>
{
    try
    {
        var result = HandleOpponents(game, raw, broadcastSvc);
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 64 };
        var json = System.Text.Json.JsonSerializer.Serialize(result, jsonOpts);
        return Results.Content(json, "application/json");
    }
    catch (Exception ex)
    {
        System.Console.Error.WriteLine($"CRITICAL: {ex.GetType()}: {ex.Message}");
        System.Console.Error.WriteLine(ex.StackTrace);
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

Dictionary<string, object?> HandleOpponents(string game, RawDataService raw, AccBroadcastService? broadcastSvc = null)
{
    var result = new Dictionary<string, object?>();

    if (game == "assettocorsac")
    {
        var oppData = raw.ReadAccGraphicsOpponents();
        var entries = new List<Dictionary<string, object?>>();

        if (oppData != null)
        {
            var positions = oppData.TryGetValue("entries", out var e)
                ? (List<Dictionary<string, object?>>?)e ?? [] : [];

            // Get broadcasting data (gear, RPM, names, lap times) if available
            var broadcast = broadcastSvc?.GetOpponentsData();
            var broadcastEntries = broadcast?.TryGetValue("entries", out var be) == true
                ? (List<Dictionary<string, object?>>)be : new();

            if (broadcastEntries.Count > 0)
            {
                // Merge: match broadcast entries to positions by car index
                foreach (var bEntry in broadcastEntries)
                {
                    var merged = new Dictionary<string, object?>(bEntry);
                    int carIndex = bEntry.TryGetValue("carIndex", out var ci) ? Convert.ToInt32(ci) : -1;
                    // Add position from MMF if available
                    var posEntry = positions.FirstOrDefault(p =>
                        p.TryGetValue("index", out var idx) && Convert.ToInt32(idx) == carIndex);
                    if (posEntry != null)
                    {
                        merged["posX"] = posEntry.GetValueOrDefault("posX");
                        merged["posY"] = posEntry.GetValueOrDefault("posY");
                        merged["posZ"] = posEntry.GetValueOrDefault("posZ");
                    }
                    entries.Add(merged);
                }
            }
            else
            {
                // Fallback: positions only
                entries = positions;
            }

            // Forward strategy/meta fields from graphics MMF
            string[] metaFields = ["activeCars", "tyreCompound", "fuelEstLaps", "usedFuel",
                "fuelPerLap", "penaltyTime", "gapAhead", "gapBehind", "trackPos",
                "clock", "windSpeed", "windDirection", "rainTyres", "trackStatus",
                "playerCarId", "playerPosition", "sessionTimeLeft", "completedLaps",
                "totalLaps", "sessionType"];
            foreach (var f in metaFields)
                if (oppData.TryGetValue(f, out var fv))
                    result[f] = fv;
        }

        result["game"] = "Assetto Corsa Competizione";
        result["totalOpponents"] = entries.Count;
        result["entries"] = entries;
        result["broadcast"] = broadcastSvc?.IsConnected == true;
        result["_broadcastMsgCount"] = broadcastSvc?.MessageCount ?? 0;
        result["_broadcastError"] = broadcastSvc?.LastError ?? "";
    }
    else if (game == "rfactor2")
    {
        var opp = raw.ReadRf2Opponents();
        var entries = opp.TryGetValue("entries", out var e) ? (List<Dictionary<string, object?>>?)e ?? [] : [];
        result["game"] = "rFactor 2";
        result["totalOpponents"] = opp.TryGetValue("_count", out var c) ? Convert.ToInt32(c) : 0;
        result["entries"] = entries;
    }
    else if (game == "raccoroom")
    {
        var values = raw.ReadR3eOpponents();
        var hasError = values.TryGetValue("_error", out var errVal);
        var numCars = values.TryGetValue("NumCars", out var nc) ? Convert.ToInt32(nc) : 0;
        result["_debug_valuesCount"] = values.Count;
        result["_debug_hasNumCars"] = values.ContainsKey("NumCars");
        result["_debug_numCars"] = numCars;
        result["_debug_hasDriverData0"] = values.ContainsKey("DriverData[0]_Name");
        if (hasError) result["_debug_error"] = errVal;
        var entries = new List<Dictionary<string, object?>>();
        for (int i = 0; i < numCars && i < 128; i++)
        {
            var entry = new Dictionary<string, object?>();
            var prefix = $"DriverData[{i}]";
            ReadOpponentField(values, entry, $"{prefix}_Name", "name");
            ReadOpponentField(values, entry, $"{prefix}_CarNumber", "carNumber");
            ReadOpponentField(values, entry, $"{prefix}_Place", "place");
            ReadOpponentField(values, entry, $"{prefix}_PlaceClass", "placeClass");
            ReadOpponentField(values, entry, $"{prefix}_CompletedLaps", "laps");
            ReadOpponentField(values, entry, $"{prefix}_LapTimeCurrentSelf", "lapTimeCurrent");
            ReadOpponentField(values, entry, $"{prefix}_TimeDeltaFront", "gapFront");
            ReadOpponentField(values, entry, $"{prefix}_TimeDeltaBehind", "gapBehind");
            ReadOpponentField(values, entry, $"{prefix}_CarSpeed", "speedMs");
            ReadOpponentField(values, entry, $"{prefix}_InPitlane", "inPits");
            ReadOpponentField(values, entry, $"{prefix}_PitStopStatus", "pitStatus");
            ReadOpponentField(values, entry, $"{prefix}_NumPitstops", "pitStops");
            ReadOpponentField(values, entry, $"{prefix}_TrackSector", "sector");
            ReadOpponentField(values, entry, $"{prefix}_DrsState", "drs");
            ReadOpponentField(values, entry, $"{prefix}_PtpState", "ptp");
            ReadOpponentField(values, entry, $"{prefix}_VirtualEnergy", "virtualEnergy");
            ReadOpponentField(values, entry, $"{prefix}_Penalty_DT", "penaltyDt");
            ReadOpponentField(values, entry, $"{prefix}_Penalty_SG", "penaltySg");
            ReadOpponentField(values, entry, $"{prefix}_Penalty_TD", "penaltyTime");
            ReadOpponentField(values, entry, $"{prefix}_TireTypeFront", "tireFront");
            ReadOpponentField(values, entry, $"{prefix}_TireTypeRear", "tireRear");
            ReadOpponentField(values, entry, $"{prefix}_FinishStatus", "finished");
            entry["index"] = i;
            if (entry.Count > 2) entries.Add(entry);
        }
        result["game"] = "RaceRoom Racing Experience";
        result["totalOpponents"] = numCars;
        result["entries"] = entries;
    }
    else if (game == "lemansultimate")
    {
        var values = raw.ReadRawValues(game, "scoring");
        var numVeh = values.TryGetValue("S_numVehicles", out var nv) ? Convert.ToInt32(nv) : 0;
        var entries = new List<Dictionary<string, object?>>();
        for (int i = 0; i < numVeh && i < 104; i++)
        {
            var prefix = $"V{i}";
            var entry = new Dictionary<string, object?>();
            ReadOpponentField(values, entry, $"{prefix}_name", "name");
            ReadOpponentField(values, entry, $"{prefix}_driver", "driver");
            ReadOpponentField(values, entry, $"{prefix}_isPlayer", "isPlayer");
            ReadOpponentField(values, entry, $"{prefix}_control", "control");
            ReadOpponentField(values, entry, $"{prefix}_place", "place");
            ReadOpponentField(values, entry, $"{prefix}_totalLaps", "laps");
            ReadOpponentField(values, entry, $"{prefix}_bestLapTime", "bestLapTime");
            ReadOpponentField(values, entry, $"{prefix}_lastLapTime", "lastLapTime");
            ReadOpponentField(values, entry, $"{prefix}_timeBehindNext", "gapFront");
            ReadOpponentField(values, entry, $"{prefix}_timeBehindLeader", "gapLeader");
            ReadOpponentField(values, entry, $"{prefix}_inPits", "inPits");
            ReadOpponentField(values, entry, $"{prefix}_fuelFraction", "fuel");
            ReadOpponentField(values, entry, $"{prefix}_drsState", "drs");
            ReadOpponentField(values, entry, $"{prefix}_vehicleClass", "vehicleClass");
            ReadOpponentField(values, entry, $"{prefix}_finishStatus", "finished");
            ReadOpponentField(values, entry, $"{prefix}_underYellow", "underYellow");
            ReadOpponentField(values, entry, $"{prefix}_flag", "flag");
            ReadOpponentField(values, entry, $"{prefix}_estimatedLapTime", "estimatedLapTime");
            ReadOpponentField(values, entry, $"{prefix}_numPenalties", "penalties");
            entry["index"] = i;
            if (entry.Count > 2) entries.Add(entry);
        }
        result["game"] = "Le Mans Ultimate";
        result["totalOpponents"] = numVeh;
        result["entries"] = entries;
    }
    else if (game == "acevo")
    {
        var values = raw.ReadRawValues(game, "graphics");
        result["_debug_valuesCount"] = values.Count;

        // Forward diagnostics from the raw reader
        foreach (var dk in new[] { "_off_ActiveCars", "_off_TotalDrivers", "_off_CurrentPos", "_off_CarCoords",
            "_raw_ActiveCars", "_raw_TotalDrivers", "_raw_CurrentPos", "_raw_CC0_X", "_raw_CC0_Y", "_raw_CC0_Z", "_off_error" })
        {
            if (values.TryGetValue(dk, out var dv))
                result[dk] = dv;
        }

        if (values.Count > 0)
        {
            int activeCars = values.TryGetValue("ActiveCars", out var ac) ? Convert.ToInt32(ac) : 0;
            int totalDrivers = values.TryGetValue("TotalDrivers", out var td) ? Convert.ToInt32(td) : 0;
            int currentPos = values.TryGetValue("CurrentPos", out var cp) ? Convert.ToInt32(cp) : 0;
            values.TryGetValue("GapAhead", out var gapAhead);
            values.TryGetValue("GapBehind", out var gapBehind);

            // ActiveCars is the game's own count of cars with active telemetry
            int limit = activeCars > 0 ? activeCars
                      : totalDrivers > 0 ? totalDrivers
                      : 0;

            var entries = new List<Dictionary<string, object?>>();
            for (int i = 0; i < limit; i++)
            {
                values.TryGetValue($"CarCoordinates_{i}_X", out var x);
                values.TryGetValue($"CarCoordinates_{i}_Y", out var y);
                values.TryGetValue($"CarCoordinates_{i}_Z", out var z);
                entries.Add(new Dictionary<string, object?>
                {
                    ["posX"] = x ?? 0f,
                    ["posY"] = y ?? 0f,
                    ["posZ"] = z ?? 0f,
                    ["index"] = i
                });
            }

            result["game"] = "Assetto Corsa EVO";
            result["totalOpponents"] = totalDrivers;
            result["activeCars"] = activeCars;
            result["currentPos"] = currentPos;
            result["gapAhead"] = gapAhead ?? 0f;
            result["gapBehind"] = gapBehind ?? 0f;
            result["entries"] = entries;
        }
    }
    else if (game == "assettocorsa")
    {
        var values = raw.ReadRawValues(game, "static");
        if (values.TryGetValue("NumCars", out var nc))
            result["totalOpponents"] = nc;
        result["game"] = "Assetto Corsa (AC1)";
        result["entries"] = Array.Empty<object>();
    }

    var hasRes = result.Any(kvp => !kvp.Key.StartsWith("_"));
    if (!hasRes)
        throw new InvalidOperationException("Cannot read opponent data. Is the game running?");

    return result;
}

static void ReadOpponentField(Dictionary<string, object?> values, Dictionary<string, object?> entry, string rawKey, string entryKey)
{
    if (values.TryGetValue(rawKey, out var val) && val != null)
    {
        // Also try with dots replaced by underscores
        entry[entryKey] = val;
    }
    else
    {
        // Try alternative key formats
        var altKey = rawKey.Replace('.', '_');
        if (values.TryGetValue(altKey, out var val2) && val2 != null)
            entry[entryKey] = val2;
    }
}

api.MapGet("/raw/{game}/coverage", (string game, RawDataService raw, SourceAnalyzerService analyzer) =>
{
    var allRawFields = raw.GetRawFields(game, "physics");
    if (game != "lemansultimate")
    {
        allRawFields.AddRange(raw.GetRawFields(game, "graphics"));
        allRawFields.AddRange(raw.GetRawFields(game, "static"));
    }

    var report = analyzer.GetGameCoverageReport(game, allRawFields);
    return Results.Json(report, new JsonSerializerOptions { WriteIndented = true });
});

// ===== LOGGING ENDPOINTS =====

api.MapPost("/log/coverage/{game}", (string game, RawDataService raw, DataLoggerService logger, SourceAnalyzerService analyzer) =>
{
    var allRawFields = raw.GetRawFields(game, "physics");
    if (game == "lemansultimate")
    {
        allRawFields.AddRange(raw.GetRawFields(game, "header"));
        allRawFields.AddRange(raw.GetRawFields(game, "scoring"));
    }
    else
    {
        allRawFields.AddRange(raw.GetRawFields(game, "graphics"));
        allRawFields.AddRange(raw.GetRawFields(game, "static"));
    }

    var report = analyzer.GetGameCoverageReport(game, allRawFields);
    var name = GameDisplayName(game);
    var filePath = logger.LogCoverage(game, name, report);

    var rawCount = report.TryGetValue("totalRaw", out var r) ? Convert.ToInt32(r) : 0;
    var mappedCount = report.TryGetValue("mappedInMainApp", out var m) ? Convert.ToInt32(m) : 0;
    return Results.Json(new
    {
        success = true,
        path = filePath,
        rawFieldCount = rawCount,
        mappedFieldCount = mappedCount
    });
});

api.MapPost("/log/raw/{game}", (string game, RawDataService raw, DataLoggerService logger) =>
{
    var sections = new[] { "physics", "graphics", "static" };
    if (game == "lemansultimate") sections = ["physics"];

    var rawData = new Dictionary<string, object?>();
    foreach (var section in sections)
    {
        var values = raw.ReadRawValues(game, section);
        foreach (var (k, v) in values)
            rawData[k] = v;
    }

    if (rawData.Count == 0)
        return Results.Json(new { success = false, message = "Cannot read raw data" });

    var name = GameDisplayName(game);
    var filePath = logger.LogRawData(game, name, rawData);

    return Results.Json(new { success = true, path = filePath, fieldCount = rawData.Count });
});

api.MapPost("/log/mapped/{game}", (string game, TelemetryService ts, DataLoggerService logger) =>
{
    var snap = ts.TryRead();
    if (snap == null)
        return Results.Json(new { success = false, message = "Not connected or no data" });

    var name = GameDisplayName(game);
    var filePath = logger.LogMappedData(game, name, snap);

    return Results.Json(new { success = true, path = filePath, fieldCount = snap.Count });
});

api.MapPost("/log/reference/{game}", (string game, RawDataService raw, DataLoggerService logger, TelemetryService ts, SourceAnalyzerService analyzer) =>
{
    var rawFields = raw.GetRawFields(game, "physics");
    if (game == "lemansultimate")
    {
        rawFields.AddRange(raw.GetRawFields(game, "header"));
        rawFields.AddRange(raw.GetRawFields(game, "scoring"));
    }
    else
    {
        rawFields.AddRange(raw.GetRawFields(game, "graphics"));
        rawFields.AddRange(raw.GetRawFields(game, "static"));
    }
    var sections = new[] { "physics", "graphics", "static" };
    if (game == "lemansultimate")
        sections = ["header", "scoring", "physics"];
    if (game == "rfactor2")
        sections = ["physics"];
    var rawValues = new Dictionary<string, object?>();
    foreach (var section in sections)
    {
        var values = raw.ReadRawValues(game, section);
        foreach (var (k, v) in values)
            rawValues[k] = v;
    }

    var mapped = ts.TryRead();
    var coverageReport = analyzer.GetGameCoverageReport(game, rawFields);
    var name = GameDisplayName(game);
    var filePath = logger.LogReference(game, name, rawFields, rawValues, mapped, coverageReport);

    return Results.Json(new { success = true, path = filePath, rawFieldCount = rawFields.Count });
});

api.MapGet("/log/files", (DataLoggerService logger) =>
{
    var files = logger.GetLogFiles().Select(f => new
    {
        name = Path.GetFileName(f),
        path = f,
        size = new FileInfo(f).Length,
        modified = File.GetLastWriteTime(f)
    });
    return Results.Json(files);
});

static string GameDisplayName(string id) => id switch
{
    "acevo" => "Assetto Corsa EVO",
    "assettocorsa" => "Assetto Corsa (AC1)",
    "raccoroom" => "RaceRoom Racing Experience",
    "lemansultimate" => "Le Mans Ultimate",
    _ => id
};

Console.WriteLine(@"
  ╔══════════════════════════════════════════╗
  ║     Telemetry Browser - Shared Memory    ║
  ║         Diagnostic Tool v1.0             ║
  ╚══════════════════════════════════════════╝
");
Console.WriteLine($"  Log directory: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner", "telemetry-browser")}");
Console.WriteLine("  Listening on http://localhost:5200");
Console.WriteLine("  Open a browser to view telemetry data.\n");

app.Run();
