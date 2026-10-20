namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Provides track data (corner names, layout GPS, pit info, start/finish) via
/// OSM Overpass relation queries with retry logic.
///
/// The bounding-box OSM approach has been removed — it produced contaminated
/// data from kart tracks, service roads, and incorrectly ordered ways.
/// Only relation-based queries are used, which give correct circuit data.
/// </summary>
public sealed class TieredTrackDataProvider : IDisposable
{
    private readonly TrackOsmRelationService _relationService;
    private readonly OpenRacetrackDbService _ortdb = new();

    /// <summary>OSM layouts farther than this from the game's lap length are probably another variant.</summary>
    private const double PreferOrtdbBeyond = 0.05;

    /// <summary>Maximum number of retries when the Overpass API fails.</summary>
    private const int MaxRetries = 3;

    /// <summary>Base delay between retries (doubles each attempt).</summary>
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(5);

    public TieredTrackDataProvider()
    {
        _relationService = new TrackOsmRelationService();
    }

    public Action<string>? StatusMessage
    {
        set { _relationService.StatusLog = value; }
    }

    /// <summary>
    /// Load from the relation cache (or embedded seed pack) without fetching.
    /// </summary>
    public TrackDetailedInfo? LoadBestCached(string trackName, string? config = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        return _relationService.LoadCached(trackName, config);
    }

    /// <summary>
    /// Fetch track data from OSM relations with automatic retry on failure.
    /// Returns validated track data, or null if all attempts fail.
    /// centerLat/centerLon should be live game GPS when available (EVO reports it);
    /// the service falls back to gazetteer then Nominatim geocoding.
    /// officialLengthM (game-reported) cross-checks the OSM candidate when &gt; 0.
    /// liveOfficialLength is re-read at each decision: a fetch can take a minute when
    /// mirrors time out, and the game's lap length often arrives after the fetch started.
    /// </summary>
    public async Task<TrackDetailedInfo?> FetchTrackDataAsync(
        string trackName,
        IList<TrackWaypoint>? waypoints = null,
        double? centerLat = null,
        double? centerLon = null,
        string? config = null,
        double officialLengthM = 0,
        Func<double>? liveOfficialLength = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        double Official()
        {
            double live = 0;
            try { live = liveOfficialLength?.Invoke() ?? 0; } catch { }
            return live > 500 ? live : officialLengthM;
        }

        // Look up GPS from TrackDatabase if not provided (service also tries Nominatim)
        if (!centerLat.HasValue || !centerLon.HasValue
            || (centerLat.Value == 0 && centerLon.Value == 0))
        {
            var loc = TrackDatabase.LookupTrackLocation(trackName);
            if (loc.HasValue)
            {
                centerLat = loc.Value.lat;
                centerLon = loc.Value.lon;
            }
        }

        // Tier 1: OSM circuit relation (has corner names + pit lane), with retry logic.
        TrackDetailedInfo? osm = null;
        TrackDetailedInfo? rejected = null; // wrong layout, but its corner names may sit on the right one
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            TrackDetailedInfo? data;
            try
            {
                data = await _relationService.FetchTrackDataAsync(trackName, centerLat, centerLon, config);
            }
            catch (OverpassRateLimitedException)
            {
                StaticLog($"Rate limited by all Overpass mirrors for {trackName} — trying Open Racetrack DB");
                _relationService.StatusLog?.Invoke("Overpass rate-limited — trying Open Racetrack DB");
                break;
            }

            if (data != null && ValidateTrackData(data, trackName, Official()))
            {
                data.DataSource = TrackDataSource.OsmRelation;
                data.ConfidenceScore = 0.8f;

                if (attempt > 1)
                    StaticLog($"Track data for {trackName} fetched on attempt {attempt}");

                osm = data;
                break;
            }

            if (data != null)
            {
                // Parsed but rejected (wrong layout / contamination). The relation service
                // already cached it — drop that, or the next session would load it unchecked.
                StaticLog($"Track data for {trackName} failed validation — discarding cache, trying Open Racetrack DB");
                _relationService.DeleteRelationCache(trackName, config);
                rejected = data;
                break;
            }

            // No data returned — retry with increasing delay
            if (attempt < MaxRetries)
            {
                var delay = RetryBaseDelay * Math.Pow(2, attempt - 1);
                StaticLog($"Attempt {attempt}/{MaxRetries} failed for {trackName}, retrying in {delay.TotalSeconds:F0}s...");
                _relationService.StatusLog?.Invoke($"Retry {attempt}/{MaxRetries} for {trackName}...");
                await Task.Delay(delay);
            }
        }

