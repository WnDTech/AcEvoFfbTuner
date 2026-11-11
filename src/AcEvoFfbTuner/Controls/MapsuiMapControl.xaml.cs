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

/// <summary>
/// An opponent disc on the Live Map: fill = class colour (ARGB), label = position drawn
/// inside the disc (may be empty), ring = the car's flag, dimmed in the pits, larger when selected.
/// </summary>
public readonly record struct MapCarMarker(double Latitude, double Longitude, string Label, bool InPits,
    uint FillArgb, bool Selected = false, AcEvoFfbTuner.Core.SharedMemory.CarFlag Flag = default);

public partial class MapsuiMapControl : UserControl
{
    private SatelliteMapService? _mapService;
    private TrackMap? _trackMap;
    private string? _trackName;

    private readonly Map _map;
    private MemoryLayer _trackLayer;
    private MemoryLayer _gpsOutlineLayer;
    private MemoryLayer _carLayer;
    private MemoryLayer _opponentLayer;
    private MemoryLayer _cornerLayer;
    private MemoryLayer _diagLayer;
    private MemoryLayer _ffbHeatLayer;
    private MemoryLayer _coachLayer;

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

        // Live Map FFB heatmap: own layer so it never replaces the downloaded outline.
        _ffbHeatLayer = new MemoryLayer("FFB Heatmap") { Features = [] };
        _map.Layers.Add(_ffbHeatLayer);

        _cornerLayer = new MemoryLayer("Corners") { Features = [] };
        _map.Layers.Add(_cornerLayer);

        _diagLayer = new MemoryLayer("Diagnostics") { Features = [] };
        _map.Layers.Add(_diagLayer);

        _coachLayer = new MemoryLayer("Coach") { Features = [] };
        _map.Layers.Add(_coachLayer);

        // Opponents below the player's car so the player dot always draws on top.
        // Style = null: features carry their own styles. Mapsui's default layer style draws a
        // 32 px white disc under every point, which swamps the small opponent dots.
        _opponentLayer = new MemoryLayer("Opponents") { Features = [], Style = null };
        _map.Layers.Add(_opponentLayer);

        _carLayer = new MemoryLayer("Car") { Features = [], Style = null };
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

