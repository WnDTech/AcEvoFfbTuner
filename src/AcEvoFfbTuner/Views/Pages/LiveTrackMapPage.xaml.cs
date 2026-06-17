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
    private readonly ContinuousTrackAligner _trackAligner = new();
    private string? _lastLoadedTrack;
    private TrackDetailedInfo? _currentOsmData;
    private float _lastCarX, _lastCarZ;
    private bool _mapCentered;
    private bool _isCalibrated;
    private int _prevSectorNumber;
    private readonly double[] _sectorHeadingDiffs = new double[4];
    private int _sectorCrossingCount;
    private int _logFrameCount;

    public LiveTrackMapPage()
    {
        InitializeComponent();
        _trackDataService.TrackDataUpdated += OnTrackDataUpdated;
        _trackDataService.StatusMessage += msg => Dispatcher.Invoke(() =>
        {
            OsmStatusText.Text = msg;
            StatusDetail.Text = msg;
        });
        _trackAligner.CalibrationLocked += () => Dispatcher.Invoke(() =>
        {
            OsmStatusText.Text = $"Calibration LOCKED — rotation: {_trackAligner.CurrentRotationDeg:F1}°";
        });
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
        bool isOnTrack, bool hasMap, string? trackName, float trackLatitude,
        float trackLongitude, float trackRotation,
        int sectorNumber = 0, int lapCount = 0,
        WaypointForceSample[]? forceHeatmap = null)
    {
        if (!_satelliteInitialized) return;

        _lastCarX = carX;
        _lastCarZ = carZ;

        // Get game sector index from raw npos (independent of recorded track map)
        int gameSector = DataContext is MainViewModel gvm ? gvm.GameSectorIndex : sectorNumber;

        // Log raw telemetry every 60 frames
        if (_logFrameCount % 60 == 0)
        {
            string rawLog = $"[{DateTime.Now:HH:mm:ss.fff}] RAW carX:{carX:F2} carZ:{carZ:F2} heading:{heading:F2} speed:{speedKmh:F1} sector:{sectorNumber} gameSector:{gameSector}\n";
            try { File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "trackdata.log"), rawLog); } catch { }
        }

        // Log raw telemetry every 60 frames
        if (_logFrameCount % 60 == 0)
        {
            string rawLog = $"[{DateTime.Now:HH:mm:ss.fff}] RAW carX:{carX:F2} carZ:{carZ:F2} heading:{heading:F2} speed:{speedKmh:F1} sector:{sectorNumber} gameSector:{gameSector}\n";
            try { File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcEvoFfbTuner", "trackdata.log"), rawLog); } catch { }
        }

        // Seed first waypoint for identity transform (car moves before calibration)
        if (speedKmh > 5f && !_trackAligner.HasFirstWaypoint)
        {
            _trackAligner.SeedFirstWaypoint(carX, carZ);
        }

        if (trackName != _lastLoadedTrack && !string.IsNullOrEmpty(trackName))
        {
            _lastLoadedTrack = trackName;
            _currentOsmData = null;
            _trackAligner.Reset();
            _ = LoadOsmDataAsync(trackName);
        }

        if (_satelliteService != null && (trackLatitude != 0 || trackLongitude != 0))
            _satelliteService.SetGeoReference(trackLatitude, trackLongitude, trackRotation);

        if (_satelliteService != null && !_satelliteService.HasGeoData && !string.IsNullOrEmpty(trackName))
        {
            var loc = TrackDatabase.LookupTrackLocation(trackName);
            if (loc != null)
                _satelliteService.SetGeoReference(loc.Value.lat, loc.Value.lon, 0f);
        }

        // Sector-based calibration fast path: detect sector 1→2 and 2→3
        // crossings, match to OSM track boundary points, compute heading offset.
        if (!_isCalibrated && _currentOsmData?.TrackLayout != null && gameSector > 0 &&
            gameSector != _prevSectorNumber)
        {
            _prevSectorNumber = gameSector;

            // Compute sector boundary points at 1/3 and 2/3 of track length
            int b1 = _currentOsmData.TrackLayout.Count / 3;
            int b2 = _currentOsmData.TrackLayout.Count * 2 / 3;

            TrackPoint? boundary = gameSector switch
            {
                2 => _currentOsmData.TrackLayout[b1],
                3 => _currentOsmData.TrackLayout[b2],
                _ => null
            };

            if (boundary != null)
            {
                // Derive heading from 5 points ahead on the OSM boundary
                int aheadIdx = Math.Min(b1 + 5, _currentOsmData.TrackLayout.Count - 1);
                double dlat = _currentOsmData.TrackLayout[aheadIdx].Latitude - boundary.Latitude;
                double dlon = _currentOsmData.TrackLayout[aheadIdx].Longitude - boundary.Longitude;
                double osmHeading = Math.Atan2(dlon, dlat);
                double diff = heading - osmHeading;
                if (diff > Math.PI) diff -= 2 * Math.PI;
                if (diff < -Math.PI) diff += 2 * Math.PI;

                // Need at least 2 crossings for a stable average
                int key = gameSector; // 2 or 3
                _sectorHeadingDiffs[key] = diff;
                _sectorCrossingCount++;

                if (_sectorCrossingCount >= 2)
                {
                    double avg = 0;
                    int cnt = 0;
                    for (int i = 0; i < _sectorHeadingDiffs.Length; i++)
                        if (_sectorHeadingDiffs[i] != 0) { avg += _sectorHeadingDiffs[i]; cnt++; }
                    if (cnt > 0)
                    {
                        avg /= cnt;
                        _trackAligner.SetRotationFromRadians(avg);
                        _isCalibrated = true;
                        OsmStatusText.Text = $"Sector-calibrated: rotation {avg * 180 / Math.PI:F1}°";
                    }
                }
            }
        }

        // Lap-based calibration with normalized position matching
        // (refines or replaces sector calibration if not yet locked)
        if (!_isCalibrated)
        {
            bool calibrated = _trackAligner.CheckLapCompletion(carX, carZ, lapCount);
            if (calibrated)
            {
                _isCalibrated = true;
                OsmStatusText.Text = $"Calibration LOCKED — rotation: {_trackAligner.CurrentRotationDeg:F1}°";
            }
        }

        // Car GPS from aligner
        var (gpsLat, gpsLon) = _trackAligner.GetCarGps(carX, carZ);
        PositionValue.Text = $"{gpsLat:F5}, {gpsLon:F5}";

        // Per-frame comparison: car GPS vs nearest OSM track point
        if (_currentOsmData?.TrackLayout != null && _currentOsmData.TrackLayout.Count > 3)
        {
            int nearestIdx = 0;
            double nearestDist2 = double.MaxValue;
            for (int i = 0; i < _currentOsmData.TrackLayout.Count; i++)
            {
                double dlat = gpsLat - _currentOsmData.TrackLayout[i].Latitude;
                double dlon = gpsLon - _currentOsmData.TrackLayout[i].Longitude;
                double d2 = dlat * dlat + dlon * dlon;
                if (d2 < nearestDist2) { nearestDist2 = d2; nearestIdx = i; }
            }
            double nearestDistM = Math.Sqrt(nearestDist2) * 111320;

            // Log every 60 frames (~1 sec at 60fps)
            _logFrameCount++;
            if (_logFrameCount % 60 == 0)
            {
                var nearestPt = _currentOsmData.TrackLayout[nearestIdx];
                string compareLog = $"[{DateTime.Now:HH:mm:ss.fff}] CAR:{gpsLat:F5},{gpsLon:F5} OSM:{nearestPt.Latitude:F5},{nearestPt.Longitude:F5} dist:{nearestDistM:F0}m rot:{_trackAligner.CurrentRotationDeg:F1}° cal:{_trackAligner.IsCalibrated}\n";
                try { File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AcEvoFfbTuner", "trackdata.log"), compareLog); } catch { }
            }
        }

        string gpsLog = $"[{DateTime.Now:HH:mm:ss.fff}] GPS lat:{gpsLat:F5} lon:{gpsLon:F5} rot:{_trackAligner.CurrentRotationDeg:F1}° cal:{_trackAligner.IsCalibrated}\n";
        try { File.AppendAllText(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "trackdata.log"), gpsLog); } catch { }

        // Map heading
        double mapHeading = (Math.PI * 0.5) - (heading - _trackAligner.CurrentRotationRad);
        mapHeading = mapHeading % (2 * Math.PI);
        if (mapHeading < 0) mapHeading += 2 * Math.PI;

        MapCtrl.UpdateCarGpsPosition(gpsLon, gpsLat, mapHeading);

        TrackName.Text = trackName ?? "--";
        SpeedValue.Text = speedKmh > 0f ? $"{speedKmh:F0} km/h" : "--";
        SectorValue.Text = gameSector > 0 ? $"S{gameSector}" : "--";
        LapValue.Text = lapCount > 0 ? $"L{lapCount}" : "--";

        if (DataContext is MainViewModel vm)
        {
            CornerNumber.Text = vm.CurrentCornerName;
            CornerName.Text = vm.CurrentCornerRealName ?? "";
            if (!string.IsNullOrEmpty(vm.SectorStats))
                ProgressValue.Text = vm.SectorStats;
            else
                ProgressValue.Text = "";
        }

        PitStatus.Text = _currentOsmData?.Pit != null ? "OSM" : "-";

        if (_currentOsmData != null)
        {
            var pitText = _currentOsmData.Pit != null ? $" pit=yes" : "";
            OsmDetail.Text = $"{_currentOsmData.Corners.Count} corners, {_currentOsmData.TrackLayout?.Count ?? 0} pts{pitText}";
        }
    }

    private async Task LoadOsmDataAsync(string trackName)
    {
        try { await _trackDataService.LoadTrackDataAsync(trackName); }
        catch { }
    }

    private void OnTrackDataUpdated(TrackDetailedInfo data)
    {
        Dispatcher.Invoke(() =>
        {
            _currentOsmData = data;

            if (data.TrackLayout != null && data.TrackLayout.Count > 3)
            {
                double anchorLat, anchorLon;
                if (data.StartFinish != null)
                {
                    anchorLat = data.StartFinish.Latitude;
                    anchorLon = data.StartFinish.Longitude;
                }
                else
                {
                    int mid = data.TrackLayout.Count / 2;
                    anchorLat = data.TrackLayout[mid].Latitude;
                    anchorLon = data.TrackLayout[mid].Longitude;
                }

                _trackAligner.InitializeTrack(
                    data.TrackLayout,
                    data.Pit?.Layout,
                    anchorLat, anchorLon);

                MapCtrl.SetGpsTrackOutline(data.TrackLayout, data.Corners);

                if (data.Pit != null)
                    MapCtrl.AddPitMarkers(data.Pit);
                if (data.StartFinish != null)
                    MapCtrl.AddStartFinishMarker(data.StartFinish);

                var pitText = data.Pit != null ? $" pit=yes" : "";
                OsmStatusText.Text = $"OSM: {data.Corners.Count} corners, {data.TrackLayout.Count} pts{pitText}";
                OsmDataCount.Text = $"{data.Corners.Count} corners";

                if (!_mapCentered)
                {
                    _mapCentered = true;
                    MapCtrl.CenterOnGps(data.TrackLayout[0].Latitude, data.TrackLayout[0].Longitude, 14);
                }
            }
            else if (data.Corners.Count > 0)
            {
                OsmStatusText.Text = $"OSM: {data.Corners.Count} corners (no layout)";
                OsmDataCount.Text = $"{data.Corners.Count} corners";
            }
            else
            {
                OsmStatusText.Text = "OSM: no track data found for this track";
                OsmDataCount.Text = "";
            }
        });
    }

    private void OnSatelliteToggled(object sender, RoutedEventArgs e) { }
}