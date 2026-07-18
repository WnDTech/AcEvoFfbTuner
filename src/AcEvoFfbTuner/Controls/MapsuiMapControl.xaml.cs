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
    private MemoryLayer _gpsOutlineLayer;
    private MemoryLayer _carLayer;
    private MemoryLayer _cornerLayer;
    private MemoryLayer _diagLayer;

    private bool _isCalibrating;
    private float _baseLat, _baseLon, _baseRot;
    private System.Windows.Point _lastMousePos;
    private bool _isDragging;
    private bool _autoFitDone;

    // WPF overlay markers — created once, positioned on viewport change
    private readonly List<(double lat, double lon, string text, double offsetX, double offsetY)> _overlayLabels = new();
    private readonly List<(double lat, double lon, string text, double offsetX, double offsetY)> _overlaySymbols = new();
    private DateTime _lastOverlayUpdate = DateTime.MinValue;
    private bool _overlayViewportSubscribed;

    public event EventHandler? CalibrationSaved;

    public MapsuiMapControl()
    {
        InitializeComponent();

        _map = new Map { CRS = "EPSG:3857" };

        _map.Layers.Add(CreateSatelliteTileLayer());

        _trackLayer = new MemoryLayer("Track") { Features = [] };
        _map.Layers.Add(_trackLayer);

        _gpsOutlineLayer = new MemoryLayer("GPS Outline") { Features = [] };
        _map.Layers.Add(_gpsOutlineLayer);

        _cornerLayer = new MemoryLayer("Corners") { Features = [] };
        _map.Layers.Add(_cornerLayer);

        _diagLayer = new MemoryLayer("Diagnostics") { Features = [] };
        _map.Layers.Add(_diagLayer);

        _carLayer = new MemoryLayer("Car") { Features = [] };
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

    public void SetGpsTrackOutline(List<TrackPoint> gpsPoints, List<TrackCornerInfo> corners)
    {
        SetGpsTrackOutline(gpsPoints, corners, null);
    }

    public void SetGpsTrackOutline(List<TrackPoint> gpsPoints, List<TrackCornerInfo> corners,
        double[]? sectorBoundaries)
    {
        var features = new List<IFeature>();

        // Draw the track outline from GPS points directly
        if (gpsPoints != null && gpsPoints.Count > 3)
        {
            int step = Math.Max(1, gpsPoints.Count / 500);
            var sampled = new List<TrackPoint>();
            for (int i = 0; i < gpsPoints.Count; i += step)
                sampled.Add(gpsPoints[i]);

            if (sampled.Count < 3) return;

            if (sectorBoundaries != null && sectorBoundaries.Length >= 3)
            {
                // Sector-colored rendering: split into sector segments
                // High-opacity, vivid colors for visibility over satellite imagery
                var sectorColors = new[]
                {
                    Color.FromRgba(0x00, 0xFF, 0x66, 0xDD), // Sector 1: bright green
                    Color.FromRgba(0xFF, 0xDD, 0x00, 0xDD), // Sector 2: bright gold
                    Color.FromRgba(0xFF, 0x44, 0x44, 0xDD)  // Sector 3: bright red
                };
                var sectorOutlines = new[]
                {
                    Color.FromRgba(0x00, 0x00, 0x00, 0x99), // Black outline for all sectors
                    Color.FromRgba(0x00, 0x00, 0x00, 0x99),
                    Color.FromRgba(0x00, 0x00, 0x00, 0x99)
                };

                // Compute cumulative distances along the sampled points
                int n = sampled.Count;
                var cumDist = new double[n];
                cumDist[0] = 0;
                for (int i = 1; i < n; i++)
                    cumDist[i] = cumDist[i - 1] + HaversineDistance(sampled[i - 1], sampled[i]);

                double totalDist = cumDist[n - 1] + HaversineDistance(sampled[n - 1], sampled[0]);
                if (totalDist > 0)
                {
                    // For each sector pair (0→1, 1→2, 2→0 wrapping), build a line segment
                    for (int s = 0; s < sectorBoundaries.Length - 1; s++)
                    {
                        double startNpos = sectorBoundaries[s];
                        double endNpos = sectorBoundaries[s + 1];
                        int colorIdx = s % sectorColors.Length;

                        var segCoords = BuildSectorLineSegment(sampled, cumDist, totalDist, startNpos, endNpos);
                        if (segCoords.Count >= 2)
                        {
                            var lineString = new LineString(segCoords.ToArray());
                            var segFeature = new GeometryFeature { Geometry = lineString };
                            segFeature.Styles.Add(new VectorStyle
                            {
                                Line = new Pen { Color = sectorColors[colorIdx], Width = 4 },
                                Outline = new Pen { Color = sectorOutlines[colorIdx], Width = 6 }
                            });
                            features.Add(segFeature);
                        }
                    }
                }
            }
            else
            {
                // Single-color rendering (existing behavior)
                var coords = new List<Coordinate>();
                for (int i = 0; i < sampled.Count; i++)
                {
                    var merc = ToMercator(sampled[i].Longitude, sampled[i].Latitude);
                    coords.Add(new Coordinate(merc.X, merc.Y));
                }
                if (coords.Count > 0)
                    coords.Add(new Coordinate(coords[0].X, coords[0].Y));

                var lineString = new LineString(coords.ToArray());
                var feature = new GeometryFeature { Geometry = lineString };
                feature.Styles.Add(new VectorStyle
                {
                    Line = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0x60), Width = 1.5 },
                    Outline = new Pen { Color = Color.FromRgba(0x00, 0xE6, 0x76, 0x20), Width = 3 }
                });
                features.Add(feature);
            }
        }

        // Corner labels via overlay
        if (corners != null)
        {
            foreach (var corner in corners)
                _overlayLabels.Add((corner.Latitude, corner.Longitude, corner.Number.ToString(), 0, 0));
        }

        _gpsOutlineLayer.Features = features;
        _gpsOutlineLayer.FeaturesWereModified();
        BuildOverlay();
    }

    /// <summary>
    /// Build a Mercator-projected coordinate list for a sector segment,
    /// wrapping from startNpos to endNpos along the sampled GPS points.
    /// </summary>
    private static List<Coordinate> BuildSectorLineSegment(
        List<TrackPoint> sampled, double[] cumDist, double totalDist,
        double startNpos, double endNpos)
    {
        var coords = new List<Coordinate>();
        int n = sampled.Count;
        if (n < 2 || totalDist <= 0) return coords;

        double startDist = startNpos * totalDist;
        double endDist = endNpos * totalDist;

        // Determine which way around the track the segment goes
        // (some sectors may span the start/finish line — wrap around)
        if (endNpos > startNpos)
        {
            // Normal case: sector does not wrap
            coords.AddRange(GetPointsInRange(sampled, cumDist, totalDist, startDist, endDist));
        }
        else
        {
            // Wrapping case: sector spans across start/finish
            // First half: startDist → end of track
            coords.AddRange(GetPointsInRange(sampled, cumDist, totalDist, startDist, totalDist));
            // Second half: start of track → endDist
            coords.AddRange(GetPointsInRange(sampled, cumDist, totalDist, 0, endDist));
        }

        return coords;
    }

    /// <summary>
    /// Extract Mercator-projected coordinates between two cumulative distances.
    /// </summary>
    private static List<Coordinate> GetPointsInRange(
        List<TrackPoint> sampled, double[] cumDist, double totalDist,
        double fromDist, double toDist)
    {
        var coords = new List<Coordinate>();
        if (toDist <= fromDist) return coords;

        int startIdx = FindIndexAtDist(cumDist, fromDist);
        int endIdx = FindIndexAtDist(cumDist, toDist);

        for (int i = startIdx; i <= endIdx && i < sampled.Count; i++)
        {
            var merc = ToMercator(sampled[i].Longitude, sampled[i].Latitude);
            coords.Add(new Coordinate(merc.X, merc.Y));
        }

        return coords;
    }

    /// <summary>
    /// Binary-search for the index in cumDist closest to the target distance.
    /// </summary>
    private static int FindIndexAtDist(double[] cumDist, double target)
    {
        if (target <= cumDist[0]) return 0;
        if (target >= cumDist[^1]) return cumDist.Length - 1;

        int lo = 0, hi = cumDist.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (cumDist[mid] <= target) lo = mid;
            else hi = mid;
        }
        // Return the closer index
        return (target - cumDist[lo] < cumDist[hi] - target) ? lo : hi;
    }

    /// <summary>Haversine distance in meters between two GPS points.</summary>
    private static double HaversineDistance(TrackPoint a, TrackPoint b)
    {
        const double R = 6371000.0;
        double dLat = (b.Latitude - a.Latitude) * Math.PI / 180.0;
        double dLon = (b.Longitude - a.Longitude) * Math.PI / 180.0;
        double sinDLat = Math.Sin(dLat / 2);
        double sinDLon = Math.Sin(dLon / 2);
        double h = sinDLat * sinDLat +
                   Math.Cos(a.Latitude * Math.PI / 180.0) *
                   Math.Cos(b.Latitude * Math.PI / 180.0) *
                   sinDLon * sinDLon;
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }

    public void ClearGpsTrackOutline()
    {
        _gpsOutlineLayer.Features = [];
        _gpsOutlineLayer.FeaturesWereModified();
        _overlayLabels.Clear();
        _overlaySymbols.Clear();
        MarkerOverlay.Children.Clear();
    }

    public void AddOverlayLabel(double lat, double lon, string text, string colorHex)
    {
        _overlayLabels.Add((lat, lon, text, 0, 0));
    }

    public void AddOverlaySymbol(double lat, double lon, string text)
    {
        _overlaySymbols.Add((lat, lon, text, 0, 0));
    }

    public void BuildOverlay()
    {
        MarkerOverlay.Children.Clear();
        double halfChar = 5.5; // ~half of 11px font for centering

        foreach (var m in _overlayLabels)
        {
            var tb = new TextBlock
            {
                Text = m.text,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                Foreground = System.Windows.Media.Brushes.Gold,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    Opacity = 0.8,
                    ShadowDepth = 1,
                    BlurRadius = 2
                }
            };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double ox = m.text.Length > 1 ? halfChar * m.text.Length : halfChar;
            MarkerOverlay.Children.Add(tb);
            int idx = MarkerOverlay.Children.Count - 1;
            // Store offset in Tag for position update
            tb.Tag = new System.Windows.Point(ox, halfChar);
        }

        foreach (var s in _overlaySymbols)
        {
            var brush = s.text == "🏁" ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gold;

            var tb = new TextBlock
            {
                Text = s.text,
                FontSize = s.text == "\U0001F3C1" ? 18 : 14,
                FontWeight = FontWeights.Bold,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                TextAlignment = TextAlignment.Center,
                Foreground = brush,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = System.Windows.Media.Colors.Black,
                    Opacity = 0.9,
                    ShadowDepth = 1,
                    BlurRadius = 2
                }
            };
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double ox = tb.DesiredSize.Width / 2;
            double oy = tb.DesiredSize.Height / 2;
            MarkerOverlay.Children.Add(tb);
        }

        _lastOverlayUpdate = DateTime.MinValue; // force first update
        UpdateOverlayPositions();

        // Subscribe to viewport changes — reposition overlays only when map moves
        if (!_overlayViewportSubscribed)
        {
            _overlayViewportSubscribed = true;
            _map.Navigator.ViewportChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(UpdateOverlayPositions), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    public void UpdateOverlayPositions()
    {
        if (_overlayLabels.Count == 0 && _overlaySymbols.Count == 0) return;

        int idx = 0;
        foreach (var m in _overlayLabels)
        {
            if (idx >= MarkerOverlay.Children.Count) break;
            var merc = ToMercator(m.lon, m.lat);
            var screen = _map.Navigator.Viewport.WorldToScreen(merc);
            var tb = MarkerOverlay.Children[idx] as TextBlock;
            if (tb == null) { idx++; continue; }

            if (screen.X < -200 || screen.X > ActualWidth + 200 ||
                screen.Y < -200 || screen.Y > ActualHeight + 200)
            { tb.Visibility = Visibility.Collapsed; idx++; continue; }

            tb.Visibility = Visibility.Visible;
            var off = tb.Tag is System.Windows.Point p ? p : new System.Windows.Point(8, 6);
            Canvas.SetLeft(tb, screen.X - off.X);
            Canvas.SetTop(tb, screen.Y - off.Y);
            idx++;
        }

        foreach (var s in _overlaySymbols)
        {
            if (idx >= MarkerOverlay.Children.Count) break;
            var merc = ToMercator(s.lon, s.lat);
            var screen = _map.Navigator.Viewport.WorldToScreen(merc);
            var tb = MarkerOverlay.Children[idx] as TextBlock;
            if (tb == null) { idx++; continue; }

            if (screen.X < -200 || screen.X > ActualWidth + 200 ||
                screen.Y < -200 || screen.Y > ActualHeight + 200)
            { tb.Visibility = Visibility.Collapsed; idx++; continue; }

            tb.Visibility = Visibility.Visible;
            Canvas.SetLeft(tb, screen.X - 9);
            Canvas.SetTop(tb, screen.Y - 9);
            idx++;
        }
    }

    public void AddPitMarkers(TrackPitInfo? pit)
    {
        if (pit == null) return;

        var existing = _gpsOutlineLayer.Features.ToList();

        // Pit lane line (dashed yellow, keep on outline layer)
        if (pit.Layout != null && pit.Layout.Count > 0)
        {
            var coords = new List<Coordinate>();
            foreach (var pt in pit.Layout)
            {
                var merc = ToMercator(pt.Longitude, pt.Latitude);
                coords.Add(new Coordinate(merc.X, merc.Y));
            }
            var lineString = new LineString(coords.ToArray());
            var lineFeature = new GeometryFeature { Geometry = lineString };
            lineFeature.Styles.Add(new VectorStyle
            {
                Line = new Pen
                {
                    Color = Mapsui.Styles.Color.FromRgba(0xFF, 0xFF, 0x00, 0xCC),
                    Width = 2,
                    PenStyle = PenStyle.Dash
                }
            });
            existing.Add(lineFeature);
        }

        // Entry/exit labels via overlay
        _overlaySymbols.Add((pit.EntryLatitude, pit.EntryLongitude, "\u25BC", 0, 0));
        _overlaySymbols.Add((pit.ExitLatitude, pit.ExitLongitude, "\u25B2", 0, 0));

        _gpsOutlineLayer.Features = existing;
        _gpsOutlineLayer.FeaturesWereModified();
        BuildOverlay();
    }

    public void AddStartFinishMarker(TrackPoint? sf)
    {
        if (sf == null) return;
        _overlaySymbols.Add((sf.Latitude, sf.Longitude, "\U0001F3C1", 0, 0));
        BuildOverlay();
    }

    public void CenterOnGps(double latitude, double longitude, int zoom = 16)
    {
        var merc = ToMercator(longitude, latitude);
        _map.Navigator.CenterOn(merc.X, merc.Y);
        _map.Navigator.ZoomTo(zoom);
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
        RenderCarDot(lon, lat, heading);
    }

    public void UpdateCarGpsPosition(double longitude, double latitude, double heading)
    {
        RenderCarDot(longitude, latitude, heading);
    }

    private void RenderCarDot(double longitude, double latitude, double heading)
    {
        var merc = ToMercator(longitude, latitude);

        var vp = _map.Navigator.Viewport;
        double headingLen = vp.Resolution * 28;
        double rad = heading;

        // Create fresh car dot feature
        var carPt = new PointFeature(merc.X, merc.Y);
        carPt.Styles.Add(new SymbolStyle
        {
            SymbolType = SymbolType.Ellipse,
            SymbolScale = 1.0,
            Fill = new Brush { Color = Color.FromRgba(0x00, 0xFF, 0x00, 0xFF) },
            Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xFF), Width = 2 }
        });

        // Create fresh heading line
        var headX = merc.X + Math.Sin(rad) * headingLen;
        var headY = merc.Y - Math.Cos(rad) * headingLen;
        var headingLine = new GeometryFeature
        {
            Geometry = new LineString(new[]
            {
                new Coordinate(merc.X, merc.Y),
                new Coordinate(headX, headY)
            })
        };
        headingLine.Styles.Add(new VectorStyle
        {
            Line = new Pen
            {
                Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xE0),
                Width = 3
            }
        });

        _carLayer.Features = [carPt, headingLine];
        _carLayer.FeaturesWereModified();
        MapCtrl.Refresh();
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
