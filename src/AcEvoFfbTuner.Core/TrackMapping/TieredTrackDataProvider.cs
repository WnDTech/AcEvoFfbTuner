namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Orchestrates the fallback chain for track data:
///
///   Tier 1: OSM Route Relations (most accurate, curated circuit data)
///   Tier 2: [reserved for future RacingCircuits.info or similar]
///   Tier 3: OSM Bounding-Box Way Search (current implementation)
///   Tier 4: Self-recorded driving data [future]
///
/// Each tier returns a <see cref="TrackDetailedInfo"/> with its <see cref="TrackDetailedInfo.DataSource"/>
/// populated. The highest-accuracy available data wins.
/// </summary>
public sealed class TieredTrackDataProvider : IDisposable
{
    private readonly TrackOsmRelationService _relationService;
    private readonly TrackOsmService _osmService;

    public TieredTrackDataProvider()
    {
        _relationService = new TrackOsmRelationService();
        _osmService = new TrackOsmService();
    }

    public Action<string>? StatusMessage
    {
        set
        {
            _relationService.StatusLog = value;
            _osmService.StatusLog = value;
        }
    }

    /// <summary>
    /// Load from any tier's cache without fetching. Returns the best cached data available.
    /// Checks in priority order: Relation → OSM BBox.
    /// </summary>
    public TrackDetailedInfo? LoadBestCached(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;

        // Tier 1 cache
        var cached = _relationService.LoadCached(trackName);
        if (cached != null)
            return cached;

        // Tier 3 cache
        cached = _osmService.LoadCached(trackName);
        if (cached != null)
        {
            cached.DataSource = TrackDataSource.OsmBoundingBox;
            return cached;
        }

        return null;
    }

    /// <summary>
    /// Fetch track data by trying each tier in priority order.
    /// Returns the best available result, or null if all tiers fail.
    /// </summary>
    public async Task<TrackDetailedInfo?> FetchTrackDataAsync(
        string trackName,
        IList<TrackWaypoint>? waypoints = null,
        double? centerLat = null,
        double? centerLon = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;

        // Look up GPS from TrackDatabase if not provided
        if (!centerLat.HasValue || !centerLon.HasValue)
        {
            var loc = TrackDatabase.LookupTrackLocation(trackName);
            if (loc.HasValue)
            {
                centerLat = loc.Value.lat;
                centerLon = loc.Value.lon;
            }
        }

        // Tier 1: OSM Route Relations
        var relationData = await _relationService.FetchTrackDataAsync(trackName, centerLat, centerLon);
        if (relationData != null && ValidateTrackData(relationData, trackName))
        {
            relationData.DataSource = TrackDataSource.OsmRelation;
            relationData.ConfidenceScore = 0.8f;
            return relationData;
        }

        // Tier 3: OSM Bounding-Box Way Search (current implementation)
        var bboxData = await _osmService.FetchTrackDataAsync(trackName, waypoints, centerLat, centerLon);
        if (bboxData != null && bboxData.TrackLayout != null && bboxData.TrackLayout.Count >= 10)
        {
            bboxData.DataSource = TrackDataSource.OsmBoundingBox;
            bboxData.ConfidenceScore = ComputeBboxConfidence(bboxData, trackName);

            // Final validation: reject if corner names suggest contamination
            if (!ValidateTrackData(bboxData, trackName))
            {
                StaticLog($"Bounding-box data for {trackName} failed validation (contaminated with non-circuit ways)");
                return null;
            }

            return bboxData;
        }

        // All tiers exhausted
        return null;
    }

    /// <summary>
    /// Validate that track data doesn't contain known contamination signals
    /// (kart tracks, moto layouts, etc. that weren't properly excluded).
    /// </summary>
    private static bool ValidateTrackData(TrackDetailedInfo data, string trackName)
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
                StaticLog($"REJECTED {trackName}: corner name contamination detected ({string.Join(", ", contaminatedCorners.Select(c => c.Name))})");
                return false;
            }

            // If the only "corner" is the track name itself, the data is likely wrong
            int realCornerCount = data.Corners.Count(c =>
                !string.IsNullOrEmpty(c.Name) &&
                !c.Name.Contains(trackName, StringComparison.OrdinalIgnoreCase));
            if (realCornerCount == 0 && data.Corners.Count > 0)
            {
                StaticLog($"REJECTED {trackName}: all corners match track name (likely wrong relation)");
                return false;
            }
        }

        // Check track length sanity (most circuits are between 1km and 30km)
        if (data.TrackLengthM > 0 && (data.TrackLengthM < 500 || data.TrackLengthM > 60000))
        {
            StaticLog($"REJECTED {trackName}: implausible track length {data.TrackLengthM:F0}m");
            return false;
        }

        // Check for minimum number of layout points
        if (data.TrackLayout == null || data.TrackLayout.Count < 20)
        {
            StaticLog($"REJECTED {trackName}: too few layout points ({data.TrackLayout?.Count ?? 0})");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Compute a confidence score for bounding-box data based on way count, layout density, and agreement with known track length.
    /// </summary>
    private static float ComputeBboxConfidence(TrackDetailedInfo data, string trackName)
    {
        float confidence = 0.5f; // baseline

        // More layout points → more detail
        if (data.TrackLayout != null)
        {
            int ptCount = data.TrackLayout.Count;
            if (ptCount > 200) confidence += 0.15f;
            else if (ptCount > 100) confidence += 0.10f;
            else if (ptCount > 50) confidence += 0.05f;
        }

        // Corners increase confidence
        if (data.Corners.Count >= 5) confidence += 0.10f;

        // Pit lane data increases confidence
        if (data.Pit != null) confidence += 0.05f;

        // Start/finish increases confidence
        if (data.StartFinish != null) confidence += 0.05f;

        // Penalize if any corner names suggest it's not a real circuit
        if (data.Corners.Any(c =>
            c.Name.Contains("Kart", StringComparison.OrdinalIgnoreCase) ||
            c.Name.Contains("Moto", StringComparison.OrdinalIgnoreCase)))
            confidence -= 0.3f;

        return Math.Clamp(confidence, 0.0f, 1.0f);
    }

    private static void StaticLog(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[TieredProvider] {msg}");
    }

    /// <summary>
    /// Delete all cached track data files for the given track.
    /// This forces a fresh fetch from the best available source on next request.
    /// </summary>
    public void DeleteCache(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var safe = string.Join("_", trackName.Split(Path.GetInvalidFileNameChars()));
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "TrackData");

            // Delete relation cache
            var relPath = Path.Combine(cacheDir, $"{safe}_relation.json");
            if (File.Exists(relPath))
            {
                File.Delete(relPath);
                StaticLog($"Deleted relation cache: {safe}_relation.json");
            }

            // Delete bounding-box cache
            var bboxPath = Path.Combine(cacheDir, $"{safe}.json");
            if (File.Exists(bboxPath))
            {
                File.Delete(bboxPath);
                StaticLog($"Deleted bbox cache: {safe}.json");
            }

            // Also delete any alias-named cache files (look for partial matches)
            if (Directory.Exists(cacheDir))
            {
                foreach (var f in Directory.GetFiles(cacheDir, $"{safe}*.json"))
                {
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
        _osmService.Dispose();
    }
}
