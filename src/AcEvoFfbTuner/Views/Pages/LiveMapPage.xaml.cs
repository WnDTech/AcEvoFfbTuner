using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Core.TrackMapping;
using Mapsui.Projections;

namespace AcEvoFfbTuner.Views.Pages;

public partial class LiveMapPage : UserControl
{
    private readonly SatelliteMapService _mapService = new();
    private TrackMap? _currentMap;
    private string? _trackName;
    private bool _centerOnCarEnabled;

    public LiveMapPage()
    {
        InitializeComponent();
        MapCtrl.Initialize(_mapService);
        MapCtrl.CalibrationSaved += OnCalibrationSaved;
    }

    public void SetTrackName(string trackName)
    {
        if (_trackName == trackName) return;
        _trackName = trackName;
        TrackNameLabel.Text = $"Track: {trackName ?? "--"}";
        MapCtrl.SetTrackName(trackName ?? "");
    }

    public void SetTrackMap(TrackMap map)
    {
        if (map == _currentMap) return;
        _currentMap = map;
        MapCtrl.SetTrackMap(map);
    }

    public void SetGeoReference(float latitude, float longitude, float rotationDeg)
    {
        if (latitude == 0f && longitude == 0f) return;

        if (_mapService.TrackCenterLatitude == 0 && _mapService.TrackCenterLongitude == 0)
        {
            _mapService.SetGeoReference(latitude, longitude, rotationDeg);
        }

        if (_currentMap != null && _currentMap.Waypoints.Count >= 3)
        {
            _mapService.ComputeGameToGpsTransform(_currentMap);
        }

        MapCtrl.SetGeoCenter(latitude, longitude);
        StatusOverlay.Visibility = Visibility.Collapsed;
        AlignmentLabel.Text = $"Alignment: {rotationDeg:F1}°";
    }

    public void UpdateCarPosition(float gameX, float gameZ, float heading, float speedKmh, bool isOnTrack)
    {
        if (_mapService.HasGeoData)
        {
            MapCtrl.UpdateCarPosition(gameX, gameZ, heading, isOnTrack);

            var (lat, lon) = _mapService.GameToGps(gameX, gameZ);
            CarPosLabel.Text = $"Car: {gameX:F1}, {gameZ:F1}";
            GpsPosLabel.Text = $"GPS: {lat:F5}, {lon:F5}";
            SpeedLabel.Text = $"Speed: {speedKmh:F0} km/h";
        }
        else
        {
            CarPosLabel.Text = $"Car: {gameX:F1}, {gameZ:F1}";
        }

        if (_centerOnCarEnabled && _mapService.HasGeoData)
        {
            var (lat, lon) = _mapService.GameToGps(gameX, gameZ);
            var merc = SphericalMercator.FromLonLat(lon, lat);
            // MapCtrl doesn't expose navigator directly for centering, but we can use CenterOnTrack
        }
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        if (MapCtrl.IsCalibrating)
        {
            MapCtrl.ExitCalibrationMode();
            CalibrateBtn.Content = "Calibrate Position";
            ControlsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            if (!_mapService.HasGeoData) return;
            MapCtrl.EnterCalibrationMode();
            CalibrateBtn.Content = "Exit Calibration";
            ControlsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCalibrationSaved(object? sender, EventArgs e)
    {
        CalibrateBtn.Content = "Calibrate Position";
        ControlsPanel.Visibility = Visibility.Visible;
        AlignmentLabel.Text = $"Alignment: {_mapService.GetRotationDeg():F1}° (calibrated)";
    }

    private void OnCenterOnCar(object sender, RoutedEventArgs e)
    {
        _centerOnCarEnabled = !_centerOnCarEnabled;
        CenterOnCarBtn.Content = _centerOnCarEnabled ? "Free Camera" : "Center on Car";
    }

    public void Reset()
    {
        _currentMap = null;
        _trackName = null;
        _mapService.SetGeoReference(0, 0, 0);
        MapCtrl.Reset();
        StatusOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "Waiting for track data...";
        AlignmentLabel.Text = "Alignment: --";
        CarPosLabel.Text = "Car: --";
        GpsPosLabel.Text = "GPS: --";
        SpeedLabel.Text = "Speed: --";
    }
}
