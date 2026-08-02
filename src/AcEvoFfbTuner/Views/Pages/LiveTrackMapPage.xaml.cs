using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views.Pages;

public partial class LiveTrackMapPage : UserControl
{
    private SatelliteMapService? _satelliteService;
    private bool _satelliteInitialized;
    private readonly TrackDataService _trackDataService = new();
    private string? _lastLoadedTrack;
    private TrackDetailedInfo? _currentOsmData;
    private bool _mapCentered;
    private double[] _osmCumDist = Array.Empty<double>();
    private double _osmTotalDist;
    private int _osmStartFinishIndex;
    private double _osmSfCumDist; // fractional cumulative distance at the SF line

    // The game's TrackMap (used for GameToGps calibration when available)
    private TrackMap? _currentTrackMap;

    // Last-known geo-reference values (gate SetGeoReference to value changes)
    private float _lastKnownLat, _lastKnownLon, _lastKnownRotation;
    private string? _alignedTrackName;

    // Diagnostic logging (debug builds only — unbounded file growth is not shippable)
#if DEBUG
    private int _logFrameCount;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "livemap_pos.log");
#endif

    // Auto-calibration: detect Npos wrap to find the real start/finish position
    private float _prevNpos = -1f;
    private bool _sfAutoCalibrated;

    // Cache for the nearest-OSM-point fallback (avoid a full layout scan every tick)
    private int _lastNearestIdx = -1;
    private double _lastNearestLat, _lastNearestLon;

    public LiveTrackMapPage()
    {
        InitializeComponent();
        _trackDataService.TrackDataUpdated += OnTrackDataUpdated;
        _trackDataService.StatusMessage += msg => Dispatcher.Invoke(() =>
        {
            OsmStatusText.Text = msg;
            StatusDetail.Text = msg;
        });
        // When the page becomes visible again, invalidate the stale Npos sample so a
        // session jump can't be mistaken for a start/finish line crossing.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                _prevNpos = -1f;
        };
    }

    public void Initialize()
    {
        if (_satelliteInitialized) return;
        _satelliteInitialized = true;
        _satelliteService = new SatelliteMapService();
        MapCtrl.Initialize(_satelliteService);
        OsmStatusText.Text = "Ready — waiting for track data...";
    }

    public void UpdateDisplay(float carX, float carZ, float heading, float speedKmh,
        bool isOnTrack, bool hasMap, float npos, string? trackName,
        float trackLatitude, float trackLongitude, float trackRotation,
        int sectorNumber = 0, int lapCount = 0,
        WaypointForceSample[]? forceHeatmap = null,
        TrackMap? currentMap = null)
    {
        if (!_satelliteInitialized || Visibility != Visibility.Visible) return;

        // Cache the TrackMap for GameToGps calibration
        if (currentMap != null && currentMap != _currentTrackMap)
        {
            _currentTrackMap = currentMap;
            // Auto-compute the GameToGps transform from the TrackMap
            if (_satelliteService != null && currentMap.Waypoints.Count > 10 && currentMap.TrackLengthM > 100)
                _satelliteService.ComputeGameToGpsTransform(currentMap);
        }

        if (trackName != _lastLoadedTrack && !string.IsNullOrEmpty(trackName))
        {
            _lastLoadedTrack = trackName;
            _currentOsmData = null;
            _mapCentered = false;
            _prevNpos = -1f;
            _sfAutoCalibrated = false;
            _lastNearestIdx = -1;
            _ = LoadOsmDataAsync(trackName);
        }

        // Geo-reference from game telemetry — ONLY when values changed.
        // (Gated like the proven TrackMapPage so it doesn't clobber saved alignment every frame.)
        if (_satelliteService != null && (trackLatitude != 0 || trackLongitude != 0))
        {
            if (_lastKnownLat != trackLatitude || _lastKnownLon != trackLongitude || _lastKnownRotation != trackRotation)
            {
                _lastKnownLat = trackLatitude;
                _lastKnownLon = trackLongitude;
                _lastKnownRotation = trackRotation;
                _satelliteService.SetGeoReference(trackLatitude, trackLongitude, trackRotation);
            }
        }

        if (_satelliteService != null && !_satelliteService.HasGeoData && !string.IsNullOrEmpty(trackName))
        {
            var loc = TrackDatabase.LookupTrackLocation(trackName);
            if (loc != null)
            {
                _satelliteService.SetGeoReference(loc.Value.lat, loc.Value.lon, 0f);
                _lastKnownLat = loc.Value.lat;
                _lastKnownLon = loc.Value.lon;
                _lastKnownRotation = 0f;
            }
        }

        // Apply the saved corner alignment on track change — runs AFTER the
        // geo-reference so the calibrated rotation WINS (same as TrackMapPage).
        if (_lastLoadedTrack != _alignedTrackName && !string.IsNullOrEmpty(_lastLoadedTrack))
        {
            _alignedTrackName = _lastLoadedTrack;
            TryApplySavedAlignment();
        }

        // Position the car using the game's Npos (normalized track position, 0..1).
        // Npos maps DIRECTLY onto the OSM track layout via GetPositionOnOsmTrack.
        double carLat = 0, carLon = 0;
        double carHeading = heading;
        bool hasPosition = false;

        // Auto-calibrate: when Npos wraps (~0.99 → ~0.01), the car has crossed
        // the start/finish line. Use the car's GPS position at that moment to
        // set the correct SF anchor — works for ANY circuit layout.
        bool nposCrossedLine = _prevNpos >= 0 && npos >= 0f
            && ((npos < 0.1f && _prevNpos > 0.9f) || (npos > 0.9f && _prevNpos < 0.1f));
        _prevNpos = npos;

        if (nposCrossedLine && _currentOsmData?.TrackLayout is { Count: > 2 } layout
            && _satelliteService != null && _satelliteService.HasGeoData
            && carX != 0f && carZ != 0f)
        {
            var (gpsLat, gpsLon) = _satelliteService.GameToGps(carX, carZ);
            if (IsValidGps(gpsLat, gpsLon))
            {
                // Find the nearest layout point, then walk backward to find a
                // point on the STRAIGHT (low curvature). The start/finish line
                // is on the straight before T1, not at the corner apex.
                var (snapLat, snapLon, snapIdx) = FindNearestOsmPoint(gpsLat, gpsLon, layout);
                int sfIdx = FindStraightPointFrom(layout, snapIdx);
                _osmStartFinishIndex = sfIdx;

                // Compute the exact fractional cumulative distance at the car's crossing point.
                // Find the two layout points that bracket the car's GPS and interpolate.
                _osmSfCumDist = InterpolateCumDistAtGps(gpsLat, gpsLon, layout, _osmCumDist);

                _sfAutoCalibrated = true;
                _currentOsmData.StartFinish = new TrackPoint(layout[sfIdx].Latitude, layout[sfIdx].Longitude);
                MapCtrl.SetGpsTrackOutline(layout, _currentOsmData.Corners, _currentOsmData.SectorBoundaries);
                if (_currentOsmData.Pit != null) MapCtrl.AddPitMarkers(_currentOsmData.Pit);
                MapCtrl.AddStartFinishMarker(_currentOsmData.StartFinish);
                StaticLog($"Auto-calibrated SF: sfIdx={sfIdx} sfCumDist={_osmSfCumDist:F5} ({layout[sfIdx].Latitude:F5},{layout[sfIdx].Longitude:F5})");
            }
        }

        if (_currentOsmData?.TrackLayout != null && _currentOsmData.TrackLayout.Count > 2 && npos >= 0f)
        {
            var (lat, lon, osmHeading) = GetPositionOnOsmTrack(npos);
            carLat = lat;
            carLon = lon;
            carHeading = osmHeading;
            hasPosition = lat != 0 || lon != 0;
        }
        // Fallback: game world coords via calibrated transform (when Npos unavailable)
        else if (carX != 0f || carZ != 0f)
        {
            if (_satelliteService != null && _satelliteService.HasGeoData)
            {
                var (gpsLat, gpsLon) = _satelliteService.GameToGps(carX, carZ);
                if (IsValidGps(gpsLat, gpsLon))
                {
                    carLat = gpsLat;
                    carLon = gpsLon;
                    hasPosition = true;

                    // Snap to nearest OSM point for clean track-following
                    if (_currentOsmData?.TrackLayout != null && _currentOsmData.TrackLayout.Count > 2)
                    {
                        var (snapLat, snapLon, snapIdx) = FindNearestOsmPointCached(gpsLat, gpsLon, _currentOsmData.TrackLayout);
                        carLat = snapLat;
                        carLon = snapLon;
                        carHeading = GetHeadingAtOsmIndex(snapIdx, _currentOsmData.TrackLayout);
                    }
                }
            }
        }

        if (hasPosition)
        {
            PositionValue.Text = $"{carLat:F5}, {carLon:F5}";
            var cal = _sfAutoCalibrated ? " AUTO" : "";
            ProgressValue.Text = $"npos={npos:F4} sfIdx={_osmStartFinishIndex}{cal}";
            MapCtrl.UpdateCarGpsPosition(carLon, carLat, carHeading);
        }
        else
        {
            ProgressValue.Text = $"npos={npos:F4} data={(_currentOsmData != null ? "OK" : "NULL")}";
        }

#if DEBUG
        // Diagnostic logging — every 30 frames (~0.5s) write positioning data to file
        if (++_logFrameCount % 30 == 0)
        {
            LogPos(npos, carX, carZ, carLat, carLon, trackName ?? "?");
        }
#endif

        int gameSector = DataContext is MainViewModel gvm ? gvm.GameSectorIndex : sectorNumber;

        TrackName.Text = trackName ?? "--";
        SpeedValue.Text = speedKmh > 0f ? $"{speedKmh:F0} km/h" : "--";
        SectorValue.Text = gameSector > 0 ? $"S{gameSector}" : "--";
        LapValue.Text = lapCount > 0 ? $"L{lapCount}" : "--";

        if (DataContext is MainViewModel vm)
        {
            CornerNumber.Text = vm.CurrentCornerName;
            CornerName.Text = vm.CurrentCornerRealName ?? "";
        }

        PitStatus.Text = _currentOsmData?.Pit != null ? "OSM" : "-";

        if (_currentOsmData != null)
        {
            var pitText = _currentOsmData.Pit != null ? $" pit=yes" : "";
            var srcText = DataSourceLabelText(_currentOsmData.DataSource);
            OsmDetail.Text = $"{srcText}: {_currentOsmData.Corners.Count} corners, {_currentOsmData.TrackLayout?.Count ?? 0} pts{pitText}";
        }
    }

    private static bool IsValidGps(double lat, double lon) =>
        !double.IsNaN(lat) && !double.IsNaN(lon) && lat != 0 && lon != 0
        && Math.Abs(lat) < 90 && Math.Abs(lon) < 180;

    /// <summary>
    /// Diagnostic log: raw Npos from game, game coords, computed GPS, and OSM layout metadata.
    /// </summary>
    private void LogPos(float npos, float carX, float carZ, double carLat, double carLon, string trackName)
    {
        try
        {
            string sfInfo = "sf=none";
            string layoutInfo = "layout=0";
            string interpInfo = "";
            if (_currentOsmData?.TrackLayout is { Count: > 2 } layout)
            {
                layoutInfo = $"layout={layout.Count}";
                if (_currentOsmData.StartFinish != null)
                    sfInfo = $"sf=({_currentOsmData.StartFinish.Latitude:F5},{_currentOsmData.StartFinish.Longitude:F5})";
            }
            if (_osmCumDist.Length > 0 && _osmTotalDist > 0 && npos >= 0f)
            {
                double target = _osmSfCumDist + npos * _osmTotalDist;
                interpInfo = $" interp={target:F5}/{_osmTotalDist:F4} sfDist={_osmSfCumDist:F5} sfIdx={_osmStartFinishIndex}";
            }
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] npos={npos:F4} game=({carX:F1},{carZ:F1}) " +
                       $"gps=({carLat:F5},{carLon:F5}) {sfInfo} sfIdx={_osmStartFinishIndex} auto={_sfAutoCalibrated}" +
                       $" {layoutInfo}{interpInfo} track={trackName}";
            System.Diagnostics.Debug.WriteLine(line);
#if DEBUG
            File.AppendAllText(LogPath, line + Environment.NewLine);
#endif
        }
        catch { }
    }

    /// <summary>
    /// Apply the saved corner alignment for the current track so GameToGps
    /// is properly calibrated (same mechanism as the proven TrackMapPage).
    /// </summary>
    private void TryApplySavedAlignment()
    {
        if (_satelliteService == null) return;
        var trackName = _lastLoadedTrack;
        if (string.IsNullOrEmpty(trackName)) return;

        // Try alignment database first (has corner point data)
        if (TrackAlignmentService.TryApplyAlignment(_satelliteService, trackName,
                out var anchorLat, out var anchorLon, out var rotDeg))
        {
            // Recompute the game center from the TrackMap if available (matches old map)
            if (_currentTrackMap != null && _currentTrackMap.Waypoints.Count > 10)
                _satelliteService.ComputeGameToGpsTransform(_currentTrackMap);

            // Record so the game telemetry geo-ref doesn't clobber it
            _lastKnownLat = anchorLat;
            _lastKnownLon = anchorLon;
            _lastKnownRotation = rotDeg;
            StaticLog($"Applied saved alignment for {trackName}: rot={rotDeg:F1}°");
            return;
        }

        // Fall back to simple saved calibration (center + rotation)
        var calib = TrackAlignmentService.LoadCalibration(trackName);
        if (calib != null)
        {
            _satelliteService.SetGeoReference(calib.Value.lat, calib.Value.lon, calib.Value.rotationDeg);
            _lastKnownLat = calib.Value.lat;
            _lastKnownLon = calib.Value.lon;
            _lastKnownRotation = calib.Value.rotationDeg;
            StaticLog($"Applied saved calibration for {trackName}: rot={calib.Value.rotationDeg:F1}°");
        }
    }

    private static void StaticLog(string msg)
    {
        System.Diagnostics.Debug.WriteLine($"[LiveTrackMap] {msg}");
    }

    private static double GetHeadingAtOsmIndex(int idx, List<TrackPoint> layout)
    {
        if (idx > 0 && idx < layout.Count)
        {
            var prev = layout[idx - 1];
            var curr = layout[idx];
            return Math.Atan2(curr.Longitude - prev.Longitude, curr.Latitude - prev.Latitude);
        }
        return 0;
    }

    private static (double lat, double lon, int index) FindNearestOsmPoint(
        double lat, double lon, List<TrackPoint> layout)
    {
        int nearest = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < layout.Count; i++)
        {
            double dlat = lat - layout[i].Latitude;
            double dlon = lon - layout[i].Longitude;
            double d = dlat * dlat + dlon * dlon;
            if (d < bestDist) { bestDist = d; nearest = i; }
        }
        return (layout[nearest].Latitude, layout[nearest].Longitude, nearest);
    }

    private (double lat, double lon, int index) FindNearestOsmPointCached(
        double lat, double lon, List<TrackPoint> layout)
    {
        // Reuse the previous result when the car hasn't moved much (~10 m) to avoid
        // a full O(n) layout scan on every tick.
        if (_lastNearestIdx >= 0 && _lastNearestIdx < layout.Count)
        {
            double dlat = lat - _lastNearestLat;
            double dlon = lon - _lastNearestLon;
            if (dlat * dlat + dlon * dlon < 1e-8)
                return (layout[_lastNearestIdx].Latitude, layout[_lastNearestIdx].Longitude, _lastNearestIdx);
        }

        var nearest = FindNearestOsmPoint(lat, lon, layout);
        _lastNearestIdx = nearest.index;
        _lastNearestLat = nearest.lat;
        _lastNearestLon = nearest.lon;
        return nearest;
    }

    /// <summary>
    /// Walk backward from a layout index to find a point on a straight section.
    /// The start/finish line is on the straight before T1, not at the corner apex.
    /// Walks backward (decreasing index, wrapping) until curvature is low (straight)
    /// or a maximum distance is reached.
    /// </summary>
    private static int FindStraightPointFrom(List<TrackPoint> layout, int startIdx)
    {
        int n = layout.Count;
        if (n < 5) return startIdx;

        // Compute curvature at each point: angle change between consecutive segments
        // Low curvature = straight, high curvature = corner
        const double maxWalkM = 200.0;       // don't walk more than 200m from the corner

        // First, check if startIdx is already on a straight
        if (!IsCornerAt(layout, startIdx))
            return startIdx;

        // Walk backward to find the start of the straight section
        int idx = startIdx;
        double walked = 0;
        for (int steps = 0; steps < n; steps++)
        {
            int prev = (idx - 1 + n) % n;
            double segDist = Math.Sqrt(
                (layout[idx].Latitude - layout[prev].Latitude) * (layout[idx].Latitude - layout[prev].Latitude) * 111320 * 111320 +
                (layout[idx].Longitude - layout[prev].Longitude) * (layout[idx].Longitude - layout[prev].Longitude) * 69000 * 69000);
            walked += segDist;
            if (walked > maxWalkM) break;

            if (!IsCornerAt(layout, prev))
                return prev; // Found a straight point!

            idx = prev;
        }

        // Couldn't find a clear straight point — return what we have
        return idx;
    }

    /// <summary>
    /// Check if a layout point is at a corner (high curvature).
    /// </summary>
    private static bool IsCornerAt(List<TrackPoint> layout, int idx)
    {
        int n = layout.Count;
        int prev = (idx - 1 + n) % n;
        int next = (idx + 1) % n;

        double dx1 = layout[idx].Longitude - layout[prev].Longitude;
        double dy1 = layout[idx].Latitude - layout[prev].Latitude;
        double dx2 = layout[next].Longitude - layout[idx].Longitude;
        double dy2 = layout[next].Latitude - layout[idx].Latitude;

        double cross = Math.Abs(dx1 * dy2 - dy1 * dx2);
        double dot = dx1 * dx2 + dy1 * dy2;
        double angle = Math.Atan2(cross, dot + 1e-10);

        return angle > 0.12; // ~7 degrees = corner
    }

    /// <summary>
    /// Interpolate the exact cumulative distance along the layout at a given GPS position.
    /// Finds the nearest layout segment and linearly interpolates between its two endpoints.
    /// </summary>
    private static double InterpolateCumDistAtGps(
        double gpsLat, double gpsLon, List<TrackPoint> layout, double[] cumDist)
    {
        int n = layout.Count;
        if (n < 2) return 0;

        // Find the nearest layout segment (pair of consecutive points)
        double bestDist = double.MaxValue;
        int bestIdx = 0;

        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double d = PointToSegmentDist(
                gpsLat, gpsLon,
                layout[i].Latitude, layout[i].Longitude,
                layout[j].Latitude, layout[j].Longitude);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }

        // Interpolate: find the fraction along the segment where the GPS point falls
        int i1 = bestIdx;
        int i2 = (bestIdx + 1) % n;
        double segLen = Math.Sqrt(
            (layout[i2].Latitude - layout[i1].Latitude) * (layout[i2].Latitude - layout[i1].Latitude) +
            (layout[i2].Longitude - layout[i1].Longitude) * (layout[i2].Longitude - layout[i1].Longitude));
        double totalLoop = cumDist[n - 1] + Math.Sqrt(
            (layout[0].Latitude - layout[n - 1].Latitude) * (layout[0].Latitude - layout[n - 1].Latitude) +
            (layout[0].Longitude - layout[n - 1].Longitude) * (layout[0].Longitude - layout[n - 1].Longitude));

        if (segLen < 1e-10) return cumDist[i1]; // degenerate segment

        // Project GPS onto the segment
        double dx = layout[i2].Longitude - layout[i1].Longitude;
        double dy = layout[i2].Latitude - layout[i1].Latitude;
        double px = gpsLon - layout[i1].Longitude;
        double py = gpsLat - layout[i1].Latitude;
        double t = Math.Max(0, Math.Min(1, (px * dx + py * dy) / (dx * dx + dy * dy + 1e-15)));

        double baseDist = cumDist[i1];
        double addedDist = t * segLen;

        // Handle wrap: if i2 wraps (i1=n-1, i2=0), add the closing segment distance
        if (i2 == 0)
        {
            double closingDist = totalLoop - cumDist[n - 1];
            addedDist = t * closingDist;
            return baseDist + addedDist;
        }

        return baseDist + addedDist;
    }

    /// <summary>Distance from a point to a line segment (in degree-space).</summary>
    private static double PointToSegmentDist(
        double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-15) return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));
        double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double projX = ax + t * dx, projY = ay + t * dy;
        return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
    }

    /// <summary>Interpolate position on the OSM track using game Npos (0..1 where 0 = start/finish).</summary>
    private (double lat, double lon, double heading) GetPositionOnOsmTrack(float npos)
    {
        var pts = _currentOsmData!.TrackLayout!;
        int n = pts.Count;

        if (n < 2 || _osmTotalDist <= 0) return (0, 0, 0);

        // Wrap Npos to [0..1)
        npos = npos - (float)Math.Floor(npos);
        if (npos < 0) npos += 1f;

        // Offset by the fractional SF distance so Npos=0 maps to the exact start/finish
        double target = _osmSfCumDist + npos * _osmTotalDist;
        if (target >= _osmTotalDist) target -= _osmTotalDist;

        int idx = 0;
        for (int i = 1; i < _osmCumDist.Length; i++)
        {
            if (_osmCumDist[i] >= target) { idx = i; break; }
        }

        if (idx == 0 && target > _osmCumDist[^1])
        {
            // Wrap-around segment: last pt → first pt
            double segLen = _osmTotalDist - _osmCumDist[^1];
            if (segLen <= 0) return (pts[0].Latitude, pts[0].Longitude, 0);
            double t = (target - _osmCumDist[^1]) / segLen;
            double lat = pts[n - 1].Latitude + (pts[0].Latitude - pts[n - 1].Latitude) * t;
            double lon = pts[n - 1].Longitude + (pts[0].Longitude - pts[n - 1].Longitude) * t;
            double h = Math.Atan2(pts[0].Longitude - pts[n - 1].Longitude, pts[0].Latitude - pts[n - 1].Latitude);
            return (lat, lon, h);
        }

        int prev = idx - 1;
        double segLen2 = _osmCumDist[idx] - _osmCumDist[prev];
        if (segLen2 <= 0) return (pts[idx].Latitude, pts[idx].Longitude, 0);
        double t2 = (target - _osmCumDist[prev]) / segLen2;
        double lat2 = pts[prev].Latitude + (pts[idx].Latitude - pts[prev].Latitude) * t2;
        double lon2 = pts[prev].Longitude + (pts[idx].Longitude - pts[prev].Longitude) * t2;
        double h2 = Math.Atan2(pts[idx].Longitude - pts[prev].Longitude, pts[idx].Latitude - pts[prev].Latitude);

        return (lat2, lon2, h2);
    }

    private async Task LoadOsmDataAsync(string trackName)
    {
        try { await _trackDataService.LoadTrackDataAsync(trackName); }
        catch { }
    }

    private static string DataSourceLabelText(TrackDataSource src) => src switch
    {
        TrackDataSource.OsmRelation => "Relation",
        TrackDataSource.OsmBoundingBox => "OSM BBox",
        TrackDataSource.Recorded => "Recorded",
        _ => "?"
    };

    private void OnTrackDataUpdated(TrackDetailedInfo data)
    {
        Dispatcher.Invoke(() =>
        {
            _currentOsmData = data;
            DataSourceLabel.Text = DataSourceLabelText(data.DataSource);

            if (data.TrackLayout != null && data.TrackLayout.Count > 3)
            {
                // Precompute cumulative distances for Npos-based interpolation
                var pts = data.TrackLayout;
                int n = pts.Count;
                _osmCumDist = new double[n];
                _osmCumDist[0] = 0;
                for (int i = 1; i < n; i++)
                {
                    double dlat = pts[i].Latitude - pts[i - 1].Latitude;
                    double dlon = pts[i].Longitude - pts[i - 1].Longitude;
                    _osmCumDist[i] = _osmCumDist[i - 1] + Math.Sqrt(dlat * dlat + dlon * dlon);
                }
                double closeDlat = pts[0].Latitude - pts[n - 1].Latitude;
                double closeDlon = pts[0].Longitude - pts[n - 1].Longitude;
                _osmTotalDist = _osmCumDist[n - 1] + Math.Sqrt(closeDlat * closeDlat + closeDlon * closeDlon);

                // Find the layout point closest to the start/finish line
                // Npos=0 should map here
                _osmStartFinishIndex = 0;
                if (data.StartFinish != null)
                {
                    double sfLat = data.StartFinish.Latitude;
                    double sfLon = data.StartFinish.Longitude;
                    double bestDist2 = double.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        double dlat2 = pts[i].Latitude - sfLat;
                        double dlon2 = pts[i].Longitude - sfLon;
                        double d2 = dlat2 * dlat2 + dlon2 * dlon2;
                        if (d2 < bestDist2) { bestDist2 = d2; _osmStartFinishIndex = i; }
                    }
                }
                // Initialize the fractional SF distance from the integer index
                _osmSfCumDist = _osmCumDist[_osmStartFinishIndex];

                // Log track data load details for diagnosis
                try
                {
                    string sfText = data.StartFinish != null
                        ? $"({data.StartFinish.Latitude:F5},{data.StartFinish.Longitude:F5})"
                        : "none";
                    var line = $"[{DateTime.Now:HH:mm:ss.fff}] TRACKDATA track={data.TrackName} src={data.DataSource} " +
                               $"layout={n} pts totalDist={_osmTotalDist:F4} sf={sfText} sfIdx={_osmStartFinishIndex} " +
                               $"corners={data.Corners.Count} pit={(data.Pit != null)} lengthM={data.TrackLengthM:F0}";
                    System.Diagnostics.Debug.WriteLine(line);
#if DEBUG
                    File.AppendAllText(LogPath, line + Environment.NewLine);
#endif
                }
                catch { }

                MapCtrl.SetGpsTrackOutline(data.TrackLayout, data.Corners, data.SectorBoundaries);

                if (data.Pit != null)
                    MapCtrl.AddPitMarkers(data.Pit);
                if (data.StartFinish != null)
                    MapCtrl.AddStartFinishMarker(data.StartFinish);

                var pitText = data.Pit != null ? $" pit=yes" : "";
                var srcName = DataSourceLabelText(data.DataSource);
                OsmStatusText.Text = $"{srcName}: {data.Corners.Count} corners, {data.TrackLayout.Count} pts{pitText}";
                OsmDataCount.Text = $"{data.Corners.Count} corners";

                if (!_mapCentered)
                {
                    _mapCentered = true;
                    MapCtrl.CenterOnGps(data.TrackLayout[0].Latitude, data.TrackLayout[0].Longitude, 14);
                }
            }
            else if (data.Corners.Count > 0)
            {
                OsmStatusText.Text = $"{DataSourceLabelText(data.DataSource)}: {data.Corners.Count} corners (no layout)";
                OsmDataCount.Text = $"{data.Corners.Count} corners";
            }
            else
            {
                OsmStatusText.Text = $"{DataSourceLabelText(data.DataSource)}: no track data found";
                OsmDataCount.Text = "";
            }
        });
    }

    private void OnSatelliteToggled(object sender, RoutedEventArgs e) { }

    private async void OnRefreshData(object sender, RoutedEventArgs e)
    {
        var trackName = _lastLoadedTrack;
        if (string.IsNullOrEmpty(trackName))
        {
            OsmStatusText.Text = "No track loaded to refresh";
            return;
        }

        // Disable button during refresh to prevent double-click
        RefreshDataBtn.IsEnabled = false;
        RefreshDataBtn.Content = "⟳ Refreshing...";
        OsmStatusText.Text = $"Refreshing track data for {trackName}...";

        try
        {
            // Delete cache files so the next load gets fresh data
            _trackDataService.DeleteCache(trackName);

            // Look up GPS for the force refresh
            double? centerLat = null, centerLon = null;
            var loc = TrackDatabase.LookupTrackLocation(trackName);
            if (loc != null)
            {
                centerLat = loc.Value.lat;
                centerLon = loc.Value.lon;
            }

            // Force a fresh fetch bypassing cache
            await _trackDataService.ForceRefreshAsync(trackName, centerLat, centerLon);

            // Reset map centering so it re-fits the new layout
            _mapCentered = false;

            OsmStatusText.Text = $"Track data refreshed for {trackName}";
        }
        catch (Exception ex)
        {
            OsmStatusText.Text = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            RefreshDataBtn.IsEnabled = true;
            RefreshDataBtn.Content = "⟳ Refresh Data";
        }
    }
}