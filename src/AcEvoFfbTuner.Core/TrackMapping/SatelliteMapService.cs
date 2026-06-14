namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed class SatelliteMapService : IDisposable
{
    private static readonly string CalibrationDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackCalibration");

    private static (float lat, float lon)? LookupTrackGeo(string trackName)
    {
        return TrackDatabase.LookupTrackLocation(trackName);
    }

    public static (float lat, float lon)? LookupTrackLocation(string trackName)
    {
        return TrackDatabase.LookupTrackLocation(trackName);
    }

    public static float? LookupTrackRotation(string trackName)
    {
        // Rotation is game-coordinate-system dependent and stored per-user via calibration.
        // Return null to let the caller use PCA or manual calibration.
        return null;
    }

    public static (float lat, float lon, float rotationDeg)? LoadCalibration(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        var path = Path.Combine(CalibrationDir, $"{SanitizeFileName(trackName)}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var parts = json.Split('|');
            if (parts.Length == 3
                && float.TryParse(parts[0], out var lat)
                && float.TryParse(parts[1], out var lon)
                && float.TryParse(parts[2], out var rot))
                return (lat, lon, rot);
        }
        catch { }
        return null;
    }

    public static void SaveCalibration(string trackName, float lat, float lon, float rotationDeg)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            Directory.CreateDirectory(CalibrationDir);
            var path = Path.Combine(CalibrationDir, $"{SanitizeFileName(trackName)}.json");
            File.WriteAllText(path, $"{lat}|{lon}|{rotationDeg}");
        }
        catch { }
    }

    public static void DeleteCalibration(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var path = Path.Combine(CalibrationDir, $"{SanitizeFileName(trackName)}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private readonly HttpClient _http;
    private readonly string _tileCacheDir;

    public float TrackCenterLatitude { get; set; }
    public float TrackCenterLongitude { get; set; }
    public bool HasGeoData => TrackCenterLatitude != 0 || TrackCenterLongitude != 0;

    private float _gameCenterX, _gameCenterZ;
    private bool _transformComputed;
    private double _rotationRad;

    public int ZoomLevel { get; set; } = 16;

    public SatelliteMapService()
    {
        _http = new HttpClient(new HttpClientHandler { MaxConnectionsPerServer = 16 });
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner/1.0");
        _http.Timeout = TimeSpan.FromSeconds(10);

        _tileCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "tilecache");
        Directory.CreateDirectory(_tileCacheDir);
    }

    public void SetGeoReference(float latitude, float longitude, float rotationDeg = 0f)
    {
        TrackCenterLatitude = latitude;
        TrackCenterLongitude = longitude;
        _rotationRad = rotationDeg * Math.PI / 180.0;
        _transformComputed = true;
    }

    public void AdjustCalibration(double dLat, double dLon, double dRotDeg)
    {
        TrackCenterLatitude = (float)(TrackCenterLatitude + dLat);
        TrackCenterLongitude = (float)(TrackCenterLongitude + dLon);
        _rotationRad += dRotDeg * Math.PI / 180.0;
    }

    public double GetRotationDeg() => _rotationRad * 180.0 / Math.PI;

    public void ComputeGameToGpsTransform(TrackMap map)
    {
        if (map.Waypoints.Count < 3) return;

        float sumX = 0f, sumZ = 0f;
        foreach (var wp in map.Waypoints)
        {
            sumX += wp.X;
            sumZ += wp.Z;
        }

        _gameCenterX = sumX / map.Waypoints.Count;
        _gameCenterZ = sumZ / map.Waypoints.Count;
        _transformComputed = true;
    }

    public (double latitude, double longitude) GameToGps(float gameX, float gameZ)
    {
        if (!_transformComputed || !HasGeoData)
            return (TrackCenterLatitude, TrackCenterLongitude);

        double dx = gameX - _gameCenterX;
        double dz = _gameCenterZ - gameZ;

        double cosR = Math.Cos(_rotationRad);
        double sinR = Math.Sin(_rotationRad);
        double eastMeters = dx * cosR + dz * sinR;
        double northMeters = -dx * sinR + dz * cosR;

        double lat = TrackCenterLatitude + northMeters / 111320.0;
        double lon = TrackCenterLongitude + eastMeters / (111320.0 * Math.Cos(TrackCenterLatitude * Math.PI / 180.0));

        return (lat, lon);
    }

    public (float gameX, float gameZ) GpsToGame(double latitude, double longitude)
    {
        if (!_transformComputed || !HasGeoData)
            return (0, 0);

        double dLat = latitude - TrackCenterLatitude;
        double dLon = longitude - TrackCenterLongitude;

        double northMeters = dLat * 111320.0;
        double eastMeters = dLon * 111320.0 * Math.Cos(TrackCenterLatitude * Math.PI / 180.0);

        double cosR = Math.Cos(_rotationRad);
        double sinR = Math.Sin(_rotationRad);
        double dx = eastMeters * cosR - northMeters * sinR;
        double dz = eastMeters * sinR + northMeters * cosR;

        return ((float)(dx + _gameCenterX), (float)(_gameCenterZ - dz));
    }

    public static (int tileX, int tileY) LatLonToTile(double lat, double lon, int zoom)
    {
        int n = 1 << zoom;
        double latRad = lat * Math.PI / 180.0;
        int tileX = (int)((lon + 180.0) / 360.0 * n);
        int tileY = (int)((1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n);
        tileX = Math.Clamp(tileX, 0, n - 1);
        tileY = Math.Clamp(tileY, 0, n - 1);
        return (tileX, tileY);
    }

    public static (double lat, double lon) TileToLatLon(int tileX, int tileY, int zoom)
    {
        int n = 1 << zoom;
        double lon = (double)tileX / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * tileY / n)));
        double lat = latRad * 180.0 / Math.PI;
        return (lat, lon);
    }

    public static (double lat, double lon) TileToLatLonCorner(int tileX, int tileY, int zoom,
        double pixelX, double pixelY, int tileSize = 256)
    {
        int n = 1 << zoom;
        double lon = ((double)tileX + pixelX / tileSize) / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * ((double)tileY + pixelY / tileSize) / n)));
        return (latRad * 180.0 / Math.PI, lon);
    }

    private static readonly string[] TileServers = ["server", "services", "tiles"];
    private int _serverIndex;

    public string GetTileUrl(int x, int y, int z)
    {
        var server = TileServers[_serverIndex % TileServers.Length];
        Interlocked.Increment(ref _serverIndex);
        return $"https://{server}.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
    }

    public string GetTileCachePath(int x, int y, int z)
    {
        return Path.Combine(_tileCacheDir, $"{z}_{x}_{y}.jpg");
    }

    public async Task<string?> FetchTileAsync(int x, int y, int z)
    {
        string cachePath = GetTileCachePath(x, y, z);

        if (File.Exists(cachePath))
            return cachePath;

        try
        {
            string url = GetTileUrl(x, y, z);
            var response = await _http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, bytes);
                return cachePath;
            }
        }
        catch
        {
        }

        return null;
    }

    public async Task<Dictionary<(int x, int y), string>> FetchTilesForBounds(
        double minLat, double minLon, double maxLat, double maxLon, int zoom)
    {
        var (minTx, minTy) = LatLonToTile(minLat, minLon, zoom);
        var (maxTx, maxTy) = LatLonToTile(maxLat, maxLon, zoom);

        int x1 = Math.Min(minTx, maxTx);
        int x2 = Math.Max(minTx, maxTx);
        int y1 = Math.Min(minTy, maxTy);
        int y2 = Math.Max(minTy, maxTy);

        int maxTiles = 12;
        if ((x2 - x1 + 1) * (y2 - y1 + 1) > maxTiles * maxTiles)
        {
            int cx = (x1 + x2) / 2;
            int cy = (y1 + y2) / 2;
            int half = maxTiles / 2;
            x1 = cx - half; x2 = cx + half;
            y1 = cy - half; y2 = cy + half;
        }

        var results = new Dictionary<(int x, int y), string>();
        var tasks = new List<Task>();

        for (int tx = x1; tx <= x2; tx++)
        {
            for (int ty = y1; ty <= y2; ty++)
            {
                int localTx = tx, localTy = ty;
                tasks.Add(Task.Run(async () =>
                {
                    var path = await FetchTileAsync(localTx, localTy, zoom);
                    if (path != null)
                    {
                        lock (results)
                            results[(localTx, localTy)] = path;
                    }
                }));
            }
        }

        await Task.WhenAll(tasks);
        return results;
    }

    public (int optimalZoom, double centerLat, double centerLon) ComputeOptimalZoom(
        double minLat, double minLon, double maxLat, double maxLon, int viewWidth, int viewHeight)
    {
        double centerLat = (minLat + maxLat) / 2.0;
        double centerLon = (minLon + maxLon) / 2.0;

        double latRange = Math.Abs(maxLat - minLat);
        double lonRange = Math.Abs(maxLon - minLon);

        if (latRange < 0.0001) latRange = 0.001;
        if (lonRange < 0.0001) lonRange = 0.001;

        int bestZoom = 16;
        for (int z = 5; z <= 19; z++)
        {
            int n = 1 << z;
            double tilesLon = lonRange / 360.0 * n;
            double tilesLat = latRange / 180.0 * n;

            if (tilesLon * 256 > viewWidth * 1.5 || tilesLat * 256 > viewHeight * 1.5)
            {
                bestZoom = Math.Max(5, z - 1);
                break;
            }
            bestZoom = z;
        }

        return (bestZoom, centerLat, centerLon);
    }

    public void ClearTileCache()
    {
        try
        {
            if (Directory.Exists(_tileCacheDir))
            {
                foreach (var f in Directory.GetFiles(_tileCacheDir, "*.jpg"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}