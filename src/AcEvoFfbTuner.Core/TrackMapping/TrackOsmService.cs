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
            // Quick check: old format has no "Surface" property
            if (!json.Contains("\"Surface\""))
            {
                StaticLog($"Old-format cache (no Surface): deleting {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }
            var data = JsonSerializer.Deserialize<TrackDetailedInfo>(json);
            StaticLog($"Cache loaded: {trackName} ({data?.Corners.Count ?? 0} corners, {data?.TrackLayout?.Count ?? 0} layout pts)");
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
                if (highway != "raceway") continue;

                var service = TryGetTag(tags, "service");
                var racewayTag = TryGetTag(tags, "raceway");
                var name = TryGetTag(tags, "name");
                var nodes = new List<long>();
                if (el.TryGetProperty("nodes", out var nodesArr))
                {
                    foreach (var n in nodesArr.EnumerateArray())
                    {
                        var nid = n.GetInt64();
                        nodes.Add(nid);
                        racewayNodeIds.Add(nid);
                    }
                }

                if (highway == "raceway")
                {
                    // Check for start/finish on the way itself
                    if (racewayTag == "start-finish" || racewayTag == "start" || racewayTag == "finish")
                    {
                        double sumLat = 0, sumLon = 0; int cnt = 0;
                        if (el.TryGetProperty("geometry", out var geom))
                        {
                            foreach (var pt in geom.EnumerateArray())
                            {
                                sumLat += pt.GetProperty("lat").GetDouble();
                                sumLon += pt.GetProperty("lon").GetDouble();
                                cnt++;
                            }
                        }
                        if (cnt > 0)
                            result.StartFinish ??= new TrackPoint(sumLat / cnt, sumLon / cnt);
                    }
                }

                bool isPit = service == "pit_lane" ||
                             (name != null && name.Contains("Pit Lane", StringComparison.OrdinalIgnoreCase));
                // Exclude non-main-track items
                bool exclude = (name != null && (
                    name.Contains("Go Kart", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Rally", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Disused", StringComparison.OrdinalIgnoreCase)));

                wayMap[id] = (name, service, highway, nodes, isPit || exclude);

                // Collect surface type from raceway ways
                var surface = TryGetTag(tags, "surface");
                if (!string.IsNullOrEmpty(surface))
                    surfaceTags.Add(surface);
            }
        }

        if (wayMap.Count == 0) return null;
        result.Surface = surfaceTags.Count > 0 ? string.Join(", ", surfaceTags) : null;

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
            // Walk forward from the first way
            long? current = mainWayIds.First();
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

        // Parse pit lane from pit way or named "Pit Lane" ways
        foreach (var wid in pitWayIds.Concat(mainWayIds))
        {
            var (name, service, _, nodes, _) = wayMap[wid];
            bool isPit = service == "pit_lane" ||
                         (name != null && name.Contains("Pit Lane", StringComparison.OrdinalIgnoreCase));

            if (isPit && nodes.Count >= 2 && nodeMap.Count > 0)
            {
                var first = nodeMap.GetValueOrDefault(nodes.First());
                var last = nodeMap.GetValueOrDefault(nodes.Last());
                var geom = new List<TrackPoint>();
                foreach (var nid in nodes)
                {
                    if (nodeMap.TryGetValue(nid, out var pt))
                        geom.Add(new TrackPoint(pt.lat, pt.lon));
                }

                if (first != default && last != default)
                {
                    result.Pit = new TrackPitInfo
                    {
                        EntryLatitude = first.lat,
                        EntryLongitude = first.lon,
                        ExitLatitude = last.lat,
                        ExitLongitude = last.lon,
                        Layout = geom.Count > 2 ? geom : null
                    };
                    break;
                }
            }
        }

        // Rotate corners so that T1 is the first corner AFTER start/finish
        // Fall back to pit exit position if no SF data (pit exit is just before SF)
        TrackPoint? refPoint = result.StartFinish;
        if (refPoint == null && result.Pit != null)
            refPoint = new TrackPoint(result.Pit.ExitLatitude, result.Pit.ExitLongitude);

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