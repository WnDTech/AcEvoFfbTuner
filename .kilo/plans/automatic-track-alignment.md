# Automatic Track Alignment Using Corner Features

## Overview
Improve the reliability of overlaying recorded track layouts on satellite imagery by using corner-based automatic alignment. This leverages the rich corner data already computed by the track analysis system to create more accurate alignments than the current bounding-box-center method.

## Current Alignment Limitation
The existing alignment in SatelliteMapService uses only the track's bounding box center:
- `_gameCenterX = (minX + maxX) / 2f`
- `_gameCenterZ = (minZ + maxZ) / 2f`
This assumes the geographic center corresponds to the bounding box center, which may not hold due to:
- Offset between track coordinate origin and geographic center
- Rotation mismatches between game coordinates and true north
- Non-uniform track distribution in coordinate space

## Solution: Corner-Based Automatic Alignment
Use detected corners as ground control points (GCPs) to compute a more accurate similarity transform (translation, rotation, scale) between game coordinates and real-world GPS coordinates.

### Key Insight
The TrackMap system already computes:
- Ordered list of corners with angles, types, and positions
- Pit entry/exit points
- Start/finish line detection
These provide distinctive, repeatable features for alignment.

## Implementation Plan

### 1. Data Structures
```csharp
public class GroundControlPoint
{
    public float GameX { get; set; }    // from track waypoint (meters)
    public float GameZ { get; set; }    // from track waypoint (meters)
    public double Latitude { get; set; } // real-world (degrees)
    public double Longitude { get; set; } // real-world (degrees)
    public string PointId { get; set; } // e.g., "C3Apex", "PitEntry"
    public float Weight { get; set; } = 1.0f; // for weighted least squares
}

public class TrackAlignment
{
    public string TrackName { get; set; } = "";
    public List<GroundControlPoint> Points { get; set; } = new();
    public string Method { get; set; } = "Unknown"; // "CornerMatch", "Manual", "AutoSave"
    public DateTime CreatedAt { get; set; }
    public float ErrorResidual { get; set; } // RMS error in meters
}
```

### 2. Alignment Database Service
Create `TrackAlignmentService` (static class) with:
- Storage: JSON files in `AppData/TrackAlignments/`
- Methods:
  - `LoadAlignment(string trackName) : TrackAlignment?`
  - `SaveAlignment(TrackAlignment alignment) : void`
  - `DeleteAlignment(string trackName) : void`

### 3. TrackMap Enhancements
Add to `TrackMap` class:
```csharp
// Returns list of corner points with identifiers
public List<(float X, float Z, string PointId, float AngleDeg)> GetAlignedPoints()
{
    var points = new List<(float X, float Z, string PointId, float AngleDeg)>();
    if (Corners == null || Corners.Count == 0) return points;
    
    var cumDist = GetCumulativeDistances();
    for (int i = 0; i < Corners.Count; i++)
    {
        var corner = Corners[i];
        var wp = Waypoints[corner.ApexWaypointIndex];
        points.Add((wp.X, wp.Z, $"C{corner.CornerNumber}", corner.TotalAngleDeg));
    }
    return points;
}

public (float X, float Z) GetPitEntryPoint() => 
    PitEntry != null ? (PitEntry.X, PitEntry.Z) : (0, 0);
    
public (float X, float Z) GetPitExitPoint() => 
    PitExit != null ? (PitExit.X, PitExit.Z) : (0, 0);
```

### 4. Alignment Computation
In `TrackAlignmentService`, add:
```csharp
public static bool TryComputeTransform(
    List<GroundControlPoint> points, 
    out float translationX, out float translationZ, 
    out float rotationRad, out float scale)
{
    // Implement similarity transform solving using least squares
    // Returns true if successful with sufficient points (>=2)
    // translationX/Z: east/north meters offset
    // rotationRad: radians (game to true north)
    // scale: unit scale (should be ~1.0)
}
```

### 5. Integration Points

#### A. Automatic Alignment Database Population
In `MainViewModel.StaticDataReceived` (after setting initial TrackLatitude/Longitude/TrackRotation):
```csharp
// After setting initial geo-reference from calibration/static/lookup
if (IsTrackMapAvailable && _telemetryLoop.MapBuilder.CurrentMap?.Corners?.Count >= 3)
{
    // If we have a good track map and recent calibration, save it to alignment DB
    var map = _telemetryLoop.MapBuilder.CurrentMap;
    if (map != null)
    {
        var alignment = CreateAlignmentFromCurrentState(map);
        if (alignment != null && alignment.Points.Count >= 3)
        {
            TrackAlignmentService.SaveAlignment(alignment);
        }
    }
}
```

