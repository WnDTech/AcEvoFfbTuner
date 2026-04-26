using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.ViewModels;

namespace AcEvoFfbTuner.Views;

public partial class TrackMapPopout : Window
{
    private bool _isTransparent;

    private readonly SolidColorBrush _trackLineBrush = new(Color.FromRgb(0x00, 0xBC, 0xD4));
    private readonly SolidColorBrush _trackFillBrush = new(Color.FromArgb(0x18, 0x00, 0xBC, 0xD4));
    private readonly SolidColorBrush _trackStartBrush = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private readonly SolidColorBrush _carDotBrush = new(Color.FromRgb(0xFF, 0x45, 0x00));
    private readonly SolidColorBrush _carDotOffTrackBrush = new(Color.FromRgb(0xFF, 0x00, 0x00));
    private readonly SolidColorBrush _recordingBrush = new(Color.FromArgb(0x80, 0xFF, 0x45, 0x00));

    private readonly Polyline _trackPolyline = new() { StrokeThickness = 2 };
    private readonly Polyline _trackFillPolyline = new() { StrokeThickness = 0 };
    private readonly Ellipse _carDot = new() { Width = 10, Height = 10 };
    private readonly Ellipse _startDot = new() { Width = 8, Height = 8, Visibility = Visibility.Collapsed };
    private readonly Line _headingLine = new() { StrokeThickness = 2 };
    private readonly Polyline _recordingTrail = new() { StrokeThickness = 1 };

    private TrackMap? _displayedMap;
    private float _mapMinX, _mapMaxX, _mapMinZ, _mapMaxZ, _mapScale;
    private double _mapOffsetX, _mapOffsetY;
    private int _lastDisplayedWaypointCount;
    private int _lastDisplayedSectorCount;
    private bool _lastDisplayedPitDetected;
    private int _heatmapRedrawCounter;

    private readonly List<Point> _recordingWorldPts = new();

    public TrackMapPopout()
    {
        InitializeComponent();

        _trackPolyline.Stroke = _trackLineBrush;
        _trackFillPolyline.Fill = _trackFillBrush;
        _carDot.Fill = _carDotBrush;
        _startDot.Fill = _trackStartBrush;
        _headingLine.Stroke = _carDotBrush;
        _recordingTrail.Stroke = _recordingBrush;

        OvCanvas.Children.Add(_trackFillPolyline);
        OvCanvas.Children.Add(_trackPolyline);
        OvCanvas.Children.Add(_recordingTrail);
        OvCanvas.Children.Add(_startDot);
        OvCanvas.Children.Add(_headingLine);
        OvCanvas.Children.Add(_carDot);
    }

