namespace AcEvoFfbTuner.Core.TrackMapping;

public class TrackDataService : IDisposable
{
    private readonly TrackOsmService _osm;
    private TrackDetailedInfo? _currentTrackData;
    private string? _currentTrackName;

    public TrackDetailedInfo? CurrentTrackData => _currentTrackData;

    public event Action<TrackDetailedInfo>? TrackDataUpdated;
    public event Action<string>? StatusMessage;

    public TrackDataService()
    {
        _osm = new TrackOsmService();
        _osm.StatusLog = msg => StatusMessage?.Invoke(msg);
    }

    public async Task LoadTrackDataAsync(string trackName,
        IList<TrackWaypoint>? waypoints = null,
        double? centerLat = null, double? centerLon = null,
        bool forceRefresh = false)
    {
        _currentTrackName = trackName;

        // Try cache first
        if (!forceRefresh)
        {
            var cached = _osm.LoadCached(trackName);
            if (cached != null)
            {
                _currentTrackData = cached;
                TrackDataUpdated?.Invoke(cached);
                return;
            }
        }

        // Fetch from OSM
        var data = await _osm.FetchTrackDataAsync(trackName, waypoints, centerLat, centerLon);
        if (data != null && data.Corners.Count > 0)
        {
            _osm.SaveCache(trackName, data);
            _currentTrackData = data;
            TrackDataUpdated?.Invoke(data);
        }
        else
        {
            _currentTrackData = null;
        }
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
        _osm?.Dispose();
    }
}