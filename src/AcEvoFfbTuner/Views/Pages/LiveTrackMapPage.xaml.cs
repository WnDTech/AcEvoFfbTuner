using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AcEvoFfbTuner.Controls;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.TrackMapping;
using AcEvoFfbTuner.ViewModels;
using SolidColorBrushMedia = System.Windows.Media.SolidColorBrush;

namespace AcEvoFfbTuner.Views.Pages;

public partial class LiveTrackMapPage : UserControl
{
    private SatelliteMapService? _satelliteService;
    private bool _satelliteInitialized;
    private readonly TrackDataService _trackDataService = new();
    private string? _lastLoadedTrack;
    private TrackDetailedInfo? _currentOsmData;

    // Zoom-to-track: pending after every new layout / track change, applied once the map is
    // visible and laid out (a fit while hidden has no viewport — the old "didn't zoom" bug).
    private bool _fitPending;
    private bool _layoutLoadFailed;
    private string _lastStatusMessage = "";

    // Legend + Field panel (refreshed ~2 Hz from the 10 Hz car snapshots).
    private readonly System.Collections.ObjectModel.ObservableCollection<FieldRow> _fieldRows = new();
    private readonly Dictionary<int, (double lat, double lon)> _carGps = new();
    private int _selectedCarId = int.MinValue;
    private long _lastPanelTicks;
    private string _diagText = "";
    private string _legendSignature = "";

    // OSM layout in a local meter frame (east/north). All distance and heading math
    // runs here — raw lat/lon degrees over-weight east-west travel by 1/cos(lat)
    // (up to ~300 m of dot error around Monza).
    private LocalProjection _osmProj;
    private (double e, double n)[] _osmEn = Array.Empty<(double, double)>();
    private double[] _osmCumDist = Array.Empty<double>();
    private double _osmTotalDist;
    private int _osmStartFinishIndex;
    private double _osmSfCumDist; // cumulative distance (m) at the SF line

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

    // SF anchor + direction. Priority: geo-fit > persisted anchor > wrap calibration > parse heuristic.
    private bool _osmReversed;
    private string _anchorSource = "heuristic";

    // Cache for the nearest-OSM-point fallback (avoid a full layout scan every tick)
    private int _lastNearestIdx = -1;
    private double _lastNearestE, _lastNearestN;

    // ── Geo-fit: world (X, Z) → GPS, solved from the driven path vs the OSM layout ──
    // Captured even while the page is hidden so the fit is ready when it is opened.
    private const double SnapMaxM = 15.0;      // snap to the OSM centerline within this distance
    private const double PathJumpResetM = 80.0; // larger jump between samples = teleport / new stint
    private readonly List<GeoPathSample> _pathBuffer = new();
    private string? _pathTrack;
    private float _lastPathX, _lastPathZ;
    private bool _hasLastPath;
    private double _pathTotalM;
    private double _pathLenAtLastFit;
    private bool _fitInProgress;
    private TrackGeoFit? _geoFit;
    private string _fitStatus = "fit: waiting";

    // Heading from motion when the dot is not snapped to a layout segment
    private double _lastPlacedE, _lastPlacedN, _lastCompass;
    private bool _hasLastPlaced;

    // Opponents: TelemetryLoop publishes a new snapshot array ~10 Hz; re-render on change.
    private IReadOnlyList<CarPosition>? _lastCars;
    private bool _opponentsDirty;
    private readonly List<MapCarMarker> _opponentMarkers = new();