    public void Update(MainViewModel vm)
    {
        double w = OvCanvas.ActualWidth;
        double h = OvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        float carX = vm.CarPosX;
        float carZ = vm.CarPosZ;
        float heading = vm.CarHeading;
        float speedKmh = vm.SpeedKmh;
        bool isOnTrack = vm.IsOnTrackMap;
        float trackProgress = vm.TrackProgress;
        int waypointCount = vm.TrackWaypointCount;
        bool isRecording = vm.IsTrackMapRecording;
        bool hasMap = vm.IsTrackMapAvailable;

        OvCarX.Text = carX.ToString("F1");
        OvCarZ.Text = carZ.ToString("F1");
        OvWaypoints.Text = waypointCount > 0 ? waypointCount.ToString() : "--";
        OvProgress.Text = hasMap ? $"{trackProgress * 100f:F1}%" : "--";
        OvCorner.Text = vm.CurrentCornerName ?? "--";
        OvSector.Text = vm.CurrentSectorNumber > 0 ? $"S{vm.CurrentSectorNumber}" : "--";
        OvTrackName.Text = vm.DetectedTrackName ?? "";

        if (isOnTrack)
        {
            OvOnTrack.Text = "ON";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        }
        else if (hasMap)
        {
            OvOnTrack.Text = "OFF";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else
        {
            OvOnTrack.Text = "--";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        if (isRecording)
        {
            OvMapStatus.Text = "Recording...";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
            BtnRecord.Background = new SolidColorBrush(Color.FromArgb(0x80, 0xE6, 0x7E, 0x22));
        }
        else if (hasMap)
        {
            OvMapStatus.Text = "Map Ready";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
            BtnRecord.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x55));
        }
        else
        {
            OvMapStatus.Text = "No Map";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            BtnRecord.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x33, 0x33, 0x55));
        }

        OvTrackStats.Text = hasMap ? $"{vm.CornerCount}C / {vm.SectorCount}S / {vm.TrackLengthM:F0}m" : "";

        var currentMap = vm.CurrentTrackMap;

        bool showHeatmap = OvShowHeatmap.IsChecked == true;
        bool showEdges = OvShowEdges.IsChecked == true;
        bool showDiag = OvShowDiag.IsChecked == true;

        if (hasMap && currentMap != null)
        {
            bool redraw = currentMap != _displayedMap
                || currentMap.Waypoints.Count != _lastDisplayedWaypointCount
                || currentMap.Sectors.Count != _lastDisplayedSectorCount
                || currentMap.PitLane.IsDetected != _lastDisplayedPitDetected;

            if (showHeatmap)
            {
                _heatmapRedrawCounter++;
                if (_heatmapRedrawCounter >= 30)
                {
                    _heatmapRedrawCounter = 0;
                    redraw = true;
                }
            }

            if (redraw)
            {
                _displayedMap = currentMap;
                _lastDisplayedWaypointCount = currentMap.Waypoints.Count;
                _lastDisplayedSectorCount = currentMap.Sectors.Count;
                _lastDisplayedPitDetected = currentMap.PitLane.IsDetected;
                ComputeMapTransform(currentMap, w, h);

                var vm2 = Application.Current?.MainWindow?.DataContext as MainViewModel;
                DrawTrackLine(currentMap, w, h, null, showHeatmap, showEdges, null, showDiag);
                _recordingWorldPts.Clear();
            }

            DrawCarOnMap(carX, carZ, heading, isOnTrack);
            _startDot.Visibility = Visibility.Visible;
            _recordingTrail.Points = new PointCollection();
        }
        else if (isRecording)
        {
            if (currentMap != _displayedMap)
            {
                _displayedMap = null;
                _lastDisplayedWaypointCount = 0;
            }

            if (speedKmh > 5f &&
                (_recordingWorldPts.Count == 0 ||
                Math.Abs(carX - (float)_recordingWorldPts[^1].X) > 0.5f ||
                Math.Abs(carZ - (float)_recordingWorldPts[^1].Y) > 0.5f))
            {
                _recordingWorldPts.Add(new Point(carX, carZ));
            }

            if (_recordingWorldPts.Count >= 2)
            {
                double padding = 30;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in _recordingWorldPts)
                {
                    if ((float)p.X < minX) minX = (float)p.X;
                    if ((float)p.X > maxX) maxX = (float)p.X;
                    if ((float)p.Y < minZ) minZ = (float)p.Y;
                    if ((float)p.Y > maxZ) maxZ = (float)p.Y;
                }
                float rangeX = maxX - minX;
                float rangeZ = maxZ - minZ;
                if (rangeX < 1f) rangeX = 1f;
                if (rangeZ < 1f) rangeZ = 1f;
                double scaleX = (w - padding * 2) / rangeX;
                double scaleZ = (h - padding * 2) / rangeZ;
                double scale = Math.Min(scaleX, scaleZ);
                double offX = (w - rangeX * scale) / 2;
                double offY = (h - rangeZ * scale) / 2;

                var displayPts = new PointCollection(_recordingWorldPts.Count);
                foreach (var p in _recordingWorldPts)
                    displayPts.Add(new Point((p.X - minX) * scale + offX, (p.Y - minZ) * scale + offY));
                _recordingTrail.Points = displayPts;

                float cx = (float)((carX - minX) * scale + offX);
                float cz = (float)((carZ - minZ) * scale + offY);
                DrawCarDot(cx, cz, heading, isOnTrack);
            }

            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
        }
        else if (!hasMap)
        {
            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
            _carDot.Visibility = Visibility.Collapsed;
            _headingLine.Visibility = Visibility.Collapsed;
            _recordingTrail.Points = new PointCollection();
            _recordingWorldPts.Clear();
            ClearDynamicElements();
        }
    }

    public void UpdateFromMainWindow(float carX, float carZ, float heading, float speedKmh,
        bool isOnTrack, float trackProgress, float distanceFromCenter,
        float trackLengthM, int waypointCount, bool isRecording, bool hasMap,
        TrackMap? currentMap,
        WaypointForceSample[]? forceHeatmap,
        bool showHeatmap,
        bool showTrackEdges,
        WaypointDiagnosticSample[]? diagnosticHeatmap,
        bool showDiagnostics)
    {
        double w = OvCanvas.ActualWidth;
        double h = OvCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        OvCarX.Text = carX.ToString("F1");
        OvCarZ.Text = carZ.ToString("F1");
        OvWaypoints.Text = waypointCount > 0 ? waypointCount.ToString() : "--";
        OvProgress.Text = hasMap ? $"{trackProgress * 100f:F1}%" : "--";

        if (isOnTrack)
        {
            OvOnTrack.Text = "ON";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        }
        else if (hasMap)
        {
            OvOnTrack.Text = "OFF";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else
        {
            OvOnTrack.Text = "--";
            OvOnTrack.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }

        if (isRecording)
        {
            OvMapStatus.Text = "Recording...";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22));
        }
        else if (hasMap)
        {
            OvMapStatus.Text = "Map Ready";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));
        }
        else
        {
            OvMapStatus.Text = "No Map";
            OvMapStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        bool useHeatmap = OvShowHeatmap.IsChecked == true;
        bool useEdges = OvShowEdges.IsChecked == true;
        bool useDiag = OvShowDiag.IsChecked == true;

        if (hasMap && currentMap != null)
        {
            bool redraw = currentMap != _displayedMap
                || currentMap.Waypoints.Count != _lastDisplayedWaypointCount
                || currentMap.Sectors.Count != _lastDisplayedSectorCount
                || currentMap.PitLane.IsDetected != _lastDisplayedPitDetected;

            if (useHeatmap && forceHeatmap != null)
            {
                _heatmapRedrawCounter++;
                if (_heatmapRedrawCounter >= 30)
                {
                    _heatmapRedrawCounter = 0;
                    redraw = true;
                }
            }

            if (redraw)
            {
                _displayedMap = currentMap;
                _lastDisplayedWaypointCount = currentMap.Waypoints.Count;
                _lastDisplayedSectorCount = currentMap.Sectors.Count;
                _lastDisplayedPitDetected = currentMap.PitLane.IsDetected;
                ComputeMapTransform(currentMap, w, h);
                DrawTrackLine(currentMap, w, h, forceHeatmap, useHeatmap, useEdges,
                    diagnosticHeatmap, useDiag);
                _recordingWorldPts.Clear();
            }

            DrawCarOnMap(carX, carZ, heading, isOnTrack);
            _startDot.Visibility = Visibility.Visible;
            _recordingTrail.Points = new PointCollection();
        }
        else if (isRecording)
        {
            if (currentMap != _displayedMap)
            {
                _displayedMap = null;
                _lastDisplayedWaypointCount = 0;
            }

            if (speedKmh > 5f &&
                (_recordingWorldPts.Count == 0 ||
                Math.Abs(carX - (float)_recordingWorldPts[^1].X) > 0.5f ||
                Math.Abs(carZ - (float)_recordingWorldPts[^1].Y) > 0.5f))
            {
                _recordingWorldPts.Add(new Point(carX, carZ));
            }

            if (_recordingWorldPts.Count >= 2)
            {
                double padding = 30;
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in _recordingWorldPts)
                {
                    if ((float)p.X < minX) minX = (float)p.X;
                    if ((float)p.X > maxX) maxX = (float)p.X;
                    if ((float)p.Y < minZ) minZ = (float)p.Y;
                    if ((float)p.Y > maxZ) maxZ = (float)p.Y;
                }
                float rangeX = maxX - minX;
                float rangeZ = maxZ - minZ;
                if (rangeX < 1f) rangeX = 1f;
                if (rangeZ < 1f) rangeZ = 1f;
                double scaleX = (w - padding * 2) / rangeX;
                double scaleZ = (h - padding * 2) / rangeZ;
                double scale = Math.Min(scaleX, scaleZ);
                double offX = (w - rangeX * scale) / 2;
                double offY = (h - rangeZ * scale) / 2;

                var displayPts = new PointCollection(_recordingWorldPts.Count);
                foreach (var p in _recordingWorldPts)
                    displayPts.Add(new Point((p.X - minX) * scale + offX, (p.Y - minZ) * scale + offY));
                _recordingTrail.Points = displayPts;

                float cx = (float)((carX - minX) * scale + offX);
                float cz = (float)((carZ - minZ) * scale + offY);
                DrawCarDot(cx, cz, heading, isOnTrack);
            }

            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
        }
        else if (!hasMap)
        {
            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
            _startDot.Visibility = Visibility.Collapsed;
            _carDot.Visibility = Visibility.Collapsed;
            _headingLine.Visibility = Visibility.Collapsed;
            _recordingTrail.Points = new PointCollection();
            _recordingWorldPts.Clear();
            ClearDynamicElements();
        }
    }

    private void ComputeMapTransform(TrackMap map, double canvasW, double canvasH)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var wp in map.Waypoints)
        {
            if (wp.X < minX) minX = wp.X;
            if (wp.X > maxX) maxX = wp.X;
            if (wp.Z < minZ) minZ = wp.Z;
            if (wp.Z > maxZ) maxZ = wp.Z;
        }

        _mapMinX = minX; _mapMaxX = maxX;
        _mapMinZ = minZ; _mapMaxZ = maxZ;

        float rangeX = maxX - minX;
        float rangeZ = maxZ - minZ;
        if (rangeX < 1f) rangeX = 1f;
        if (rangeZ < 1f) rangeZ = 1f;

        double padding = 30;
        double scaleX = (canvasW - padding * 2) / rangeX;
        double scaleZ = (canvasH - padding * 2) / rangeZ;
        _mapScale = (float)Math.Min(scaleX, scaleZ);
        _mapOffsetX = (canvasW - rangeX * _mapScale) / 2;
        _mapOffsetY = (canvasH - rangeZ * _mapScale) / 2;
    }

    private Point MapToCanvas(float worldX, float worldZ)
    {
        return new Point(
            (worldX - _mapMinX) * _mapScale + _mapOffsetX,
            (worldZ - _mapMinZ) * _mapScale + _mapOffsetY);
    }

    private void DrawTrackLine(TrackMap map, double canvasW, double canvasH,
        WaypointForceSample[]? forceHeatmap, bool showHeatmap, bool showTrackEdges,
        WaypointDiagnosticSample[]? diagnosticHeatmap, bool showDiagnostics)
    {
        ClearDynamicElements();

        _trackPolyline.Points = new PointCollection();
        _trackFillPolyline.Points = new PointCollection();

        if (showHeatmap && forceHeatmap != null && forceHeatmap.Length == map.Waypoints.Count)
        {
            var heatmapEl = new System.Windows.Shapes.Path
            {
                Tag = "dynamic",
                StrokeThickness = 4
            };

            int step = Math.Max(1, map.Waypoints.Count / 500);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < map.Waypoints.Count; i += step)
                {
                    int next = (i + step) % map.Waypoints.Count;
                    if (next >= map.Waypoints.Count) next = map.Waypoints.Count - 1;

                    var p1 = MapToCanvas(map.Waypoints[i].X, map.Waypoints[i].Z);
                    var p2 = MapToCanvas(map.Waypoints[next].X, map.Waypoints[next].Z);

                    ctx.BeginFigure(p1, false, false);
                    ctx.LineTo(p2, true, false);
                }
            }
            heatmapEl.Data = geo;

            var brush = new LinearGradientBrush();
            int colorSteps = Math.Min(map.Waypoints.Count, 100);
            for (int i = 0; i < colorSteps; i++)
            {
                int idx = (int)((float)i / colorSteps * map.Waypoints.Count);
                var s = forceHeatmap[idx];
                Color c = ForceColor(s.OutputForce, s.IsClipping);
                brush.GradientStops.Add(new GradientStop(c, (double)i / colorSteps));
            }
            heatmapEl.Stroke = brush;
            heatmapEl.Visibility = Visibility.Visible;
            OvCanvas.Children.Insert(OvCanvas.Children.IndexOf(_trackPolyline), heatmapEl);
            _trackPolyline.Visibility = Visibility.Collapsed;
            _trackFillPolyline.Visibility = Visibility.Collapsed;
        }
        else
        {
            var pts = new PointCollection(map.Waypoints.Count + 1);
            foreach (var wp in map.Waypoints)
                pts.Add(MapToCanvas(wp.X, wp.Z));
            pts.Add(MapToCanvas(map.Waypoints[0].X, map.Waypoints[0].Z));
            _trackPolyline.Points = pts;
            _trackFillPolyline.Points = pts;
            _trackPolyline.Visibility = Visibility.Visible;
            _trackFillPolyline.Visibility = Visibility.Visible;
        }

        if (showTrackEdges && map.TrackEdges.Count == map.Waypoints.Count)
        {
            int step = Math.Max(1, map.Waypoints.Count / 300);
            var leftPts = new PointCollection();
            var rightPts = new PointCollection();

            for (int i = 0; i < map.Waypoints.Count; i += step)
            {
                var edge = map.TrackEdges[i];
                if (edge.LeftX != 0 || edge.LeftZ != 0)
                    leftPts.Add(MapToCanvas(edge.LeftX, edge.LeftZ));
                if (edge.RightX != 0 || edge.RightZ != 0)
                    rightPts.Add(MapToCanvas(edge.RightX, edge.RightZ));
            }

            if (leftPts.Count > 1)
            {
                OvCanvas.Children.Add(new Polyline
                {
                    Points = leftPts,
                    StrokeThickness = 1,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    Tag = "dynamic"
                });
            }
            if (rightPts.Count > 1)
            {
                OvCanvas.Children.Add(new Polyline
                {
                    Points = rightPts,
                    StrokeThickness = 1,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                    Tag = "dynamic"
                });
            }
        }

        var startPt = MapToCanvas(map.Waypoints[0].X, map.Waypoints[0].Z);
        Canvas.SetLeft(_startDot, startPt.X - 4);
        Canvas.SetTop(_startDot, startPt.Y - 4);

        DrawCorners(map);
        DrawSectorBoundaries(map);
        DrawPitMarker(map);
        DrawDiagnosticMarkers(map, diagnosticHeatmap, showDiagnostics);
    }

    private void ClearDynamicElements()
    {
        for (int i = OvCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (OvCanvas.Children[i] is FrameworkElement fe && fe.Tag as string == "dynamic")
                OvCanvas.Children.RemoveAt(i);
        }
    }

    private void DrawCorners(TrackMap map)
    {
        foreach (var corner in map.Corners)
        {
            if (corner.ApexWaypointIndex < map.Waypoints.Count)
            {
                var apex = map.Waypoints[corner.ApexWaypointIndex];
                var pt = MapToCanvas(apex.X, apex.Z);

                var label = new TextBlock
                {
                    Text = corner.DisplayName,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)),
                    Tag = "dynamic"
                };
                Canvas.SetLeft(label, pt.X + 6);
                Canvas.SetTop(label, pt.Y - 6);
                OvCanvas.Children.Add(label);
            }
        }
    }

    private void DrawSectorBoundaries(TrackMap map)
    {
        if (map.Sectors.Count <= 1) return;

        foreach (var sector in map.Sectors)
        {
            if (sector.StartWaypointIndex < 0 || sector.StartWaypointIndex >= map.Waypoints.Count)
                continue;

            var wp = map.Waypoints[sector.StartWaypointIndex];
            var pt = MapToCanvas(wp.X, wp.Z);

            int prev = sector.StartWaypointIndex > 0 ? sector.StartWaypointIndex - 1 : map.Waypoints.Count - 1;
            int next = (sector.StartWaypointIndex + 1) % map.Waypoints.Count;
            float tangX = map.Waypoints[next].X - map.Waypoints[prev].X;
            float tangZ = map.Waypoints[next].Z - map.Waypoints[prev].Z;
            float tangLen = MathF.Sqrt(tangX * tangX + tangZ * tangZ);
            if (tangLen > 0.001f) { tangX /= tangLen; tangZ /= tangLen; }
            float normX = -tangZ;
            float normZ = tangX;
            float lineHalfLen = 25f / _mapScale;
            var p1 = MapToCanvas(wp.X - normX * lineHalfLen, wp.Z - normZ * lineHalfLen);
            var p2 = MapToCanvas(wp.X + normX * lineHalfLen, wp.Z + normZ * lineHalfLen);

            OvCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = p1.X, Y1 = p1.Y,
                X2 = p2.X, Y2 = p2.Y,
                StrokeThickness = 1.5,
                Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0xBC, 0xD4)),
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Tag = "dynamic"
            });

            var label = new TextBlock
            {
                Text = $"S{sector.SectorNumber}",
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0x00, 0xBC, 0xD4)),
                Tag = "dynamic"
            };
            Canvas.SetLeft(label, p1.X + 2);
            Canvas.SetTop(label, p1.Y - 10);
            OvCanvas.Children.Add(label);
        }
    }

    private void DrawPitMarker(TrackMap map)
    {
        if (!map.PitLane.IsDetected) return;

        if (map.PitLane.EntryWaypointIndex >= 0 && map.PitLane.EntryWaypointIndex < map.Waypoints.Count)
        {
            var entryWp = map.Waypoints[map.PitLane.EntryWaypointIndex];
            var pt = MapToCanvas(entryWp.X, entryWp.Z);
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)),
                Tag = "dynamic"
            };
            Canvas.SetLeft(dot, pt.X - 4);
            Canvas.SetTop(dot, pt.Y - 4);
            OvCanvas.Children.Add(dot);
        }

        if (map.PitLane.ExitWaypointIndex >= 0 && map.PitLane.ExitWaypointIndex < map.Waypoints.Count
            && map.PitLane.ExitWaypointIndex != map.PitLane.EntryWaypointIndex)
        {
            var exitWp = map.Waypoints[map.PitLane.ExitWaypointIndex];
            var pt = MapToCanvas(exitWp.X, exitWp.Z);
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xD6, 0x00)),
                Tag = "dynamic"
            };
            Canvas.SetLeft(dot, pt.X - 4);
            Canvas.SetTop(dot, pt.Y - 4);
            OvCanvas.Children.Add(dot);
        }
    }

    private void DrawDiagnosticMarkers(TrackMap map,
        WaypointDiagnosticSample[]? diagnosticHeatmap, bool showDiagnostics)
    {
        if (!showDiagnostics || diagnosticHeatmap == null || diagnosticHeatmap.Length != map.Waypoints.Count)
            return;

        int step = Math.Max(1, map.Waypoints.Count / 200);

        for (int i = 0; i < map.Waypoints.Count; i += step)
        {
            var d = diagnosticHeatmap[i];
            if (d.TotalEventCount == 0) continue;

            var wp = map.Waypoints[i];
            var pt = MapToCanvas(wp.X, wp.Z);

            Color markerColor;
            double size;

            if (d.SuspiciousCount > 0)
            {
                markerColor = Color.FromRgb(0xFF, 0x00, 0x00);
                size = Math.Min(4 + d.SuspiciousCount, 10);
            }
            else if (d.ExpectedCount > 0)
            {
                markerColor = Color.FromRgb(0x00, 0xE6, 0x76);
                size = Math.Min(3 + d.ExpectedCount * 0.5, 8);
            }
            else
            {
                markerColor = Color.FromRgb(0xFF, 0xD6, 0x00);
                size = 3;
            }

            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(markerColor),
                Tag = "dynamic"
            };
            Canvas.SetLeft(dot, pt.X - size / 2);
            Canvas.SetTop(dot, pt.Y - size / 2);
            OvCanvas.Children.Add(dot);
        }
    }

    private void DrawCarOnMap(float carX, float carZ, float heading, bool isOnTrack)
    {
        var pt = MapToCanvas(carX, carZ);
        DrawCarDot((float)pt.X, (float)pt.Y, heading, isOnTrack);
    }

    private void DrawCarDot(float screenX, float screenZ, float heading, bool isOnTrack)
    {
        _carDot.Fill = isOnTrack ? _carDotBrush : _carDotOffTrackBrush;
        Canvas.SetLeft(_carDot, screenX - 5);
        Canvas.SetTop(_carDot, screenZ - 5);
        _carDot.Visibility = Visibility.Visible;

        float lineLen = 20f;
        float endX = screenX + MathF.Sin(heading) * lineLen;
        float endZ = screenZ - MathF.Cos(heading) * lineLen;
        _headingLine.X1 = screenX;
        _headingLine.Y1 = screenZ;
        _headingLine.X2 = endX;
        _headingLine.Y2 = endZ;
        _headingLine.Stroke = isOnTrack ? _carDotBrush : _carDotOffTrackBrush;
        _headingLine.Visibility = Visibility.Visible;
    }

    private static Color ForceColor(float force, bool isClipping)
    {
        float absF = MathF.Abs(force);
        if (isClipping || absF > 0.95f)
            return Color.FromRgb(0xFF, 0x00, 0x00);
        if (absF > 0.3f)
        {
            float t = (absF - 0.3f) / 0.65f;
            byte g = (byte)(0xE6 * (1f - t));
            return Color.FromRgb((byte)(0x00 + 0xFF * t), g, 0x00);
        }
        float t2 = absF / 0.3f;
        return Color.FromRgb(0x00, (byte)(0x99 * t2), (byte)(0xFF * (1f - t2)));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 20;
        Top = screen.Top + 20;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Topmost = false;
        Topmost = true;
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleTransparency(object sender, RoutedEventArgs e)
    {
        _isTransparent = !_isTransparent;

        if (_isTransparent)
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "TRN";
        }
        else
        {
            ContentBorder.Background = new SolidColorBrush(Color.FromArgb(0x8A, 0x0D, 0x0D, 0x0D));
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xE6, 0x7E, 0x22));
            TransparencyIcon.Text = "OPQ";
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? -25 : 25;
        double newW = Width + delta;
        double newH = Height + delta;

        if (newW < 350 || newH < 350) return;
        if (newW > 900 || newH > 900) return;

        Left += delta / 2;
        Top += delta / 2;
        Width = newW;
        Height = newH;
    }

    private void StartRecording(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.StartTrackMapRecordingCommand.Execute(null);
        }
    }

    private void CompleteAndSave(object sender, RoutedEventArgs e)
    {
        if (Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.CompleteAndSaveTrackMapCommand.Execute(null);
        }
    }

    private void CloseOverlay(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