        if (osm != null)
        {
            // Within the 15 % gate but > 5 % off the game's lap: likely a different variant of
            // the venue (e.g. GP relation for an Endurance/Outer layout). A closely matching
            // named ORTDB layout is the better outline; otherwise keep OSM.
            double official = Official();
            bool lengthKnown = official > 500 && osm.TrackLengthM > 0;
            if (lengthKnown && Math.Abs(osm.TrackLengthM - official) / official > PreferOrtdbBeyond)
            {
                var alt = await FetchOrtdbAsync(trackName, config, centerLat, centerLon, official);
                if (alt != null) return alt;
                var loop = await FetchNetworkLoopAsync(trackName, config, centerLat, centerLon, official, waypoints, osm);
                if (loop != null) return loop;
            }
            return osm;
        }

        // Tier 2: Open Racetrack DB named layouts — variants OSM relations lack, tracks
        // with no usable relation, and Overpass outages.
        var ortdb = await FetchOrtdbAsync(trackName, config, centerLat, centerLon, Official());
        if (ortdb != null) return ortdb;

        // Tier 3: a loop assembled from the venue's raceway ways — variants nobody published.
        return await FetchNetworkLoopAsync(trackName, config, centerLat, centerLon, Official(), waypoints, rejected);
    }

    /// <summary>
    /// Tier 3 (<see cref="TrackNetworkLoops"/>): needs the game's lap length to pick a loop. A
    /// recorded lap of this track (<paramref name="waypoints"/>) confirms the pick by shape; only
    /// confirmed picks are cached, so an unconfirmed one is chosen again once a lap exists.
    /// </summary>
    private async Task<TrackDetailedInfo?> FetchNetworkLoopAsync(string trackName, string? config,
        double? lat, double? lon, double officialLengthM, IList<TrackWaypoint>? waypoints, TrackDetailedInfo? venueData)
    {
        if (officialLengthM <= 500 || lat is not double la || lon is not double lo || (la == 0 && lo == 0))
            return null;
        // Every point of a loop of length L lies within L/2 of every other; the margin covers a
        // gazetteer centre that sits beside the track rather than on it.
        var ways = await _relationService.FetchRacewayNetworkAsync(trackName, la, lo, officialLengthM / 2 + 1500);
        if (ways == null || ways.Count == 0) return null;

        var variant = TrackNetworkLoops.VariantTokens(trackName, TrackDatabase.GetEntry(trackName));
        var loops = TrackNetworkLoops.FindLoops(ways, officialLengthM, variant);
        TrackOsmRelationService.AppendLog($"[Network] {trackName}: {loops.Count} loop(s) within ±{TrackNetworkLoops.LengthTolerance:P0} of {officialLengthM:F0} m"
            + (variant.Count > 0 ? $" (variant: {string.Join(" ", variant)})" : "")
            + string.Concat(loops.Select(l => $"\n    {l.LengthM:F0} m score={l.Score:F3} [{string.Join(", ", l.WayNames)}]")));
        if (loops.Count == 0) return null;

        var pick = loops[0];
        bool confirmed = false;
        if (waypoints is { Count: >= 50 })
        {
            var lap = waypoints.Select(w => new GeoPathSample(w.X, w.Z, w.Npos)).ToList();
            var byShape = TrackNetworkLoops.PickByShape(loops, lap, out _, out var diag);
            TrackOsmRelationService.AppendLog($"[Network] {trackName}: shape check {diag}");
            if (byShape != null) { pick = byShape; confirmed = true; }
        }

        var info = new TrackDetailedInfo
        {
            TrackName = trackName,
            TrackLayout = pick.Layout,
            TrackLengthM = pick.LengthM,
            DataSource = TrackDataSource.OsmNetwork,
            ConfidenceScore = confirmed ? 0.75f : 0.5f,
        };
        // Names and markers of the venue's (wrong-layout) relation that lie on this loop.
        if (venueData != null)
        {
            info.Corners = venueData.Corners
                .Where(c => TrackNetworkLoops.IsNear(new TrackPoint(c.Latitude, c.Longitude), pick.Layout, 40))
                .ToList();
            if (venueData.StartFinish != null && TrackNetworkLoops.IsNear(venueData.StartFinish, pick.Layout, 40))
                info.StartFinish = venueData.StartFinish;
            if (venueData.Pit is { } pit
                && TrackNetworkLoops.IsNear(new TrackPoint(pit.EntryLatitude, pit.EntryLongitude), pick.Layout, 60)
                && TrackNetworkLoops.IsNear(new TrackPoint(pit.ExitLatitude, pit.ExitLongitude), pick.Layout, 60))
                info.Pit = pit;
        }
        TrackOsmRelationService.AppendLog($"[Network] {trackName}: using {pick.LengthM:F0} m loop " +
            $"[{string.Join(", ", pick.WayNames)}] {(confirmed ? "— shape-confirmed, cached" : "— unconfirmed (no recorded lap yet), not cached")}");
        if (confirmed) _relationService.SaveCache(trackName, info, config);
        return info;
    }

    private async Task<TrackDetailedInfo?> FetchOrtdbAsync(string trackName, string? config,
        double? lat, double? lon, double officialLengthM)
    {
        var data = await _ortdb.FetchAsync(trackName, config, lat, lon, officialLengthM,
            msg => { TrackOsmRelationService.AppendLog(msg); _relationService.StatusLog?.Invoke(msg); });
        if (data == null) return null;
        _relationService.SaveCache(trackName, data, config);
        return data;
    }

    /// <summary>
    /// Validate that track data doesn't contain known contamination signals.
    /// When the game reports an official length, reject OSM candidates that
    /// disagree by more than 15% (wrong layout / wrong circuit).
    /// </summary>
    private static bool ValidateTrackData(TrackDetailedInfo data, string trackName, double officialLengthM = 0)
    {
        // Check for contamination in corner names
        if (data.Corners.Count > 0)
        {
            var contaminatedCorners = data.Corners
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .Where(c =>
                    c.Name.Contains("Kart", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Contains("Moto", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Contains("Rally", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Contains("Disused", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (contaminatedCorners.Count > 0)
            {
                StaticLog($"REJECTED {trackName}: contamination ({string.Join(", ", contaminatedCorners.Select(c => c.Name))})");
                return false;
            }

            int realCornerCount = data.Corners.Count(c =>
                !string.IsNullOrEmpty(c.Name) &&
                !c.Name.Contains(trackName, StringComparison.OrdinalIgnoreCase));
            if (realCornerCount == 0 && data.Corners.Count > 0)
            {
                StaticLog($"REJECTED {trackName}: all corners match track name");
                return false;
            }
        }

        // Track length sanity
        if (data.TrackLengthM > 0 && (data.TrackLengthM < 500 || data.TrackLengthM > 60000))
        {
            StaticLog($"REJECTED {trackName}: implausible track length {data.TrackLengthM:F0}m");
            return false;
        }

        // Official-length cross-check: OSM candidate must agree with the game
        // within 15% or it's the wrong layout/circuit (e.g. GP vs Nordschleife).
        if (officialLengthM > 500 && data.TrackLengthM > 0)
        {
            double diff = Math.Abs(data.TrackLengthM - officialLengthM) / officialLengthM;
            if (diff > 0.15)
            {
                StaticLog($"REJECTED {trackName}: OSM length {data.TrackLengthM:F0}m disagrees with official {officialLengthM:F0}m ({diff:P0})");
                return false;
            }
        }

        // Minimum layout points
        if (data.TrackLayout == null || data.TrackLayout.Count < 10)
        {
            StaticLog($"REJECTED {trackName}: too few layout points ({data.TrackLayout?.Count ?? 0})");
            return false;
        }

        return true;
    }

    private static void StaticLog(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[TieredProvider] {msg}");
    }

    /// <summary>
    /// Persist a shape-solved SF anchor into the cached relation data.
    /// </summary>
    public void StoreAnchor(string trackName, string? config, double fraction, bool reversed, string source)
    {
        _relationService.StoreAnchor(trackName, config, fraction, reversed, source);
    }

    /// <summary>
    /// Delete all cached track data files for the given track (layout-aware key + legacy).
    /// </summary>
    public void DeleteCache(string trackName, string? config = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "TrackData");

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                TrackMapKeys.Sanitize(TrackMapKeys.BuildKey(trackName, config)),
                TrackMapKeys.Sanitize(trackName),
            };

            foreach (var key in keys)
            {
                var relPath = Path.Combine(cacheDir, $"{key}_relation.json");
                if (File.Exists(relPath))
                {
                    File.Delete(relPath);
                    StaticLog($"Deleted relation cache: {key}_relation.json");
                }
            }

            // Also delete any old bounding-box caches for this track. Geo-fits
            // ({track}_{game}_geofit.json) are world→GPS transforms that stay valid
            // across layout refreshes — keep them.
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.GetFiles(cacheDir, $"{TrackMapKeys.Sanitize(trackName)}*.json"))
                {
                    if (f.EndsWith("_geofit.json", StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(f); StaticLog($"Deleted cache: {Path.GetFileName(f)}"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            StaticLog($"Error deleting cache for {trackName}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _relationService.Dispose();
        _ortdb.Dispose();
    }
}
