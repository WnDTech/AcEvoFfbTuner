using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AcEvoFfbTuner.Core.TrackMapping;

namespace AcEvoFfbTuner.Controls;

public partial class SatelliteMapControl : UserControl
{
    private SatelliteMapService? _mapService;
    private TrackMap? _trackMap;

    private double _viewCenterLat;
    private double _viewCenterLon;
    private int _zoom = 16;
    private double _panOffsetX;
    private double _panOffsetY;
    private Point _lastMousePos;
    private bool _isDragging;

    private readonly Dictionary<(int x, int y), Image> _tileImages = new();
    private readonly Polyline _trackOutline = new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x00, 0xE6, 0x76)),
        StrokeThickness = 2.5
    };
    private readonly Ellipse _carDot = new() { Width = 14, Height = 14 };
    private readonly Line _headingLine = new() { StrokeThickness = 2.5 };
    private readonly SolidColorBrush _carBrush = new(Color.FromRgb(0xFF, 0x45, 0x00));
    private readonly SolidColorBrush _carOffTrackBrush = new(Color.FromRgb(0xFF, 0x00, 0x00));
    private readonly SolidColorBrush _headingBrush = new(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));

    private double _carLat;
    private double _carLon;
    private float _carHeading;
    private bool _carOnTrack;
    private bool _tilesLoaded;
    private bool _refreshInProgress;
    private bool _pendingGeoRefresh;
    private CancellationTokenSource? _refreshCts;

    private bool _isCalibrating;
    private float _baseLat, _baseLon, _baseRot;
    private string? _trackName;
    private bool _autoFitDone;

    public event EventHandler? CalibrationSaved;

    private int _tileSize = 256;

    public SatelliteMapControl()
    {
        InitializeComponent();
        _carDot.Fill = _carBrush;
        _headingLine.Stroke = _headingBrush;

        OverlayCanvas.Children.Add(_trackOutline);
        OverlayCanvas.Children.Add(_headingLine);
        OverlayCanvas.Children.Add(_carDot);

        MouseWheel += OnMouseWheel;
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseMove += OnMouseMove;
        SizeChanged += OnSizeChanged;
        KeyDown += OnKeyDown;
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
                AutoFitTrack();
            else
                UpdateTrackOutline();
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
                AutoFitTrack();
                return;
            }
        }

        if (!_autoFitDone)
        {
            _viewCenterLat = latitude;
            _viewCenterLon = longitude;
            _panOffsetX = 0;
            _panOffsetY = 0;
        }

        UpdateTrackOutline();
        UpdateCarMarker();

        if (!_autoFitDone)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () =>
            {
                await RefreshTilesAsync();
            });
        }
    }

    public void ShowNoGeoMessage()
    {
        NoGeoOverlay.Visibility = Visibility.Visible;
    }

    private void AutoFitTrack()
    {
        if (_mapService == null || _trackMap == null || !_mapService.HasGeoData || _autoFitDone) return;
        if (MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0) return;

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

        double centerLat = (minLat + maxLat) / 2.0;
        double centerLon = (minLon + maxLon) / 2.0;

        var (optimalZoom, _, _) = _mapService.ComputeOptimalZoom(
            minLat, minLon, maxLat, maxLon,
            (int)MapCanvas.ActualWidth, (int)MapCanvas.ActualHeight);

        _zoom = Math.Clamp(optimalZoom, 5, 19);
        _viewCenterLat = centerLat;
        _viewCenterLon = centerLon;
        _panOffsetX = 0;
        _panOffsetY = 0;

        UpdateTrackOutline();
        ZoomLabel.Text = $"Zoom: {_zoom}";

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () =>
        {
            await RefreshTilesAsync();
        });
    }

    public void UpdateCarPosition(float gameX, float gameZ, float heading, bool isOnTrack)
    {
        if (_mapService == null || !_mapService.HasGeoData) return;

        (_carLat, _carLon) = _mapService.GameToGps(gameX, gameZ);
        _carHeading = heading;
        _carOnTrack = isOnTrack;

        UpdateCarMarker();
    }

    private void UpdateTrackOutline()
    {
        if (_mapService == null || _trackMap == null || !_mapService.HasGeoData) return;

        var pts = new PointCollection();
        int step = Math.Max(1, _trackMap.Waypoints.Count / 500);
        for (int i = 0; i < _trackMap.Waypoints.Count; i += step)
        {
            var wp = _trackMap.Waypoints[i];
            var (lat, lon) = _mapService.GameToGps(wp.X, wp.Z);
            var screen = GeoToScreen(lat, lon);
            pts.Add(new Point(screen.X + _panOffsetX, screen.Y + _panOffsetY));
        }
        if (_trackMap.Waypoints.Count > 0)
        {
            var wp0 = _trackMap.Waypoints[0];
            var (lat0, lon0) = _mapService.GameToGps(wp0.X, wp0.Z);
            var s = GeoToScreen(lat0, lon0);
            pts.Add(new Point(s.X + _panOffsetX, s.Y + _panOffsetY));
        }

        _trackOutline.Points = pts;
    }

    private void UpdateCarMarker()
    {
        var screen = GeoToScreen(_carLat, _carLon);
        double sx = screen.X + _panOffsetX;
        double sy = screen.Y + _panOffsetY;

        _carDot.Fill = _carOnTrack ? _carBrush : _carOffTrackBrush;
        Canvas.SetLeft(_carDot, sx - 7);
        Canvas.SetTop(_carDot, sy - 7);
        _carDot.Visibility = Visibility.Visible;

        float lineLen = 28f;
        float endX = (float)sx + MathF.Sin(_carHeading) * lineLen;
        float endY = (float)sy - MathF.Cos(_carHeading) * lineLen;
        _headingLine.X1 = sx;
        _headingLine.Y1 = sy;
        _headingLine.X2 = endX;
        _headingLine.Y2 = endY;
        _headingLine.Visibility = Visibility.Visible;
    }

    private Point GeoToScreen(double lat, double lon)
    {
        int n = 1 << _zoom;
        double x = (lon + 180.0) / 360.0 * n * _tileSize;
        double latRad = lat * Math.PI / 180.0;
        double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n * _tileSize;

        double viewN = 1 << _zoom;
        double viewX = (_viewCenterLon + 180.0) / 360.0 * viewN * _tileSize;
        double viewLatRad = _viewCenterLat * Math.PI / 180.0;
        double viewY = (1.0 - Math.Log(Math.Tan(viewLatRad) + 1.0 / Math.Cos(viewLatRad)) / Math.PI) / 2.0 * viewN * _tileSize;

        double canvasCenterX = MapCanvas.ActualWidth / 2.0;
        double canvasCenterY = MapCanvas.ActualHeight / 2.0;

        return new Point(
            x - viewX + canvasCenterX,
            y - viewY + canvasCenterY);
    }

    private async Task RefreshTilesAsync()
    {
        if (_mapService == null || !_mapService.HasGeoData) return;
        if (MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0)
        {
            _pendingGeoRefresh = true;
            return;
        }

        _refreshCts?.Cancel();
        _refreshCts = new CancellationTokenSource();
        var ct = _refreshCts.Token;

        if (_refreshInProgress)
        {
            _pendingGeoRefresh = true;
            return;
        }
        _refreshInProgress = true;
        _pendingGeoRefresh = false;

        try
        {
            double halfW = MapCanvas.ActualWidth / 2.0;
            double halfH = MapCanvas.ActualHeight / 2.0;
            int n = 1 << _zoom;

            double viewNormX = (_viewCenterLon + 180.0) / 360.0;
            double viewLatRad = _viewCenterLat * Math.PI / 180.0;
            double viewNormY = (1.0 - Math.Log(Math.Tan(viewLatRad) + 1.0 / Math.Cos(viewLatRad)) / Math.PI) / 2.0;

            double viewTileX = viewNormX * n;
            double viewTileY = viewNormY * n;

            int tilesX = (int)Math.Ceiling(MapCanvas.ActualWidth / (double)_tileSize) + 2;
            int tilesY = (int)Math.Ceiling(MapCanvas.ActualHeight / (double)_tileSize) + 2;

            int centerX = (int)Math.Floor(viewTileX);
            int centerY = (int)Math.Floor(viewTileY);
            int halfTilesX = tilesX / 2 + 1;
            int halfTilesY = tilesY / 2 + 1;

            int x1 = Math.Max(0, centerX - halfTilesX);
            int x2 = Math.Min(n - 1, centerX + halfTilesX);
            int y1 = Math.Max(0, centerY - halfTilesY);
            int y2 = Math.Min(n - 1, centerY + halfTilesY);

            var tiles = new List<(int x, int y, double screenX, double screenY)>();

            for (int tx = x1; tx <= x2; tx++)
            {
                for (int ty = y1; ty <= y2; ty++)
                {
                    double screenX = (tx - viewTileX) * _tileSize + halfW;
                    double screenY = (ty - viewTileY) * _tileSize + halfH;
                    tiles.Add((tx, ty, screenX, screenY));
                }
            }

            var cacheResults = await Task.WhenAll(
                tiles.Select(t => _mapService.FetchTileAsync(t.x, t.y, _zoom)).ToArray());

            if (ct.IsCancellationRequested) return;

            var uncachedIndices = new List<int>();
            bool hasCached = false;

            Dispatcher.Invoke(() =>
            {
                if (ct.IsCancellationRequested) return;
                foreach (var child in _tileImages.Values)
                    MapCanvas.Children.Remove(child);
                _tileImages.Clear();

                for (int i = 0; i < tiles.Count; i++)
                {
                    if (cacheResults[i] != null)
                    {
                        AddTileImage(tiles[i], cacheResults[i]!);
                        hasCached = true;
                    }
                    else
                    {
                        uncachedIndices.Add(i);
                    }
                }

                UpdateTrackOutline();
                UpdateCarMarker();
                ZoomLabel.Text = $"Zoom: {_zoom}";

                if (uncachedIndices.Count == 0)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    _tilesLoaded = true;
                }
                else
                {
                    LoadingOverlay.Visibility = hasCached ? Visibility.Collapsed : Visibility.Visible;
                }
            });

            if (uncachedIndices.Count > 0)
            {
                var fetchTasks = uncachedIndices.Select(async idx =>
                {
                    var path = await _mapService.FetchTileAsync(tiles[idx].x, tiles[idx].y, _zoom);
                    return (idx, path);
                }).ToArray();

                foreach (var task in fetchTasks)
                {
                    var (idx, path) = await task;
                    if (ct.IsCancellationRequested) return;

                    if (path != null)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (ct.IsCancellationRequested) return;
                            AddTileImage(tiles[idx], path);
                        });
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    _tilesLoaded = true;
                });
            }
        }
        finally
        {
            _refreshInProgress = false;

            if (_pendingGeoRefresh && (_refreshCts == null || !_refreshCts.IsCancellationRequested))
            {
#pragma warning disable CS4014
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, async () =>
                {
                    await RefreshTilesAsync();
                });
#pragma warning restore CS4014
            }
        }
    }

    private void AddTileImage((int x, int y, double screenX, double screenY) tile, string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            var img = new Image
            {
                Source = bmp,
                Width = _tileSize,
                Height = _tileSize,
                Stretch = Stretch.Fill
            };

            Canvas.SetLeft(img, tile.screenX + _panOffsetX);
            Canvas.SetTop(img, tile.screenY + _panOffsetY);
            MapCanvas.Children.Add(img);
            _tileImages[(tile.x, tile.y)] = img;
        }
        catch { }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isCalibrating && _mapService != null)
        {
            double rotStep = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2.0 : 0.5;
            double delta = e.Delta > 0 ? rotStep : -rotStep;
            _mapService.AdjustCalibration(0, 0, delta);
            UpdateTrackOutline();
            UpdateCarMarker();
            UpdateCalibrationDisplay();
            e.Handled = true;
            return;
        }

        int zoomDelta = e.Delta > 0 ? 1 : -1;
        int newZoom = Math.Clamp(_zoom + zoomDelta, 5, 19);
        if (newZoom == _zoom) return;

        CommitPanToCenter();
        _panOffsetX = 0;
        _panOffsetY = 0;

        var mousePos = e.GetPosition(MapCanvas);
        double canvasCenterX = MapCanvas.ActualWidth / 2.0;
        double canvasCenterY = MapCanvas.ActualHeight / 2.0;

        int oldN = 1 << _zoom;

        double viewWorldX = (_viewCenterLon + 180.0) / 360.0 * oldN * _tileSize;
        double viewLatRad = _viewCenterLat * Math.PI / 180.0;
        double viewWorldY = (1.0 - Math.Log(Math.Tan(viewLatRad) + 1.0 / Math.Cos(viewLatRad)) / Math.PI) / 2.0 * oldN * _tileSize;

        double mouseWorldX = (mousePos.X - canvasCenterX) + viewWorldX;
        double mouseWorldY = (mousePos.Y - canvasCenterY) + viewWorldY;

        double normX = mouseWorldX / (oldN * _tileSize);
        double normY = mouseWorldY / (oldN * _tileSize);

        _zoom = newZoom;
        int newN = 1 << _zoom;

        double newViewWorldX = normX * newN * _tileSize - mousePos.X + canvasCenterX;
        double newViewWorldY = normY * newN * _tileSize - mousePos.Y + canvasCenterY;

        _viewCenterLon = Math.Clamp(newViewWorldX / (newN * _tileSize) * 360.0 - 180.0, -180.0, 180.0);
        double newViewLatRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * newViewWorldY / (newN * _tileSize))));
        _viewCenterLat = Math.Clamp(newViewLatRad * 180.0 / Math.PI, -85.0, 85.0);

        ZoomLabel.Text = $"Zoom: {_zoom}";
        _ = RefreshTilesAsync();
    }

    private void CommitPanToCenter()
    {
        if (Math.Abs(_panOffsetX) < 0.5 && Math.Abs(_panOffsetY) < 0.5) return;

        int n = 1 << _zoom;
        double pixelToLon = 360.0 / (n * _tileSize);
        double cosLat = Math.Cos(_viewCenterLat * Math.PI / 180.0);
        if (cosLat < 0.01) cosLat = 0.01;

        _viewCenterLon += _panOffsetX * pixelToLon;
        _viewCenterLat -= _panOffsetY * pixelToLon * cosLat;

        _viewCenterLon = Math.Clamp(_viewCenterLon, -180.0, 180.0);
        _viewCenterLat = Math.Clamp(_viewCenterLat, -85.0, 85.0);

        _panOffsetX = 0;
        _panOffsetY = 0;
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _lastMousePos = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        double dx = pos.X - _lastMousePos.X;
        double dy = pos.Y - _lastMousePos.Y;
        _lastMousePos = pos;

        if (_isCalibrating && _mapService != null)
        {
            int n = 1 << _zoom;
            double pixelToLon = 360.0 / (n * _tileSize);
            double cosLat = Math.Cos(_mapService.TrackCenterLatitude * Math.PI / 180.0);
            if (cosLat < 0.01) cosLat = 0.01;
            double dLon = dx * pixelToLon;
            double dLat = -dy * pixelToLon * cosLat;
            _mapService.AdjustCalibration(dLat, dLon, 0);
            UpdateTrackOutline();
            UpdateCarMarker();
            UpdateCalibrationDisplay();
        }
        else
        {
            _panOffsetX += dx;
            _panOffsetY += dy;

            foreach (var img in _tileImages.Values)
            {
                double left = Canvas.GetLeft(img) + dx;
                double top = Canvas.GetTop(img) + dy;
                Canvas.SetLeft(img, left);
                Canvas.SetTop(img, top);
            }

            UpdateTrackOutline();
            UpdateCarMarker();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mapService?.HasGeoData == true && _trackMap != null && !_autoFitDone)
        {
            AutoFitTrack();
        }
        else if (_mapService?.HasGeoData == true && (_pendingGeoRefresh || _tilesLoaded))
        {
            _pendingGeoRefresh = false;
            _ = RefreshTilesAsync();
        }
    }

    public void CenterOnTrack()
    {
        _panOffsetX = 0;
        _panOffsetY = 0;
        _ = RefreshTilesAsync();
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
        CalibrationOverlay.Visibility = Visibility.Visible;
        UpdateCalibrationDisplay();
        Focus();
        Keyboard.Focus(this);
    }

    public void ExitCalibrationMode()
    {
        _isCalibrating = false;
        CalibrationOverlay.Visibility = Visibility.Collapsed;
    }

    public bool IsCalibrating => _isCalibrating;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCalibrating || _mapService == null)
            return;

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        double step = shift ? 0.0005 : 0.0001;
        double rotStep = shift ? 2.0 : 0.5;

        switch (e.Key)
        {
            case Key.Left:
                _mapService.AdjustCalibration(0, -step, 0);
                break;
            case Key.Right:
                _mapService.AdjustCalibration(0, step, 0);
                break;
            case Key.Up:
                _mapService.AdjustCalibration(step, 0, 0);
                break;
            case Key.Down:
                _mapService.AdjustCalibration(-step, 0, 0);
                break;
            case Key.OemPlus:
            case Key.Add:
                _mapService.AdjustCalibration(0, 0, rotStep);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                _mapService.AdjustCalibration(0, 0, -rotStep);
                break;
            case Key.Escape:
                _mapService.SetGeoReference(_baseLat, _baseLon, _baseRot);
                ExitCalibrationMode();
                break;
            default:
                return;
        }

        UpdateTrackOutline();
        UpdateCarMarker();
        UpdateCalibrationDisplay();
        e.Handled = true;
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
        UpdateTrackOutline();
        UpdateCarMarker();
        UpdateCalibrationDisplay();
    }

    public void Reset()
    {
        _refreshCts?.Cancel();
        _refreshCts = null;
        _refreshInProgress = false;
        _pendingGeoRefresh = false;
        _tilesLoaded = false;
        _autoFitDone = false;
        _trackMap = null;
        _mapService = null;

        Dispatcher.Invoke(() =>
        {
            foreach (var child in _tileImages.Values)
                MapCanvas.Children.Remove(child);
            _tileImages.Clear();

            _trackOutline.Points = new PointCollection();
            _carDot.Visibility = Visibility.Collapsed;
            _headingLine.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Collapsed;
        });
    }
}
