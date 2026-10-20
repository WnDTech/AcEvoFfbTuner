using System.Reflection;
using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>A venue from the bundled Open Racetrack DB index.</summary>
public sealed record OrtdbVenue(string Id, string Name, string[] Aliases, double? Latitude, double? Longitude,
    string File, int TraceCount);

/// <summary>One named layout trace of a venue (GeoJSON LineString, WGS84).</summary>
public sealed class OrtdbLayout
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Closed { get; init; }
    public double LengthM { get; init; }
    public string GeometryStatus { get; init; } = "";
    public List<TrackPoint> Trace { get; init; } = new();
    public TrackPoint? StartFinish { get; init; }
}

/// <summary>
/// Bundled Open Racetrack DB venue index (Data/OpenRacetrackDb/index.json, ODbL, pinned
/// commit — see NOTICE.md there). 763 venues with location + aliases; the per-layout
/// GeoJSON traces are fetched on demand by <see cref="OpenRacetrackDbService"/>.
/// </summary>
public static class OpenRacetrackDbIndex
{
    private static readonly Lazy<IReadOnlyList<OrtdbVenue>> _venues =
        new(LoadVenues, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<List<(string key, OrtdbVenue value)>> _keys =
        new(BuildKeys, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<OrtdbVenue> Venues => _venues.Value;

    private static IReadOnlyList<OrtdbVenue> LoadVenues()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("AcEvoFfbTuner.Core.Data.OpenRacetrackDb.index.json");
            if (stream == null) return Array.Empty<OrtdbVenue>();
            using var doc = JsonDocument.Parse(stream);
            var list = new List<OrtdbVenue>();
            foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
            {
                double? lat = null, lon = null;
                if (t.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Array && loc.GetArrayLength() == 2)
                {
                    lon = loc[0].GetDouble();
                    lat = loc[1].GetDouble();
                }
                var aliases = t.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array
                    ? al.EnumerateArray().Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray()
                    : Array.Empty<string>();
                list.Add(new OrtdbVenue(
                    t.GetProperty("id").GetString() ?? "",
                    t.GetProperty("name").GetString() ?? "",
                    aliases, lat, lon,
                    t.GetProperty("file").GetString() ?? "",
                    t.TryGetProperty("traceCount", out var tc) ? tc.GetInt32() : 0));
            }
            return list;
        }
        catch
        {
            return Array.Empty<OrtdbVenue>();
        }
    }

    private static List<(string key, OrtdbVenue value)> BuildKeys()
    {
        var keys = new List<(string key, OrtdbVenue value)>();
        foreach (var v in Venues)
        {
            if (v.TraceCount <= 0) continue;
            foreach (var n in v.Aliases.Prepend(v.Name))
            {
                var k = TrackNameMatcher.Normalize(n);
                if (k.Length >= 4) keys.Add((k, v));
            }
        }
        return keys;
    }

    /// <summary>
    /// Venue for a game track: by name (normalised, word-boundary, longest match) and by
    /// proximity to the venue GPS (nearest within 4 km). A name match farther than 15 km
    /// from known GPS is a name collision — proximity wins then.
    /// </summary>
    public static OrtdbVenue? FindVenue(string trackName, double? lat, double? lon)
    {
        var norm = TrackNameMatcher.Normalize(trackName);
        var byName = TrackNameMatcher.BestMatch(norm, _keys.Value, minKeyLength: 4, minQueryLengthForReverse: 6);

        bool hasGps = lat.HasValue && lon.HasValue && (lat.Value != 0 || lon.Value != 0);
        if (!hasGps) return byName;

        OrtdbVenue? byGps = null;
        double best = 4000;
        foreach (var v in Venues)
        {
            if (v.TraceCount <= 0 || v.Latitude is not double vl || v.Longitude is not double vo) continue;
            double d = TrackNameMatcher.DistanceM(lat!.Value, lon!.Value, vl, vo);
            if (d < best) { best = d; byGps = v; }
        }

        if (byName != null && (byName.Latitude is not double nl || byName.Longitude is not double no
            || TrackNameMatcher.DistanceM(lat!.Value, lon!.Value, nl, no) < 15000))
            return byName;
        return byGps;
    }
}

/// <summary>Pure parsing and selection helpers (unit-tested against real ORTDB files).</summary>
public static class OrtdbLayouts
{
    private const double LengthTolerance = 0.05;

