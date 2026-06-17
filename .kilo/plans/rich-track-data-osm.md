# Rich Track Data from OpenStreetMap

## Goal
Enhance the Track Map with **real corner names, real corner numbers, pit entry/exit markers, and start/finish line** sourced from OpenStreetMap (OSM). This replaces the current auto-detected "T1, T2..." naming with actual corner names like "Eau Rouge", "La Source", "Maggotts", etc.

## The Source: OpenStreetMap
OSM has comprehensive race track data using tags:
- `highway=raceway` — the track layout way
- `service=pit_lane` — pit lane way
- `raceway=start-finish`, `raceway=start`, `raceway=finish` — start/finish nodes
- Named nodes on the raceway way — corner names, e.g. `name="Eau Rouge"`

The **Overpass API** (`https://overpass-api.de/api/interpreter`) lets us query this data:
```
[out:json];
area[name="Circuit de Spa-Francorchamps"]->.a;
way(area.a)[highway=raceway];
out body geom;
>;
out skel qt;
```

## Implementation Plan

### Phase 1: Data Model & Fetching

#### 1A. Extend the data model
Add to existing `TrackDatabase.cs` or new file:

```csharp
public class TrackCornerInfo
{
    public int Number { get; set; }       // Real corner number (1, 2, 3...)
    public string Name { get; set; } = ""; // Real name: "Eau Rouge"
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class TrackPitInfo
{
    public double EntryLatitude { get; set; }
    public double EntryLongitude { get; set; }
    public double ExitLatitude { get; set; }
    public double ExitLongitude { get; set; }
    public List<(double lat, double lon)>? Layout { get; set; }
}

public class TrackDetailedInfo
{
    public string TrackName { get; set; } = "";
    public List<TrackCornerInfo> Corners { get; set; } = new();
    public TrackPitInfo? Pit { get; set; }
    public (double lat, double lon)? StartFinish { get; set; }
    public DateTime FetchedAt { get; set; }
}
```

#### 1B. Create `TrackOsmService`
New file: `src/AcEvoFfbTuner.Core/TrackMapping/TrackOsmService.cs`

```csharp
public static class TrackOsmService
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackData");

    // Fetch track data from OSM Overpass API
    public static async Task<TrackDetailedInfo?> FetchTrackDataAsync(string trackName, 
        IList<TrackWaypoint>? waypoints = null);

    // Load cached data
    public static TrackDetailedInfo? LoadCached(string trackName);

    // Save to cache
    public static void SaveCache(string trackName, TrackDetailedInfo info);

    // Build the Overpass query from track name or GPS bounds
    private static string BuildQuery(string trackName, double? centerLat, double? centerLon);
}
```

#### 1C. Overpass Query Strategy
For each track, build an Overpass query that:
1. Searches by track name (using OSM `name` tag)
2. Falls back to GPS bounding box if name search fails
3. Returns: raceway way geometry, named nodes (corners), pit lane ways, start/finish nodes

If we have waypoints from a recorded lap, we can compute the GPS bounding box using the current GeoReference transform and use that to narrow the OSM search.

### Phase 2: Corner Matching

#### 2A. Sequential Matching (primary method)
Auto-detected corners are already ordered along the lap (0→Npos→100%). OSM corners are also ordered along the raceway way. Match them in sequence:

```
Auto-detected corners:  C1, C2, C3, C4, C5...
OSM corners:            "La Source", "Eau Rouge", "Raidillon", "Les Combes", "Bruxelles"...
Match:                  C1→"La Source", C2→"Eau Rouge", C3→"Raidillon"...
```

If corner counts differ, use GPS proximity matching:
- Convert each OSM corner's GPS coords → game coords (using inverse of GameToGps)
- Match each auto-detected corner to its nearest OSM corner

#### 2B. Update `TrackCorner.DisplayName`
Add a `RealName` property to `TrackCorner`:
```csharp
public string? RealName { get; set; }
public string DisplayName => RealName ?? $"T{CornerNumber}";
```

### Phase 3: Loading & Caching Flow

**On app startup / track detection:**
1. `MainViewModel.StaticDataReceived` fires with track name
2. `TrackOsmService` checks cache for that track
3. If cached: load corner data, match to auto-detected corners
4. If not cached: start background fetch from OSM Overpass API
5. On fetch complete: save to cache, apply corner names

**On each lap completion:**
1. `TrackMapCompleted` event fires
2. TrackMap.Analyze() detects corners
3. Load cached OSM detailed info for this track
4. Match OSM corners to auto-detected corners
5. Update TrackCorner.RealName properties

### Phase 4: UI Enhancements

#### 4A. Corner Info Panel (Row 1)
Currently shows: "T1", "S1"
Update to show: "T1 — La Source" (with real name in a different color)

**TrackMapPage.xaml changes:**
```xml
<!-- Current: -->
<TextBlock x:Name="TrackMapCorner" Text="--" />
<!-- Replace with stacked real + auto names: -->
<StackPanel>
    <TextBlock x:Name="TrackMapCornerName" Text="--" FontWeight="Bold" FontSize="15" Foreground="#FFFFD600" />
    <TextBlock x:Name="TrackMapCornerAuto" Text="--" FontSize="11" Foreground="#FF8B949E" />
</StackPanel>
```

