namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Provides track data (corner names, layout GPS, pit info, start/finish) using
/// <see cref="TieredTrackDataProvider"/> with automatic retry on failure.
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
    /// Load track data for the given track. Checks the seed pack + tiered cache
    /// first, then fetches from the best available source if uncached.
    /// centerLat/centerLon should be live game GPS when available (EVO reports it);
    /// config is the layout name (EVO TrackConfiguration / R3E LayoutName) and
    /// officialLengthM the game-reported length for OSM candidate validation.
    /// </summary>
    public Task LoadTrackDataAsync(string trackName,
        IList<TrackWaypoint>? waypoints = null,
        double? centerLat = null, double? centerLon = null,
        bool forceRefresh = false,
        string? config = null,
        double officialLengthM = 0,
        Func<double>? liveOfficialLength = null)
    {
        var key = TrackMapKeys.BuildKey(trackName, config);
        lock (_inflightLock)
        {
            // A second request for the same track while one is running (Refresh pressed during
            // a slow mirror, page re-entry) joins it instead of racing it. The duplicate used to
            // hit the per-track rate limiter, fall back without a lap length and show the wrong
            // layout first (Le Mans Bugatti for Circuit de la Sarthe, 2026-10-20).
            if (_inflight is { IsCompleted: false } && _inflightKey == key)
                return _inflight;
            _inflightKey = key;
            _inflight = LoadCoreAsync(trackName, waypoints, centerLat, centerLon, forceRefresh, config,
                officialLengthM, liveOfficialLength);
            return _inflight;
        }
    }

    private readonly object _inflightLock = new();
    private Task? _inflight;
    private string? _inflightKey;

    private async Task LoadCoreAsync(string trackName, IList<TrackWaypoint>? waypoints,
        double? centerLat, double? centerLon, bool forceRefresh, string? config,
        double officialLengthM, Func<double>? liveOfficialLength)
    {
        _currentTrackName = trackName;
        double Official()
        {
            double live = 0;
            try { live = liveOfficialLength?.Invoke() ?? 0; } catch { }
            return live > 500 ? live : officialLengthM;
        }

        // What we already have (the cache) — a refresh must never replace it with something worse.
        var existing = _provider.LoadBestCached(trackName, config);

        // Try cache first (seed pack + tiered caches, layout-aware)
        if (!forceRefresh && existing != null)
        {
            _currentTrackData = existing;
            TrackDataUpdated?.Invoke(existing);
            StatusMessage?.Invoke($"Loaded cached track data ({existing.DataSource})");
            await EnrichNamesAsync(trackName, config, existing);
            await TryUpgradeAsync(trackName, config, existing, Official(), centerLat, centerLon);
            return;
        }

        // Fetch from tiered provider
        var data = await _provider.FetchTrackDataAsync(trackName, waypoints, centerLat, centerLon, config, officialLengthM,
            liveOfficialLength);
        if (existing != null && TieredTrackDataProvider.QualityRank(data, Official())
                                < TieredTrackDataProvider.QualityRank(existing, Official()))
        {
            // E.g. ⟳ while OpenStreetMap is down only reached an Open Racetrack DB draft: keep the
            // better layout we had (it was overwritten in the cache by the fetch — restore it).
            TrackOsmRelationService.AppendLog($"[Refresh] {trackName}: new {data?.DataSource.ToString() ?? "nothing"} " +
                $"is worse than the cached {existing.DataSource} — keeping the cached layout");
            _provider.RestoreCache(trackName, config, existing);
            data = existing;
            StatusMessage?.Invoke($"Kept the existing {existing.DataSource} layout (the refresh found nothing better)");
        }
        if (data != null)
        {
            _currentTrackData = data;
            StatusMessage?.Invoke($"Loaded track data from {data.DataSource} ({data.Corners.Count} corners)");
            TrackDataUpdated?.Invoke(data);
            await EnrichNamesAsync(trackName, config, data);
            await TryUpgradeAsync(trackName, config, data, Official(), centerLat, centerLon);
        }
        else
        {
            _currentTrackData = null;
            StatusMessage?.Invoke("No track data found for this track");
        }
    }

    // Fallback layouts (Open Racetrack DB drafts, network loops) cached during an Overpass outage
    // were never revisited: try the OSM relation once per track per session.
    private readonly HashSet<string> _upgradeTried = new(StringComparer.OrdinalIgnoreCase);

    private async Task TryUpgradeAsync(string trackName, string? config, TrackDetailedInfo current,
        double officialLengthM, double? lat, double? lon)
    {
        if (current.DataSource is not (TrackDataSource.OpenRacetrackDb or TrackDataSource.OsmNetwork)) return;
        if (!_upgradeTried.Add(TrackMapKeys.BuildKey(trackName, config))) return;
        try
        {
            var better = await _provider.TryUpgradeToRelationAsync(trackName, config, current, officialLengthM, lat, lon);
            if (better != null && ReferenceEquals(_currentTrackData, current))
            {
                _currentTrackData = better;
                StatusMessage?.Invoke("Upgraded to the OpenStreetMap circuit layout");
                TrackDataUpdated?.Invoke(better);
            }
        }
        catch { /* the fallback layout stays */ }
    }

    // One name lookup per track per session: a venue with no named ways must not re-query Overpass
    // every time the page reloads the layout.
    // A failed lookup (Overpass down/slow) is retried after NameRetry, in the background.
    private readonly HashSet<string> _namesTried = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NameRetry = TimeSpan.FromMinutes(3);
    private const int MaxNameAttempts = 5;

    private async Task EnrichNamesAsync(string trackName, string? config, TrackDetailedInfo data, int attempt = 1)
    {
        var key = TrackMapKeys.BuildKey(trackName, config);
        if (attempt == 1 && !_namesTried.Add(key)) return;
        bool? result;
        try { result = await _provider.EnrichCornerNamesAsync(trackName, config, data); }
        catch (Exception) { result = null; }

        if (result == true && ReferenceEquals(_currentTrackData, data))
        {
            StatusMessage?.Invoke($"Added {data.Corners.Count} corner names");
            TrackDataUpdated?.Invoke(data);
        }
        else if (result == null && attempt < MaxNameAttempts)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(NameRetry);
                // Only while this track is still the current one and the cache was not replaced.
                if (ReferenceEquals(_currentTrackData, data) && _namesTried.Contains(key))
                    await EnrichNamesAsync(trackName, config, data, attempt + 1);
            });
        }
    }

    /// <summary>
    /// Force a refresh from the best available source, bypassing cache.
    /// </summary>
    public Task ForceRefreshAsync(string trackName,
        double? centerLat = null, double? centerLon = null,
        string? config = null, double officialLengthM = 0,
        Func<double>? liveOfficialLength = null)
    {
        _namesTried.Remove(TrackMapKeys.BuildKey(trackName, config));
        _upgradeTried.Remove(TrackMapKeys.BuildKey(trackName, config));
        return LoadTrackDataAsync(trackName, null, centerLat, centerLon, forceRefresh: true, config: config,
            officialLengthM: officialLengthM, liveOfficialLength: liveOfficialLength);
    }

    /// <summary>
    /// Persist a shape-solved SF anchor into the cached relation data.
    /// </summary>
    public void StoreAnchor(string trackName, string? config, double fraction, bool reversed, string source)
    {
        _provider.StoreAnchor(trackName, config, fraction, reversed, source);
    }

    /// <summary>
    /// Delete all cached track data for the given track name.
    /// Call before <see cref="ForceRefreshAsync"/> to ensure a completely fresh fetch.
    /// </summary>
    public void DeleteCache(string trackName, string? config = null)
    {
        _provider.DeleteCache(trackName, config);
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