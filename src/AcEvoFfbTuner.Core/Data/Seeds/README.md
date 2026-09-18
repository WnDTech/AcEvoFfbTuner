# Offline Track Seed Pack

This folder holds pre-fetched OSM relation JSON files (`*_relation.json`) that ship
as embedded resources so track maps work on first run with **zero internet**.

## File naming

- Bare track: `{track}_relation.json` (e.g. `spa_relation.json`)
- With layout: `{track}__{config}_relation.json` (e.g. `nurburgring__nordschleife_relation.json`)

Names must match `TrackMapKeys.BuildKey(track, config)` + `_relation.json`, sanitized
for file names. Lookup tries the layout-aware name first, then the legacy bare name.

## Generating seeds at release time

1. Run the app (or a small script using `TieredTrackDataProvider`) once per track
   in `Data/tracks.json` with internet access.
2. Copy each `%AppData%/AcEvoFfbTuner/TrackData/{name}_relation.json` here.
3. Rebuild — the `.csproj` embeds `Data/Seeds/*_relation.json` automatically.
4. `TrackOsmRelationService.LoadSeeded()` picks them up before the disk cache.

Seeds must be `CacheVersion == TrackDetailedInfo.CurrentCacheVersion` or they are
ignored on load (disk cache validation still applies to fetched data only; seeds
are validated for layout presence at read time).
