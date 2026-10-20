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
    Recorded,         // Tier 4: Self-recorded from driving history
    OpenRacetrackDb,  // Named-layout traces from Open Racetrack DB (ODbL) — variants OSM relations lack
    OsmNetwork        // Loop assembled from the venue's OSM raceway ways (TrackNetworkLoops) — variants nobody published
}

public class TrackDetailedInfo
{
    public string TrackName { get; set; } = "";

    /// <summary>
    /// Layout / configuration name (EVO TrackConfiguration, R3E LayoutName).
    /// Empty when the game reports no layout. Part of the cache key.
    /// </summary>
    public string TrackConfig { get; set; } = "";
    public List<TrackCornerInfo> Corners { get; set; } = new();
    public List<TrackPoint>? TrackLayout { get; set; }
    public TrackPitInfo? Pit { get; set; }
    public TrackPoint? StartFinish { get; set; }

    /// <summary>
    /// Rotation-free SF anchor: fractional distance (0..1) along TrackLayout where
    /// the game's Npos=0 (start/finish) sits, solved by shape-correlating a driven
    /// (recorded) lap against the OSM layout. -1 = unsolved.
    /// </summary>
    public double SfAnchorFraction { get; set; } = -1;

    /// <summary>
    /// True when the OSM layout order runs opposite to the game's driving direction.
    /// Solved together with <see cref="SfAnchorFraction"/>.
    /// </summary>
    public bool SfAnchorReversed { get; set; }

    /// <summary>How the SF anchor was determined: "heuristic", "wrap", "shape".</summary>
    public string SfAnchorSource { get; set; } = "heuristic";
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
    /// v4: Switched to OSM node-ID adjacency for circuit ordering (replaces coordinate proximity)
    /// v3: Added foreign way filtering (kart tracks, moto, etc.)
    /// v10: Added TrackConfig (layout-aware cache keys) + OfficialLengthM validation
    /// v11: Added SfAnchorFraction/SfAnchorReversed/SfAnchorSource (shape-solved SF anchor)
    /// </summary>
    public const int CurrentCacheVersion = 11;
}

public static class TrackMapKeys
{
    /// <summary>
    /// Build a layout-aware key: "track" or "track__config".
    /// Bare track name is kept for backwards compatibility (legacy files).
    /// </summary>
    public static string BuildKey(string trackName, string? config)
    {
        var track = (trackName ?? "").Trim();
        var cfg = (config ?? "").Trim();
        if (string.IsNullOrEmpty(cfg)) return track;
        return $"{track}__{cfg}";
    }

    public static string Sanitize(string key) =>
        string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
}

/// <summary>
/// Venue gazetteer (name → coordinates), three tiers searched in order:
///  1. Data/tracks.json — curated, game-oriented names and aliases;
///  2. Data/tracks_wikidata.json — Wikidata snapshot, ~1,200 motorsport tracks (CC0);
///  3. Open Racetrack DB venue index — 763 venues with aliases (ODbL).
/// Exact (normalised) matches in any tier beat partial matches; partial matches go
/// tier by tier, longest key first, on word boundaries (see <see cref="TrackNameMatcher"/>).
/// </summary>
public static class TrackDatabase
{
    private sealed class Gazetteer
    {
        public readonly Dictionary<string, TrackDatabaseEntry> RawExact = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, TrackDatabaseEntry> NormExact = new(StringComparer.Ordinal);
        public readonly List<(string key, TrackDatabaseEntry value)> Curated = new();
        public readonly List<(string key, TrackDatabaseEntry value)> Wikidata = new();
        public readonly List<(string key, TrackDatabaseEntry value)> Ortdb = new();
    }

    private static Gazetteer _gaz = new();
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

    private static readonly object _loadLock = new();