    /// <summary>Shows or hides the ESRI satellite base layer (e.g. from the
    /// Live Map satellite toggle). Track/car overlays are unaffected.</summary>
    public void SetSatelliteVisible(bool visible)
    {
        var layer = _map.Layers.FirstOrDefault(l => l.Name == "Satellite");
        if (layer != null)
            layer.Enabled = visible;
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
        // The outline is the reset point for the overlay markers (callers re-add pit and
        // start/finish afterwards). Appending here piled up duplicate DropShadow TextBlocks
        // on every redraw — each geo-fit update and each SF auto-calibration lap — and
        // the overlay rebuild/reposition cost grew for the whole session.
        _overlayLabels.Clear();
        _overlaySymbols.Clear();

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

                // Compute cumulative distances along the sampled points (circular — loop closes)
                int n = sampled.Count;
                var circularPts = new List<TrackPoint>(sampled);
                circularPts.Add(sampled[0]); // closing point

                var cumDist = new double[n + 1];
                cumDist[0] = 0;
                for (int i = 1; i <= n; i++)
                    cumDist[i] = cumDist[i - 1] + HaversineDistance(circularPts[i - 1], circularPts[i]);

                double totalDist = cumDist[n]; // includes closing segment
                if (totalDist > 0)
                {
                    // For each sector pair, build a line segment.
                    // The circular list guarantees the loop closes: the last sector
                    // wraps back to the first point (endNpos <= startNpos → +1.0).
                    for (int s = 0; s < sectorBoundaries.Length - 1; s++)
                    {
                        double startNpos = sectorBoundaries[s];
                        double endNpos = sectorBoundaries[s + 1];
                        if (endNpos <= startNpos)
                            endNpos += 1.0; // sector spans the start/finish line

                        int colorIdx = s % sectorColors.Length;

                        var segCoords = BuildSectorLineSegment(circularPts, cumDist, totalDist, startNpos, endNpos);
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
    /// Build a Mercator-projected coordinate list for a sector segment.
    /// The point list is circular (last point = first point), so sectors that
    /// span the start/finish line are handled by endNpos > startNpos after +1.0.
    /// </summary>
    private static List<Coordinate> BuildSectorLineSegment(
        List<TrackPoint> circularPts, double[] cumDist, double totalDist,
        double startNpos, double endNpos)
    {
        double startDist = startNpos * totalDist;
        double endDist = endNpos * totalDist;
        return GetPointsInRange(circularPts, cumDist, totalDist, startDist, endDist);
    }

    /// <summary>
    /// Extract Mercator-projected coordinates between two cumulative distances.
    /// Works on the circular point list (index n = closing point = first point).
    /// </summary>
    private static List<Coordinate> GetPointsInRange(
        List<TrackPoint> circularPts, double[] cumDist, double totalDist,
        double fromDist, double toDist)
    {
        var coords = new List<Coordinate>();
        if (toDist <= fromDist) return coords;

        int startIdx = FindIndexAtDist(cumDist, fromDist);
        int endIdx = FindIndexAtDist(cumDist, toDist);

        for (int i = startIdx; i <= endIdx && i < circularPts.Count; i++)
        {
            var merc = ToMercator(circularPts[i].Longitude, circularPts[i].Latitude);
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

        bool cornersShown = _cornerLabelsEnabled && _map.Navigator.Viewport.Resolution <= CornerLabelMaxResolution;
        int idx = 0;
        foreach (var m in _overlayLabels)
        {
            if (idx >= MarkerOverlay.Children.Count) break;
            var merc = ToMercator(m.lon, m.lat);
            var screen = _map.Navigator.Viewport.WorldToScreen(merc);
            var tb = MarkerOverlay.Children[idx] as TextBlock;
            if (tb == null) { idx++; continue; }

            if (!cornersShown || screen.X < -200 || screen.X > ActualWidth + 200 ||
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

        // Replace (not stack) the pit line and its entry/exit symbols.
        var existing = _gpsOutlineLayer.Features.Where(f => !Equals(f["Kind"], "pit")).ToList();
        _overlaySymbols.RemoveAll(s => s.text is "▼" or "▲");

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
            lineFeature["Kind"] = "pit";
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
        _overlaySymbols.RemoveAll(s => s.text == "\U0001F3C1"); // one start/finish flag, moved not stacked
        _overlaySymbols.Add((sf.Latitude, sf.Longitude, "\U0001F3C1", 0, 0));
        BuildOverlay();
    }

    public void CenterOnGps(double latitude, double longitude, int zoom = 16)
    {
        var merc = ToMercator(longitude, latitude);
        _map.Navigator.CenterOn(merc.X, merc.Y);
        _map.Navigator.ZoomTo(zoom);
    }

    /// <summary>Pan to a GPS point keeping the current zoom (follow-car, field-list click).</summary>
    public void PanToGps(double latitude, double longitude)
    {
        var merc = ToMercator(longitude, latitude);
        _map.Navigator.CenterOn(merc.X, merc.Y);
    }

    /// <summary>
    /// Zoom so the whole layout fits with a margin. Needs a laid-out control: while the page
    /// is hidden (0 px) the viewport is undefined, so this returns false and the caller retries
    /// once the map is visible — a fit attempted while hidden is what left the map unzoomed.
    /// </summary>
    public bool FitToGps(IReadOnlyList<TrackPoint> points, double marginFraction = 0.08)
    {
        // Mapsui's own viewport must be sized too — it updates after the WPF control's layout.
        var vp = _map.Navigator.Viewport;
        if (points.Count < 2 || ActualWidth < 50 || ActualHeight < 50 || vp.Width < 50 || vp.Height < 50) return false;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            var m = ToMercator(p.Longitude, p.Latitude);
            if (m.X < minX) minX = m.X;
            if (m.X > maxX) maxX = m.X;
            if (m.Y < minY) minY = m.Y;
            if (m.Y > maxY) maxY = m.Y;
        }
        double padX = (maxX - minX) * marginFraction + 50, padY = (maxY - minY) * marginFraction + 50;
        _map.Navigator.ZoomToBox(new MRect(minX - padX, minY - padY, maxX + padX, maxY + padY));
        return true;
    }

    private bool _cornerLabelsEnabled = true;
    /// <summary>
    /// Corner-number labels on/off. Even when on they hide while zoomed far out (coarser than
    /// <see cref="CornerLabelMaxResolution"/> m/px): at overview scale 30+ numbers only clutter.
    /// </summary>
    public bool ShowCornerLabels
    {
        get => _cornerLabelsEnabled;
        set { _cornerLabelsEnabled = value; UpdateOverlayPositions(); }
    }
    private const double CornerLabelMaxResolution = 10.0;

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

    /// <summary>
    /// Place the car dot at a GPS position. <paramref name="compassHeading"/> is radians
    /// clockwise from north (0 = N, π/2 = E). RenderCarDot uses the game-heading
    /// convention of UpdateCarPosition (0 = game +Z = map south under GameToGps), so
    /// convert: direction (sin h, −cos h) == (sin c, cos c) ⇔ h = π − c.
    /// </summary>
    public void UpdateCarGpsPosition(double longitude, double latitude, double compassHeading)
    {
        RenderCarDot(longitude, latitude, Math.PI - compassHeading);
    }

    // Car/opponent styles are cached and shared: the markers are re-created 10–30 times a
    // second, and fresh Style/Pen/Brush objects per marker per frame were pure allocation
    // churn (and defeat Mapsui's style-keyed render caches).
    private readonly Dictionary<(uint argb, bool pits, bool selected, AcEvoFfbTuner.Core.SharedMemory.CarFlag flag), SymbolStyle> _dotStyles = new();
    private static readonly LabelStyle TagLabel = new()
    {
        LabelColumn = "Label",
        ForeColor = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xFF),
        BackColor = new Brush { Color = Color.FromRgba(0x0D, 0x11, 0x17, 0xD8) },
        Font = new Font { Size = 10, Bold = true },
        Offset = new Offset { X = 14, Y = 0 },
    };

    // Opponent dot geometry (SymbolScale × 32 px): ~12 px dots stay narrower than the racing
    // line at track scale so bunched cars no longer bury each other; selected cars grow a little.
    private const double DotScale = 0.38;
    private const double DotScaleSelected = 0.55;

    // Player marker: a navigation arrow (screen px), pointing along the car's heading.
    private const double ArrowTip = 15, ArrowBack = 10, ArrowNotch = 4, ArrowHalfWidth = 10;
    private readonly Dictionary<uint, VectorStyle> _playerArrowStyles = new();
    private static readonly VectorStyle PlayerArrowShadow = new()
    {
        Fill = new Brush { Color = Color.FromRgba(0x00, 0x00, 0x00, 0x60) },
        Outline = new Pen { Color = Color.FromRgba(0x00, 0x00, 0x00, 0x60), Width = 6 },
    };

    /// <summary>Fill colour (ARGB) of the player's own car — the page passes the theme accent.</summary>
    public uint PlayerColorArgb { get; set; } = 0xFF50C878;

    private static Color ToMapsui(uint argb, byte? alpha = null) =>
        Color.FromRgba((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF),
            alpha ?? (int)(argb >> 24));

    /// <summary>True when black text reads better than white on this fill (perceived luminance).</summary>
    public static bool IsLightFill(uint argb)
    {
        double r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return 0.299 * r + 0.587 * g + 0.114 * b > 160;
    }

    /// <summary>
    /// Opponent dot: class-colour fill, state carried by the dot itself —
    /// finished = hollow ring in the class colour, retired = grey and faded, in pits = faded,
    /// yellow/blue/penalty = coloured outline, otherwise a thin dark outline for contrast on imagery.
    /// </summary>
    private SymbolStyle DotStyle(uint argb, bool pits, bool selected, AcEvoFfbTuner.Core.SharedMemory.CarFlag flag)
    {
        var key = (argb, pits, selected, flag);
        if (_dotStyles.TryGetValue(key, out var s)) return s;
        double scale = selected ? DotScaleSelected : DotScale;
        Brush fill;
        Pen outline;
        switch (flag)
        {
            case AcEvoFfbTuner.Core.SharedMemory.CarFlag.Checkered:
                fill = new Brush { Color = Color.FromRgba(0x0D, 0x11, 0x17, 0x90) };
                outline = new Pen { Color = ToMapsui(argb), Width = 3 };
                break;
            case AcEvoFfbTuner.Core.SharedMemory.CarFlag.Retired:
                fill = new Brush { Color = Color.FromRgba(0x6E, 0x76, 0x81, 0x66) };
                outline = new Pen { Color = Color.FromRgba(0x0D, 0x11, 0x17, 0x66), Width = 1 };
                break;
            default:
                fill = new Brush { Color = ToMapsui(argb, pits ? (byte)0x70 : (byte)0xFF) };
                Color edge = flag switch
                {
                    AcEvoFfbTuner.Core.SharedMemory.CarFlag.Yellow => Color.FromRgba(0xFF, 0xD3, 0x3D, 0xFF),
                    AcEvoFfbTuner.Core.SharedMemory.CarFlag.Blue => Color.FromRgba(0x2F, 0x81, 0xF7, 0xFF),
                    AcEvoFfbTuner.Core.SharedMemory.CarFlag.Penalty => Color.FromRgba(0x00, 0x00, 0x00, 0xFF),
                    _ => selected ? Color.FromRgba(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromRgba(0x0D, 0x11, 0x17, 0xE0),
                };
                bool flagged = flag != AcEvoFfbTuner.Core.SharedMemory.CarFlag.None;
                outline = new Pen { Color = edge, Width = flagged ? 3 : selected ? 2 : 1.5 };
                break;
        }
        s = new SymbolStyle { SymbolType = SymbolType.Ellipse, SymbolScale = scale, Fill = fill, Outline = outline };
        _dotStyles[key] = s;
        return s;
    }

    /// <summary>Replace the opponent dots (class-coloured; a side tag only when the page gives a label).</summary>
    public void UpdateOpponents(IReadOnlyList<MapCarMarker> cars)
    {
        var features = new List<IFeature>(cars.Count);
        // Unlabelled first so tagged cars (ahead/behind/selected) draw on top of a bunch.
        foreach (var c in cars.OrderBy(c => string.IsNullOrEmpty(c.Label) ? 0 : 1))
        {
            var merc = ToMercator(c.Longitude, c.Latitude);
            var f = new PointFeature(merc.X, merc.Y);
            f.Styles.Add(DotStyle(c.FillArgb, c.InPits, c.Selected, c.Flag));
            if (!string.IsNullOrEmpty(c.Label))
            {
                f["Label"] = c.Label;
                f.Styles.Add(TagLabel);
            }
            features.Add(f);
        }
        _opponentLayer.Features = features;
        _opponentLayer.FeaturesWereModified();
        MapCtrl.Refresh();
    }

    private void RenderCarDot(double longitude, double latitude, double heading)
    {
        var merc = ToMercator(longitude, latitude);
        double res = _map.Navigator.Viewport.Resolution; // metres per screen pixel

        // Forward and side unit vectors in map space (same convention as the old heading line).
        double fx = Math.Sin(heading), fy = -Math.Cos(heading);
        double sx = -fy, sy = fx;
        Coordinate P(double fwd, double side) =>
            new(merc.X + (fx * fwd + sx * side) * res, merc.Y + (fy * fwd + sy * side) * res);

        // Navigation arrow (chevron with a notched tail): distinct in shape from every opponent
        // dot, so the player never depends on colour alone (the accent can match a class colour).
        var ring = new[]
        {
            P(ArrowTip, 0),
            P(-ArrowBack, ArrowHalfWidth),
            P(-ArrowNotch, 0),
            P(-ArrowBack, -ArrowHalfWidth),
            P(ArrowTip, 0),
        };
        var arrow = new GeometryFeature
        {
            Geometry = new NetTopologySuite.Geometries.Polygon(new NetTopologySuite.Geometries.LinearRing(ring))
        };
        if (!_playerArrowStyles.TryGetValue(PlayerColorArgb, out var style))
        {
            style = new VectorStyle
            {
                Fill = new Brush { Color = ToMapsui(PlayerColorArgb) },
                Outline = new Pen { Color = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xFF), Width = 2.5 },
            };
            _playerArrowStyles[PlayerColorArgb] = style;
        }
        arrow.Styles.Add(PlayerArrowShadow);
        arrow.Styles.Add(style);

        _carLayer.Features = [arrow];
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

    /// <summary>
    /// Live Map FFB overlays: force/clipping heatmap along the driven line and FFB event
    /// markers (expected / notable / suspicious). <paramref name="project"/> maps world
    /// (X, Z) to GPS — the Live Map passes its geo-fit so markers sit on the real circuit.
    /// Returns null from <paramref name="project"/> to skip a point.
    /// </summary>
    public void UpdateLiveFfbOverlays(TrackMap? map,
        WaypointForceSample[]? forceHeatmap, bool showHeatmap,
        WaypointDiagnosticSample[]? diagnosticHeatmap, bool showDiagnostics,
        Func<float, float, (double lat, double lon)?> project)
    {
        var heat = new List<IFeature>();
        var diag = new List<IFeature>();
        int n = map?.Waypoints.Count ?? 0;

        if (map != null && showHeatmap && forceHeatmap != null && forceHeatmap.Length == n && n > 1)
        {
            int step = Math.Max(1, n / 400);
            for (int i = 0; i < n; i += step)
            {
                var sample = forceHeatmap[i];
                if (sample == null || sample.SampleCount == 0) continue;
                int next = Math.Min(i + step, n - 1);
                if (next == i) break;
                var a = project(map.Waypoints[i].X, map.Waypoints[i].Z);
                var b = project(map.Waypoints[next].X, map.Waypoints[next].Z);
                if (a == null || b == null) continue;
                var m1 = ToMercator(a.Value.lon, a.Value.lat);
                var m2 = ToMercator(b.Value.lon, b.Value.lat);
                var seg = new GeometryFeature
                {
                    Geometry = new LineString(new[] { new Coordinate(m1.X, m1.Y), new Coordinate(m2.X, m2.Y) })
                };
                seg.Styles.Add(new VectorStyle { Line = new Pen { Color = ForceColor(sample.OutputForce, sample.IsClipping), Width = 6 } });
                heat.Add(seg);
            }
        }

        if (map != null && showDiagnostics && diagnosticHeatmap != null && diagnosticHeatmap.Length == n && n > 1)
        {
            int step = Math.Max(1, n / 200);
            for (int i = 0; i < n; i += step)
            {
                var d = diagnosticHeatmap[i];
                if (d == null || d.TotalEventCount == 0) continue;
                var p = project(map.Waypoints[i].X, map.Waypoints[i].Z);
                if (p == null) continue;
                var merc = ToMercator(p.Value.lon, p.Value.lat);
                Color c; double scale;
                if (d.SuspiciousCount > 0) { c = Color.FromRgba(0xFF, 0x30, 0x30, 0xE8); scale = Math.Min(0.35 + d.SuspiciousCount * 0.08, 0.8); }
                else if (d.ExpectedCount > 0) { c = Color.FromRgba(0x00, 0xE6, 0x76, 0xD0); scale = Math.Min(0.25 + d.ExpectedCount * 0.04, 0.6); }
                else { c = Color.FromRgba(0xFF, 0xD6, 0x00, 0xD8); scale = 0.25; }
                var f = new PointFeature(merc.X, merc.Y);
                f.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = scale,
                    Fill = new Brush { Color = c },
                    Outline = new Pen { Color = Color.FromRgba(0, 0, 0, 0xA0), Width = 1 }
                });
                diag.Add(f);
            }
        }

        _ffbHeatLayer.Features = heat;
        _ffbHeatLayer.FeaturesWereModified();
        _diagLayer.Features = diag;
        _diagLayer.FeaturesWereModified();
        MapCtrl.Refresh();
    }

    /// <summary>Driver Coach braking-point markers: (lat, lon, ARGB fill, label drawn beside it).</summary>
    public void UpdateCoachMarkers(IReadOnlyList<(double lat, double lon, uint argb, string label)> markers)
    {
        var features = new List<IFeature>();
        foreach (var m in markers)
        {
            var merc = ToMercator(m.lon, m.lat);
            var f = new PointFeature(merc.X, merc.Y);
            var c = Color.FromRgba((int)((m.argb >> 16) & 0xFF), (int)((m.argb >> 8) & 0xFF), (int)(m.argb & 0xFF), (int)(m.argb >> 24));
            f.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Rectangle,
                SymbolScale = 0.45,
                Fill = new Brush { Color = c },
                Outline = new Pen { Color = Color.FromRgba(0, 0, 0, 0xC0), Width = 1.5 },
            });
            if (!string.IsNullOrEmpty(m.label))
                f.Styles.Add(new LabelStyle
                {
                    Text = m.label,
                    ForeColor = c,
                    BackColor = new Brush { Color = Color.FromRgba(0x10, 0x14, 0x18, 0xC8) },
                    Offset = new Offset { X = 0, Y = -16 },
                    Font = new Font { Size = 10 },
                    Halo = new Pen { Color = Color.FromRgba(0x00, 0x00, 0x00, 0xFF), Width = 1 },
                });
            features.Add(f);
        }
        _coachLayer.Features = features;
        _coachLayer.FeaturesWereModified();
        MapCtrl.Refresh();
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
