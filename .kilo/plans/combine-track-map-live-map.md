# Combine Track Map and Live Map Functionality

## Overview
The ACEVO Telemetry FFB application currently has two separate map functionalities:
1. **Track Map** (`TrackMapPage.xaml.cs`) - Focuses on track analysis, recording, and visualization of track data with vector/satellite modes
2. **Live Map** (`LiveMapPage.xaml.cs`) - Focuses on real-time positioning with satellite imagery, GPS coordinates, and calibration tools

The goal is to combine these into a unified map view that retains the strengths of both functionalities.

## Current Implementation Analysis

### Track Map Features:
- Track visualization (recorded track layout)
- Car position in game coordinates
- Heading indicator
- Recording trail (when recording new track)
- Force heatmap overlay
- Diagnostic markers
- Corner and sector detection/display
- Pit lane visualization
- Track edges display
- Satellite/vector map toggle
- Track statistics (waypoints, corners, sectors, lap data)
- Popout functionality

### Live Map Features:
- Real-time GPS coordinate conversion
- Satellite imagery display (via Mapsui or custom tile rendering)
- Car marker with heading on satellite map
- Calibration tools (manual adjustment, reset, save)
- Center-on-car tracking
- Zoom controls
- Status overlay with alignment information
- GPS position display
- Speed display

## Proposed Solution

Enhance the TrackMapPage to incorporate Live Map features while maintaining its track analysis capabilities:

### 1. UI Enhancements to TrackMapPage
Add Live Map-specific UI elements:
- GPS coordinate display (latitude/longitude)
- Speed display
- Alignment/rotation display
- Enhanced calibration controls (more prominent)
- Status overlay for mapping state
- Improved center-on-car tracking option

### 2. Functional Improvements
- Always show live car position (even when not recording)
- Enhanced satellite map integration with better tile loading
- GPS coordinate display in both game and real-world coordinates
- Unified calibration system that works for both map modes
- Option to show/hide track analysis overlays for cleaner live view

### 3. Navigation Changes
Option 1: Keep both pages but share underlying implementation
Option 2: Merge into single "Map" page in navigation
Option 3: Make TrackMap the primary map view and remove LiveMap page

Recommended: Option 3 - Enhance TrackMapPage to be the unified map view and remove LiveMap navigation entry.

## Implementation Plan

### Phase 1: Enhance TrackMapPage with Live Map Features
1. Add GPS coordinate display elements to TrackMapPage.xaml
2. Add speed display
3. Add alignment display
4. Enhance calibration UI (make controls more accessible)
5. Add status overlay similar to LiveMapPage
6. Improve center-on-car tracking functionality
7. Ensure live car position updates even when not recording

### Phase 2: Refine Integration
1. Unify coordinate systems (game coords vs GPS coords)
2. Ensure calibration works consistently across map modes
3. Optimize performance for real-time updates
4. Add option to simplify UI for pure live mapping mode

### Phase 3: Navigation Update
1. Remove LiveMap page from NavPage enum
2. Update MainWindow.xaml.cs navigation handling
3. Update any references to LiveMap page
4. Ensure TrackMapPage handles all map functionality

### Phase 4: Testing and Refinement
1. Test track recording functionality still works
2. Test satellite map mode with live positioning
3. Test vector map mode with live positioning
4. Test calibration workflows
5. Test UI responsiveness

## Files to Modify
1. `src\AcEvoFfbTuner\Views\Pages\TrackMapPage.xaml` - Add UI elements
2. `src\AcEvoFfbTuner\Views\Pages\TrackMapPage.xaml.cs` - Add functionality
3. `src\AcEvoFfbTuner\ViewModels\MainViewModel.cs` - Add new properties for GPS/speed/alignment
4. `src\AcEvoFfbTuner\Views\MainWindow.xaml.cs` - Update navigation
5. `src\AcEvoFfbTuner\ViewModels\NavPage.cs` - Remove LiveMap entry
6. Potentially update SatelliteMapService or related services if needed

## Dependencies
- Maintains existing track analysis capabilities
- Preserves recording functionality
- Keeps satellite/vector map toggle
- Preserves popout functionality
- Maintains all existing UI customization options

## Risks and Mitigations
- Performance impact: Mitigate by optimizing update frequency and using efficient rendering
- UI clutter: Mitigate by making advanced features collapsible or optional
- Regression in track analysis: Mitigate by thorough testing of existing features
- User confusion: Mitigate by maintaining familiar workflows while adding enhancements

## Success Criteria
1. TrackMapPage shows live car position in both vector and satellite modes
2. GPS coordinates, speed, and alignment are displayed
3. Calibration tools are accessible and functional
4. Track recording and analysis features work unchanged
5. Performance remains acceptable for real-time use
6. Navigation reflects the unified map functionality