    /// <summary>Parse a schema-1 layout GeoJSON: the "trace" LineString plus an optional S/F gate.</summary>
    public static OrtdbLayout? Parse(string layoutId, string layoutName, string geojson)
    {
        try
        {
            using var doc = JsonDocument.Parse(geojson);
            var root = doc.RootElement;
            bool closed = false;
            double lengthM = 0;
            string status = "";
            if (root.TryGetProperty("metadata", out var md))
            {
                // Aggregate "network" layouts (schema 2, MultiLineString) are branch views, not laps.
                if (md.TryGetProperty("geometryKind", out var kind) && kind.GetString() == "network") return null;
                closed = md.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
                lengthM = md.TryGetProperty("lengthM", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : 0;
                status = md.TryGetProperty("geometryStatus", out var s) ? s.GetString() ?? "" : "";
            }

            List<TrackPoint>? trace = null;
            TrackPoint? sf = null, start = null;
            foreach (var f in root.GetProperty("features").EnumerateArray())
            {
                var role = f.TryGetProperty("properties", out var p) && p.TryGetProperty("role", out var r) ? r.GetString() : null;
                var geom = f.GetProperty("geometry");
                if (geom.GetProperty("type").GetString() != "LineString") continue;
                var pts = geom.GetProperty("coordinates").EnumerateArray()
                    .Select(c => new TrackPoint(c[1].GetDouble(), c[0].GetDouble())).ToList();
                if (role == "trace") trace = pts;
                else if (role == "start_finish" && pts.Count >= 2) sf = Mid(pts[0], pts[^1]);
                else if (role == "start" && pts.Count >= 2) start = Mid(pts[0], pts[^1]);
            }
            if (trace == null || trace.Count < 3) return null;

            // Closed traces repeat the first position at the end; the app's layouts are implicitly closed.
            if (trace.Count > 3 && trace[0].Latitude == trace[^1].Latitude && trace[0].Longitude == trace[^1].Longitude)
                trace.RemoveAt(trace.Count - 1);

            return new OrtdbLayout
            {
                Id = layoutId,
                Name = layoutName,
                Closed = closed,
                LengthM = lengthM,
                GeometryStatus = status,
                Trace = trace,
                StartFinish = sf ?? start,
            };
        }
        catch
        {
            return null;
        }
    }

    private static TrackPoint Mid(TrackPoint a, TrackPoint b) =>
        new((a.Latitude + b.Latitude) / 2, (a.Longitude + b.Longitude) / 2);

    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal)
    {
        // "international"/"national" stay meaningful (Silverstone International); when they
        // are part of the venue name ("Bahrain International Circuit") venue tokens remove them.
        "circuit", "raceway", "speedway", "autodromo", "autodrome", "motor",
        "racing", "race", "park", "track", "ring", "de", "la", "le", "du", "di", "del", "the", "and"
    };

    private static HashSet<string> Tokens(string name)
    {
        var norm = TrackNameMatcher.Normalize(name).Replace("grand prix", "gp");
        return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && !StopTokens.Contains(t)).ToHashSet();
    }

    /// <summary>
    /// Choose the layout a game is driving. With a known lap length only CLOSED layouts
    /// within ±5 % qualify (draft traces match official lengths to ~0.2 %; Bahrain GP
    /// 5419.6 vs 5412 m) — no guess beyond that, e.g. a missing "Outer" must not fall back
    /// to the 8 %-off Paddock loop. Among qualifiers, the layout naming the game's variant
    /// words ("national", "outer", "club"…) wins, then the closest length. Without a length:
    /// a variant-name match, else the venue's default layout.
    /// </summary>
    public static OrtdbLayout? Select(IReadOnlyList<OrtdbLayout> layouts, OrtdbVenue venue, string gameTrackName,
        double officialLengthM, string? defaultLayoutId, out string reason)
    {
        var candidates = layouts.Where(l => l.Closed && l.Trace.Count >= 10 && l.LengthM > 300).ToList();
        if (candidates.Count == 0) { reason = "no closed layout traces"; return null; }

        var venueTokens = new HashSet<string>(venue.Aliases.Prepend(venue.Name).SelectMany(Tokens));
        var variant = Tokens(gameTrackName).Where(t => !venueTokens.Contains(t)).ToHashSet();
        int Score(OrtdbLayout l)
        {
            var lt = Tokens(l.Name + " " + l.Id.Replace('-', ' '));
            return variant.Count(lt.Contains);
        }

        if (officialLengthM > 500)
        {
            // Venues often list one trace under several names (Sebring: "Connected course
            // candidate" = "Sebring" = "SRO Practice", all 5865.7 m): on equal length prefer the
            // venue default, then any named layout over the generic "main" candidate.
            var within = candidates
                .Where(l => Math.Abs(l.LengthM - officialLengthM) / officialLengthM <= LengthTolerance)
                .OrderByDescending(Score)
                .ThenBy(l => Math.Round(Math.Abs(l.LengthM - officialLengthM)))
                .ThenByDescending(l => l.Id == defaultLayoutId)
                .ThenBy(l => l.Id == "main")
                .ToList();
            if (within.Count == 0)
            {
                reason = $"no layout within ±{LengthTolerance:P0} of {officialLengthM:F0} m (have " +
                         string.Join(", ", candidates.Select(l => $"{l.Name} {l.LengthM:F0} m")) + ")";
                return null;
            }
            var pick = within[0];
            reason = $"length match {pick.LengthM:F0} m vs official {officialLengthM:F0} m, variant score {Score(pick)}";
            return pick;
        }

        var named = candidates.OrderByDescending(Score).First();
        if (Score(named) > 0) { reason = $"variant name match (score {Score(named)})"; return named; }

        // No length, no variant word: only safe when every candidate is the same course.
        // (Guessing the venue default showed "Le Mans Bugatti" for "Circuit de la Sarthe"
        // when the lap length wasn't known yet — 2026-10-20.)
        int distinctCourses = candidates.Select(l => Math.Round(l.LengthM / 25.0)).Distinct().Count();
        if (distinctCourses > 1)
        {
            reason = $"{distinctCourses} different layouts and no lap length — not guessing";
            return null;
        }
        var def = candidates.FirstOrDefault(l => l.Id == defaultLayoutId) ?? candidates[0];
        reason = "single course at venue (no official length)";
        return def;
    }
}