    /// <summary>
    /// Thread-safe one-time load. The gazetteer is read from the UI thread and from
    /// background OSM fetch/parse tasks; the old version set _loaded before filling the
    /// dictionary, so a concurrent first caller saw an empty gazetteer (lost aliases →
    /// relation skipped). Build into a fresh dictionary, publish it, then set _loaded.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (Volatile.Read(ref _loaded)) return;
        lock (_loadLock)
        {
            if (_loaded) return;
            var built = BuildGazetteer();
            _gaz = built;
            Volatile.Write(ref _loaded, true);
        }
    }

    private static List<TrackDatabaseEntry> LoadEmbeddedEntries(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream == null) return new();
            using var reader = new StreamReader(stream);
            // The JSON files use lowercase keys ("tracks", "name", "aliases"); without
            // case-insensitive binding nothing deserialized and only the 36-entry
            // fallback list below was ever used (no Le Mans, Sebring, Bahrain...).
            var db = JsonSerializer.Deserialize<TrackDatabaseFile>(reader.ReadToEnd(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return db?.Tracks?.ToList() ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static Gazetteer BuildGazetteer()
    {
        var gaz = new Gazetteer();

        var entries = LoadEmbeddedEntries("AcEvoFfbTuner.Core.Data.tracks.json");

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

        foreach (var entry in entries)
            AddTier(gaz, gaz.Curated, entry, minKeyLength: 3, rawExact: true);

        foreach (var entry in LoadEmbeddedEntries("AcEvoFfbTuner.Core.Data.tracks_wikidata.json"))
            AddTier(gaz, gaz.Wikidata, entry, minKeyLength: 5, rawExact: false);

        foreach (var venue in OpenRacetrackDbIndex.Venues)
        {
            if (venue.Latitude is not double lat || venue.Longitude is not double lon) continue;
            AddTier(gaz, gaz.Ortdb, new TrackDatabaseEntry
            {
                Name = venue.Name,
                Aliases = venue.Aliases,
                Latitude = (float)lat,
                Longitude = (float)lon,
            }, minKeyLength: 5, rawExact: false);
        }
        return gaz;
    }

    private static void AddTier(Gazetteer gaz, List<(string key, TrackDatabaseEntry value)> tier,
        TrackDatabaseEntry entry, int minKeyLength, bool rawExact)
    {
        entry.Aliases ??= Array.Empty<string>();
        foreach (var name in entry.Aliases.Prepend(entry.Name))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (rawExact) gaz.RawExact.TryAdd(name.Trim(), entry);
            var key = TrackNameMatcher.Normalize(name, splitDigits: true);
            if (key.Length == 0) continue;
            // Exact equality is safe at any length ("AVUS"); only partial matching needs the minimum.
            gaz.NormExact.TryAdd(key, entry); // first tier to claim a name keeps it
            if (key.Length < minKeyLength) continue;
            tier.Add((key, entry));
        }
    }

    public static (float lat, float lon)? LookupTrackLocation(string trackName)
    {
        var entry = GetEntry(trackName);
        return entry != null ? (entry.Latitude, entry.Longitude) : null;
    }

    /// <summary>
    /// Exact name/alias match first (raw, then normalised across all tiers), then partial
    /// word-boundary matches tier by tier. Games append layout variants to the name
    /// ("Circuit de la Sarthe Mulsanne"), so the partial pass matters; the LONGEST
    /// matching key wins so a short key ("spa", "ims", "vir") can't beat a specific one.
    /// </summary>
    public static TrackDatabaseEntry? GetEntry(string trackName)
    {
        EnsureLoaded();
        var gaz = _gaz;

        if (string.IsNullOrWhiteSpace(trackName))
            return null;

        if (gaz.RawExact.TryGetValue(trackName.Trim(), out var entry))
            return entry;

        var norm = TrackNameMatcher.Normalize(trackName, splitDigits: true);
        if (gaz.NormExact.TryGetValue(norm, out entry))
            return entry;

        return TrackNameMatcher.BestMatch(norm, gaz.Curated, minKeyLength: 3, minQueryLengthForReverse: 4)
            ?? TrackNameMatcher.BestMatch(norm, gaz.Wikidata, minKeyLength: 5, minQueryLengthForReverse: 6)
            ?? TrackNameMatcher.BestMatch(norm, gaz.Ortdb, minKeyLength: 5, minQueryLengthForReverse: 6)
            ?? DistinctiveWordMatch(norm, gaz);
    }

    /// <summary>
    /// Last resort: one distinctive word of the game's name that names exactly one venue.
    /// "Lusail Short Circuit" contains no whole key of "Lusail International Circuit", nor
    /// "Estoril Circuit" of "Circuito do Estoril". A word naming venues more than 5 km apart is
    /// ambiguous and skipped (see <see cref="TrackNameMatcher.DistinctiveWords"/>).
    /// </summary>
    private static TrackDatabaseEntry? DistinctiveWordMatch(string norm, Gazetteer gaz)
    {
        foreach (var word in TrackNameMatcher.DistinctiveWords(norm))
        {
            TrackDatabaseEntry? hit = null;
            bool ambiguous = false;
            foreach (var tier in new[] { gaz.Curated, gaz.Wikidata, gaz.Ortdb })
            {
                foreach (var (key, entry) in tier)
                {
                    if (!TrackNameMatcher.ContainsWords(key, word)) continue;
                    if (hit == null) hit = entry;
                    else if (TrackNameMatcher.DistanceM(hit.Latitude, hit.Longitude, entry.Latitude, entry.Longitude) > 5000)
                    {
                        ambiguous = true;
                        break;
                    }
                }
                if (ambiguous) break;
            }
            if (hit != null && !ambiguous) return hit;
        }
        return null;
    }
}