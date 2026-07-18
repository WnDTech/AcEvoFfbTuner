namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Provides track data (corner names, layout GPS, pit info, start/finish) using
/// a tiered fallback chain via <see cref="TieredTrackDataProvider"/>.
///
/// Tier 1: OSM route relations (most accurate)
/// Tier 3: OSM bounding-box way search (current implementation, fallback)
/// </summary>
public class TrackDataService : IDisposable
{
    private readonly TieredTrackDataProvider _provider;
    private TrackDetailedInfo? _currentTrackData;
    private string? _currentTrackName;

    /// <summary>The data source that produced the current track data.</summary>
    public TrackDataSource CurrentDataSource =>
        _currentTrackData?.DataSource ?? TrackDataSource.Unknown;

    /// <summary>The current track data, or null if not loaded.</summary>
    public TrackDetailedInfo? CurrentTrackData => _currentTrackData;

    public event Action<TrackDetailedInfo>? TrackDataUpdated;
    public event Action<string>? StatusMessage;

    public TrackDataService()
    {
        _provider = new TieredTrackDataProvider();
        _provider.StatusMessage = msg => StatusMessage?.Invoke(msg);
    }

    /// <summary>
    /// Load track data for the given track. Checks the tiered cache first, then
    /// fetches from the best available source if uncached.
    /// </summary>
    public async Task LoadTrackDataAsync(string trackName,
        IList<TrackWaypoint>? waypoints = null,
        double? centerLat = null, double? centerLon = null,
        bool forceRefresh = false)
    {
        _currentTrackName = trackName;

        // Try cache first (checks all tiers' caches in priority order)
        if (!forceRefresh)
        {
            var cached = _provider.LoadBestCached(trackName);
            if (cached != null)
            {
                _currentTrackData = cached;
                TrackDataUpdated?.Invoke(cached);
                StatusMessage?.Invoke($"Loaded cached track data ({cached.DataSource})");
                return;
            }
        }

        // Fetch from tiered provider
        var data = await _provider.FetchTrackDataAsync(trackName, waypoints, centerLat, centerLon);
        if (data != null)
        {
            _currentTrackData = data;
            StatusMessage?.Invoke($"Loaded track data from {data.DataSource} ({data.Corners.Count} corners)");
            TrackDataUpdated?.Invoke(data);
        }
        else
        {
            _currentTrackData = null;
            StatusMessage?.Invoke("No track data found for this track");
        }
    }

    /// <summary>
    /// Force a refresh from the best available source, bypassing cache.
    /// </summary>
    public async Task ForceRefreshAsync(string trackName,
        double? centerLat = null, double? centerLon = null)
    {
        await LoadTrackDataAsync(trackName, null, centerLat, centerLon, forceRefresh: true);
    }

    /// <summary>
    /// Delete all cached track data for the given track name.
    /// Call before <see cref="ForceRefreshAsync"/> to ensure a completely fresh fetch.
    /// </summary>
    public void DeleteCache(string trackName)
    {
        _provider.DeleteCache(trackName);
    }

    public void ApplyCornerNames(List<TrackCorner> corners)
    {
        if (_currentTrackData == null || corners.Count == 0) return;

        var osmCorners = _currentTrackData.Corners;
        if (osmCorners.Count == 0) return;

        // If counts match, do sequential assignment
        if (corners.Count == osmCorners.Count)
        {
            for (int i = 0; i < corners.Count; i++)
                corners[i].RealName = osmCorners[i].Name;
        }
        else
        {
            // Otherwise, assign in order up to the smaller count
            int matchCount = Math.Min(corners.Count, osmCorners.Count);
            for (int i = 0; i < matchCount; i++)
                corners[i].RealName = osmCorners[i].Name;
        }
    }

    public (double entryLat, double entryLon, double exitLat, double exitLon)? GetPitInfo()
    {
        if (_currentTrackData?.Pit == null) return null;
        return (
            _currentTrackData.Pit.EntryLatitude,
            _currentTrackData.Pit.EntryLongitude,
            _currentTrackData.Pit.ExitLatitude,
            _currentTrackData.Pit.ExitLongitude
        );
    }

    public IReadOnlyList<(string name, double lat, double lon)> GetCornerList()
    {
        if (_currentTrackData?.Corners == null)
            return Array.Empty<(string name, double lat, double lon)>();
        return _currentTrackData.Corners
            .Select(c => (c.Name, c.Latitude, c.Longitude))
            .ToList();
    }

    public void Dispose()
    {
        _provider?.Dispose();
    }
}