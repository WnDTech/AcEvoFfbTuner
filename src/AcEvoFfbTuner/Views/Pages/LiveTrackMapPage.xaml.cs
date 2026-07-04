using System;
using System.Collections.Generic;
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

    public LiveTrackMapPage()
    {
        InitializeComponent();
        _trackDataService.TrackDataUpdated += OnTrackDataUpdated;
        _trackDataService.StatusMessage += msg => Dispatcher.Invoke(() =>
        {
            OsmStatusText.Text = msg;
            StatusDetail.Text = msg;
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
        bool isOnTrack, bool hasMap, float npos, string? trackName,
        float trackLatitude, float trackLongitude, float trackRotation,
        int sectorNumber = 0, int lapCount = 0,
        WaypointForceSample[]? forceHeatmap = null)
    {
        if (!_satelliteInitialized) return;

        if (trackName != _lastLoadedTrack && !string.IsNullOrEmpty(trackName))
        {
            _lastLoadedTrack = trackName;
            _currentOsmData = null;
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

        // Use game Npos (0..1 from start/finish line) to interpolate along OSM layout
        double carLat = 0, carLon = 0;
        double carHeading = heading;
        bool hasPosition = false;

        if (_currentOsmData?.TrackLayout != null && _currentOsmData.TrackLayout.Count > 2 && npos >= 0f)
        {
            var (lat, lon, osmHeading) = GetPositionOnOsmTrack(npos);
            carLat = lat;
            carLon = lon;
            carHeading = osmHeading;
            hasPosition = true;
            PositionValue.Text = $"{carLat:F5}, {carLon:F5}";
            ProgressValue.Text = $"{(npos * 100):F1}%";
        }

        if (hasPosition)
        {
            MapCtrl.UpdateCarGpsPosition(carLon, carLat, carHeading);
        }

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
            OsmDetail.Text = $"{_currentOsmData.Corners.Count} corners, {_currentOsmData.TrackLayout?.Count ?? 0} pts{pitText}";
        }
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

        // Offset by the start/finish index so Npos=0 maps to the start/finish line
        double distAtSf = _osmCumDist[_osmStartFinishIndex];
        double target = distAtSf + npos * _osmTotalDist;
        if (target >= _osmTotalDist) target -= _osmTotalDist;

        int idx = 0;
        for (int i = 1; i < _osmCumDist.Length; i++)
        {
            if (_osmCumDist[i] >= target) { idx = i; break; }
        }

        if (idx == 0)
        {
            // Wrap-around segment: last pt → first pt
            double segLen = _osmTotalDist - _osmCumDist[^1];
            if (segLen <= 0) return (pts[0].Latitude, pts[0].Longitude, 0);
            double t = target / segLen;
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

    private void OnTrackDataUpdated(TrackDetailedInfo data)
    {
        Dispatcher.Invoke(() =>
        {
            _currentOsmData = data;

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