/// <summary>
/// Downloads + caches Open Racetrack DB venue/layout files from a pinned commit and picks
/// the layout matching the game. Fallback tier for variants OSM relations lack (Bahrain
/// Inner/Paddock, Silverstone National/International, Sebring Club…) and for tracks with
/// no usable OSM relation. ODbL data; files are cached unmodified.
/// </summary>
public sealed class OpenRacetrackDbService : IDisposable
{
    public const string PinnedCommit = "03dac92c77d7b7dbc4e892650557c23068ac3ec9";
    private const string RawBase = "https://raw.githubusercontent.com/markthebault/open-racetrack-db/" + PinnedCommit + "/data/";

    private static readonly string CacheRoot = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackData", "ortdb", PinnedCommit[..8]));

    private readonly HttpClient _http;

    public OpenRacetrackDbService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner/1.0 (open-racetrack-db layout fetch)");
    }

    public async Task<TrackDetailedInfo?> FetchAsync(string trackName, string? config, double? lat, double? lon,
        double officialLengthM, Action<string>? log = null)
    {
        var venue = OpenRacetrackDbIndex.FindVenue(trackName, lat, lon);
        if (venue == null) { log?.Invoke($"[ORTDB] no venue for '{trackName}'"); return null; }

        var trackJson = await GetCachedAsync(venue.File);
        if (trackJson == null) { log?.Invoke($"[ORTDB] {venue.Id}: track.json unavailable"); return null; }

        string venueDir = venue.File.Contains('/') ? venue.File[..venue.File.LastIndexOf('/')] : "";
        string? defaultLayoutId = null;
        var layouts = new List<OrtdbLayout>();
        using (var doc = JsonDocument.Parse(trackJson))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("defaultLayoutId", out var d)) defaultLayoutId = d.GetString();
            foreach (var l in root.GetProperty("layouts").EnumerateArray())
            {
                string id = l.GetProperty("id").GetString() ?? "";
                string name = l.GetProperty("name").GetString() ?? id;
                string file = l.GetProperty("file").GetString() ?? "";
                var geo = await GetCachedAsync(venueDir.Length > 0 ? venueDir + "/" + file : file);
                if (geo == null) continue;
                var parsed = OrtdbLayouts.Parse(id, name, geo);
                if (parsed != null) layouts.Add(parsed);
            }
        }

        var pick = OrtdbLayouts.Select(layouts, venue, trackName, officialLengthM, defaultLayoutId, out var reason);
        if (pick == null) { log?.Invoke($"[ORTDB] {venue.Name}: {reason}"); return null; }
        log?.Invoke($"[ORTDB] {venue.Name} → '{pick.Name}' ({pick.Trace.Count} pts, {pick.GeometryStatus}): {reason}");

        return new TrackDetailedInfo
        {
            TrackName = trackName,
            TrackConfig = config ?? "",
            TrackLayout = pick.Trace,
            StartFinish = pick.StartFinish,
            TrackLengthM = pick.LengthM,
            DataSource = TrackDataSource.OpenRacetrackDb,
            ConfidenceScore = pick.GeometryStatus == "reviewed" ? 0.75f : 0.6f,
            FetchedAt = DateTime.UtcNow,
            CacheVersion = TrackDetailedInfo.CurrentCacheVersion,
        };
    }

    /// <summary>Repo-relative data file → cached text (downloads once per pinned commit).</summary>
    private async Task<string?> GetCachedAsync(string relPath)
    {
        // Paths come from the data files: relative, '/'-separated, no traversal (data contract §2).
        if (string.IsNullOrWhiteSpace(relPath) || relPath.Contains("..") || relPath.StartsWith('/')
            || relPath.Contains(':') || relPath.Contains('\\'))
            return null;
        var local = Path.GetFullPath(Path.Combine(CacheRoot, relPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!local.StartsWith(CacheRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (File.Exists(local)) return await File.ReadAllTextAsync(local);
            var text = await _http.GetStringAsync(RawBase + relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await File.WriteAllTextAsync(local, text);
            return text;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
