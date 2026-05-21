using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

namespace AcEvoFfbTuner.Controls;

public partial class MapsuiMapControl : UserControl
{
    private SatelliteMapService? _mapService;
    private TrackMap? _trackMap;
    private string? _trackName;

    private readonly Map _map;
    private MemoryLayer _trackLayer;
    private MemoryLayer _carLayer;
    private MemoryLayer _cornerLayer;
    private MemoryLayer _diagLayer;

    private bool _isCalibrating;
    private float _baseLat, _baseLon, _baseRot;
    private System.Windows.Point _lastMousePos;
    private bool _isDragging;
    private bool _autoFitDone;

    private readonly PointFeature _carFeature = new(0, 0);
    private readonly SymbolStyle _carOnTrackStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.6,
        Fill = new Brush { Color = Color.FromRgba(0xFF, 0x45, 0x00, 0xE0) },
        Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x80), Width = 1 }
    };
    private readonly SymbolStyle _carOffTrackStyle = new()
    {
        SymbolType = SymbolType.Ellipse,
        SymbolScale = 0.6,
        Fill = new Brush { Color = Color.FromRgba(0xFF, 0x00, 0x00, 0xE0) },
        Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x80), Width = 1 }
    };
    private readonly GeometryFeature _headingFeature = new() { Geometry = new LineString(new[] { new Coordinate(0, 0), new Coordinate(0, 0) }) };
    private readonly VectorStyle _headingStyle = new() { Line = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xE0), Width = 2.5 } };
    private readonly List<IFeature> _carFeatures;

    public event EventHandler? CalibrationSaved;

    public MapsuiMapControl()
    {
        InitializeComponent();

        _map = new Map { CRS = "EPSG:3857" };

        _map.Layers.Add(CreateSatelliteTileLayer());

        _trackLayer = new MemoryLayer("Track") { Features = [] };
        _map.Layers.Add(_trackLayer);

        _cornerLayer = new MemoryLayer("Corners") { Features = [] };
        _map.Layers.Add(_cornerLayer);

        _diagLayer = new MemoryLayer("Diagnostics") { Features = [] };
        _map.Layers.Add(_diagLayer);

        _carFeature.Styles.Add(_carOnTrackStyle);
        _headingFeature.Styles.Add(_headingStyle);
        _carFeatures = [_carFeature, _headingFeature];

        _carLayer = new MemoryLayer("Car") { Features = _carFeatures };
        _map.Layers.Add(_carLayer);

        MapCtrl.Map = _map;

        CalibInputOverlay.PreviewMouseLeftButtonDown += OnMouseLeftDown;
        CalibInputOverlay.PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
        CalibInputOverlay.PreviewMouseMove += OnMouseMove;
        CalibInputOverlay.PreviewMouseWheel += OnPreviewMouseWheel;
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

    public void Initialize(SatelliteMapService service)
    {
        _mapService = service;
    }

    public void SetTrackMap(TrackMap map)
    {
        if (map == _trackMap) return;
        _trackMap = map;
        _autoFitDone = false;

        if (_mapService != null && map.Waypoints.Count >= 3)
        {
            _mapService.ComputeGameToGpsTransform(map);

            if (_mapService.HasGeoData)
            {
                RebuildTrackLayer();
                RebuildCornerLayer();
                AutoFitTrack();
            }
        }
    }

    public void SetGeoCenter(float latitude, float longitude)
    {
        if (_mapService == null) return;
        _mapService.SetGeoReference(latitude, longitude);

        NoGeoOverlay.Visibility = Visibility.Collapsed;

        if (_trackMap != null && _trackMap.Waypoints.Count >= 3)
        {
            _mapService.ComputeGameToGpsTransform(_trackMap);

            if (!_autoFitDone)
            {
                RebuildTrackLayer();
                RebuildCornerLayer();
                AutoFitTrack();
                return;
            }
        }

        if (!_autoFitDone)
        {
            var merc = ToMercator(longitude, latitude);
            _map.Navigator.CenterOn(merc.X, merc.Y);
            _map.Navigator.ZoomTo(16);
        }
    }

    public void ShowNoGeoMessage()
    {
        NoGeoOverlay.Visibility = Visibility.Visible;
    }

    private void AutoFitTrack()
    {
        if (_mapService == null || _trackMap == null || !_mapService.HasGeoData || _autoFitDone) return;

        _autoFitDone = true;

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;

        int step = Math.Max(1, _trackMap.Waypoints.Count / 500);
        for (int i = 0; i < _trackMap.Waypoints.Count; i += step)
        {
            var wp = _trackMap.Waypoints[i];
            var (lat, lon) = _mapService.GameToGps(wp.X, wp.Z);
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

    public void UpdateCarPosition(float gameX, float gameZ, float heading, bool isOnTrack)
    {
        if (_mapService == null || !_mapService.HasGeoData) return;

        var (lat, lon) = _mapService.GameToGps(gameX, gameZ);
        var merc = ToMercator(lon, lat);

        var vp = _map.Navigator.Viewport;
        double headingLen = vp.Resolution * 28;
        double rad = heading;

        _carFeature.Point.X = merc.X;
        _carFeature.Point.Y = merc.Y;
        _carFeature.Styles.Clear();
        _carFeature.Styles.Add(isOnTrack ? _carOnTrackStyle : _carOffTrackStyle);

        var headX = merc.X + Math.Sin(rad) * headingLen;
        var headY = merc.Y - Math.Cos(rad) * headingLen;

        var geom = (LineString)_headingFeature.Geometry!;
        geom.CoordinateSequence.SetOrdinate(0, 0, merc.X);
        geom.CoordinateSequence.SetOrdinate(0, 1, merc.Y);
        geom.CoordinateSequence.SetOrdinate(1, 0, headX);
        geom.CoordinateSequence.SetOrdinate(1, 1, headY);
        _headingFeature.Geometry = geom;

        _carLayer.FeaturesWereModified();
    }

    private void RebuildTrackLayer()
    {
        if (_mapService == null || _trackMap == null || !_mapService.HasGeoData) return;

        var features = new List<IFeature>();

        var coords = new List<Coordinate>();
        int step = Math.Max(1, _trackMap.Waypoints.Count / 500);
        for (int i = 0; i < _trackMap.Waypoints.Count; i += step)
        {
            var wp = _trackMap.Waypoints[i];
            var (lat, lon) = _mapService.GameToGps(wp.X, wp.Z);
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
                Line = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0xC0), Width = 2.5 },
                Outline = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0x40), Width = 5 }
            });
            features.Add(trackFeature);
        }

        var startWp = _trackMap.Waypoints[0];
        var (startLat, startLon) = _mapService.GameToGps(startWp.X, startWp.Z);
        var startMerc = ToMercator(startLon, startLat);
        var startFeature = new PointFeature(startMerc.X, startMerc.Y);
        startFeature.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = 0.4,
            Fill = new Brush { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0xFF) }
        });
        features.Add(startFeature);

        _trackLayer.Features = features;
        _trackLayer.FeaturesWereModified();
    }

    private void RebuildCornerLayer()
    {
        if (_mapService == null || _trackMap == null || !_mapService.HasGeoData) return;

        var features = new List<IFeature>();

        foreach (var corner in _trackMap.Corners)
        {
            if (corner.ApexWaypointIndex < _trackMap.Waypoints.Count)
            {
                var apex = _trackMap.Waypoints[corner.ApexWaypointIndex];
                var (lat, lon) = _mapService.GameToGps(apex.X, apex.Z);
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

    public void UpdateDiagnosticMarkers(TrackMap map,
        WaypointDiagnosticSample[]? diagnosticHeatmap, bool showDiagnostics)
    {
        if (_mapService == null || !_mapService.HasGeoData) return;

        if (!showDiagnostics || diagnosticHeatmap == null || map.Waypoints.Count != diagnosticHeatmap.Length)
        {
            _diagLayer.Features = [];
            _diagLayer.FeaturesWereModified();
            return;
        }

        var features = new List<IFeature>();
        int step = Math.Max(1, map.Waypoints.Count / 200);

        for (int i = 0; i < map.Waypoints.Count; i += step)
        {
            var d = diagnosticHeatmap[i];
            if (d.TotalEventCount == 0) continue;

            var wp = map.Waypoints[i];
            var (lat, lon) = _mapService.GameToGps(wp.X, wp.Z);
            var merc = ToMercator(lon, lat);

            Color markerColor;
            double scale;

            if (d.SuspiciousCount > 0)
            {
                markerColor = Color.FromRgba(0xFF, 0x00, 0x00, 0xE0);
                scale = Math.Min(0.3 + d.SuspiciousCount * 0.08, 0.8);
            }
            else if (d.ExpectedCount > 0)
            {
                markerColor = Color.FromRgba(0x00, 0xE6, 0x76, 0xE0);
                scale = Math.Min(0.2 + d.ExpectedCount * 0.04, 0.6);
            }
            else
            {
                markerColor = Color.FromRgba(0xFF, 0xD6, 0x00, 0xE0);
                scale = 0.2;
            }

            var feature = new PointFeature(merc.X, merc.Y);
            feature.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = scale,
                Fill = new Brush { Color = markerColor }
            });
            features.Add(feature);
        }

        _diagLayer.Features = features;
        _diagLayer.FeaturesWereModified();
    }

    public void UpdateHeatmapOverlay(TrackMap map,
        WaypointForceSample[]? forceHeatmap, bool showHeatmap)
    {
        if (_mapService == null || !_mapService.HasGeoData) return;

        if (!showHeatmap || forceHeatmap == null || forceHeatmap.Length != map.Waypoints.Count)
        {
            RebuildTrackLayer();
            return;
        }

        var features = new List<IFeature>();
        int step = Math.Max(1, map.Waypoints.Count / 300);

        for (int i = 0; i < map.Waypoints.Count; i += step)
        {
            int next = (i + step) % map.Waypoints.Count;
            if (next >= map.Waypoints.Count) next = map.Waypoints.Count - 1;

            var wp1 = map.Waypoints[i];
            var wp2 = map.Waypoints[next];
            var (lat1, lon1) = _mapService.GameToGps(wp1.X, wp1.Z);
            var (lat2, lon2) = _mapService.GameToGps(wp2.X, wp2.Z);

            var merc1 = ToMercator(lon1, lat1);
            var merc2 = ToMercator(lon2, lat2);

            var sample = forceHeatmap[i];
            Color c = ForceColor(sample.OutputForce, sample.IsClipping);

            var segment = new GeometryFeature
            {
                Geometry = new LineString(new[]
                {
                    new Coordinate(merc1.X, merc1.Y),
                    new Coordinate(merc2.X, merc2.Y)
                })
            };
            segment.Styles.Add(new VectorStyle
            {
                Line = new Pen { Color = c, Width = 4 }
            });
            features.Add(segment);
        }

        if (features.Count > 0)
        {
            _trackLayer.Features = features;
            _trackLayer.FeaturesWereModified();
        }
        else
        {
            RebuildTrackLayer();
        }
    }

    private static Color ForceColor(float force, bool isClipping)
    {
        float absF = MathF.Abs(force);
        if (isClipping || absF > 0.95f)
            return Color.FromRgba(0xFF, 0x00, 0x00, 0xFF);
        if (absF > 0.3f)
        {
            float t = (absF - 0.3f) / 0.65f;
            byte g = (byte)(0xE6 * (1f - t));
            return Color.FromRgba((byte)(0x00 + 0xFF * t), g, 0x00, 0xFF);
        }
        float t2 = absF / 0.3f;
        return Color.FromRgba(0x00, (byte)(0x99 * t2), (byte)(0xFF * (1f - t2)), 0xFF);
    }

    public void CenterOnTrack()
    {
        if (_trackMap != null && _mapService?.HasGeoData == true)
        {
            _autoFitDone = false;
            AutoFitTrack();
        }
    }

    public void SetTrackName(string trackName)
    {
        _trackName = trackName;
    }

    public void EnterCalibrationMode()
    {
        if (_mapService == null || !_mapService.HasGeoData) return;
        _isCalibrating = true;
        _baseLat = _mapService.TrackCenterLatitude;
        _baseLon = _mapService.TrackCenterLongitude;
        _baseRot = (float)_mapService.GetRotationDeg();
        CalibInputOverlay.Visibility = Visibility.Visible;
        CalibrationOverlay.Visibility = Visibility.Visible;
        _map.Navigator.PanLock = true;
        _map.Navigator.ZoomLock = true;
        UpdateCalibrationDisplay();
        Focus();
        Keyboard.Focus(this);
    }

    public void ExitCalibrationMode()
    {
        _isCalibrating = false;
        _isDragging = false;
        CalibInputOverlay.Visibility = Visibility.Collapsed;
        CalibrationOverlay.Visibility = Visibility.Collapsed;
        _map.Navigator.PanLock = false;
        _map.Navigator.ZoomLock = false;
        Mouse.Capture(null);
    }

    public bool IsCalibrating => _isCalibrating;

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isCalibrating) return;
        _isDragging = true;
        _lastMousePos = e.GetPosition(CalibInputOverlay);
        Mouse.Capture(CalibInputOverlay);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isCalibrating) return;
        _isDragging = false;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCalibrating || !_isDragging || _mapService == null) return;

        var pos = e.GetPosition(CalibInputOverlay);
        double dx = pos.X - _lastMousePos.X;
        double dy = pos.Y - _lastMousePos.Y;
        _lastMousePos = pos;

        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double factor = shift ? 5.0 : 1.0;

        var vp = _map.Navigator.Viewport;
        double scale = vp.Resolution;

        double dEastMeters = dx * scale * factor;
        double dNorthMeters = -dy * scale * factor;

        double cosLat = Math.Cos(_mapService.TrackCenterLatitude * Math.PI / 180.0);
        if (cosLat < 0.01) cosLat = 0.01;
        double dLon = dEastMeters / (111320.0 * cosLat);
        double dLat = dNorthMeters / 111320.0;

        _mapService.AdjustCalibration(dLat, dLon, 0);

        RefreshTrackOverlay();
        MapCtrl.Refresh();
        e.Handled = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_isCalibrating || _mapService == null) return;

        double rotStep = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2.0 : 0.5;
        double delta = e.Delta > 0 ? rotStep : -rotStep;
        _mapService.AdjustCalibration(0, 0, delta);

        RefreshTrackOverlay();
        MapCtrl.Refresh();
        e.Handled = true;
    }

    private void RefreshTrackOverlay()
    {
        if (_trackMap != null && _mapService?.HasGeoData == true)
        {
            _mapService.ComputeGameToGpsTransform(_trackMap);
            RebuildTrackLayer();
            RebuildCornerLayer();
        }
        UpdateCalibrationDisplay();
    }

    private void UpdateCalibrationDisplay()
    {
        if (_mapService == null) return;
        CalibLatLabel.Text = $"Lat: {_mapService.TrackCenterLatitude:F4}";
        CalibLonLabel.Text = $"Lon: {_mapService.TrackCenterLongitude:F4}";
        CalibRotLabel.Text = $"Rot: {_mapService.GetRotationDeg():F2}°";
    }

    private void OnCalibSave(object sender, RoutedEventArgs e)
    {
        if (_mapService == null) return;
        var name = _trackName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            SatelliteMapService.SaveCalibration(name,
                _mapService.TrackCenterLatitude,
                _mapService.TrackCenterLongitude,
                (float)_mapService.GetRotationDeg());
        }
        ExitCalibrationMode();
        CalibrationSaved?.Invoke(this, EventArgs.Empty);
    }

    private void OnCalibReset(object sender, RoutedEventArgs e)
    {
        if (_mapService == null) return;
        _mapService.SetGeoReference(_baseLat, _baseLon, _baseRot);
        RefreshTrackOverlay();
    }

    public void Reset()
    {
        _trackMap = null;
        _mapService = null;
        _autoFitDone = false;

        _trackLayer.Features = [];
        _trackLayer.FeaturesWereModified();
        _carLayer.Features = [];
        _carLayer.FeaturesWereModified();
        _cornerLayer.Features = [];
        _cornerLayer.FeaturesWereModified();
        _diagLayer.Features = [];
        _diagLayer.FeaturesWereModified();
    }
}