    public LiveTrackMapPage()
    {
        InitializeComponent();
        _trackDataService.TrackDataUpdated += OnTrackDataUpdated;
        _trackDataService.StatusMessage += msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastStatusMessage = msg;
            if (LoadingOverlay.Visibility == Visibility.Visible && !_layoutLoadFailed)
                LoadingDetail.Text = msg;
        }));
        // When the page becomes visible again, invalidate the stale Npos sample so a
        // session jump can't be mistaken for a start/finish line crossing, and apply a
        // pending zoom-to-track once layout has given the map a size.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _prevNpos = -1f;
            Dispatcher.BeginInvoke(new Action(TryFitPending), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        MapCtrl.SizeChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(TryFitPending), System.Windows.Threading.DispatcherPriority.Background);
        FieldList.ItemsSource = _fieldRows;
    }

    public void Initialize()
    {
        if (_satelliteInitialized) return;
        _satelliteInitialized = true;
        _satelliteService = new SatelliteMapService();
        MapCtrl.Initialize(_satelliteService);
    }

    /// <summary>Zoom to the whole layout if a fit is pending and the map can take it now.</summary>
    private void TryFitPending()
    {
        if (!_fitPending || !IsVisible || _currentOsmData?.TrackLayout is not { Count: > 3 } layout) return;
        if (FollowToggle.IsChecked == true) { _fitPending = false; return; }
        if (MapCtrl.FitToGps(layout)) _fitPending = false;
    }

    private uint AccentArgb()
    {
        if (TryFindResource("AccentBrush") is SolidColorBrushMedia b)
            return (uint)(b.Color.A << 24 | b.Color.R << 16 | b.Color.G << 8 | b.Color.B);
        return 0xFFB06AE0;
    }

    public void UpdateDisplay(float carX, float carZ, float heading, float speedKmh,
        bool isOnTrack, bool hasMap, float npos, string? trackName,
        float trackLatitude, float trackLongitude, float trackRotation,
        int sectorNumber = 0, int lapCount = 0,
        WaypointForceSample[]? forceHeatmap = null,
        TrackMap? currentMap = null,
        IReadOnlyList<CarPosition>? cars = null)
    {
        if (!_satelliteInitialized) return;

        CapturePathSample(carX, carZ, npos, speedKmh, trackName);

        if (Visibility != Visibility.Visible) return;

        // Cache the TrackMap for GameToGps calibration
        if (currentMap != null && currentMap != _currentTrackMap)
        {
            _currentTrackMap = currentMap;
            // Auto-compute the GameToGps transform from the TrackMap
            if (_satelliteService != null && currentMap.Waypoints.Count > 10 && currentMap.TrackLengthM > 100)
                _satelliteService.ComputeGameToGpsTransform(currentMap);
        }

        bool trackChanged = false;
        if (trackName != _lastLoadedTrack && !string.IsNullOrEmpty(trackName))
        {
            trackChanged = true;
            _lastLoadedTrack = trackName;
            // The last-known geo-reference is the previous venue's: LoadOsmDataAsync falls back
            // to it, and a stale Le Mans centre sent the Bahrain search to France (2026-10-20).
            _lastKnownLat = 0f;
            _lastKnownLon = 0f;
            _lastKnownRotation = 0f;
            _currentOsmData = null;
            _fitPending = true;
            _layoutLoadFailed = false;
            _selectedCarId = int.MinValue;
            // Never leave the previous circuit on screen while the new one loads.
            MapCtrl.ClearGpsTrackOutline();
            MapCtrl.UpdateOpponents(Array.Empty<MapCarMarker>());
            ShowLoading($"Loading {trackName}", "Looking up the circuit layout…");
            _prevNpos = -1f;
            _osmReversed = false;
            _anchorSource = "heuristic";
            _lastNearestIdx = -1;
            _geoFit = null;
            _fitStatus = "fit: waiting";
            _hasLastPlaced = false;
            _opponentsDirty = true;
            _ = LoadOsmDataAsync(trackName, trackLatitude, trackLongitude);
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

        if (_satelliteService != null && !string.IsNullOrEmpty(trackName)
            && (!_satelliteService.HasGeoData || (trackChanged && trackLatitude == 0 && trackLongitude == 0)))
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

        MaybeStartGeoFit();

        double carLat = 0, carLon = 0;
        double carHeading = _lastCompass; // compass radians: 0 = north, clockwise
        bool hasPosition = false;
        string placement = "none";

        // Wrap calibration (legacy fallback): when Npos wraps (~0.99 → ~0.01) the car
        // is on the SF line; locate it on OSM through GameToGps. Skipped once a geo-fit
        // exists, and for LMU — GameToGps assumes north = -Z, but LMU's world is
        // (X, Z) = (east, north) (verified 2026-10-19), so it would anchor a mirror.
        bool nposCrossedLine = _prevNpos > 0f && npos > 0f
            && ((npos < 0.1f && _prevNpos > 0.9f) || (npos > 0.9f && _prevNpos < 0.1f));
        _prevNpos = npos;

        if (nposCrossedLine && _geoFit == null && (_anchorSource is "heuristic" or "wrap") && !IsLmu()
            && _currentOsmData?.TrackLayout is { Count: > 2 } layout && _osmEn.Length == layout.Count
            && _satelliteService != null && _satelliteService.HasGeoData
            && carX != 0f && carZ != 0f)
        {
            var (gpsLat, gpsLon) = _satelliteService.GameToGps(carX, carZ);
            if (IsValidGps(gpsLat, gpsLon))
            {
                var (ge, gn) = _osmProj.ToLocal(gpsLat, gpsLon);
                // The start/finish line is on the straight before T1, not at the corner apex.
                int snapIdx = FindNearestOsmIndex(ge, gn);
                int sfIdx = FindStraightPointFrom(snapIdx);
                _osmStartFinishIndex = sfIdx;
                _osmSfCumDist = InterpolateCumDistAt(ge, gn);

                _anchorSource = "wrap";
                _currentOsmData.StartFinish = new TrackPoint(layout[sfIdx].Latitude, layout[sfIdx].Longitude);
                RedrawLayoutMarkers();
                StaticLog($"Auto-calibrated SF: sfIdx={sfIdx} sfCumDist={_osmSfCumDist:F1} m ({layout[sfIdx].Latitude:F5},{layout[sfIdx].Longitude:F5})");
            }
        }

        bool layoutReady = _currentOsmData?.TrackLayout is { Count: > 2 } && _osmEn.Length > 2;

        // 1. Geo-fit: world position → GPS, snapped to the OSM centerline when close.
        //    Exact anywhere (pit lane, off-track) and independent of Npos.
        if (_geoFit != null && layoutReady && (carX != 0f || carZ != 0f))
        {
            var (fLat, fLon) = _geoFit.WorldToGps(carX, carZ);
            var (e, n) = _osmProj.ToLocal(fLat, fLon);
            var (se, sn, seg, dist) = NearestOnLayout(e, n);
            if (dist <= SnapMaxM)
            {
                e = se; n = sn;
                carHeading = FlipHeadingForDirection(SegmentCompass(seg));
            }
            else
            {
                carHeading = MotionCompass(e, n);
            }
            (carLat, carLon) = _osmProj.ToGps(e, n);
            RememberPlaced(e, n, carHeading);
            hasPosition = true;
            placement = dist <= SnapMaxM ? "fit" : "fit-free";
        }
        // 2. Npos along the OSM layout (meters), anchored at the SF line.
        else if (layoutReady && npos > 0f)
        {
            var (e, n, compass) = GetPositionOnOsmTrack(npos);
            (carLat, carLon) = _osmProj.ToGps(e, n);
            carHeading = compass;
            RememberPlaced(e, n, compass);
            hasPosition = true;
            placement = "npos";
        }
        // 3. Legacy: uncalibrated GameToGps, snapped to the nearest OSM point.
        else if ((carX != 0f || carZ != 0f) && _satelliteService != null && _satelliteService.HasGeoData)
        {
            var (gpsLat, gpsLon) = _satelliteService.GameToGps(carX, carZ);
            if (IsValidGps(gpsLat, gpsLon))
            {
                carLat = gpsLat;
                carLon = gpsLon;
                hasPosition = true;
                placement = "gps";

                if (layoutReady)
                {
                    var (ge, gn) = _osmProj.ToLocal(gpsLat, gpsLon);
                    int snapIdx = FindNearestOsmIndexCached(ge, gn);
                    var p = _osmEn[snapIdx];
                    (carLat, carLon) = _osmProj.ToGps(p.e, p.n);
                    carHeading = FlipHeadingForDirection(SegmentCompass((snapIdx - 1 + _osmEn.Length) % _osmEn.Length));
                    RememberPlaced(p.e, p.n, carHeading);
                }
            }
        }

        UpdateOpponents(cars, layoutReady);

        if (hasPosition)
        {
            MapCtrl.PlayerColorArgb = AccentArgb();
            MapCtrl.UpdateCarGpsPosition(carLon, carLat, carHeading);
            if (FollowToggle.IsChecked == true) MapCtrl.PanToGps(carLat, carLon);
        }
        TryFitPending();

#if DEBUG
        // Diagnostic logging — every 30 frames (~1 s) write positioning data to file
        if (++_logFrameCount % 30 == 0)
        {
            LogPos(npos, carX, carZ, carLat, carLon, trackName ?? "?", placement);
        }
#endif

        var rev = _osmReversed ? " REV" : "";
        _diagText = $"npos={npos:F4}  pos={placement}  sf={_anchorSource}{rev}  {_fitStatus}  cars={_opponentMarkers.Count}  " +
                    $"gps=({carLat:F5}, {carLon:F5})  path={_pathTotalM / 1000:F2} km  " +
                    $"src={(_currentOsmData != null ? DataSourceLabelText(_currentOsmData.DataSource) : "none")}  " +
                    $"layout={_currentOsmData?.TrackLayout?.Count ?? 0} pts  corners={_currentOsmData?.Corners.Count ?? 0}  " +
                    $"pit={(_currentOsmData?.Pit != null ? "yes" : "no")}  last: {_lastStatusMessage}";
        if (DiagPanel.Visibility == Visibility.Visible) DiagText.Text = _diagText;

        int gameSector = DataContext is MainViewModel gvm ? gvm.GameSectorIndex : sectorNumber;
        UpdateHeader(trackName, speedKmh, gameSector, lapCount);
        UpdateLockPill();

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastPanelTicks > System.Diagnostics.Stopwatch.Frequency / 2)
        {
            _lastPanelTicks = now;
            UpdatePanels(cars);
        }
    }

    // ── Header, status, legend and field panel ───────────────────────────────

    private void UpdateHeader(string? trackName, float speedKmh, int gameSector, int lapCount)
    {
        TrackTitle.Text = string.IsNullOrEmpty(trackName) ? "Waiting for a session" : trackName;
        if (_currentOsmData?.TrackLayout is { Count: > 3 } && _osmTotalDist > 0)
        {
            var parts = new List<string> { $"{_osmTotalDist / 1000:F2} km", SourceDisplayName(_currentOsmData.DataSource) };
            if (_currentOsmData.Corners.Count > 0) parts.Add($"{_currentOsmData.Corners.Count} corners");
            TrackSubtitle.Text = string.Join("  ·  ", parts);
        }
        else
        {
            TrackSubtitle.Text = _layoutLoadFailed ? "No layout available for this circuit" : "Loading circuit layout…";
        }

        SpeedValue.Text = speedKmh > 0.5f ? $"{speedKmh:F0} km/h" : "0 km/h";
        SectorValue.Text = gameSector > 0 ? $"S{gameSector}" : "--";
        LapValue.Text = lapCount > 0 ? lapCount.ToString() : "--";
        if (DataContext is MainViewModel vm)
        {
            var real = vm.CurrentCornerRealName;
            CornerValue.Text = string.IsNullOrWhiteSpace(real) ? vm.CurrentCornerName : $"{vm.CurrentCornerName} · {real}";
        }
    }

    private void UpdateLockPill()
    {
        string text;
        string brushKey;
        if (_geoFit != null)
        {
            text = $"Locked ±{_geoFit.MedianResidualM:F0} m";
            brushKey = "GreenBrush";
        }
        else if (_currentOsmData?.TrackLayout is { Count: > 3 })
        {
            double need = Math.Min(TrackGeoFitter.MinPathM, _osmTotalDist * 0.5);
            text = DataContext is MainViewModel { IsAssettoCorsaCompetizione: true }
                ? "Lap-position mode"
                : $"Aligning {Math.Min(_pathTotalM, need) / 1000:F1} / {need / 1000:F1} km";
            brushKey = "YellowBrush";
        }
        else if (_layoutLoadFailed)
        {
            text = "No layout";
            brushKey = "RedBrush";
        }
        else
        {
            text = string.IsNullOrEmpty(_lastLoadedTrack) ? "Waiting" : "Loading layout";
            brushKey = "TextDimBrush";
        }
        if (LockText.Text != text) LockText.Text = text;
        if (brushKey != _lockBrushKey)
        {
            _lockBrushKey = brushKey;
            LockDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        }
    }

    private string _lockBrushKey = "";

    private void ShowLoading(string title, string detail)
    {
        LoadingTitle.Text = title;
        LoadingDetail.Text = detail;
        LoadingOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>Legend chips per class family and the Field list, both from the latest car snapshot.</summary>
    private void UpdatePanels(IReadOnlyList<CarPosition>? cars)
    {
        var list = cars ?? Array.Empty<CarPosition>();

        // Player's race position and class (header).
        CarPosition? me = null;
        foreach (var c in list) if (c.IsPlayer) { me = c; break; }
        RacePositionValue.Text = me is { Place: > 0 } p ? $"P{p.Place} / {list.Count}" : "--";
        if (me is { CarClass.Length: > 0 } mine)
        {
            var cat = CarClasses.Categorize(mine.CarClass);
            ClassDot.Fill = CarClassPalette.Brush(CarClassPalette.ArgbFor(cat, mine.CarClass));
            ClassDot.Visibility = Visibility.Visible;
            ClassValue.Text = mine.CarClass;
        }
        else
        {
            ClassDot.Visibility = Visibility.Collapsed;
            ClassValue.Text = "--";
        }

        // Legend: one chip per class family present, ordered by size of the field.
        var groups = list
            .Select(c => (c, cat: CarClasses.Categorize(c.CarClass)))
            .Where(x => x.cat != CarClassCategory.Unknown)
            .GroupBy(x => CarClassPalette.LabelFor(x.cat, x.c.CarClass))
            .Select(g => new LegendItem
            {
                Label = g.Key,
                Count = g.Count(),
                Brush = CarClassPalette.Brush(CarClassPalette.ArgbFor(g.First().cat, g.First().c.CarClass)),
            })
            .OrderByDescending(i => i.Count).ThenBy(i => i.Label)
            .ToList();
        // Active flag rings (opponents only — the rings are drawn around other cars).
        groups.AddRange(list
            .Where(c => !c.IsPlayer && c.Flag != CarFlag.None)
            .GroupBy(c => c.Flag)
            .OrderBy(g => g.Key)
            .Select(g => new LegendItem
            {
                Label = CarFlagPalette.Label(g.Key),
                Count = g.Count(),
                Brush = CarClassPalette.Brush(CarFlagPalette.ArgbFor(g.Key) == 0xFF000000 ? 0xFF484F58 : CarFlagPalette.ArgbFor(g.Key)),
                IsRing = true,
            }));
        // Re-template the chips only when their content changes (this runs every 0.5 s).
        string signature = string.Join("|", groups.Select(g => $"{g.Label}:{g.Count}:{g.IsRing}:{g.Brush.Color}"));
        if (signature != _legendSignature)
        {
            _legendSignature = signature;
            LegendList.ItemsSource = groups;
        }
        LegendCard.Visibility = groups.Count > 0 && _currentOsmData != null ? Visibility.Visible : Visibility.Collapsed;

        // Field: standings order (unplaced cars last), updated in place.
        var ordered = list
            .OrderBy(c => c.Place > 0 ? c.Place : int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        while (_fieldRows.Count > ordered.Count) _fieldRows.RemoveAt(_fieldRows.Count - 1);
        for (int i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            if (i >= _fieldRows.Count) _fieldRows.Add(new FieldRow());
            var row = _fieldRows[i];
            var cat = CarClasses.Categorize(c.CarClass);
            row.Id = c.Id;
            row.Place = c.Place > 0 ? c.Place.ToString() : "–";
            row.Name = c.Name.Length > 0 ? c.Name : (c.IsPlayer ? "You" : $"Car {c.Id + 1}");
            row.Number = c.Number.Length > 0 ? $"#{c.Number}" : "";
            row.ClassLabel = c.CarClass.Length > 0 ? c.CarClass : (c.IsPlayer ? "You" : "");
            row.ClassBrush = CarClassPalette.Brush(CarClassPalette.ArgbFor(cat, c.CarClass));
            row.IsPlayer = c.IsPlayer;
            row.Tag = CarFlagPalette.Tag(c.Flag, c.InPits);
            row.TagBrush = CarClassPalette.Brush(CarFlagPalette.TagArgb(c.Flag, c.InPits));
        }
        FieldCount.Text = list.Count > 0 ? $"{list.Count} cars" : "";
        FieldEmpty.Visibility = list.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
        FieldPanel.Visibility = FieldToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsValidGps(double lat, double lon) =>
        !double.IsNaN(lat) && !double.IsNaN(lon) && lat != 0 && lon != 0
        && Math.Abs(lat) < 90 && Math.Abs(lon) < 180;

    private bool IsLmu() => DataContext is MainViewModel vm && vm.IsLeMansUltimate;

    private string CurrentGameKey() => DataContext is MainViewModel vm ? vm.SelectedGame.ToString() : "Unknown";

    private string? CurrentConfig() =>
        DataContext is MainViewModel vm && !string.IsNullOrWhiteSpace(vm.DetectedTrackConfig) ? vm.DetectedTrackConfig : null;

    // ── Driven-path capture + geo-fit ────────────────────────────────────────

    private void ResetPath()
    {
        _pathBuffer.Clear();
        _hasLastPath = false;
        _pathTotalM = 0;
        _pathLenAtLastFit = 0;
    }

    /// <summary>
    /// Record one continuous stint of real world positions (with Npos) for the geo-fit.
    /// A pit visit or a teleport-sized jump starts a new stint: the fit needs a
    /// gap-free path, and pit-lane driving is not part of the racing line.
    /// </summary>
    private void CapturePathSample(float x, float z, float npos, float speedKmh, string? trackName)
    {
        if (trackName != _pathTrack)
        {
            _pathTrack = trackName;
            ResetPath();
        }
        if (x == 0f && z == 0f) return;
        if (DataContext is MainViewModel vm)
        {
            // ACC publishes no world coordinates (TelemetryLoop dead-reckons them) — not fit-worthy.
            if (vm.IsAssettoCorsaCompetizione) return;
            if (vm.IsInPit) { ResetPath(); return; }
        }
        if (speedKmh < 30f) return;

        if (_hasLastPath)
        {
            double dx = x - _lastPathX, dz = z - _lastPathZ;
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d > PathJumpResetM) ResetPath();
            else if (d < 2.0) return;
            else _pathTotalM += d;
        }
        _pathBuffer.Add(new GeoPathSample(x, z, npos));
        _lastPathX = x;
        _lastPathZ = z;
        _hasLastPath = true;

        // Keep ~1.1 laps (samples are ≥ 2 m apart, so count/2 m bounds the length).
        int cap = (int)((_osmTotalDist > 0 ? _osmTotalDist * 1.1 : 9000) / 2.0);
        if (_pathBuffer.Count > cap + 500)
            _pathBuffer.RemoveRange(0, _pathBuffer.Count - cap);
    }

    /// <summary>Run the fitter in the background every ~400 m of new driving until a near-full-lap fit is locked.</summary>
    private void MaybeStartGeoFit()
    {
        if (_fitInProgress || _osmTotalDist <= 0 || _currentOsmData?.TrackLayout is not { Count: >= 20 } layout) return;
        if (string.IsNullOrEmpty(_lastLoadedTrack) || _pathTrack != _lastLoadedTrack) return;

        double need = Math.Min(TrackGeoFitter.MinPathM, _osmTotalDist * 0.5);
        if (_pathTotalM < need || _pathTotalM - _pathLenAtLastFit < 400) return;
        // A fit solved on ~a full lap is as good as it gets (the fitter caps at 95 % of a lap).
        if (_geoFit != null && _geoFit.PathLengthM >= _osmTotalDist * 0.85) return;

        _pathLenAtLastFit = _pathTotalM;
        _fitInProgress = true;
        var samples = _pathBuffer.ToArray();
        var layoutCopy = new List<TrackPoint>(layout);
        string track = _lastLoadedTrack;
        string game = CurrentGameKey();
        string? config = CurrentConfig();
        _fitStatus = "fit: solving";

        Task.Run(() =>
        {
            var fit = TrackGeoFitter.Fit(samples, layoutCopy, out var diag);
            return (fit, diag);
        }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
        {
            _fitInProgress = false;
            if (t.IsFaulted)
            {
                _fitStatus = "fit: error";
                TrackGeoFitStore.Log($"{track} [{game}] error: {t.Exception?.GetBaseException().Message}");
                return;
            }
            var (fit, diag) = t.Result;
            TrackGeoFitStore.Log($"{track} [{game}] samples={samples.Length} {diag}");
            if (track != _lastLoadedTrack) return;
            if (fit == null)
            {
                if (_geoFit == null) _fitStatus = "fit: no match yet";
                return;
            }
            if (!IsBetterFit(fit, _geoFit)) return;
            fit.Game = game;
            fit.TrackName = track;
            ApplyGeoFit(fit, persist: true, config);
        }));
    }

    private static bool IsBetterFit(TrackGeoFit candidate, TrackGeoFit? current)
    {
        if (current == null) return true;
        if (candidate.PathLengthM > current.PathLengthM * 1.3) return true;
        return candidate.PathLengthM >= current.PathLengthM * 0.9
            && candidate.MedianResidualM <= current.MedianResidualM * 1.25;
    }

    private void ApplyGeoFit(TrackGeoFit fit, bool persist, string? config)
    {
        _geoFit = fit;
        _osmReversed = fit.Reversed;
        _hasLastPlaced = false;
        _opponentsDirty = true;
        _fitStatus = $"fit: ±{fit.MedianResidualM:F0} m{(fit.Mirrored ? " mir" : "")}";

        // The SF fraction belongs to the layout it was solved on — only trust it when
        // the current layout has the same length.
        if (fit.SfFraction >= 0 && _osmTotalDist > 0
            && Math.Abs(fit.OsmLengthM - _osmTotalDist) < 0.02 * _osmTotalDist)
        {
            double newSf = fit.SfFraction * _osmTotalDist;
            // Refits arrive every ~400 m during the first lap; only a real move of the SF
            // anchor warrants redrawing markers and rewriting the relation cache.
            double moved = Math.Abs(newSf - _osmSfCumDist);
            moved = Math.Min(moved, _osmTotalDist - moved);
            bool sfChanged = _anchorSource != "fit" || moved > 10.0;

            _osmSfCumDist = newSf;
            _osmStartFinishIndex = IndexAtArc(_osmSfCumDist);
            _anchorSource = "fit";
            if (sfChanged && _currentOsmData != null)
            {
                var (e, n) = PointAtArc(_osmSfCumDist);
                var (lat, lon) = _osmProj.ToGps(e, n);
                _currentOsmData.StartFinish = new TrackPoint(lat, lon);
                _currentOsmData.SfAnchorFraction = fit.SfFraction;
                _currentOsmData.SfAnchorReversed = fit.Reversed;
                _currentOsmData.SfAnchorSource = "fit";
                RedrawLayoutMarkers();
            }
            if (persist && sfChanged)
                _trackDataService.StoreAnchor(fit.TrackName, config, fit.SfFraction, fit.Reversed, "fit");
        }

        if (persist) TrackGeoFitStore.Save(fit, config);
        _lastStatusMessage = $"Position locked to track (±{fit.MedianResidualM:F0} m)";
    }

    /// <summary>
    /// Place every other car: world position through the geo-fit (all games with world
    /// coordinates; snapped to the centerline unless in the pits), else the car's own
    /// lap fraction along the layout (LMU, R3E). EVO opponents need the fit — the game
    /// publishes no per-car lap position.
    /// </summary>
    private void UpdateOpponents(IReadOnlyList<CarPosition>? cars, bool layoutReady)
    {
        if (ReferenceEquals(cars, _lastCars) && !_opponentsDirty) return;
        _lastCars = cars;
        _opponentsDirty = false;
        _opponentMarkers.Clear();
        _carGps.Clear();

        if (cars != null && layoutReady)
        {
            foreach (var car in cars)
            {
                if (car.IsPlayer)
                {
                    if (_hasLastPlaced)
                    {
                        var (pLat, pLon) = _osmProj.ToGps(_lastPlacedE, _lastPlacedN);
                        _carGps[car.Id] = (pLat, pLon);
                    }
                    continue;
                }
                double e, n;
                if (_geoFit != null)
                {
                    var (lat, lon) = _geoFit.WorldToGps(car.X, car.Z);
                    (e, n) = _osmProj.ToLocal(lat, lon);
                    if (!car.InPits)
                    {
                        var (se, sn, _, dist) = NearestOnLayout(e, n);
                        if (dist <= SnapMaxM) { e = se; n = sn; }
                    }
                }
                else if (car.Npos > 0f && !car.InPits)
                {
                    (e, n, _) = GetPositionOnOsmTrack(car.Npos);
                }
                else
                {
                    continue;
                }
                var (mLat, mLon) = _osmProj.ToGps(e, n);
                _carGps[car.Id] = (mLat, mLon);
                uint fill = CarClassPalette.ArgbFor(CarClasses.Categorize(car.CarClass), car.CarClass);
                // Position inside the disc (race number stays in the Field list). Practice/qualifying
                // positions follow best laps, not running order.
                string label = car.Place > 0 ? car.Place.ToString() : car.Number;
                _opponentMarkers.Add(new MapCarMarker(mLat, mLon, label,
                    car.InPits, fill, car.Id == _selectedCarId, car.Flag));
            }
        }
        MapCtrl.UpdateOpponents(_opponentMarkers);
    }

    private void RedrawLayoutMarkers()
    {
        if (_currentOsmData?.TrackLayout == null) return;
        MapCtrl.SetGpsTrackOutline(_currentOsmData.TrackLayout, _currentOsmData.Corners, _currentOsmData.SectorBoundaries);
        if (_currentOsmData.Pit != null) MapCtrl.AddPitMarkers(_currentOsmData.Pit);
        if (_currentOsmData.StartFinish != null) MapCtrl.AddStartFinishMarker(_currentOsmData.StartFinish);
    }

    // ── Layout geometry (local meters) ───────────────────────────────────────

    /// <summary>Compass heading (0 = north, clockwise) of segment idx → idx+1.</summary>
    private double SegmentCompass(int idx)
    {
        int n = _osmEn.Length;
        var a = _osmEn[(idx % n + n) % n];
        var b = _osmEn[(idx + 1) % n];
        return Math.Atan2(b.e - a.e, b.n - a.n);
    }

    private double MotionCompass(double e, double n)
    {
        if (_hasLastPlaced)
        {
            double de = e - _lastPlacedE, dn = n - _lastPlacedN;
            if (de * de + dn * dn > 1.0) return Math.Atan2(de, dn);
        }
        return _lastCompass;
    }

    private void RememberPlaced(double e, double n, double compass)
    {
        _lastPlacedE = e;
        _lastPlacedN = n;
        _lastCompass = compass;
        _hasLastPlaced = true;
    }

    /// <summary>Nearest point on the closed layout polyline: (point, segment index, distance m).</summary>
    private (double e, double n, int seg, double dist) NearestOnLayout(double e, double n)
    {
        int count = _osmEn.Length;
        double bestD2 = double.MaxValue, be = e, bn = n;
        int bestSeg = 0;
        for (int i = 0; i < count; i++)
        {
            var a = _osmEn[i];
            var b = _osmEn[(i + 1) % count];
            double sx = b.e - a.e, sy = b.n - a.n;
            double l2 = sx * sx + sy * sy;
            double u = l2 > 1e-9 ? Math.Clamp(((e - a.e) * sx + (n - a.n) * sy) / l2, 0, 1) : 0;
            double pe = a.e + sx * u, pn = a.n + sy * u;
            double d2 = (e - pe) * (e - pe) + (n - pn) * (n - pn);
            if (d2 < bestD2) { bestD2 = d2; be = pe; bn = pn; bestSeg = i; }
        }
        return (be, bn, bestSeg, Math.Sqrt(bestD2));
    }

    private int FindNearestOsmIndex(double e, double n)
    {
        int nearest = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _osmEn.Length; i++)
        {
            double de = e - _osmEn[i].e, dn = n - _osmEn[i].n;
            double d = de * de + dn * dn;
            if (d < bestDist) { bestDist = d; nearest = i; }
        }
        return nearest;
    }

    private int FindNearestOsmIndexCached(double e, double n)
    {
        // Reuse the previous result when the car hasn't moved much (~1 m) to avoid
        // a full O(n) layout scan on every tick.
        if (_lastNearestIdx >= 0 && _lastNearestIdx < _osmEn.Length)
        {
            double de = e - _lastNearestE, dn = n - _lastNearestN;
            if (de * de + dn * dn < 1.0) return _lastNearestIdx;
        }
        _lastNearestIdx = FindNearestOsmIndex(e, n);
        _lastNearestE = e;
        _lastNearestN = n;
        return _lastNearestIdx;
    }

    /// <summary>
    /// Walk backward from a layout index to a point on a straight section (the
    /// start/finish line is on the straight before T1, not at the corner apex).
    /// </summary>
    private int FindStraightPointFrom(int startIdx)
    {
        int n = _osmEn.Length;
        if (n < 5) return startIdx;
        const double maxWalkM = 200.0; // don't walk more than 200 m from the corner

        if (!IsCornerAt(startIdx))
            return startIdx;

        int idx = startIdx;
        double walked = 0;
        for (int steps = 0; steps < n; steps++)
        {
            int prev = (idx - 1 + n) % n;
            double de = _osmEn[idx].e - _osmEn[prev].e, dn = _osmEn[idx].n - _osmEn[prev].n;
            walked += Math.Sqrt(de * de + dn * dn);
            if (walked > maxWalkM) break;
            if (!IsCornerAt(prev))
                return prev;
            idx = prev;
        }
        return idx;
    }

    /// <summary>High curvature at a layout point (&gt; ~7° heading change).</summary>
    private bool IsCornerAt(int idx)
    {
        int n = _osmEn.Length;
        var p = _osmEn[(idx - 1 + n) % n];
        var c = _osmEn[idx];
        var q = _osmEn[(idx + 1) % n];
        double dx1 = c.e - p.e, dy1 = c.n - p.n;
        double dx2 = q.e - c.e, dy2 = q.n - c.n;
        double cross = Math.Abs(dx1 * dy2 - dy1 * dx2);
        double dot = dx1 * dx2 + dy1 * dy2;
        return Math.Atan2(cross, dot + 1e-10) > 0.12;
    }

    /// <summary>Cumulative distance (m) along the layout at the projection of a local point.</summary>
    private double InterpolateCumDistAt(double e, double n)
    {
        var (_, _, seg, _) = NearestOnLayout(e, n);
        int count = _osmEn.Length;
        var a = _osmEn[seg];
        var b = _osmEn[(seg + 1) % count];
        double sx = b.e - a.e, sy = b.n - a.n;
        double l2 = sx * sx + sy * sy;
        double u = l2 > 1e-9 ? Math.Clamp(((e - a.e) * sx + (n - a.n) * sy) / l2, 0, 1) : 0;
        return _osmCumDist[seg] + u * Math.Sqrt(l2);
    }

    private int IndexAtArc(double arc)
    {
        int idx = 0;
        for (int i = 0; i < _osmCumDist.Length; i++)
            if (_osmCumDist[i] <= arc) idx = i;
        return idx;
    }

    private (double e, double n) PointAtArc(double target)
    {
        var (e, n, _) = PositionAtArc(target);
        return (e, n);
    }

    /// <summary>Point + segment compass heading at a cumulative distance (m) along the closed layout.</summary>
    private (double e, double n, double compass) PositionAtArc(double target)
    {
        int count = _osmEn.Length;
        target %= _osmTotalDist;
        if (target < 0) target += _osmTotalDist;

        int seg = count - 1; // closing segment last → first
        for (int i = 1; i < count; i++)
        {
            if (_osmCumDist[i] >= target) { seg = i - 1; break; }
        }
        double segStart = _osmCumDist[seg];
        double segLen = (seg == count - 1 ? _osmTotalDist : _osmCumDist[seg + 1]) - segStart;
        var a = _osmEn[seg];
        var b = _osmEn[(seg + 1) % count];
        double t = segLen > 1e-9 ? (target - segStart) / segLen : 0;
        return (a.e + (b.e - a.e) * t, a.n + (b.n - a.n) * t, Math.Atan2(b.e - a.e, b.n - a.n));
    }

    /// <summary>Position on the OSM layout from game Npos (0..1 where 0 = start/finish).</summary>
    private (double e, double n, double compass) GetPositionOnOsmTrack(float npos)
    {
        npos -= (float)Math.Floor(npos);
        // When the OSM loop order opposes the driving direction, traverse backwards.
        double target = _osmReversed
            ? _osmSfCumDist - npos * _osmTotalDist
            : _osmSfCumDist + npos * _osmTotalDist;
        var (e, n, compass) = PositionAtArc(target);
        return (e, n, FlipHeadingForDirection(compass));
    }

    /// <summary>
    /// When the OSM loop order opposes the driving direction, segment headings
    /// point backwards — rotate by π so the car arrow faces travel direction.
    /// </summary>
    private double FlipHeadingForDirection(double h)
    {
        if (!_osmReversed) return h;
        h += Math.PI;
        if (h > Math.PI) h -= 2 * Math.PI;
        return h;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Diagnostic log: raw Npos from game, game coords, computed GPS, and OSM layout metadata.
    /// </summary>
    private void LogPos(float npos, float carX, float carZ, double carLat, double carLon, string trackName, string placement)
    {
        try
        {
            string sfInfo = "sf=none";
            string layoutInfo = "layout=0";
            if (_currentOsmData?.TrackLayout is { Count: > 2 } layout)
            {
                layoutInfo = $"layout={layout.Count}";
                if (_currentOsmData.StartFinish != null)
                    sfInfo = $"sf=({_currentOsmData.StartFinish.Latitude:F5},{_currentOsmData.StartFinish.Longitude:F5})";
            }
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] npos={npos:F4} game=({carX:F1},{carZ:F1}) " +
                       $"gps=({carLat:F5},{carLon:F5}) pos={placement} {sfInfo} sfIdx={_osmStartFinishIndex} anchor={_anchorSource}" +
                       $" sfDist={_osmSfCumDist:F1}/{_osmTotalDist:F1} m {layoutInfo} {_fitStatus} track={trackName}";
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

    // ── OSM data ─────────────────────────────────────────────────────────────

    private async Task LoadOsmDataAsync(string trackName, float liveLat = 0f, float liveLon = 0f)
    {
        try
        {
            // Live game GPS first (EVO reports real lat/lon for ANY track incl. mods),
            // then gazetteer/Nominatim inside the service.
            double? lat = null, lon = null;
            string? config = null;
            double officialLengthM = 0;
            if (liveLat != 0 || liveLon != 0)
            {
                lat = liveLat;
                lon = liveLon;
            }
            else if (_lastKnownLat != 0 || _lastKnownLon != 0)
            {
                lat = _lastKnownLat;
                lon = _lastKnownLon;
            }
            if (DataContext is MainViewModel vm)
            {
                if (!lat.HasValue && (vm.TrackLatitude != 0 || vm.TrackLongitude != 0))
                {
                    lat = vm.TrackLatitude;
                    lon = vm.TrackLongitude;
                }
                config = string.IsNullOrWhiteSpace(vm.DetectedTrackConfig) ? null : vm.DetectedTrackConfig;
                officialLengthM = vm.OfficialTrackLengthM;
            }
            // A recorded lap of THIS track lets the network tier confirm its loop by shape.
            var map = _currentTrackMap;
            List<TrackWaypoint>? lap = map != null && map.Waypoints.Count >= 50
                && string.Equals(map.TrackName, trackName, StringComparison.OrdinalIgnoreCase)
                ? map.Waypoints.ToList() : null;
            await _trackDataService.LoadTrackDataAsync(trackName, lap, lat, lon, false, config, officialLengthM,
                LiveOfficialLength());
            AfterLoad(trackName);
        }
        catch { }
    }

    /// <summary>
    /// The game's lap length read at decision time: LMU only knows it once the scoring header
    /// resolves, which can be after the layout fetch started (a fetch with no length picked the
    /// wrong Le Mans layout on 2026-10-20). Plain double read — safe from the fetch thread.
    /// </summary>
    private Func<double> LiveOfficialLength()
    {
        var vm = DataContext as MainViewModel;
        string track = _lastLoadedTrack ?? "";
        return () =>
        {
            double official = vm?.OfficialTrackLengthM ?? 0;
            if (official > 500) return official;
            // Games that report no lap length (ACC documents trackSPlineLength as unused) fall
            // back to a recorded lap of this track, so length checks and the network tier work.
            var map = _currentTrackMap;
            return map != null && map.TrackLengthM > 500
                && string.Equals(map.TrackName, track, StringComparison.OrdinalIgnoreCase)
                ? map.TrackLengthM : 0;
        };
    }

    /// <summary>A load that produced nothing raises no update event — show the empty state here.</summary>
    private void AfterLoad(string trackName)
    {
        if (trackName != _lastLoadedTrack) return;
        if (_trackDataService.CurrentTrackData?.TrackLayout is not { Count: > 3 } && _currentOsmData?.TrackLayout is not { Count: > 3 })
            ShowLayoutUnavailable();
    }

    private static string DataSourceLabelText(TrackDataSource src) => src switch
    {
        TrackDataSource.OsmRelation => "Relation",
        TrackDataSource.OsmBoundingBox => "OSM BBox",
        TrackDataSource.Recorded => "Recorded",
        TrackDataSource.OpenRacetrackDb => "Open Racetrack DB",
        TrackDataSource.OsmNetwork => "OSM network",
        _ => "?"
    };

    private void OnTrackDataUpdated(TrackDetailedInfo data)
    {
        Dispatcher.Invoke(() =>
        {
            // A late answer for a track we already left must not replace the current one.
            if (!string.IsNullOrEmpty(_lastLoadedTrack) && !string.IsNullOrEmpty(data.TrackName)
                && !data.TrackName.Contains(_lastLoadedTrack, StringComparison.OrdinalIgnoreCase)
                && !_lastLoadedTrack.Contains(data.TrackName, StringComparison.OrdinalIgnoreCase))
                return;
            _currentOsmData = data;

            if (data.TrackLayout != null && data.TrackLayout.Count > 3)
            {
                _layoutLoadFailed = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                // Project the layout to local meters and precompute cumulative distances
                // for Npos-based interpolation.
                var pts = data.TrackLayout;
                int n = pts.Count;
                _osmProj = LocalProjection.ForLayout(pts);
                _osmEn = new (double e, double n)[n];
                for (int i = 0; i < n; i++)
                    _osmEn[i] = _osmProj.ToLocal(pts[i].Latitude, pts[i].Longitude);
                _osmCumDist = new double[n];
                for (int i = 1; i < n; i++)
                {
                    double de = _osmEn[i].e - _osmEn[i - 1].e, dn = _osmEn[i].n - _osmEn[i - 1].n;
                    _osmCumDist[i] = _osmCumDist[i - 1] + Math.Sqrt(de * de + dn * dn);
                }
                double ce = _osmEn[0].e - _osmEn[n - 1].e, cn = _osmEn[0].n - _osmEn[n - 1].n;
                _osmTotalDist = _osmCumDist[n - 1] + Math.Sqrt(ce * ce + cn * cn);
                _lastNearestIdx = -1;

                // Npos=0 maps to the layout point closest to the parsed start/finish line.
                _osmStartFinishIndex = 0;
                if (data.StartFinish != null)
                {
                    var (sfE, sfN) = _osmProj.ToLocal(data.StartFinish.Latitude, data.StartFinish.Longitude);
                    _osmStartFinishIndex = FindNearestOsmIndex(sfE, sfN);
                }
                _osmSfCumDist = _osmCumDist[_osmStartFinishIndex];

                // Prefer a persisted solved anchor (fit / shape) over the parse heuristic.
                if (data.SfAnchorFraction >= 0 && data.SfAnchorFraction < 1)
                {
                    _osmReversed = data.SfAnchorReversed;
                    _anchorSource = string.IsNullOrWhiteSpace(data.SfAnchorSource) ? "shape" : data.SfAnchorSource;
                    _osmSfCumDist = data.SfAnchorFraction * _osmTotalDist;
                    _osmStartFinishIndex = IndexAtArc(_osmSfCumDist);
                    // The cache stores the solved fraction, not the point — draw the flag there.
                    var (sfE, sfN) = PointAtArc(_osmSfCumDist);
                    var (sfLat, sfLon) = _osmProj.ToGps(sfE, sfN);
                    data.StartFinish = new TrackPoint(sfLat, sfLon);
                }

                // Log track data load details for diagnosis
                try
                {
                    string sfText = data.StartFinish != null
                        ? $"({data.StartFinish.Latitude:F5},{data.StartFinish.Longitude:F5})"
                        : "none";
                    var line = $"[{DateTime.Now:HH:mm:ss.fff}] TRACKDATA track={data.TrackName} src={data.DataSource} " +
                               $"layout={n} pts totalDist={_osmTotalDist:F0} m sf={sfText} sfIdx={_osmStartFinishIndex} " +
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

                // A previously solved world→GPS fit for this game + track is valid
                // immediately (it does not depend on this session's driving). An existing
                // fit is re-applied so its SF anchor follows a refreshed layout.
                if (_geoFit != null)
                {
                    ApplyGeoFit(_geoFit, persist: false, CurrentConfig());
                }
                else if (!string.IsNullOrEmpty(_lastLoadedTrack))
                {
                    var stored = TrackGeoFitStore.Load(CurrentGameKey(), _lastLoadedTrack, CurrentConfig());
                    if (stored != null)
                    {
                        ApplyGeoFit(stored, persist: false, CurrentConfig());
                        TrackGeoFitStore.Log($"{_lastLoadedTrack} [{stored.Game}] loaded stored fit: {stored}");
                    }
                }

                // Every new layout (including a corrected one) re-fits the view.
                _fitPending = true;
                TryFitPending();
            }
            else
            {
                ShowLayoutUnavailable();
            }
        });
    }

    private void ShowLayoutUnavailable()
    {
        _layoutLoadFailed = true;
        ShowLoading("No layout available",
            $"Neither OpenStreetMap nor Open Racetrack DB has a layout matching {_lastLoadedTrack ?? "this circuit"}. " +
            "Your car is still tracked on the Track Map page once a lap is recorded.");
        // Show the venue, not the whole world, when its location is known.
        if (!string.IsNullOrEmpty(_lastLoadedTrack) && TrackDatabase.LookupTrackLocation(_lastLoadedTrack) is { } venue)
            MapCtrl.CenterOnGps(venue.lat, venue.lon, 15);
    }

    private static string SourceDisplayName(TrackDataSource src) => src switch
    {
        TrackDataSource.OsmRelation => "OpenStreetMap",
        TrackDataSource.OpenRacetrackDb => "Open Racetrack DB",
        TrackDataSource.OsmNetwork => "OpenStreetMap track network",
        TrackDataSource.Recorded => "Recorded lap",
        _ => "Map data",
    };

    // ── Toolbar ──────────────────────────────────────────────────────────────
    // NOTE: IsChecked="True" in XAML fires Checked during InitializeComponent, before the
    // named elements are assigned — every handler ignores that early firing.

    private void OnSatelliteToggled(object sender, RoutedEventArgs e)
    {
        if (MapCtrl == null) return;
        MapCtrl.SetSatelliteVisible(SatelliteToggle.IsChecked != false);
    }

    private void OnCornersToggled(object sender, RoutedEventArgs e)
    {
        if (MapCtrl == null) return;
        MapCtrl.ShowCornerLabels = CornersToggle.IsChecked != false;
    }

    private void OnFieldToggled(object sender, RoutedEventArgs e)
    {
        if (FieldPanel == null) return;
        FieldPanel.Visibility = FieldToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        FollowToggle.IsChecked = false;
        _fitPending = true;
        TryFitPending();
    }

    private void OnLockPillClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DiagPanel.Visibility = DiagPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        DiagText.Text = _diagText;
    }

    /// <summary>Field row click: highlight that car on the map and pan to it (click again to clear).</summary>
    private void OnFieldRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FieldRow row) return;
        _selectedCarId = _selectedCarId == row.Id ? int.MinValue : row.Id;
        _opponentsDirty = true;
        if (_selectedCarId != int.MinValue && _carGps.TryGetValue(row.Id, out var gps))
        {
            FollowToggle.IsChecked = false;
            MapCtrl.PanToGps(gps.lat, gps.lon);
        }
    }

    private async void OnRefreshData(object sender, RoutedEventArgs e)
    {
        var trackName = _lastLoadedTrack;
        if (string.IsNullOrEmpty(trackName)) return;

        // Disable button during refresh to prevent double-click
        RefreshDataBtn.IsEnabled = false;
        ShowLoading($"Refreshing {trackName}", "Re-downloading the circuit layout…");
        _layoutLoadFailed = false;

        try
        {
            // Delete cache files so the next load gets fresh data
            _trackDataService.DeleteCache(trackName);

            // Live game GPS first, then gazetteer (service tries Nominatim last).
            double? centerLat = null, centerLon = null;
            string? config = null;
            double officialLengthM = 0;
            if (_lastKnownLat != 0 || _lastKnownLon != 0)
            {
                centerLat = _lastKnownLat;
                centerLon = _lastKnownLon;
            }
            if (DataContext is MainViewModel vm)
            {
                if (!centerLat.HasValue && (vm.TrackLatitude != 0 || vm.TrackLongitude != 0))
                {
                    centerLat = vm.TrackLatitude;
                    centerLon = vm.TrackLongitude;
                }
                config = string.IsNullOrWhiteSpace(vm.DetectedTrackConfig) ? null : vm.DetectedTrackConfig;
                officialLengthM = vm.OfficialTrackLengthM;
            }
            if (!centerLat.HasValue)
            {
                var loc = TrackDatabase.LookupTrackLocation(trackName);
                if (loc != null)
                {
                    centerLat = loc.Value.lat;
                    centerLon = loc.Value.lon;
                }
            }

            // Force a fresh fetch bypassing cache (joins a load already in flight)
            _trackDataService.DeleteCache(trackName, config);
            _fitPending = true;
            await _trackDataService.ForceRefreshAsync(trackName, centerLat, centerLon, config, officialLengthM,
                LiveOfficialLength());
            AfterLoad(trackName);
        }
        catch (Exception ex)
        {
            ShowLoading("Refresh failed", ex.Message);
        }
        finally
        {
            RefreshDataBtn.IsEnabled = true;
        }
    }
}
