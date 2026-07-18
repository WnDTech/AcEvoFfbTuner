using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

public class TrackOsmService : IDisposable
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackData");

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "trackdata.log");

    private readonly HttpClient _http;
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";
    private DateTime _lastFetchAttempt = DateTime.MinValue;

    public TrackOsmService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner/1.0 (track-data-fetcher)");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.Timeout = TimeSpan.FromSeconds(30);
        Directory.CreateDirectory(CacheDir);
    }

    public Action<string>? StatusLog { get; set; }

    private void Log(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        StatusLog?.Invoke(msg);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    private static void StaticLog(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    public TrackDetailedInfo? LoadCached(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        var path = GetCachePath(trackName);
        if (!File.Exists(path)) { StaticLog($"No cache for {trackName}"); return null; }
        try
        {
            var json = File.ReadAllText(path);
            // Check old format (no "Surface" property)
            if (!json.Contains("\"Surface\""))
            {
                StaticLog($"Old-format cache (no Surface): deleting {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }
            var data = JsonSerializer.Deserialize<TrackDetailedInfo>(json);
            if (data == null) { StaticLog($"Deserialize returned null for {trackName}"); return null; }

            // Check cache version — invalidate if outdated
            if (data.CacheVersion < TrackDetailedInfo.CurrentCacheVersion)
            {
                StaticLog($"Old cache version ({data.CacheVersion} < {TrackDetailedInfo.CurrentCacheVersion}): deleting {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }

            StaticLog($"Cache loaded: {trackName} ({data.Corners.Count} corners, {data.TrackLayout?.Count ?? 0} layout pts)");
            return data;
        }
        catch (Exception ex) { StaticLog($"Cache load error for {trackName}: {ex.Message}"); return null; }
    }

    public void SaveCache(string trackName, TrackDetailedInfo info)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var path = GetCachePath(trackName);
            info.TrackName = trackName;
            info.FetchedAt = DateTime.UtcNow;
            info.CacheVersion = TrackDetailedInfo.CurrentCacheVersion;
            var json = JsonSerializer.Serialize(info);
            File.WriteAllText(path, json);
            Log($"Saved cache: {trackName} ({info.Corners.Count} corners)");
        }
        catch (Exception ex) { Log($"Save cache error: {ex.Message}"); }
    }

    public async Task<TrackDetailedInfo?> FetchTrackDataAsync(string trackName,
        IList<TrackWaypoint>? waypoints = null, double? centerLat = null, double? centerLon = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;

        // Rate limiting: don't retry more than once per 60 seconds
        var timeSinceLastAttempt = DateTime.UtcNow - _lastFetchAttempt;
        if (timeSinceLastAttempt.TotalSeconds < 60 && _lastFetchAttempt != DateTime.MinValue)
        {
            Log($"Rate-limited: skipping fetch for {trackName} (waiting {60 - timeSinceLastAttempt.TotalSeconds:F0}s)");
            return null;
        }
        _lastFetchAttempt = DateTime.UtcNow;

        try
        {
            // Look up GPS from TrackDatabase if not provided
            if (!centerLat.HasValue || !centerLon.HasValue)
            {
                var loc = TrackDatabase.LookupTrackLocation(trackName);
                if (loc.HasValue)
                {
                    centerLat = loc.Value.lat;
                    centerLon = loc.Value.lon;
                }
            }

            var query = BuildQuery(trackName, centerLat, centerLon);
            Log($"Fetching: {trackName} (GPS: {centerLat?.ToString("F3") ?? "?"}, {centerLon?.ToString("F3") ?? "?"})");
            Log($"Query:\n{query}");

            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

            var response = await _http.PostAsync(OverpassUrl, content);
            Log($"HTTP {(int)response.StatusCode} for {trackName}");

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                    Log($"Rate limited by Overpass API - will wait 60s before retry");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log($"Response: {json.Length} bytes for {trackName}");

            if (string.IsNullOrEmpty(json) || json.Length < 50)
            {
                Log($"Empty/too-small response for {trackName}");
                return null;
            }

            var result = ParseOsmResponse(json, trackName);
            if (result != null)
            {
                Log($"Parsed: {result.Corners.Count} corners, pit={(result.Pit != null ? "yes" : "no")} for {trackName}");
            }
            else
            {
                Log($"Failed to parse response for {trackName}");
            }

            return result;
        }
        catch (TaskCanceledException)
        {
            Log($"Timeout for {trackName} (30s)");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            return null;
        }
    }

    private static string BuildQuery(string trackName, double? centerLat, double? centerLon)
    {
        if (centerLat.HasValue && centerLon.HasValue)
        {
            // Use bounding box around the track center - much faster than global search
            double radiusDeg = 0.03; // ~3km radius
            double minLat = centerLat.Value - radiusDeg;
            double maxLat = centerLat.Value + radiusDeg;
            double minLon = centerLon.Value - radiusDeg;
            double maxLon = centerLon.Value + radiusDeg;

            return $"[out:json][timeout:25];\n" +
                   $"(\n" +
                   $"  way({minLat},{minLon},{maxLat},{maxLon})[\"highway\"=\"raceway\"];\n" +
                   $"  way({minLat},{minLon},{maxLat},{maxLon})[\"service\"=\"pit_lane\"];\n" +
                   $");\n" +
                   $"(._;>;);\n" +
                   $"out body geom;";
        }

        // Fallback: name search (slower, global)
        var escaped = trackName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"[out:json][timeout:25];\n" +
               $"(\n" +
               $"  way[\"name\"~\"{escaped}\",i][\"highway\"=\"raceway\"];\n" +
               $");\n" +
               $"(._;>;);\n" +
               $"out body geom;";
    }

    private static TrackDetailedInfo? ParseOsmResponse(string json, string trackName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("elements", out var elements))
            return null;

        var result = new TrackDetailedInfo { TrackName = trackName };

        // Build a map: nodeId -> (lat, lon)
        var nodeMap = new Dictionary<long, (double lat, double lon)>();
        var wayMap = new Dictionary<long, (string? name, string? service, string? highway, List<long> nodes, bool isPit)>();
        var wayGeometries = new Dictionary<long, List<TrackPoint>>(); // Fallback geometry from out body geom
        var racewayNodeIds = new HashSet<long>();
        var surfaceTags = new HashSet<string>();

        foreach (var el in elements.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();

            if (type == "node")
            {
                var id = el.GetProperty("id").GetInt64();
                var lat = el.GetProperty("lat").GetDouble();
                var lon = el.GetProperty("lon").GetDouble();
                nodeMap[id] = (lat, lon);

                // Check for start/finish line
                var tags = el.TryGetProperty("tags", out var t2) ? t2 : default;
                var raceway = TryGetTag(tags, "raceway");
                if (raceway == "start-finish" || raceway == "start" || raceway == "finish")
                    result.StartFinish ??= new TrackPoint(lat, lon);
            }
            else if (type == "way")
            {
                var id = el.GetProperty("id").GetInt64();
                var tags = el.TryGetProperty("tags", out var t) ? t : default;
                var highway = TryGetTag(tags, "highway");
                var service = TryGetTag(tags, "service");
                var racewayTag = TryGetTag(tags, "raceway");
                var name = TryGetTag(tags, "name");

                // Process raceway ways AND pit lane ways (which often have highway=service)
                bool isRaceway = highway == "raceway";
                bool isPitLane = service == "pit_lane" ||
                                 (name != null && name.Contains("Pit Lane", StringComparison.OrdinalIgnoreCase));
                if (!isRaceway && !isPitLane) continue;

                // Extract nodes and geometry
                var nodes = new List<long>();
                var geomPts = new List<TrackPoint>();

                if (el.TryGetProperty("nodes", out var nodesArr))
                {
                    foreach (var n in nodesArr.EnumerateArray())
                    {
                        var nid = n.GetInt64();
                        nodes.Add(nid);
                        if (isRaceway)
                            racewayNodeIds.Add(nid);
                    }
                }

                // Extract inline geometry (available with out body geom)
                if (el.TryGetProperty("geometry", out var geomArr))
                {
                    foreach (var pt in geomArr.EnumerateArray())
                    {
                        geomPts.Add(new TrackPoint(
                            pt.GetProperty("lat").GetDouble(),
                            pt.GetProperty("lon").GetDouble()));
                    }
                }

                if (isRaceway)
                {
                    // Check for start/finish on the way itself
                    if (racewayTag == "start-finish" || racewayTag == "start" || racewayTag == "finish")
                    {
                        double sumLat = 0, sumLon = 0; int cnt = 0;
                        foreach (var gp in geomPts)
                        {
                            sumLat += gp.Latitude;
                            sumLon += gp.Longitude;
                            cnt++;
                        }
                        if (cnt > 0)
                            result.StartFinish ??= new TrackPoint(sumLat / cnt, sumLon / cnt);
                    }
                }

                wayMap[id] = (name, service, highway, nodes, isPitLane);
                if (geomPts.Count > 0)
                    wayGeometries[id] = geomPts;

                // Collect surface type from raceway ways
                var surface = TryGetTag(tags, "surface");
                if (!string.IsNullOrEmpty(surface) && isRaceway)
                    surfaceTags.Add(surface);
            }
        }

        if (wayMap.Count == 0) return null;
        result.Surface = surfaceTags.Count > 0 ? string.Join(", ", surfaceTags) : null;

        // ---------------------------------------------------------------
        // CRITICAL FILTER: Remove ways that are NOT part of this track.
        // We know the track name — anything named "Kart", "Moto", "Rally",
        // "Support", "Disused" etc. does NOT belong to the main circuit.
        // Delete them entirely so they cannot contaminate the layout,
        // corner names, or pit detection.
        // ---------------------------------------------------------------
        var trackNameLower = trackName.ToLowerInvariant();
        var trackTokens = TokenizeTrackName(trackNameLower);
        var foreignWayIds = new List<long>();

        foreach (var kvp in wayMap)
        {
            var wName = kvp.Value.name;
            if (string.IsNullOrEmpty(wName)) continue;

            bool isForeign = IsForeignWay(wName, trackNameLower, trackTokens);
            if (isForeign)
                foreignWayIds.Add(kvp.Key);
        }

        foreach (var id in foreignWayIds)
        {
            var wName = wayMap.TryGetValue(id, out var we) ? we.name : "?";
            wayMap.Remove(id);
            wayGeometries.Remove(id);
            StaticLog($"Removed foreign way {id}: \"{wName}\"");
        }

        if (wayMap.Count == 0)
        {
            StaticLog($"All ways removed from {trackName} after foreign filter");
            return null;
        }

        // Separate pit ways from main track ways
        var pitWayIds = wayMap.Where(kvp => kvp.Value.isPit).Select(kvp => kvp.Key).ToHashSet();
        var mainWayIds = wayMap.Keys.Where(id => !pitWayIds.Contains(id)).ToHashSet();

        // Build node-to-way adjacency for the main track ways only
        var nodeToWays = new Dictionary<long, List<long>>();
        foreach (var wid in mainWayIds)
        {
            var (_, _, _, nodes, _) = wayMap[wid];
            foreach (var nid in nodes)
            {
                if (!nodeToWays.ContainsKey(nid))
                    nodeToWays[nid] = new List<long>();
                nodeToWays[nid].Add(wid);
            }
        }

        // Order the main track ways by walking the circuit chain (depth-first, not BFS)
        var orderedWayIds = new List<long>();
        if (mainWayIds.Count > 0)
        {
            var visited = new HashSet<long>();

            // Find the best starting way: prefer named corner ways with the most nodes.
            // Named ways (Eau Rouge, Raidillon, etc.) are reliably part of the main circuit,
            // while unnamed connector ways could connect anywhere.
            long? current = FindBestStartWay(mainWayIds, wayMap, wayGeometries);
            while (current.HasValue && visited.Add(current.Value))
            {
                orderedWayIds.Add(current.Value);
                var (_, _, _, nodes, _) = wayMap[current.Value];
                if (nodes.Count < 2) { current = null; break; }

                // Find the unvisited neighbor connected to the LAST node of this way
                long lastNode = nodes.Last();
                long? next = null;
                if (nodeToWays.TryGetValue(lastNode, out var connected))
                {
                    foreach (var cwid in connected)
                    {
                        if (!visited.Contains(cwid))
                        {
                            next = cwid;
                            break;
                        }
                    }
                }
                current = next;
            }

            // Walk backward from the first way to get the other half of the circuit
            var reverseWays = new List<long>();
            long? reverse = null;
            var (_, _, _, firstNodes, _) = wayMap[orderedWayIds[0]];
            if (firstNodes.Count >= 2 && nodeToWays.TryGetValue(firstNodes.First(), out var revConnected))
            {
                foreach (var cwid in revConnected)
                {
                    if (!visited.Contains(cwid))
                    {
                        reverse = cwid;
                        break;
                    }
                }
            }

            while (reverse.HasValue && visited.Add(reverse.Value))
            {
                reverseWays.Insert(0, reverse.Value);
                var (_, _, _, nodes, _) = wayMap[reverse.Value];
                if (nodes.Count < 2) { break; }

                long firstNode = nodes.First();
                long? nextRev = null;
                if (nodeToWays.TryGetValue(firstNode, out var revConnected2))
                {
                    foreach (var cwid in revConnected2)
                    {
                        if (!visited.Contains(cwid))
                        {
                            nextRev = cwid;
                            break;
                        }
                    }
                }
                reverse = nextRev;
            }

            // Insert reverse walk before the forward walk
            orderedWayIds.InsertRange(0, reverseWays);
        }

        StaticLog($"Circuit chain: {orderedWayIds.Count} ways in order");

        // Filter out dead-end spur ways
        var circuitWayIds = new List<long>();
        var logDetails = new System.Text.StringBuilder();

        foreach (var wid in orderedWayIds)
        {
            var (_, _, _, nodes, _) = wayMap[wid];
            if (nodes.Count < 2) continue;

            int firstNodeConnections = 0, lastNodeConnections = 0;
            var firstNid = nodes.First();
            var lastNid = nodes.Last();

            foreach (var cwid in orderedWayIds)
            {
                if (cwid == wid) continue;
                var (_, _, _, cnodes, _) = wayMap[cwid];
                if (cnodes.Count > 0)
                {
                    if (cnodes.First() == firstNid || cnodes.Last() == firstNid)
                        firstNodeConnections++;
                    if (cnodes.First() == lastNid || cnodes.Last() == lastNid)
                        lastNodeConnections++;
                }
            }

            if (firstNodeConnections >= 1 && lastNodeConnections >= 1)
                circuitWayIds.Add(wid);
            else
                StaticLog($"Spur/end way filtered: {wid} (firstConn={firstNodeConnections}, lastConn={lastNodeConnections})");
        }

        StaticLog($"Circuit ways: {circuitWayIds.Count} of {orderedWayIds.Count} total ordered ways");
        if (circuitWayIds.Count < 2)
        {
            StaticLog($"Spur filter removed everything! Using all {orderedWayIds.Count} ways.");
            circuitWayIds = orderedWayIds;
        }

        // Build the full track GPS layout
        var fullLayout = new List<TrackPoint>();
        int reversedCount = 0, orphanCount = 0;
        for (int wi = 0; wi < circuitWayIds.Count; wi++)
        {
            var wid = circuitWayIds[wi];
            var (_, _, _, nodes, _) = wayMap[wid];
            if (nodes.Count == 0) continue;

            bool reverse = false;
            if (fullLayout.Count > 0)
            {
                // Use exact node matching to determine direction
                var lastPt = fullLayout[^1];
                var firstNodePt = nodeMap.GetValueOrDefault(nodes[0]);
                var lastNodePt = nodeMap.GetValueOrDefault(nodes[^1]);

                if (firstNodePt != default &&
                    Math.Abs(firstNodePt.lat - lastPt.Latitude) < 1e-8 &&
                    Math.Abs(firstNodePt.lon - lastPt.Longitude) < 1e-8)
                {
                    reverse = false;
                }
                else if (lastNodePt != default &&
                    Math.Abs(lastNodePt.lat - lastPt.Latitude) < 1e-8 &&
                    Math.Abs(lastNodePt.lon - lastPt.Longitude) < 1e-8)
                {
                    reverse = true;
                }
                else
                {
                    // No exact match — orphan way. Determine direction by nearest distance.
                    orphanCount++;
                    double d1 = firstNodePt != default
                        ? (firstNodePt.lat - lastPt.Latitude) * (firstNodePt.lat - lastPt.Latitude)
                        + (firstNodePt.lon - lastPt.Longitude) * (firstNodePt.lon - lastPt.Longitude)
                        : double.MaxValue;
                    double d2 = lastNodePt != default
                        ? (lastNodePt.lat - lastPt.Latitude) * (lastNodePt.lat - lastPt.Latitude)
                        + (lastNodePt.lon - lastPt.Longitude) * (lastNodePt.lon - lastPt.Longitude)
                        : double.MaxValue;
                    if (d2 < d1)
                        reverse = true;

                    // Interpolate bridge points to span the gap smoothly
                    var bridgePt = reverse
                        ? nodeMap.GetValueOrDefault(nodes[^1])
                        : nodeMap.GetValueOrDefault(nodes[0]);
                    if ((bridgePt.lat != 0 || bridgePt.lon != 0))
                    {
                        double gapDist = Math.Sqrt(
                            (bridgePt.lat - lastPt.Latitude) * (bridgePt.lat - lastPt.Latitude) +
                            (bridgePt.lon - lastPt.Longitude) * (bridgePt.lon - lastPt.Longitude));
                        if (gapDist > 0.0005)
                        {
                            int steps = Math.Min(10, (int)(gapDist / 0.0001));
                            for (int s = 1; s <= steps; s++)
                            {
                                double t = (double)s / (steps + 1);
                                fullLayout.Add(new TrackPoint(
                                    lastPt.Latitude + t * (bridgePt.lat - lastPt.Latitude),
                                    lastPt.Longitude + t * (bridgePt.lon - lastPt.Longitude)));
                            }
                        }
                    }
                }
            }

            if (reverse) reversedCount++;
            var nodeList = reverse
                ? ((IEnumerable<long>)nodes).Reverse().ToList()
                : nodes;

            for (int ni = 0; ni < nodeList.Count; ni++)
            {
                var nid = nodeList[ni];
                if (ni == 0 && fullLayout.Count > 0)
                {
                    var last = fullLayout[^1];
                    if (nodeMap.TryGetValue(nid, out var pt) &&
                        Math.Abs(pt.lat - last.Latitude) < 1e-10 &&
                        Math.Abs(pt.lon - last.Longitude) < 1e-10)
                        continue;
                }
                if (nodeMap.TryGetValue(nid, out var point))
                    fullLayout.Add(new TrackPoint(point.lat, point.lon));
            }
        }
        StaticLog($"Layout: {fullLayout.Count} pts from {circuitWayIds.Count} ways ({reversedCount} rev, {orphanCount} orphans)");
        if (fullLayout.Count > 3)
            result.TrackLayout = fullLayout;

        // Extract corner names from the ordered way segments
        int cornerNumber = 0;
        foreach (var wid in circuitWayIds)
        {
            var (name, _, _, nodes, _) = wayMap[wid];
            if (!string.IsNullOrEmpty(name))
            {
                cornerNumber++;
                // Use the midpoint/node average as the corner position
                double sumLat = 0, sumLon = 0;
                int count = 0;
                foreach (var nid in nodes.Take(5)) // use first 5 nodes as estimate
                {
                    if (nodeMap.TryGetValue(nid, out var pt))
                    {
                        sumLat += pt.lat;
                        sumLon += pt.lon;
                        count++;
                    }
                }
                double lat = count > 0 ? sumLat / count : 0;
                double lon = count > 0 ? sumLon / count : 0;

                result.Corners.Add(new TrackCornerInfo
                {
                    Number = cornerNumber,
                    Name = name,
                    Latitude = lat,
                    Longitude = lon
                });
            }
        }

        // Parse pit lane — merge multiple connected segments, filter non-primary pits
        result.Pit = BuildPitLane(pitWayIds, mainWayIds, wayMap, wayGeometries, nodeMap, result.StartFinish);

        // Rotate corners so that T1 is the first corner AFTER start/finish
        TrackPoint? refPoint = result.StartFinish;

        if (refPoint != null && result.Corners.Count > 0 && fullLayout.Count > 0)
        {
            // Find the reference position on the track layout
            int sfIdx = 0;
            double sfBest = double.MaxValue;
            for (int i = 0; i < fullLayout.Count; i++)
            {
                double dlat = fullLayout[i].Latitude - refPoint.Latitude;
                double dlon = fullLayout[i].Longitude - refPoint.Longitude;
                double d = dlat * dlat + dlon * dlon;
                if (d < sfBest) { sfBest = d; sfIdx = i; }
            }

            // For each corner, find its closest layout index
            var cornerIndices = result.Corners.Select(c =>
            {
                int best = 0; double bestD = double.MaxValue;
                for (int i = 0; i < fullLayout.Count; i++)
                {
                    double dlat = fullLayout[i].Latitude - c.Latitude;
                    double dlon = fullLayout[i].Longitude - c.Longitude;
                    double d = dlat * dlat + dlon * dlon;
                    if (d < bestD) { bestD = d; best = i; }
                }
                return (corner: c, layoutIdx: best);
            }).ToList();

            // Find the first corner whose layout index is AFTER the SF (wrapping around)
            var rotIdx = -1;
            for (int offset = 1; offset <= cornerIndices.Count; offset++)
            {
                int testIdx = (sfIdx + offset) % fullLayout.Count;
                // Check if any corner is on or after this layout point
                for (int ci = 0; ci < cornerIndices.Count; ci++)
                {
                    int ciIdx = cornerIndices[ci].layoutIdx;
                    // Check if this corner comes after sfIdx (wrapping)
                    if (ciIdx >= testIdx && (ciIdx - testIdx) < 100)
                    {
                        rotIdx = ci;
                        break;
                    }
                }
                if (rotIdx >= 0) break;
            }

            if (rotIdx > 0)
            {
                result.Corners = result.Corners.Skip(rotIdx)
                    .Concat(result.Corners.Take(rotIdx))
                    .Select((c, i) => { c.Number = i + 1; return c; })
                    .ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Build pit lane from OSM ways, merging connected segments and filtering secondary pits.
    /// </summary>
    private static TrackPitInfo? BuildPitLane(
        HashSet<long> pitWayIds,
        HashSet<long> mainWayIds,
        Dictionary<long, (string? name, string? service, string? highway, List<long> nodes, bool isPit)> wayMap,
        Dictionary<long, List<TrackPoint>> wayGeometries,
        Dictionary<long, (double lat, double lon)> nodeMap,
        TrackPoint? startFinish)
    {
        // Step 1: Collect all pit lane candidate geometries, skipping secondary pits
        var segments = new List<List<TrackPoint>>();

        foreach (var wid in pitWayIds.Concat(mainWayIds))
        {
            if (!wayMap.TryGetValue(wid, out var entry)) continue;
            var (name, service, _, nodes, _) = entry;

            bool isPit = service == "pit_lane" ||
                         (name != null && name.Contains("Pit Lane", StringComparison.OrdinalIgnoreCase));
            if (!isPit) continue;

            // Skip non-primary pit lanes (Support, Secondary, Service Road)
            if (name != null && IsNonPrimaryPitLane(name)) continue;

            // Resolve geometry: try nodeMap first, fall back to inline geometry
            var pts = ResolvePitGeometry(nodes, wid, wayGeometries, nodeMap);
            if (pts.Count >= 2)
                segments.Add(pts);
        }

        if (segments.Count == 0) return null;

        // Step 2: Merge connected segments end-to-end
        var merged = MergeConnectedPitSegments(segments);

        if (merged.Count < 2) return null;

        // Step 3: Determine entry vs exit using the start/finish reference
        // Pit entry is typically BEFORE start/finish (where cars leave the track)
        // Pit exit is typically AFTER start/finish (where cars rejoin)
        TrackPoint? sfRef = startFinish;

        // If no start/finish, use the centroid of the main layout (first layout point)
        // as a rough reference — entry is usually on the approach to start/finish
        if (sfRef == null && merged.Count > 0)
        {
            // Use the midpoint of the merged pit lane as anchor
            int mid = merged.Count / 2;
            sfRef = merged[mid];
        }

        // Distance from each pit end to start/finish
        double distFirst = sfRef != null ? HaversineM(sfRef, merged[0]) : 0;
        double distLast = sfRef != null ? HaversineM(sfRef, merged[^1]) : 0;

        // The end closer to start/finish is typically the exit (cars rejoin near SF)
        // The end farther from start/finish is the entry (cars leave earlier)
        // But this depends on track layout — for most tracks, pit exit is near T1
        // which is just after SF. So exit ≈ closer to SF.
        bool exitIsFirst = distFirst < distLast;

        return new TrackPitInfo
        {
            EntryLatitude = exitIsFirst ? merged[^1].Latitude : merged[0].Latitude,
            EntryLongitude = exitIsFirst ? merged[^1].Longitude : merged[0].Longitude,
            ExitLatitude = exitIsFirst ? merged[0].Latitude : merged[^1].Latitude,
            ExitLongitude = exitIsFirst ? merged[0].Longitude : merged[^1].Longitude,
            Layout = merged
        };
    }

    /// <summary>
    /// Check if a pit lane name indicates it's a non-primary (secondary/support) pit.
    /// </summary>
    private static bool IsNonPrimaryPitLane(string name)
    {
        return name.Contains("Support", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Service Road", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Old", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find the best starting way for the circuit ordering walk.
    /// Prefers ways that:
    ///   1. Have a corner name (most reliable — these are definitely part of the main circuit)
    ///   2. Have the most geometry points (longest = most likely main circuit)
    ///   3. Are not unnamed short connectors (< 3 nodes or < 10 geom pts)
    /// </summary>
    private static long FindBestStartWay(
        HashSet<long> mainWayIds,
        Dictionary<long, (string? name, string? service, string? highway, List<long> nodes, bool isPit)> wayMap,
        Dictionary<long, List<TrackPoint>> wayGeometries)
    {
        long bestId = mainWayIds.First();
        int bestScore = -1;

        foreach (var wid in mainWayIds)
        {
            var (name, _, _, nodes, _) = wayMap[wid];
            int nodeCount = nodes?.Count ?? 0;

            // Get geom point count from wayGeometries as secondary measure
            int geomCount = 0;
            if (wayGeometries.TryGetValue(wid, out var geom))
                geomCount = geom.Count;

            int score = 0;

            // Named ways are most reliable (corner names = definitely part of main circuit)
            if (!string.IsNullOrEmpty(name))
                score += 100;

            // Longer ways are more reliable
            int length = Math.Max(nodeCount, geomCount);
            if (length > 50) score += 50;
            else if (length > 20) score += 30;
            else if (length > 10) score += 10;

            // Prefer ways with "Pit" or "Start"/"Finish" in the name (landmark ways)
            if (name != null)
            {
                if (name.Contains("Start", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Finish", StringComparison.OrdinalIgnoreCase))
                    score += 50;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestId = wid;
            }
        }

        StaticLog($"Best start way: {bestId} (score={bestScore})");
        return bestId;
    }

    /// <summary>
    /// Resolve pit lane geometry from OSM nodes or fall back to inline geometry array.
    /// </summary>
    private static List<TrackPoint> ResolvePitGeometry(
        List<long> nodes,
        long wayId,
        Dictionary<long, List<TrackPoint>> wayGeometries,
        Dictionary<long, (double lat, double lon)> nodeMap)
    {
        var pts = new List<TrackPoint>();

        // Try node-based resolution first (most accurate)
        if (nodes.Count >= 2 && nodeMap.Count > 0)
        {
            int validNodes = 0;
            foreach (var nid in nodes)
            {
                if (nodeMap.TryGetValue(nid, out var np))
                {
                    pts.Add(new TrackPoint(np.lat, np.lon));
                    validNodes++;
                }
            }
            if (validNodes >= 2)
                return pts;
        }

        // Fallback: use pre-extracted geometry from the way element
        if (wayGeometries.TryGetValue(wayId, out var geom) && geom.Count >= 2)
        {
            return new List<TrackPoint>(geom);
        }

        return pts;
    }

    /// <summary>
    /// Merge connected pit lane segments end-to-end into one continuous lane.
    /// Segments are connected if their endpoints are within 10m of each other.
    /// </summary>
    private static List<TrackPoint> MergeConnectedPitSegments(List<List<TrackPoint>> segments)
    {
        if (segments.Count == 0) return new List<TrackPoint>();
        if (segments.Count == 1) return new List<TrackPoint>(segments[0]);

        var merged = new List<TrackPoint>(segments[0]);
        var remaining = new List<List<TrackPoint>>(segments.Skip(1));
        const double connectThreshold = 0.0001; // ~10m at GPS scale

        bool madeProgress = true;
        while (madeProgress && remaining.Count > 0)
        {
            madeProgress = false;

            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var seg = remaining[i];
                if (seg.Count < 2) { remaining.RemoveAt(i); continue; }

                double distFirstToFirst = DistSqPoints(merged[0], seg[0]);
                double distFirstToLast = DistSqPoints(merged[0], seg[^1]);
                double distLastToFirst = DistSqPoints(merged[^1], seg[0]);
                double distLastToLast = DistSqPoints(merged[^1], seg[^1]);

                double minDist = Math.Min(Math.Min(distFirstToFirst, distFirstToLast),
                                          Math.Min(distLastToFirst, distLastToLast));

                if (minDist > connectThreshold)
                    continue; // Not connected

                if (minDist == distLastToFirst)
                {
                    // seg starts where merged ends — simple append
                    for (int j = 1; j < seg.Count; j++)
                        merged.Add(seg[j]);
                }
                else if (minDist == distLastToLast)
                {
                    // seg ends where merged ends — reverse and append
                    for (int j = seg.Count - 2; j >= 0; j--)
                        merged.Add(seg[j]);
                }
                else if (minDist == distFirstToFirst)
                {
                    // seg starts where merged starts — reverse prepend
                    var reversed = new List<TrackPoint>(seg);
                    reversed.Reverse();
                    for (int j = 0; j < reversed.Count - 1; j++)
                        merged.Insert(0, reversed[j]);
                }
                else // distFirstToLast
                {
                    // seg ends where merged starts — prepend
                    for (int j = 0; j < seg.Count - 1; j++)
                        merged.Insert(0, seg[seg.Count - 1 - j]);
                }

                remaining.RemoveAt(i);
                madeProgress = true;
            }
        }

        // Append any remaining unconnected segments (in order of proximity)
        foreach (var seg in remaining)
        {
            if (seg.Count >= 2)
            {
                double d1 = DistSqPoints(merged[^1], seg[0]);
                double d2 = DistSqPoints(merged[^1], seg[^1]);
                if (d1 <= d2)
                {
                    for (int j = 1; j < seg.Count; j++)
                        merged.Add(seg[j]);
                }
                else
                {
                    for (int j = seg.Count - 2; j >= 0; j--)
                        merged.Add(seg[j]);
                }
            }
        }

        return merged;
    }

    /// <summary>Squared distance between two TrackPoints (GPS coordinate space).</summary>
    private static double DistSqPoints(TrackPoint a, TrackPoint b)
    {
        double dlat = a.Latitude - b.Latitude;
        double dlon = a.Longitude - b.Longitude;
        return dlat * dlat + dlon * dlon;
    }

    /// <summary>Haversine distance in meters between two TrackPoints.</summary>
    private static double HaversineM(TrackPoint a, TrackPoint b)
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

    /// <summary>
    /// Check if an OSM way is NOT part of the target track circuit.
    /// We know the track we're looking for — anything that doesn't belong
    /// should be removed from consideration entirely.
    ///
    /// A way is "foreign" if its name suggests it belongs to a different
    /// circuit (karting, rally, moto, etc.) or is a non-circuit facility.
    /// </summary>
    private static bool IsForeignWay(string wayName, string trackNameLower, HashSet<string> trackTokens)
    {
        var nameLower = wayName.ToLowerInvariant();

        // Known non-circuit facility types
        if (nameLower.Contains("kart") ||
            nameLower.Contains("rally") ||
            nameLower.Contains("disused") ||
            nameLower.Contains("moto") ||
            nameLower.Contains("support") ||
            nameLower.Contains("secondary") ||
            nameLower.Contains("service road") ||
            nameLower.Contains("parking") ||
            nameLower.Contains("access") ||
            nameLower.Contains("camping") ||
            nameLower.Contains("pedestrian"))
            return true;

        return false;
    }

    /// <summary>
    /// Extract significant keywords from a track name for matching against OSM ways.
    /// Removes common words like "circuit", "grand prix", etc.
    /// </summary>
    private static HashSet<string> TokenizeTrackName(string trackNameLower)
    {
        var tokens = new HashSet<string>();

        // Split by common separators
        var parts = trackNameLower.Split(new[] { ' ', '-', '_', ',', '.', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Common words to filter out
        var stopWords = new HashSet<string>
        {
            "circuit", "de", "du", "des", "la", "le", "les", "l", "d",
            "international", "auto", "racing", "park", "grand", "prix",
            "ring", "speedway", "raceway", "track", "national", "club",
            "autodromo", "automotodrom", "motorsport", "arena",
            "nazionale", "internazionale", "circuito", "di",
            "sports", "land", "centre", "center", "the",
            "mount", "mountain", "panorama", "mt",
            "and", "&", "gp", "f1", "fia"
        };

        foreach (var part in parts)
        {
            var trimmed = part.Trim().ToLowerInvariant();
            if (trimmed.Length >= 3 && !stopWords.Contains(trimmed))
                tokens.Add(trimmed);
        }

        // If we got no meaningful tokens, use the whole track name
        if (tokens.Count == 0 && trackNameLower.Length >= 3)
            tokens.Add(trackNameLower);

        return tokens;
    }

    private static string? TryGetTag(JsonElement tags, string key)
    {
        if (tags.ValueKind != JsonValueKind.Object) return null;
        return tags.TryGetProperty(key, out var val) ? val.GetString() : null;
    }

    public static string GetCachePath(string trackName)
    {
        var safe = string.Join("_", trackName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDir, $"{safe}.json");
    }

    public void Dispose() => _http.Dispose();
}