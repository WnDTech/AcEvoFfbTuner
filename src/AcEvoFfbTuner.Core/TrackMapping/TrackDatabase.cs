using System.Reflection;
using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

public class TrackDatabaseEntry
{
    public string Name { get; set; } = "";
    public string[] Aliases { get; set; } = Array.Empty<string>();
    public float Latitude { get; set; }
    public float Longitude { get; set; }
    public string[] Games { get; set; } = Array.Empty<string>();
}

public class TrackDatabaseFile
{
    public int Version { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Games { get; set; }
    public TrackDatabaseEntry[] Tracks { get; set; } = Array.Empty<TrackDatabaseEntry>();
}

public class TrackCornerInfo
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class TrackPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public TrackPoint() { }
    public TrackPoint(double lat, double lon) { Latitude = lat; Longitude = lon; }
}

public class TrackPitInfo
{
    public double EntryLatitude { get; set; }
    public double EntryLongitude { get; set; }
    public double ExitLatitude { get; set; }
    public double ExitLongitude { get; set; }
    public List<TrackPoint>? Layout { get; set; }
}

public enum TrackDataSource
{
    Unknown = 0,
    OsmRelation,      // Tier 1: OSM route relation query (curated, ordered circuit data)
    OsmBoundingBox,   // Tier 3: Bounding-box way search (current implementation, fallback)
    Recorded          // Tier 4: Self-recorded from driving history
}

public class TrackDetailedInfo
{
    public string TrackName { get; set; } = "";
    public List<TrackCornerInfo> Corners { get; set; } = new();
    public List<TrackPoint>? TrackLayout { get; set; }
    public TrackPitInfo? Pit { get; set; }
    public TrackPoint? StartFinish { get; set; }
    public string? Surface { get; set; }
    public string? MaxSpeed { get; set; }
    public DateTime FetchedAt { get; set; }

    /// <summary>Which data source produced this track data.</summary>
    public TrackDataSource DataSource { get; set; } = TrackDataSource.Unknown;

    /// <summary>Confidence in the data accuracy (0.0 = unreliable, 1.0 = verified).</summary>
    public float ConfidenceScore { get; set; } = 0.5f;

    /// <summary>Official track length in meters (if known from the data source).</summary>
    public double TrackLengthM { get; set; }

    /// <summary>Npos values (0..1) for sector boundaries, e.g. [0.0, 0.333, 0.666, 1.0].</summary>
    public double[] SectorBoundaries { get; set; } = Array.Empty<double>();

    /// <summary>
    /// Cache format version. Increment when cache structure changes to force refetch.
    /// Default 0 means unversioned (old cache) — will be invalidated on load.
    /// </summary>
    public int CacheVersion { get; set; }

    /// <summary>
    /// Current cache version. Bump when the data model or processing changes.
    /// v3: Added foreign way filtering (kart tracks, moto, etc.)
    /// </summary>
    public const int CurrentCacheVersion = 3;
}

public static class TrackDatabase
{
    private static Dictionary<string, TrackDatabaseEntry> _lookup = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    private static readonly (string name, float lat, float lon)[] FallbackTracks =
    {
        ("nurburgring", 50.3296f, 6.9399f),
        ("nürburgring", 50.3296f, 6.9399f),
        ("nuerburgring", 50.3296f, 6.9399f),
        ("spa", 50.4369f, 5.9705f),
        ("circuit de spa", 50.4369f, 5.9705f),
        ("spa francorchamps", 50.4369f, 5.9705f),
        ("imola", 44.3410f, 11.7119f),
        ("brands hatch", 51.3575f, 0.2600f),
        ("donington", 52.8299f, -1.3779f),
        ("donington park", 52.8299f, -1.3779f),
        ("silverstone", 52.0718f, -1.0143f),
        ("monza", 45.6191f, 9.2826f),
        ("laguna seca", 36.5875f, -121.7556f),
        ("suzuka", 34.8431f, 136.5396f),
        ("fuji", 35.3717f, 138.9272f),
        ("mount panorama", -33.4597f, 149.5547f),
        ("bathurst", -33.4597f, 149.5547f),
        ("daytona", 29.1919f, -81.0717f),
        ("kyalami", -25.9881f, 28.0697f),
        ("barcelona", 41.5697f, 2.2586f),
        ("catalunya", 41.5697f, 2.2586f),
        ("monaco", 43.7342f, 7.4206f),
        ("interlagos", -23.7036f, -46.6997f),
        ("hockenheim", 49.3283f, 8.5761f),
        ("zandvoort", 52.3888f, 4.5409f),
        ("red bull ring", 47.2217f, 14.7647f),
        ("spielberg", 47.2217f, 14.7647f),
        ("oval", 29.1919f, -81.0717f),
        ("valencia", 39.4664f, -0.3281f),
        ("ricard", 43.2497f, 5.7878f),
        ("paul ricard", 43.2497f, 5.7878f),
        ("oschersleben", 52.0289f, 11.2781f),
        ("misano", 43.9578f, 12.6836f),
        ("phillip island", -38.5031f, 145.1861f),
        ("road america", 43.8022f, -87.9892f),
    };

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var entries = new List<TrackDatabaseEntry>();

        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(
                "AcEvoFfbTuner.Core.Data.tracks.json");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var db = JsonSerializer.Deserialize<TrackDatabaseFile>(json);
                if (db?.Tracks != null)
                    entries.AddRange(db.Tracks);
            }
        }
        catch
        {
        }

        // Fallback: if JSON failed, use hardcoded data
        if (entries.Count == 0)
        {
            foreach (var (name, lat, lon) in FallbackTracks)
            {
                entries.Add(new TrackDatabaseEntry
                {
                    Name = name,
                    Aliases = Array.Empty<string>(),
                    Latitude = lat,
                    Longitude = lon
                });
            }
        }

        // Build lookup: add all canonical names + aliases
        _lookup.Clear();
        foreach (var entry in entries)
        {
            AddEntry(entry.Name, entry);
            foreach (var alias in entry.Aliases)
                AddEntry(alias, entry);
        }

        void AddEntry(string key, TrackDatabaseEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(key) && !_lookup.ContainsKey(key))
                _lookup[key.Trim()] = entry;
        }
    }

    public static (float lat, float lon)? LookupTrackLocation(string trackName)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        // Try exact match
        if (_lookup.TryGetValue(trackName.Trim(), out var entry))
            return (entry.Latitude, entry.Longitude);

        // Try substring match against all stored names
        var norm = trackName.Trim().ToLowerInvariant();
        foreach (var kvp in _lookup)
        {
            if (norm.Contains(kvp.Key.ToLowerInvariant()) ||
                kvp.Key.ToLowerInvariant().Contains(norm))
                return (kvp.Value.Latitude, kvp.Value.Longitude);
        }

        return null;
    }

    public static TrackDatabaseEntry? GetEntry(string trackName)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        if (_lookup.TryGetValue(trackName.Trim(), out var entry))
            return entry;

        var norm = trackName.Trim().ToLowerInvariant();
        foreach (var kvp in _lookup)
        {
            if (norm.Contains(kvp.Key.ToLowerInvariant()) ||
                kvp.Key.ToLowerInvariant().Contains(norm))
                return kvp.Value;
        }

        return null;
    }
}