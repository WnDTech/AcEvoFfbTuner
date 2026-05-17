using System.Windows;
using System.Windows.Controls;
using AcEvoFfbTuner.Core.TrackMapping;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Nts.Extensions;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using Mapsui.UI.Wpf;
using Coordinate = NetTopologySuite.Geometries.Coordinate;
using LineString = NetTopologySuite.Geometries.LineString;

namespace AcEvoFfbTuner.Views.Pages;

public partial class LiveMapPage : UserControl
{
    private readonly Map _map;
    private MemoryLayer _trackLayer;
    private MemoryLayer _osmLayer;
    private MemoryLayer _carLayer;
    private MemoryLayer _cornerLayer;

    private TrackMap? _currentMap;
    private string? _trackName;
    private bool _alignmentDone;
    private bool _autoFitDone;
    private bool _isAligning;
    private bool _centerOnCarEnabled;

    private double _gameCenterX, _gameCenterZ;
    private double _osmCenterLat, _osmCenterLon;
    private double _alignmentRotationRad;

    private readonly PointFeature _carFeature = new(0, 0);
    private readonly SymbolStyle _carOnTrackStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.7,
        Fill = new Brush { Color = Color.FromRgba(0xFF, 0x45, 0x00, 0xE0) },
        Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x80), Width = 1 }
    };
    private readonly SymbolStyle _carOffTrackStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.7,
        Fill = new Brush { Color = Color.FromRgba(0xFF, 0x00, 0x00, 0xE0) },
        Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x80), Width = 1 }
    };
    private readonly GeometryFeature _headingFeature = new()
    {
        Geometry = new LineString(new[] { new Coordinate(0, 0), new Coordinate(0, 0) })
    };
    private readonly VectorStyle _headingStyle = new()
    {
        Line = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xE0), Width = 2.5 }
    };
    private readonly List<IFeature> _carFeatures;

    public LiveMapPage()
    {
        InitializeComponent();

        _map = new Map { CRS = "EPSG:3857" };
        _map.Layers.Add(CreateSatelliteTileLayer());

        _osmLayer = new MemoryLayer("OSM Road") { Features = [] };
        _map.Layers.Add(_osmLayer);

        _trackLayer = new MemoryLayer("Track") { Features = [] };
        _map.Layers.Add(_trackLayer);

        _cornerLayer = new MemoryLayer("Corners") { Features = [] };
        _map.Layers.Add(_cornerLayer);

        _carFeature.Styles.Add(_carOnTrackStyle);
        _headingFeature.Styles.Add(_headingStyle);
        _carFeatures = [_carFeature, _headingFeature];

        _carLayer = new MemoryLayer("Car") { Features = _carFeatures };
        _map.Layers.Add(_carLayer);

        MapCtrl.Map = _map;

        SetCenter(0, 0, 3);
    }

    private static TileLayer CreateSatelliteTileLayer()
    {
        var tileSource = new HttpTileSource(
            new GlobalSphericalMercator(),
            "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
            name: "ESRI Satellite");
        return new TileLayer(tileSource) { Name = "Satellite" };
    }

    private static MPoint ToMercator(double lon, double lat)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        return new MPoint(x, y);
    }

    private void SetCenter(double lon, double lat, double zoom)
    {
        var merc = ToMercator(lon, lat);
        _map.Navigator.CenterOn(merc.X, merc.Y);
        _map.Navigator.ZoomTo(zoom);
    }

    public void SetTrackName(string trackName)
    {
        if (_trackName == trackName) return;
        _trackName = trackName;
        TrackNameLabel.Text = $"Track: {trackName ?? "--"}";
        ResetAlignment();
    }

    public void SetTrackMap(TrackMap map)
    {
        if (map == _currentMap) return;
        _currentMap = map;
        _autoFitDone = false;

        if (!_alignmentDone && !_isAligning && map.Waypoints.Count >= 10)
        {
            _ = TryAutoAlignAsync();
        }
    }

    public void UpdateCarPosition(float gameX, float gameZ, float heading, float speedKmh, bool isOnTrack)
    {
        if (!_alignmentDone)
        {
            CarPosLabel.Text = $"Car: {gameX:F1}, {gameZ:F1}";
            return;
        }

        var (lat, lon) = GameToGps(gameX, gameZ);
        var merc = ToMercator(lon, lat);

        _carFeature.Point.X = merc.X;
        _carFeature.Point.Y = merc.Y;
        _carFeature.Styles.Clear();
        _carFeature.Styles.Add(isOnTrack ? _carOnTrackStyle : _carOffTrackStyle);

        var vp = _map.Navigator.Viewport;
        double headingLen = vp.Resolution * 28;
        double rad = heading;

        var headX = merc.X + Math.Sin(rad) * headingLen;
        var headY = merc.Y - Math.Cos(rad) * headingLen;

        var geom = (LineString)_headingFeature.Geometry!;
        geom.CoordinateSequence.SetOrdinate(0, 0, merc.X);
        geom.CoordinateSequence.SetOrdinate(0, 1, merc.Y);
        geom.CoordinateSequence.SetOrdinate(1, 0, headX);
        geom.CoordinateSequence.SetOrdinate(1, 1, headY);
        _headingFeature.Geometry = geom;

        _carLayer.FeaturesWereModified();

        CarPosLabel.Text = $"Car: {gameX:F1}, {gameZ:F1}";
        GpsPosLabel.Text = $"GPS: {lat:F5}, {lon:F5}";
        SpeedLabel.Text = $"Speed: {speedKmh:F0} km/h";

        if (_centerOnCarEnabled)
        {
            _map.Navigator.CenterOn(merc.X, merc.Y);
        }
    }

    private async Task TryAutoAlignAsync()
    {
        if (_currentMap == null || string.IsNullOrWhiteSpace(_trackName) || _isAligning) return;

        _isAligning = true;
        StatusOverlay.Visibility = Visibility.Visible;
        StatusText.Text = $"Aligning {_trackName} to road data...";

        try
        {
            var alignment = await TrackAlignmentService.ComputeAlignmentAsync(_trackName, _currentMap.Waypoints);

            if (alignment != null)
            {
                ApplyAlignment(alignment);
                AlignmentLabel.Text = $"Alignment: OSM auto ({alignment.RotationDeg:F1}°)";
                StatusOverlay.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch { }

        var known = SatelliteMapService.LookupTrackLocation(_trackName);
        var knownRot = SatelliteMapService.LookupTrackRotation(_trackName);

        if (known.HasValue)
        {
            ApplyKnownLocation(known.Value.lat, known.Value.lon, knownRot ?? 0);
            AlignmentLabel.Text = "Alignment: Known location";
            StatusOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        AlignmentLabel.Text = "Alignment: Failed";
        StatusText.Text = $"Could not auto-align '{_trackName}'.\nNo OSM data or known location found.";
    }

    private void ApplyAlignment(TrackAlignment alignment)
    {
        if (_currentMap == null || _currentMap.Waypoints.Count < 3) return;

        _osmCenterLat = alignment.CenterLat;
        _osmCenterLon = alignment.CenterLon;
        _alignmentRotationRad = alignment.RotationDeg * Math.PI / 180.0;

        _gameCenterX = _currentMap.Waypoints.Average(w => (double)w.X);
        _gameCenterZ = _currentMap.Waypoints.Average(w => (double)w.Z);

        double cosLat = Math.Cos(_osmCenterLat * Math.PI / 180.0);

        var osmWaypoints = _currentMap.Waypoints.Select(w =>
        {
            double dx = w.X - _gameCenterX;
            double dz = w.Z - _gameCenterZ;
            return Math.Sqrt(dx * dx + dz * dz);
        }).ToList();

        _alignmentDone = true;

        RebuildTrackLayer();
        RebuildCornerLayer();
        AutoFitTrack();
    }

    private void ApplyKnownLocation(float lat, float lon, float rotationDeg)
    {
        if (_currentMap == null || _currentMap.Waypoints.Count < 3) return;

        _osmCenterLat = lat;
        _osmCenterLon = lon;
        _alignmentRotationRad = rotationDeg * Math.PI / 180.0;

        _gameCenterX = _currentMap.Waypoints.Average(w => (double)w.X);
        _gameCenterZ = _currentMap.Waypoints.Average(w => (double)w.Z);

        _alignmentDone = true;

        RebuildTrackLayer();
        RebuildCornerLayer();
        AutoFitTrack();
    }

    private (double lat, double lon) GameToGps(float gameX, float gameZ)
    {
        if (!_alignmentDone)
            return (_osmCenterLat, _osmCenterLon);

        double dx = gameX - _gameCenterX;
        double dz = gameZ - _gameCenterZ;

        double cosR = Math.Cos(_alignmentRotationRad);
        double sinR = Math.Sin(_alignmentRotationRad);
        double eastMeters = dx * cosR - dz * sinR;
        double northMeters = dx * sinR + dz * cosR;

        double cosLat = Math.Cos(_osmCenterLat * Math.PI / 180.0);
        if (cosLat < 0.01) cosLat = 0.01;

        double lat = _osmCenterLat + northMeters / 111320.0;
        double lon = _osmCenterLon + eastMeters / (111320.0 * cosLat);

        return (lat, lon);
    }

    private void RebuildTrackLayer()
    {
        if (_currentMap == null || !_alignmentDone) return;

        var features = new List<IFeature>();
        var coords = new List<Coordinate>();

        int step = Math.Max(1, _currentMap.Waypoints.Count / 500);
        for (int i = 0; i < _currentMap.Waypoints.Count; i += step)
        {
            var wp = _currentMap.Waypoints[i];
            var (lat, lon) = GameToGps(wp.X, wp.Z);
            var merc = ToMercator(lon, lat);
            coords.Add(new Coordinate(merc.X, merc.Y));
        }

        if (coords.Count > 0)
        {
            coords.Add(new Coordinate(coords[0].X, coords[0].Y));

            var lineString = new LineString(coords.ToArray());
            var trackFeature = new GeometryFeature { Geometry = lineString };
            trackFeature.Styles.Add(new VectorStyle
            {
                Line = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0xC0), Width = 3 },
                Outline = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0x40), Width = 6 }
            });
            features.Add(trackFeature);
        }

        var startWp = _currentMap.Waypoints[0];
        var (startLat, startLon) = GameToGps(startWp.X, startWp.Z);
        var startMerc = ToMercator(startLon, startLat);
        var startFeature = new PointFeature(startMerc.X, startMerc.Y);
        startFeature.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = 0.5,
            Fill = new Brush { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0xFF) }
        });
        features.Add(startFeature);

        _trackLayer.Features = features;
        _trackLayer.FeaturesWereModified();
    }

    private void RebuildCornerLayer()
    {
        if (_currentMap == null || !_alignmentDone) return;

        var features = new List<IFeature>();

        foreach (var corner in _currentMap.Corners)
        {
            if (corner.ApexWaypointIndex < _currentMap.Waypoints.Count)
            {
                var apex = _currentMap.Waypoints[corner.ApexWaypointIndex];
                var (lat, lon) = GameToGps(apex.X, apex.Z);
                var merc = ToMercator(lon, lat);

                var feature = new PointFeature(merc.X, merc.Y);
                feature["Label"] = corner.DisplayName;
                feature.Styles.Add(new LabelStyle
                {
                    LabelColumn = "Label",
                    ForeColor = Color.FromRgba(0xFF, 0xD6, 0x00, 0xFF),
                    BackColor = new Brush { Color = Color.FromRgba(0x00, 0x00, 0x00, 0xC0) },
                    Font = new Font { Size = 10 },
                    Halo = new Pen { Color = Color.FromRgba(0x00, 0x00, 0x00, 0xFF), Width = 1 },
                    Offset = new Offset { X = 8, Y = 0 }
                });
                features.Add(feature);
            }
        }

        _cornerLayer.Features = features;
        _cornerLayer.FeaturesWereModified();
    }

    private void AutoFitTrack()
    {
        if (_currentMap == null || !_alignmentDone || _autoFitDone) return;

        _autoFitDone = true;

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        int step = Math.Max(1, _currentMap.Waypoints.Count / 500);
        for (int i = 0; i < _currentMap.Waypoints.Count; i += step)
        {
            var wp = _currentMap.Waypoints[i];
            var (lat, lon) = GameToGps(wp.X, wp.Z);
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
            if (lon < minLon) minLon = lon;
            if (lon > maxLon) maxLon = lon;
        }

        var minMerc = ToMercator(minLon, minLat);
        var maxMerc = ToMercator(maxLon, maxLat);

        var envelope = new MRect(minMerc.X, minMerc.Y, maxMerc.X, maxMerc.Y);
        _map.Navigator.ZoomToBox(envelope);
    }

    private void ResetAlignment()
    {
        _alignmentDone = false;
        _autoFitDone = false;
        _currentMap = null;

        _trackLayer.Features = [];
        _trackLayer.FeaturesWereModified();
        _osmLayer.Features = [];
        _osmLayer.FeaturesWereModified();
        _cornerLayer.Features = [];
        _cornerLayer.FeaturesWereModified();
        _carLayer.Features = [];
        _carLayer.FeaturesWereModified();

        StatusOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "Waiting for track data...";
        AlignmentLabel.Text = "Alignment: --";
        CarPosLabel.Text = "Car: --";
        GpsPosLabel.Text = "GPS: --";
        SpeedLabel.Text = "Speed: --";
    }

    private void OnReAlign(object sender, RoutedEventArgs e)
    {
        if (_currentMap == null || string.IsNullOrWhiteSpace(_trackName)) return;
        var savedMap = _currentMap;
        var savedName = _trackName;
        ResetAlignment();
        _trackName = savedName;
        TrackNameLabel.Text = $"Track: {savedName}";
        _currentMap = savedMap;
        _ = TryAutoAlignAsync();
    }

    private void OnCenterOnCar(object sender, RoutedEventArgs e)
    {
        _centerOnCarEnabled = !_centerOnCarEnabled;
        CenterOnCarBtn.Content = _centerOnCarEnabled ? "Free Camera" : "Center on Car";
    }
}