#### B. Automatic Alignment Loading
In `MainViewModel.StaticDataReceived` (before trying static data lookup):
```csharp
// Try to load saved alignment first
var alignment = TrackAlignmentService.LoadAlignment(trackName);
if (alignment != null && alignment.Points.Count >= 2)
{
    if (TrackAlignmentService.TryComputeTransform(
        alignment.Points, out var tx, out var tz, out var rot, out var scale))
    {
        // Convert transform to geo-reference
        // This requires solving: given game points -> lat/lon, what lat/lon/rot produces this?
        // For simplicity, we can use one point to set center, and rotation from transform
        if (alignment.Points.Count > 0)
        {
            var pt = alignment.Points[0];
            // The game point (pt.GameX, pt.GameZ) should correspond to (pt.Latitude, pt.Longitude)
            // We need to find what geo-reference makes this true
            // This is complex; for v1, we can approximate or use iterative refinement
            
            // Simpler approach: store the full transform in alignment and apply it directly
            // in SatelliteMapService.GameToGps (requires modifying that method)
        }
    }
}
```

#### C. Refinement: Direct Transform Application (Alternative Approach)
Modify `SatelliteMapService.GameToGps` to accept an optional pre-computed transform:
```csharp
// Add fields to SatelliteMapService:
private float? _alignmentTx, _alignmentTz, _alignmentRad, _alignmentScale;

// In SetGeoReference, reset these to null
// Add method: SetAlignmentTransform(float tx, float tz, float rad, float scale)

// In GameToGps:
if (_alignmentTx.HasValue && _alignmentTz.HasValue && _alignmentRad.HasValue)
{
    // Apply alignment transform first, then use existing _gameCenterX/Z logic
    // This gives us full control over the alignment
}
```

This approach is cleaner: we compute the transform elsewhere and inject it into the service.

### 6. Crowd-sourcing the Alignment Database
- When user exits manual calibration mode, offer to save the current alignment to the database
- UI: [☑] Save this alignment for future use on this track
- Automatically save high-quality alignments after user confirms they're correct

### 7. UI Enhancements
In `TrackMapPage.xaml`:
```xml
<!-- Add to status bar -->
<TextBlock x:Name="TrackMapAlignmentStatus" Text="Alignment: None" 
           FontSize="12" Foreground="#FF8B949E" Margin="4,0,0,0" />
```

In `TrackMapPage.xaml.cs`:
- Update `TrackMapAlignmentStatus.Text` based on alignment source/method
- Show residual error if available: "Alignment: Auto (2.3m error)"

### 8. Implementation Order
1. Create data structures and alignment service (infrastructure)
2. Add TrackMap methods to extract points
3. Implement automatic saving after good manual calibrations
4. Implement automatic loading from alignment database
5. Add UI indicators
6. (Optional) Add corner matching for unknown tracks

## Expected Benefits
- More reliable alignment for tracks user has driven before (after first manual calibration)
- Better alignment quality than bounding-box-center method
- Database improves over time with more user contributions
- Works even if static GPS data is missing or inaccurate
- Leverages existing corner detection and analysis systems

## Risks and Mitigations
| Risk | Mitigation |
|------|------------|
| Poor corner detection | Require minimum 3 corners with sufficient angle (>5°) for alignment |
| Incorrect corner matching | Use weighted least squares; allow user to override manually |
| Database bloat | Limit to recent alignments; provide cleanup option |
| Performance impact | Compute alignment only when track is completed, not every frame |

## Files to Modify
1. `src\AcEvoFfbTuner.Core\TrackMapping\TrackMap.cs` - Add point extraction methods
2. `src\AcEvoFfbTuner.Core\TrackMapping\SatelliteMapService.cs` - Add alignment transform fields/methods
3. `src\AcEvoFfbTuner.Core\TrackMapping\TrackAlignmentService.cs` - New service class
4. `src\AcEvoFfbTuner.Views\Pages\TrackMapPage.xaml` - Add alignment status UI
5. `src\AcEvoFfbTuner.Views\Pages\TrackMapPage.xaml.cs` - Use alignment service, update UI
6. `src\AcEvoFfbTuner.ViewModels\MainViewModel.cs` - Integrate alignment logic