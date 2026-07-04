using System.Text.Json;

namespace TelemetryBrowser.Services;

public sealed class DataLoggerService
{
    private readonly string _logDir;
    private readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true, MaxDepth = 64 };

    public DataLoggerService()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "telemetry-browser");
        Directory.CreateDirectory(_logDir);
    }

    public string LogDirectory => _logDir;

    public string LogCoverage(string gameId, string gameName, object coverageData)
    {
        var timestamp = DateTime.Now;
        var fileDate = timestamp.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_logDir, $"coverage_{gameId}_{fileDate}.json");

        var entry = new
        {
            loggedAt = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            game = new { id = gameId, name = gameName },
            coverage = coverageData
        };

        var json = JsonSerializer.Serialize(entry, _jsonOpts);
        File.AppendAllText(filePath, json + "\n\n");
        return filePath;
    }

    public string LogRawData(string gameId, string gameName, Dictionary<string, object?> rawData)
    {
        var timestamp = DateTime.Now;
        var fileDate = timestamp.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_logDir, $"rawdata_{gameId}_{fileDate}.json");

        var entry = new
        {
            loggedAt = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            game = new { id = gameId, name = gameName },
            fieldCount = rawData.Count,
            fields = rawData
        };

        var json = JsonSerializer.Serialize(entry, _jsonOpts);
        File.AppendAllText(filePath, json + "\n\n");
        return filePath;
    }

    public string LogMappedData(string gameId, string gameName, FlatTelemetrySnapshot? snapshot)
    {
        if (snapshot == null || snapshot.Count == 0)
            return "";

        var timestamp = DateTime.Now;
        var fileDate = timestamp.ToString("yyyy-MM-dd");
        var filePath = Path.Combine(_logDir, $"mapped_{gameId}_{fileDate}.json");

        var fields = new Dictionary<string, object?>();
        foreach (var (k, v) in snapshot)
            if (!k.StartsWith("_"))
                fields[k] = v;

        var entry = new
        {
            loggedAt = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            game = new { id = gameId, name = gameName },
            fieldCount = fields.Count,
            fields
        };

        var json = JsonSerializer.Serialize(entry, _jsonOpts);
        File.AppendAllText(filePath, json + "\n\n");
        return filePath;
    }

    public string LogReference(string gameId, string gameName,
        List<RawFieldInfo> rawFields,
        Dictionary<string, object?> rawValues,
        FlatTelemetrySnapshot? mappedSnapshot,
        Dictionary<string, object>? coverageReport = null)
    {
        var timestamp = DateTime.Now;
        var filePath = Path.Combine(_logDir, $"reference_{gameId}.txt");
        var existing = File.Exists(filePath) ? File.ReadAllText(filePath) : "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine($"  Telemetry Reference Log — {gameName} ({gameId})");
        sb.AppendLine($"  Logged at: {timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        // Section 1: Raw native fields
        sb.AppendLine($"── Raw Native Fields ({rawFields.Count} total) ──");
        sb.AppendLine();

        var grouped = rawFields
            .GroupBy(f => f.Name.Contains('.') ? f.Name.Split('.')[0] : "Other")
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"  [{group.Key}]");
            foreach (var f in group.OrderBy(x => x.Name))
            {
                var val = rawValues.TryGetValue(f.Name.Replace(".", "_"), out var v) ? FormatLogValue(v) : "—";
                var offsetStr = f.Offset >= 0 ? $"@{f.Offset,5} (0x{f.Offset:X4})" : "     —       ";
                sb.AppendLine($"    {offsetStr}  {f.Name,-48} {f.Type,-8} {f.Unit,-6} = {val}");
            }
            sb.AppendLine();
        }

        // Section 2: Mapped fields
        if (mappedSnapshot != null)
        {
            var mappedFields = mappedSnapshot
                .Where(kv => !kv.Key.StartsWith("_"))
                .OrderBy(kv => kv.Key)
                .ToList();
            sb.AppendLine($"── Mapped Fields ({mappedFields.Count}) ──");
            sb.AppendLine();
            foreach (var (key, val) in mappedFields)
            {
                sb.AppendLine($"  {key,-50} = {FormatLogValue(val)}");
            }
            sb.AppendLine();
        }

        // Section 3: Reader coverage analysis
        int mappedCount = 0, universalTotal = 0;
        var analysisSource = coverageReport?.TryGetValue("analysisSource", out var src) == true ? src as string : "struct-comparison";

        if (coverageReport != null)
        {
            mappedCount = coverageReport.TryGetValue("mappedInMainApp", out var mc) ? Convert.ToInt32(mc) : 0;
            universalTotal = coverageReport.TryGetValue("totalUniversalStructFields", out var ut) ? Convert.ToInt32(ut) : 0;
        }

        sb.AppendLine($"── Reader Coverage: {mappedCount}/{universalTotal} universal struct fields set (analysis: {analysisSource}) ──");
        sb.AppendLine();

        // List mapped fields
        if (coverageReport?.TryGetValue("mappedPhysFields", out var mp) == true && mp is System.Collections.IList mpList)
        {
            sb.AppendLine($"  Physics fields set by reader ({mpList.Count}):");
            foreach (var f in mpList) sb.AppendLine($"    {f}");
            sb.AppendLine();
        }
        if (coverageReport?.TryGetValue("mappedGfxFields", out var mg) == true && mg is System.Collections.IList mgList)
        {
            sb.AppendLine($"  Graphics fields set by reader ({mgList.Count}):");
            foreach (var f in mgList) sb.AppendLine($"    {f}");
            sb.AppendLine();
        }
        if (coverageReport?.TryGetValue("mappedStFields", out var ms) == true && ms is System.Collections.IList msList)
        {
            sb.AppendLine($"  Static fields set by reader ({msList.Count}):");
            foreach (var f in msList) sb.AppendLine($"    {f}");
            sb.AppendLine();
        }

        // List all raw native fields with their offsets
        sb.AppendLine($"── All Raw Native Fields ({rawFields.Count}) ──");
        sb.AppendLine();
        foreach (var f in rawFields.OrderBy(x => x.Name))
        {
            var val = rawValues.TryGetValue(f.Name.Replace(".", "_"), out var v) ? FormatLogValue(v) : "—";
            var offsetStr = f.Offset >= 0 ? $"@{f.Offset,5} (0x{f.Offset:X4})" : "     —       ";
            sb.AppendLine($"  {offsetStr}  {f.Name,-48} {f.Type,-8} {f.Unit,-6} = {val}");
        }
        sb.AppendLine();
        sb.AppendLine("═══════════════════════════════════════════════════════");
        sb.AppendLine();

        var newContent = sb.ToString();
        File.WriteAllText(filePath, existing + newContent);
        return filePath;
    }

    private static string FormatLogValue(object? val)
    {
        if (val == null) return "null";
        if (val is float f) return f.ToString("F4");
        if (val is double d) return d.ToString("F4");
        if (val is string s) return s.Length > 80 ? s[..77] + "..." : s;
        if (val is Array a) return $"[{string.Join(", ", a.Cast<object>().Take(6))}{(a.Length > 6 ? "..." : "")}]";
        return val.ToString() ?? "null";
    }

    private static List<string> GetMappedNames(Type t)
    {
        var names = new List<string>();
        foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            names.Add(f.Name);
        foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            names.Add(p.Name);
        return names;
    }

    public string[] GetLogFiles()
    {
        return Directory.GetFiles(_logDir, "*.json")
            .Concat(Directory.GetFiles(_logDir, "*.txt"))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToArray();
    }
}