#### 4B. Pit Entry/Exit Markers
On satellite map, draw markers for:
- Pit entry (red marker)
- Pit exit (green marker)
- Pit lane path (dashed line)

In `MapsuiMapControl` or `TrackMapPage`:
```csharp
if (pitInfo != null) {
    SatelliteMapCtrl.AddMarker(pitInfo.EntryLat, pitInfo.EntryLon, "Pit Entry", Brushes.Red);
    SatelliteMapCtrl.AddMarker(pitInfo.ExitLat, pitInfo.ExitLon, "Pit Exit", Brushes.Green);
}
```

#### 4C. Start/Finish Line
Draw a yellow marker on the start/finish line position.

#### 4D. Corner Markers on Map
Optionally, show OSM corner name labels at their GPS positions on the satellite map, so users can see corner names overlaid on the track.

### Phase 5: Database Population

#### 5A. Cache-only approach
- OSM data is fetched on-demand and cached
- No manual database needed
- Works for any OSM-mapped track worldwide

#### 5B. Pre-populated fallback
For tracks where OSM data is incomplete, we can manually add corner data to `tracks.json`:
```json
{
  "name": "spa",
  "corners": [
    { "number": 1, "name": "La Source", "lat": 50.4372, "lon": 5.9708 },
    { "number": 2, "name": "Eau Rouge", "lat": 50.4358, "lon": 5.9725 },
    ...
  ],
  "pit": {
    "entryLat": 50.4350, "entryLon": 5.9680,
    "exitLat": 50.4385, "exitLon": 5.9720
  }
}
```

### Phase 6: OSM → Game Coordinate Conversion

Add `GpsToGame` method to `SatelliteMapService` (inverse of `GameToGps`):

```csharp
public (float gameX, float gameZ) GpsToGame(double latitude, double longitude)
{
    if (!_transformComputed || !HasGeoData)
        return (0, 0);

    double dLat = latitude - TrackCenterLatitude;
    double dLon = longitude - TrackCenterLongitude;

    double northMeters = dLat * 111320.0;
    double eastMeters = dLon * 111320.0 * Math.Cos(TrackCenterLatitude * Math.PI / 180.0);

    double cosR = Math.Cos(_rotationRad);
    double sinR = Math.Sin(_rotationRad);
    double dx = eastMeters * cosR - northMeters * sinR;
    double dz = eastMeters * sinR + northMeters * cosR;

    return ((float)(dx + _gameCenterX), (float)(_gameCenterZ - dz));
}
```

This enables placing pit markers and corner labels at correct positions on the vector map.

## Files to Create/Modify

| File | Action |
|------|--------|
| `src/AcEvoFfbTuner.Core/TrackMapping/TrackOsmService.cs` | **Create** — OSM data fetcher + cache |
| `src/AcEvoFfbTuner.Core/TrackMapping/TrackDatabase.cs` | **Modify** — Add `TrackDetailedInfo`, `TrackCornerInfo`, `TrackPitInfo` |
| `src/AcEvoFfbTuner.Core/TrackMapping/TrackCornerAnalyzer.cs` | **Modify** — Add `RealName` property, fallback in `DisplayName` |
| `src/AcEvoFfbTuner.Core/TrackMapping/SatelliteMapService.cs` | **Modify** — Add `GpsToGame()` method |
| `src/AcEvoFfbTuner/Views/Pages/TrackMapPage.xaml` | **Modify** — Add real corner name display, pit markers UI |
| `src/AcEvoFfbTuner/Views/Pages/TrackMapPage.xaml.cs` | **Modify** — Load OSM data, match corners, draw pit markers |
| `src/AcEvoFfbTuner/ViewModels/MainViewModel.cs` | **Modify** — Call `TrackOsmService` on track detection |
| `src/AcEvoFfbTuner/Data/tracks.json` | **Modify** — Add optional corner/pit data for common tracks |
| `src/AcEvoFfbTuner.Core/AcEvoFfbTuner.Core.csproj` | **Modify** — Ensure `System.Text.Json` is available (already present) |

## Implementation Order

1. Add `GpsToGame()` to `SatelliteMapService`
2. Add data model classes to `TrackDatabase.cs`
3. Create `TrackOsmService` with Overpass API query + cache
4. Update `TrackCorner` with `RealName` property
5. Integrate OSM data loading into `MainViewModel` (on track detection)
6. Update `TrackMapPage.xaml` UI for real corner names
7. Add pit entry/exit markers to satellite map
8. Pre-populate `tracks.json` for top 20 tracks

## Risk Assessment

| Risk | Mitigation |
|------|------------|
| OSM Overpass API rate limits | Cache aggressively; only fetch once per track; use user-agent header |
| OSM data quality varies per track | Fall back to auto-detected names when OSM has no data |
| Network errors / offline | Cache means previously fetched tracks work offline |
| Corner count mismatch | Sequential matching works for ~90% of tracks; GPS proximity fallback |
| Overpass query complexity | Start with simple name-based query, refine as needed |
