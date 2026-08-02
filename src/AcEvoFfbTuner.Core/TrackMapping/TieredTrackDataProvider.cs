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
    /// Load from the relation cache without fetching.
    /// </summary>
    public TrackDetailedInfo? LoadBestCached(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        return _relationService.LoadCached(trackName);
    }

    /// <summary>
    /// Fetch track data from OSM relations with automatic retry on failure.
    /// Returns validated track data, or null if all attempts fail.
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

        // Try fetching with retry logic
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            TrackDetailedInfo? data;
            try
            {
                data = await _relationService.FetchTrackDataAsync(trackName, centerLat, centerLon);
            }
            catch (OverpassRateLimitedException)
            {
                StaticLog($"Rate limited by Overpass API for {trackName} — not retrying");
                _relationService.StatusLog?.Invoke("Overpass rate-limited — try again in about a minute");
                return null;
            }

            if (data != null && ValidateTrackData(data, trackName))
            {
                data.DataSource = TrackDataSource.OsmRelation;
                data.ConfidenceScore = 0.8f;

                if (attempt > 1)
                    StaticLog($"Track data for {trackName} fetched on attempt {attempt}");

                return data;
            }

            if (data != null)
            {
                // Data was returned but failed validation — don't retry
                StaticLog($"Track data for {trackName} failed validation, not retrying");
                return null;
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

        StaticLog($"All {MaxRetries} attempts failed for {trackName}");
        return null;
    }

    /// <summary>
    /// Validate that track data doesn't contain known contamination signals.
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
    /// Delete all cached track data files for the given track.
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

            // Also delete any old bounding-box caches for this track
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
    }
}
