using System.Globalization;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class SnapshotFileEntry
{
    public string FilePath { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public long FileSize { get; init; }
    public DateTime Timestamp { get; init; }
    public string ProfileName { get; set; } = "";
    public bool HasReplay { get; set; }
}

public sealed class SnapshotCsvData
{
    public List<string> CsvLines { get; init; } = [];
    public string StatsText { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public float TorqueNm { get; init; } = 5.5f;
    public int RowCount => CsvLines.Count;
    public string? SnapshotFilePath { get; init; }

    public float? ExtractStatValue(string label)
    {
        if (string.IsNullOrEmpty(StatsText)) return null;
        var lines = StatsText.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(':');
                if (parts.Length >= 2 && float.TryParse(parts[^1].Trim().Split(' ')[0],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    return val;
            }
        }
        return null;
    }
}

public static class SnapshotFileLoader
{
    public static string SnapshotsDirectory =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "snapshots");

    public static List<SnapshotFileEntry> LoadSnapshotFiles()
    {
        var dir = SnapshotsDirectory;
        if (!Directory.Exists(dir))
            return [];

        var entries = new List<SnapshotFileEntry>();
        foreach (var file in Directory.GetFiles(dir, "snapshot_*.txt")
                     .OrderByDescending(f => f))
        {
            var info = new FileInfo(file);
            var ts = ParseTimestampFromName(file);
            entries.Add(new SnapshotFileEntry
            {
                FilePath = file,
                FileSize = info.Length,
                Timestamp = ts,
                HasReplay = File.Exists(System.IO.Path.ChangeExtension(file, ".html")
                    .Replace("snapshot_", "replay_"))
            });
        }

        foreach (var entry in entries)
        {
            var csv = ParseCsvData(entry.FilePath);
            if (csv != null)
                entry.ProfileName = csv.ProfileName;
        }

        return entries;
    }

    public static SnapshotCsvData? ParseCsvData(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        var text = File.ReadAllText(filePath);

        string profileName = "";
        var profileMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"Profile:\s*(.+)");
        if (profileMatch.Success)
            profileName = profileMatch.Groups[1].Value.Trim();

        float torqueNm = 5.5f;
        var torqueMatch = System.Text.RegularExpressions.Regex.Match(text,
            @"WheelMaxTorque:\s*([\d.]+)");
        if (torqueMatch.Success)
            float.TryParse(torqueMatch.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out torqueNm);

        string statsSection = "";
        int statsStart = text.IndexOf("=== PROFILER STATISTICS");
        int csvStart = text.IndexOf("=== TIME SERIES DATA");
        if (statsStart >= 0)
        {
            int statsEnd = csvStart > statsStart ? csvStart : text.Length;
            statsSection = text[statsStart..statsEnd].Trim();
        }

        var csvLines = new List<string>();
        if (csvStart >= 0)
        {
            int dataStart = text.IndexOf("Time,", csvStart);
            if (dataStart >= 0)
            {
                int lineStart = dataStart;
                while (lineStart < text.Length)
                {
                    int lineEnd = text.IndexOf('\n', lineStart);
                    if (lineEnd < 0) lineEnd = text.Length;
                    var line = text[lineStart..lineEnd].Trim();
                    if (string.IsNullOrEmpty(line)) break;
                    csvLines.Add(line);
                    lineStart = lineEnd + 1;
                }
            }
        }

        return new SnapshotCsvData
        {
            CsvLines = csvLines,
            StatsText = statsSection,
            ProfileName = profileName,
            TorqueNm = torqueNm,
            SnapshotFilePath = filePath
        };
    }

    public static SnapshotCsvData? LoadLatestSnapshot()
    {
        var files = LoadSnapshotFiles();
        return files.Count > 0 ? ParseCsvData(files[0].FilePath) : null;
    }

    private static DateTime ParseTimestampFromName(string filePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(filePath);
        if (name.StartsWith("snapshot_"))
        {
            var ts = name["snapshot_".Length..];
            if (ts.Length >= 15 && DateTime.TryParseExact(
                    ts.AsSpan(0, 15),
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dt))
                return dt;
        }
        return File.GetLastWriteTime(filePath);
    }
}
