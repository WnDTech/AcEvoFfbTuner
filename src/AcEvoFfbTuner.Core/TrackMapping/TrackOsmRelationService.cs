using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Fetches track circuit data from OpenStreetMap by finding MOTORSPORT RELATIONS
/// near the track's GPS location. Unlike the simpler bounding-box way search,
/// this approach:
///
/// 1. Searches for relations near the track that are motorsport-related
///    (by name match, sport tag, or by containing highway=raceway ways)
/// 2. Selects the best-matching relation (most raceway members, best name)
/// 3. Reads the ordered member ways to get correct circuit layout
/// 4. Extracts corner names, pit lanes, start/finish from member way tags
///
/// Cache: AppData/Roaming/AcEvoFfbTuner/TrackData/{trackName}_relation.json
///
/// NOTE: Relation-based circuit data is NOT available for all tracks in OSM.
/// When this returns null, the caller should fall through to the bounding-box
/// way search (TrackOsmService).
/// </summary>
public sealed class TrackOsmRelationService : IDisposable
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "TrackData");

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "trackdata_relation.log");

    private readonly HttpClient _http;
    private const string OverpassUrl = "https://overpass-api.de/api/interpreter";
    private DateTime _lastFetchAttempt = DateTime.MinValue;

    /// <summary>Minimum raceway member ways required to accept a relation as valid.</summary>
    private const int MinRacewayMembers = 8;

    public TrackOsmRelationService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "AcEvoFfbTuner/1.0 (relation-fetcher)");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.Timeout = TimeSpan.FromSeconds(30);
        Directory.CreateDirectory(CacheDir);
    }

    public Action<string>? StatusLog { get; set; }

    #region Logging

    private void Log(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [Relation] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        StatusLog?.Invoke(msg);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    #endregion

    #region Cache

    private static string GetCachePath(string trackName)
    {
        var safe = string.Join("_", trackName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(CacheDir, $"{safe}_relation.json");
    }

    public TrackDetailedInfo? LoadCached(string trackName)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;
        var path = GetCachePath(trackName);
        if (!File.Exists(path))
        {
            Log($"No relation cache for {trackName}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);

            // Validate it has the DataSource field (new format)
            if (!json.Contains("\"DataSource\""))
            {
                Log($"Old-format relation cache: deleting {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }

            var data = JsonSerializer.Deserialize<TrackDetailedInfo>(json);
            if (data == null)
            {
                Log($"Deserialize returned null for {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }

            // Check cache version — invalidate if outdated
            if (data.CacheVersion < TrackDetailedInfo.CurrentCacheVersion)
            {
                Log($"Old relation cache version ({data.CacheVersion} < {TrackDetailedInfo.CurrentCacheVersion}): deleting {trackName}");
                try { File.Delete(path); } catch { }
                return null;
            }

            if (data.TrackLayout == null || data.TrackLayout.Count < 10)
            {
                Log($"Invalid relation cache for {trackName}: no layout");
                try { File.Delete(path); } catch { }
                return null;
            }

            data.DataSource = TrackDataSource.OsmRelation;
            Log($"Relation cache loaded: {trackName} ({data.Corners.Count} corners, {data.TrackLayout.Count} pts)");
            return data;
        }
        catch (Exception ex)
        {
            Log($"Relation cache load error for {trackName}: {ex.Message}");
            return null;
        }
    }

    public void SaveCache(string trackName, TrackDetailedInfo info)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return;
        try
        {
            var path = GetCachePath(trackName);
            info.TrackName = trackName;
            info.FetchedAt = DateTime.UtcNow;
            info.DataSource = TrackDataSource.OsmRelation;
            info.CacheVersion = TrackDetailedInfo.CurrentCacheVersion;
            var json = JsonSerializer.Serialize(info);
            File.WriteAllText(path, json);
            Log($"Saved relation cache: {trackName} ({info.Corners.Count} corners, {info.TrackLayout?.Count ?? 0} pts)");
        }
        catch (Exception ex)
        {
            Log($"Save relation cache error: {ex.Message}");
        }
    }

    #endregion

    #region Overpass Query

    public async Task<TrackDetailedInfo?> FetchTrackDataAsync(
        string trackName,
        double? centerLat = null,
        double? centerLon = null)
    {
        if (string.IsNullOrWhiteSpace(trackName)) return null;

        // Rate limiting: don't retry more than once per 60 seconds
        var elapsed = DateTime.UtcNow - _lastFetchAttempt;
        if (elapsed.TotalSeconds < 60 && _lastFetchAttempt != DateTime.MinValue)
        {
            Log($"Rate-limited: skipping relation fetch for {trackName} (wait {60 - elapsed.TotalSeconds:F0}s)");
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

            if (!centerLat.HasValue || !centerLon.HasValue)
            {
                Log($"No GPS for {trackName}, can't query relations");
                return null;
            }

            // Use a larger bounding box for relation search (relations can extend beyond the track itself)
            double radiusDeg = 0.12; // ~13km — covers most circuits
            double minLat = centerLat.Value - radiusDeg;
            double maxLat = centerLat.Value + radiusDeg;
            double minLon = centerLon.Value - radiusDeg;
            double maxLon = centerLon.Value + radiusDeg;

            var query = BuildRelationQuery(trackName, minLat, minLon, maxLat, maxLon);
            Log($"Relation fetch: {trackName} at ({centerLat:F3}, {centerLon:F3}) radius={radiusDeg:F2}°");

            var content = new FormUrlEncodedContent(
                new[] { new KeyValuePair<string, string>("data", query) });
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

            var response = await _http.PostAsync(OverpassUrl, content);
            Log($"HTTP {(int)response.StatusCode} for {trackName} (relation)");

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode == 429)
                    Log($"Rate limited by Overpass API — will wait 60s before retry");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            Log($"Relation response: {json.Length} bytes for {trackName}");

            if (string.IsNullOrEmpty(json) || json.Length < 100)
            {
                Log($"Empty/too-small relation response for {trackName}");
                return null;
            }

            var result = ParseRelationResponse(json, trackName);
            if (result != null)
            {
                Log($"Relation parsed: {result.Corners.Count} corners, pit={(result.Pit != null ? "yes" : "no")}, layout={result.TrackLayout?.Count ?? 0} pts for {trackName}");
                SaveCache(trackName, result);
            }
            else
            {
                Log($"Failed to parse relation response for {trackName}");
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

    /// <summary>
    /// Build an Overpass query that finds motorsport relations by:
    /// 1. Name match (the most common way circuits are tagged)
    /// 2. sport="motor racing" tag
    /// 3. route="circuit" or route="road" with racetrack name
    /// All restricted to a bounding box for performance.
    /// </summary>
    private static string BuildRelationQuery(string trackName,
        double minLat, double minLon, double maxLat, double maxLon)
    {
        var escaped = trackName
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

        // Search for relations matching the track name (case-insensitive)
        // or tagged as motorsport circuits within the bounding box
        return
            "[out:json][timeout:25];\n" +
            "(\n" +
            // Match by name (primary: most well-tagged circuits have a name)
            $"  relation({minLat},{minLon},{maxLat},{maxLon})" +
            $"  [\"name\"~\"{escaped}\",i];\n" +
            // Match by sport tag
            $"  relation({minLat},{minLon},{maxLat},{maxLon})" +
            $"  [\"sport\"~\"motor racing|motorsport\",i];\n" +
            // Match by route=circuit (some tracks use this)
            $"  relation({minLat},{minLon},{maxLat},{maxLon})" +
            $"  [\"route\"=\"circuit\"];\n" +
            // Match by highway=raceway ways and find their parent relations
            $"  way({minLat},{minLon},{maxLat},{maxLon})" +
            $"  [\"highway\"=\"raceway\"];\n" +
            ");\n" +
            // Get parent relations of raceway ways
            "rel(bw);\n" +
            "out body;\n" +
            // Get all child ways and nodes of the found relations
            "(._;>;);\n" +
            "out body geom;";
    }

    #endregion

    #region Response Parsing

    /// <summary>
    /// Parse the Overpass response to find the best relation and extract circuit data.
    /// Strategy:
    /// 1. Collect all relations and their raceway-member ways
    /// 2. Score each relation: name match + raceway member count + start/finish presence
    /// 3. Pick the highest-scoring relation
    /// 4. Build TrackDetailedInfo from its ordered member ways
    /// </summary>
    private static TrackDetailedInfo? ParseRelationResponse(string json, string trackName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("elements", out var elements))
        {
            StaticLog($"No elements in response for {trackName}");
            return null;
        }

        // Step 1: Collect all elements by type
        var relations = new Dictionary<long, RelationData>();
        var ways = new Dictionary<long, JsonElement>();
        var nodeMap = new Dictionary<long, (double lat, double lon)>();

        foreach (var el in elements.EnumerateArray())
        {
            var type = el.GetProperty("type").GetString();
            var id = el.GetProperty("id").GetInt64();

            switch (type)
            {
                case "relation":
                    relations[id] = new RelationData(id, el);
                    break;
                case "way":
                    ways[id] = el;
                    break;
                case "node":
                    if (el.TryGetProperty("lat", out var latEl) && el.TryGetProperty("lon", out var lonEl))
                        nodeMap[id] = (latEl.GetDouble(), lonEl.GetDouble());
                    break;
            }
        }

        if (relations.Count == 0)
        {
            StaticLog($"No relations found for {trackName}");
            return null;
        }

        // Step 2: For each relation, count raceway-member ways and extract info
        var trackNameLower = trackName.ToLowerInvariant();
        var trackTokens = TokenizeTrackNameRelation(trackNameLower);
        var scoredRelations = new List<ScoredRelation>();

        foreach (var (relId, relData) in relations)
        {
            var relTags = relData.Tags;
            var relName = TryGetTag(relTags, "name") ?? "";
            var relNameLower = relName.ToLowerInvariant();
            var relType = TryGetTag(relTags, "type") ?? "";
            var relRoute = TryGetTag(relTags, "route") ?? "";

            // ---------------------------------------------------------------
            // REJECT relations whose name indicates they're NOT a real circuit:
            // kart tracks, moto tracks, rally stages, bus routes, hiking trails, etc.
            // ---------------------------------------------------------------
            if (!string.IsNullOrEmpty(relName))
            {
                // First: reject known non-circuit facility names
                if (IsForeignRelationName(relNameLower))
                {
                    StaticLog($"  Skipping relation {relId}: \"{relName}\" — not a racing circuit");
                    continue;
                }

                // Second: reject relations whose name doesn't contain ANY significant
                // token from the track name (e.g., "spa", "francorchamps" for Spa).
                // This prevents bus routes, hiking trails, etc. from being candidates.
                if (trackTokens.Count > 0 && !trackTokens.Any(t => relNameLower.Contains(t)))
                {
                    StaticLog($"  Skipping relation {relId}: \"{relName}\" — name doesn't match track \"{trackName}\"");
                    continue;
                }
            }

            // Count member ways that are raceways
            int racewayMemberCount = 0;
            int totalWayMembers = 0;
            int pitWayMembers = 0;
            bool hasStartFinish = false;
            var cornerNames = new List<string>();

            if (relData.Members != null)
            {
                foreach (var member in relData.Members)
                {
                    if (member.type != "way") continue;
                    totalWayMembers++;

                    if (!ways.TryGetValue(member.refId, out var wayEl)) continue;

                    var wayTags = TryGetObject(wayEl, "tags");
                    var highway = TryGetTag(wayTags, "highway");

                    if (highway == "raceway")
                    {
                        // Skip ways that don't belong to a real racing circuit
                        var wayName = TryGetTag(wayTags, "name");
                        if (!string.IsNullOrEmpty(wayName) && IsForeignWay(wayName))
                        {
                            StaticLog($"  Skipping foreign way {member.refId}: \"{wayName}\" in relation {relId}");
                            continue;
                        }

                        racewayMemberCount++;

                        var role = (member.role ?? "").ToLowerInvariant();
                        if (role == "pit_lane")
                            pitWayMembers++;
                        else
                        {
                            // Check for start/finish
                            var racewayTag = TryGetTag(wayTags, "raceway");
                            if (racewayTag == "start-finish" || racewayTag == "start" || racewayTag == "finish")
                                hasStartFinish = true;

                            // Collect corner names
                            if (!string.IsNullOrEmpty(wayName))
                                cornerNames.Add(wayName);
                        }
                    }
                }
            }

            if (racewayMemberCount < MinRacewayMembers)
                continue; // Not a real circuit relation

            // Compute name match score
            int nameScore = 0;
            if (string.Equals(relNameLower, trackNameLower, StringComparison.OrdinalIgnoreCase))
                nameScore = 100; // Exact match
            else if (relNameLower.Contains(trackNameLower) || trackNameLower.Contains(relNameLower))
                nameScore = 50;  // Partial match
            else
                nameScore = 10;  // Weak match (found by sport/route tag, not name)

            // Corner names matching track name is a strong signal
            foreach (var cn in cornerNames)
            {
                var cnLower = cn.ToLowerInvariant();
                if (cnLower.Contains(trackNameLower))
                {
                    nameScore += 20;
                    break;
                }
            }

            // Type-based bonus: prefer relations explicitly tagged as circuits
            int typeBonus = 0;
            if (relType == "circuit" || relRoute == "circuit")
                typeBonus = 40; // Strong signal: explicitly tagged circuit
            else if (relType == "route" || relRoute == "road")
                typeBonus = 10; // Weak signal: route/road
            else
                typeBonus = -10; // Non-circuit type — penalize (bus route, hiking trail, etc.)

            // Total score: name match + raceway count + type bonus + start/finish bonus
            int totalScore = nameScore + racewayMemberCount * 2 + typeBonus;
            if (hasStartFinish) totalScore += 30;

            scoredRelations.Add(new ScoredRelation
            {
                RelationId = relId,
                Score = totalScore,
                RacewayMemberCount = racewayMemberCount,
                PitWayCount = pitWayMembers,
                HasStartFinish = hasStartFinish,
                Name = relName,
                CornerNames = cornerNames
            });

            StaticLog($"Relation {relId}: \"{relName}\" score={totalScore} (name={nameScore} raceways={racewayMemberCount} type={typeBonus} sf={hasStartFinish})");
        }

        if (scoredRelations.Count == 0)
        {
            StaticLog($"No relations with ≥{MinRacewayMembers} raceway ways found for {trackName}");
            return null;
        }

        // Step 3: Pick the best relation
        scoredRelations.Sort((a, b) => b.Score - a.Score);
        var best = scoredRelations[0];
        StaticLog($"Best relation: {best.RelationId} \"{best.Name}\" (score={best.Score})");

        if (!relations.TryGetValue(best.RelationId, out var bestRelData))
            return null;

        // Step 4: Get the ordered member ways
        // CRITICAL: Do NOT trust the relation's member order for circuit sequence.
        // OSM relations often list members in arbitrary order. Instead, collect
        // all circuit member ways and use node-adjacency to find the correct order.
        var circuitWayIds = new HashSet<long>();
        var pitWayIds = new HashSet<long>();

        if (bestRelData.Members != null)
        {
            foreach (var member in bestRelData.Members)
            {
                if (member.type != "way") continue;
                if (!ways.ContainsKey(member.refId)) continue;

                var role = (member.role ?? "").ToLowerInvariant();

                if (role == "pit_lane")
                {
                    pitWayIds.Add(member.refId);
                }
                else
                {
                    var wayEl = ways[member.refId];
                    var wayTags = TryGetObject(wayEl, "tags");
                    var highway = TryGetTag(wayTags, "highway");

                    // Only include raceway ways in the circuit
                    if (highway != "raceway") continue;

                    // Skip foreign ways that don't belong to the main circuit
                    var wayName = TryGetTag(wayTags, "name");
                    if (!string.IsNullOrEmpty(wayName) && IsForeignWay(wayName))
                    {
                        StaticLog($"  Skipping foreign way {member.refId}: \"{wayName}\" in circuit layout");
                        continue;
                    }

                    circuitWayIds.Add(member.refId);
                }
            }
        }

        // Also include ways that are part of the circuit but NOT explicitly listed as members.
        // Sometimes named corner ways exist in the response but aren't relation members.
        // Check the best-scoring relation's corner names to validate.
        foreach (var (wayId, wayEl) in ways)
        {
            if (circuitWayIds.Contains(wayId) || pitWayIds.Contains(wayId)) continue;
            var wayTags = TryGetObject(wayEl, "tags");
            var highway = TryGetTag(wayTags, "highway");
            if (highway != "raceway") continue;
            var wayName = TryGetTag(wayTags, "name");
            if (string.IsNullOrEmpty(wayName) || IsForeignWay(wayName)) continue;

            // Only add if it connects to existing circuit ways via shared nodes
            // (check first/last node of this way against all circuit ways)
            // Skip for now — relation members should be sufficient
        }

        if (circuitWayIds.Count < 3)
        {
            StaticLog($"Not enough circuit member ways ({circuitWayIds.Count}) for {trackName}");
            return null;
        }

        // Step 5: Use node-adjacency to find the correct circuit order
        // Build node → way adjacency from the circuit member ways
        var relNodeToWays = new Dictionary<long, List<long>>();
        var relWayNodes = new Dictionary<long, List<long>>();

        foreach (var wid in circuitWayIds)
        {
            var wayEl = ways[wid];
            var wayPts = ExtractWayGeometry(wayEl);
            if (wayPts.Count < 2) continue;

            // Extract nodes from geometry (we need node IDs for adjacency, but
            // with out body geom we only have lat/lon. Use endpoint lat/lon as the connection key.)
            relWayNodes[wid] = new List<long>(); // placeholder — we'll use coordinate-based adjacency instead
        }

        // Since we may not have OSM node IDs (only geometry coordinates from out body geom),
        // use coordinate-based adjacency: two ways are connected if their endpoints are within ~5m
        var wayEndpoints = new Dictionary<long, (TrackPoint first, TrackPoint last)>();
        foreach (var wid in circuitWayIds)
        {
            var wayEl = ways[wid];
            var pts = ExtractWayGeometry(wayEl);
            if (pts.Count >= 2)
                wayEndpoints[wid] = (pts[0], pts[^1]);
        }

        const double connectThreshold = 0.00005; // ~5m at GPS scale

        // Build adjacency: for each way, find ways connected at its first/last endpoint
        var wayAdjacency = new Dictionary<long, List<(long neighbor, bool connectAtFirst)>>();
        foreach (var wid in circuitWayIds)
            wayAdjacency[wid] = new List<(long, bool)>();

        foreach (var wid in circuitWayIds)
        {
            if (!wayEndpoints.TryGetValue(wid, out var ep)) continue;

            foreach (var otherId in circuitWayIds)
            {
                if (otherId == wid || !wayEndpoints.TryGetValue(otherId, out var otherEp)) continue;

                // wid's LAST connects to other's FIRST
                if (DistSq(ep.last, otherEp.first) < connectThreshold)
                    wayAdjacency[wid].Add((otherId, false));
                // wid's LAST connects to other's LAST
                if (DistSq(ep.last, otherEp.last) < connectThreshold)
                    wayAdjacency[wid].Add((otherId, true));
                // wid's FIRST connects to other's LAST
                if (DistSq(ep.first, otherEp.last) < connectThreshold)
                    wayAdjacency[wid].Insert(0, (otherId, false));
                // wid's FIRST connects to other's FIRST
                if (DistSq(ep.first, otherEp.first) < connectThreshold)
                    wayAdjacency[wid].Insert(0, (otherId, true));
            }
        }

        // Walk the circuit: start from the best starting way, follow adjacency
        var orderedCircuitWays = new List<(long wayId, bool reversed)>();
        var visitedWays = new HashSet<long>();

        // Find best start: prefer named way with most connections
        long startWay = circuitWayIds.First();
        int bestConnections = -1;
        foreach (var wid in circuitWayIds)
        {
            int conns = wayAdjacency.ContainsKey(wid) ? wayAdjacency[wid].Count : 0;
            int ptCount = wayEndpoints.ContainsKey(wid)
                ? (wayEndpoints[wid].first != default ? 10 : 0) + (wayEndpoints[wid].last != default ? 10 : 0)
                : 0;
            var wayEl = ways.ContainsKey(wid) ? ways[wid] : default;
            var wayName = wayEl.ValueKind != JsonValueKind.Undefined
                ? TryGetTag(TryGetObject(wayEl, "tags"), "name")
                : null;
            int score = conns * 10 + ptCount + (wayName != null ? 200 : 0);
            if (score > bestConnections) { bestConnections = score; startWay = wid; }
        }

        StaticLog($"Circuit walk starting from way {startWay}: \"{TryGetTag(TryGetObject(ways.ContainsKey(startWay) ? ways[startWay] : default, "tags"), "name") ?? "?"}\"");

        // Walk forward from startWay
        long? current = startWay;
        while (current.HasValue && visitedWays.Add(current.Value))
        {
            if (!wayEndpoints.TryGetValue(current.Value, out var curEp))
                break;

            orderedCircuitWays.Add((current.Value, false));

            // Find next unvisited connected way
            long? nextWay = null;
            bool nextReversed = false;

            if (wayAdjacency.TryGetValue(current.Value, out var neighbors))
            {
                foreach (var (neighbor, needsReverse) in neighbors)
                {
                    if (!visitedWays.Contains(neighbor))
                    {
                        nextWay = neighbor;
                        nextReversed = needsReverse;
                        break;
                    }
                }
            }

            current = nextWay;
        }

        // Walk backward from startWay (to pick up the other half of the circuit)
        if (visitedWays.Count < circuitWayIds.Count)
        {
            // Find an unvisited way that connects to the startWay's first endpoint
            foreach (var wid in circuitWayIds)
            {
                if (visitedWays.Contains(wid)) continue;
                if (!wayEndpoints.TryGetValue(wid, out var ep)) continue;
                if (!wayEndpoints.TryGetValue(startWay, out var startEp)) break;

                if (DistSq(ep.last, startEp.first) < connectThreshold ||
                    DistSq(ep.first, startEp.first) < connectThreshold)
                {
                    bool needsReverse = DistSq(ep.first, startEp.first) < DistSq(ep.last, startEp.first);

                    // Walk backward from this way
                    var revCurrent = (long?)wid;
                    var revPath = new List<(long wayId, bool reversed)>();
                    var revVisited = new HashSet<long>();

                    while (revCurrent.HasValue && revVisited.Add(revCurrent.Value))
                    {
                        if (!wayEndpoints.TryGetValue(revCurrent.Value, out var revEp))
                            break;

                        bool isReversed = needsReverse;
                        revPath.Add((revCurrent.Value, isReversed));

                        // Find predecessor: a way whose LAST connects to revCurrent's FIRST
                        long? predWay = null;
                        bool predReversed = false;
                        foreach (var predId in circuitWayIds)
                        {
                            if (revVisited.Contains(predId) || visitedWays.Contains(predId)) continue;
                            if (!wayEndpoints.TryGetValue(predId, out var predEp)) continue;

                            if (DistSq(predEp.last, isReversed ? revEp.first : revEp.last) < connectThreshold)
                            {
                                predWay = predId;
                                predReversed = false;
                                break;
                            }
                            if (DistSq(predEp.first, isReversed ? revEp.first : revEp.last) < connectThreshold)
                            {
                                predWay = predId;
                                predReversed = true;
                                break;
                            }
                        }

                        if (predWay.HasValue)
                        {
                            needsReverse = predReversed;
                        }
                        revCurrent = predWay;
                    }

                    // Insert the backward path before the forward path
                    revPath.Reverse();
                    foreach (var (wayId, reversed) in revPath)
                    {
                        if (!visitedWays.Contains(wayId))
                        {
                            orderedCircuitWays.Insert(0, (wayId, reversed));
                            visitedWays.Add(wayId);
                        }
                    }
                    break;
                }
            }
        }

        StaticLog($"Circuit ordering: {orderedCircuitWays.Count} ways from {circuitWayIds.Count} candidates");

        if (orderedCircuitWays.Count < 3)
        {
            StaticLog($"Not enough ordered ways ({orderedCircuitWays.Count}) for {trackName}");
            return null;
        }

        // Step 6: Build the track GPS layout from ordered member ways
        var result = new TrackDetailedInfo
        {
            TrackName = trackName,
            DataSource = TrackDataSource.OsmRelation,
            ConfidenceScore = 0.8f
        };

        var fullLayout = new List<TrackPoint>();

        foreach (var (wayId, reversed) in orderedCircuitWays)
        {
            if (!ways.TryGetValue(wayId, out var wayEl)) continue;

            var pts = ExtractWayGeometry(wayEl);
            if (pts.Count < 2) continue;

            if (reversed)
                pts.Reverse();

            // Direction: use angle continuity when we have enough layout points
            if (fullLayout.Count >= 3)
            {
                int n = fullLayout.Count;
                var p1 = fullLayout[n - 2];
                var p2 = fullLayout[n - 1];

                // Direction vector of the existing layout's last segment
                double dx1 = p2.Longitude - p1.Longitude;
                double dy1 = p2.Latitude - p1.Latitude;

                // Try normal direction
                double dx2a = pts[0].Longitude - p2.Longitude;
                double dy2a = pts[0].Latitude - p2.Latitude;
                double dotA = dx1 * dx2a + dy1 * dy2a;
                double crossA = dx1 * dy2a - dy1 * dx2a;
                double angleA = Math.Atan2(Math.Abs(crossA), dotA + 1e-10);

                // Try reversed direction
                double dx2b = pts[^1].Longitude - p2.Longitude;
                double dy2b = pts[^1].Latitude - p2.Latitude;
                double dotB = dx1 * dx2b + dy1 * dy2b;
                double crossB = dx1 * dy2b - dy1 * dx2b;
                double angleB = Math.Atan2(Math.Abs(crossB), dotB + 1e-10);

                if (angleB < angleA)
                    pts.Reverse();
            }
            else if (fullLayout.Count > 0)
            {
                var lastPt = fullLayout[^1];
                double distFirst = DistSq(lastPt, pts[0]);
                double distLast = DistSq(lastPt, pts[^1]);
                if (distLast < distFirst)
                    pts.Reverse();
            }

            // Gap bridging
            if (fullLayout.Count > 0)
            {
                double gap = Math.Sqrt(DistSq(fullLayout[^1], pts[0]));
                if (gap > 0.00005)
                {
                    int steps = Math.Min(8, Math.Max(1, (int)(gap / 0.0001)));
                    for (int s = 1; s <= steps; s++)
                    {
                        double t = (double)s / (steps + 1);
                        fullLayout.Add(new TrackPoint(
                            fullLayout[^1].Latitude + t * (pts[0].Latitude - fullLayout[^1].Latitude),
                            fullLayout[^1].Longitude + t * (pts[0].Longitude - fullLayout[^1].Longitude)));
                    }
                }
            }

            // Skip duplicate first point
            int startIdx = fullLayout.Count > 0 ? 1 : 0;
            for (int i = startIdx; i < pts.Count; i++)
                fullLayout.Add(pts[i]);
        }

        if (fullLayout.Count < 10)
        {
            StaticLog($"Layout too small ({fullLayout.Count} pts) for {trackName}");
            return null;
        }

        result.TrackLayout = fullLayout;
        StaticLog($"Layout: {fullLayout.Count} pts from {orderedCircuitWays.Count} ways");

        // Step 7: Extract corner names from circuit member ways
        int cornerNumber = 0;
        foreach (var (wayId, _) in orderedCircuitWays)
        {
            if (!ways.TryGetValue(wayId, out var wayEl)) continue;
            var tags = TryGetObject(wayEl, "tags");
            var name = TryGetTag(tags, "name");

            if (!string.IsNullOrEmpty(name))
            {
                var pts = ExtractWayGeometry(wayEl);
                if (pts.Count > 0)
                {
                    int mid = pts.Count / 2;
                    cornerNumber++;
                    result.Corners.Add(new TrackCornerInfo
                    {
                        Number = cornerNumber,
                        Name = name,
                        Latitude = pts[mid].Latitude,
                        Longitude = pts[mid].Longitude
                    });
                }
            }
        }

        // Step 7: Extract start/finish
        if (bestRelData.Members != null)
        {
            foreach (var member in bestRelData.Members)
            {
                if (member.type != "way") continue;
                if (!ways.TryGetValue(member.refId, out var wayEl)) continue;

                var tags = TryGetObject(wayEl, "tags");
                var racewayTag = TryGetTag(tags, "raceway");
                if (racewayTag == "start-finish" || racewayTag == "start" || racewayTag == "finish")
                {
                    var pts = ExtractWayGeometry(wayEl);
                    if (pts.Count > 0)
                    {
                        int mid = pts.Count / 2;
                        result.StartFinish = new TrackPoint(pts[mid].Latitude, pts[mid].Longitude);
                        StaticLog($"Start/finish from way {member.refId} in relation");
                        break;
                    }
                }
            }
        }

        if (result.StartFinish == null)
        {
            result.StartFinish = EstimateStartFinish(fullLayout);
            StaticLog($"Start/finish estimated from longest straight");
        }

        // Step 8: Extract pit lane — merge connected segments, filter secondary pits
        result.Pit = BuildPitLaneFromRelation(pitWayIds, orderedCircuitWays, ways, result.StartFinish);

        // Step 9: Compute track length and sector boundaries
        result.TrackLengthM = ComputeTrackLength(fullLayout);
        result.SectorBoundaries = ComputeSectorBoundaries(fullLayout, result.TrackLengthM);

        // Step 10: Collect surface info
        var surfaces = new HashSet<string>();
        foreach (var (wayId, _) in orderedCircuitWays)
        {
            if (!ways.TryGetValue(wayId, out var wayEl)) continue;
            var tags = TryGetObject(wayEl, "tags");
            var surface = TryGetTag(tags, "surface");
            if (!string.IsNullOrEmpty(surface))
            {
                foreach (var s in surface.Split(','))
                    surfaces.Add(s.Trim());
            }
        }
        if (surfaces.Count > 0)
            result.Surface = string.Join(", ", surfaces);

        StaticLog($"Relation parse complete: {result.Corners.Count} corners, length={result.TrackLengthM:F0}m");
        return result;
    }

    #endregion

    #region Data Helpers

    private static List<TrackPoint> ExtractWayGeometry(JsonElement wayEl)
    {
        var pts = new List<TrackPoint>();

        if (wayEl.TryGetProperty("geometry", out var geom))
        {
            foreach (var pt in geom.EnumerateArray())
            {
                pts.Add(new TrackPoint(
                    pt.GetProperty("lat").GetDouble(),
                    pt.GetProperty("lon").GetDouble()));
            }
        }

        return pts;
    }

    /// <summary>
    /// Build pit lane from relation member ways. Merges connected segments,
    /// filters non-primary pits, and determines entry/exit by proximity to start/finish.
    /// </summary>
    private static TrackPitInfo? BuildPitLaneFromRelation(
        HashSet<long> pitWayIds,
        List<(long wayId, bool reversed)> circuitWays,
        Dictionary<long, JsonElement> ways,
        TrackPoint? startFinish)
    {
        // Collect pit way geometries — check both explicit pit lane members AND
        // circuit member ways named "Pit Lane"
        var segments = new List<List<TrackPoint>>();
        var checkedIds = new HashSet<long>();

        // First: explicit pit lane members (role="pit_lane")
        foreach (var pitId in pitWayIds)
        {
            if (!checkedIds.Add(pitId)) continue;
            if (!ways.TryGetValue(pitId, out var wayEl)) continue;

            var tags = TryGetObject(wayEl, "tags");
            var name = TryGetTag(tags, "name");

            // Skip non-primary pit lanes
            if (name != null && IsNonPrimaryPitLane(name)) continue;

            var pts = ExtractWayGeometry(wayEl);
            if (pts.Count >= 2)
                segments.Add(pts);
        }

        // Second: circuit member ways with "Pit Lane" in name (not role="pit_lane")
        foreach (var (wayId, role) in circuitWays)
        {
            if (!checkedIds.Add(wayId)) continue;
            if (!ways.TryGetValue(wayId, out var wayEl)) continue;

            var tags = TryGetObject(wayEl, "tags");
            var name = TryGetTag(tags, "name");

            if (name == null || !name.Contains("Pit Lane", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsNonPrimaryPitLane(name)) continue;

            var pts = ExtractWayGeometry(wayEl);
            if (pts.Count >= 2)
                segments.Add(pts);
        }

        if (segments.Count == 0) return null;

        // Merge connected segments end-to-end
        var merged = MergeConnectedPitSegments(segments);
        if (merged.Count < 2) return null;

        // Determine entry vs exit using start/finish reference
        TrackPoint? sfRef = startFinish;
        if (sfRef == null)
        {
            int mid = merged.Count / 2;
            sfRef = merged[mid];
        }

        double distFirst = sfRef != null ? HaversineMRelation(sfRef, merged[0]) : 0;
        double distLast = sfRef != null ? HaversineMRelation(sfRef, merged[^1]) : 0;

        // Exit is closer to start/finish (cars rejoin near SF), entry is farther
        bool exitIsFirst = distFirst < distLast;

        StaticLog($"Pit lane merged: {segments.Count} segments into {merged.Count} pts");
        return new TrackPitInfo
        {
            EntryLatitude = exitIsFirst ? merged[^1].Latitude : merged[0].Latitude,
            EntryLongitude = exitIsFirst ? merged[^1].Longitude : merged[0].Longitude,
            ExitLatitude = exitIsFirst ? merged[0].Latitude : merged[^1].Latitude,
            ExitLongitude = exitIsFirst ? merged[0].Longitude : merged[^1].Longitude,
            Layout = merged
        };
    }

    /// <summary>Check if a pit lane name indicates a secondary/support pit.</summary>
    /// <summary>
    /// Check if an OSM way name indicates it belongs to a different facility
    /// (karting, rally, moto, etc.) and should be excluded from the circuit.
    /// </summary>
    private static bool IsForeignWay(string wayName)
    {
        var nameLower = wayName.ToLowerInvariant();
        return nameLower.Contains("kart") ||
               nameLower.Contains("rally") ||
               nameLower.Contains("disused") ||
               nameLower.Contains("moto") ||
               nameLower.Contains("support") ||
               nameLower.Contains("secondary") ||
               nameLower.Contains("service road") ||
               nameLower.Contains("parking") ||
               nameLower.Contains("access") ||
               nameLower.Contains("camping") ||
               nameLower.Contains("pedestrian");
    }

    /// <summary>
    /// Reject relation names that clearly don't represent a real racing circuit.
    /// Covers: kart tracks, hiking trails, bus routes, bike paths, etc.
    /// </summary>
    private static bool IsForeignRelationName(string nameLower)
    {
        return nameLower.Contains("kart") ||
               nameLower.Contains("moto") ||
               nameLower.Contains("rally") ||
               nameLower.Contains("bus") ||
               nameLower.Contains("hiking") ||
               nameLower.Contains("trail") ||
               nameLower.Contains("walk") ||
               nameLower.Contains("bike") ||
               nameLower.Contains("cycle") ||
               nameLower.Contains("ski") ||
               nameLower.Contains("loipe") ||
               nameLower.Contains("wander") ||
               nameLower.Contains("promenade") ||
               nameLower.Contains("parking") ||
               nameLower.Contains("camping") ||
               nameLower.Contains("pedestrian") ||
               nameLower.Contains("train") ||
               nameLower.Contains("express") ||
               nameLower.Contains("boundary") ||
               nameLower.Contains("water") ||
               nameLower.Contains("forest") ||
               nameLower.Contains("nature") ||
               nameLower.Contains("parcours");
    }

    /// <summary>
    /// Extract significant keywords from a track name for matching against OSM relation names.
    /// </summary>
    private static HashSet<string> TokenizeTrackNameRelation(string trackNameLower)
    {
        var tokens = new HashSet<string>();
        var parts = trackNameLower.Split(new[] { ' ', '-', '_', ',', '.', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var stopWords = new HashSet<string>
        {
            "circuit", "de", "du", "des", "la", "le", "les", "l", "d",
            "international", "auto", "racing", "park", "grand", "prix",
            "ring", "speedway", "raceway", "track", "national", "club",
            "autodromo", "automotodrom", "motorsport", "arena",
            "nazionale", "internazionale", "circuito", "di",
            "sports", "land", "centre", "center", "the",
            "mount", "mountain", "panorama", "mt",
            "and", "&", "gp", "f1", "fia", "route",
            "type", "of", "en", "a", "to", "in", "for"
        };

        foreach (var part in parts)
        {
            var trimmed = part.Trim().ToLowerInvariant();
            if (trimmed.Length >= 3 && !stopWords.Contains(trimmed))
                tokens.Add(trimmed);
        }

        if (tokens.Count == 0 && trackNameLower.Length >= 3)
            tokens.Add(trackNameLower);

        return tokens;
    }

    private static bool IsNonPrimaryPitLane(string name)
    {
        return name.Contains("Support", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Secondary", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Service Road", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Old", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Merge connected pit lane segments end-to-end.</summary>
    private static List<TrackPoint> MergeConnectedPitSegments(List<List<TrackPoint>> segments)
    {
        if (segments.Count == 0) return new List<TrackPoint>();
        if (segments.Count == 1) return new List<TrackPoint>(segments[0]);

        var merged = new List<TrackPoint>(segments[0]);
        var remaining = new List<List<TrackPoint>>(segments.Skip(1));
        const double connectThreshold = 0.0001;

        bool madeProgress = true;
        while (madeProgress && remaining.Count > 0)
        {
            madeProgress = false;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var seg = remaining[i];
                if (seg.Count < 2) { remaining.RemoveAt(i); continue; }

                double dFF = DistSq(merged[0], seg[0]);
                double dFL = DistSq(merged[0], seg[^1]);
                double dLF = DistSq(merged[^1], seg[0]);
                double dLL = DistSq(merged[^1], seg[^1]);
                double minDist = Math.Min(Math.Min(dFF, dFL), Math.Min(dLF, dLL));

                if (minDist > connectThreshold) continue;

                if (minDist == dLF)
                {
                    for (int j = 1; j < seg.Count; j++) merged.Add(seg[j]);
                }
                else if (minDist == dLL)
                {
                    for (int j = seg.Count - 2; j >= 0; j--) merged.Add(seg[j]);
                }
                else if (minDist == dFF)
                {
                    var rev = new List<TrackPoint>(seg);
                    rev.Reverse();
                    for (int j = 0; j < rev.Count - 1; j++) merged.Insert(0, rev[j]);
                }
                else
                {
                    for (int j = 0; j < seg.Count - 1; j++)
                        merged.Insert(0, seg[seg.Count - 1 - j]);
                }

                remaining.RemoveAt(i);
                madeProgress = true;
            }
        }

        // Append any remaining unconnected segments
        foreach (var seg in remaining)
        {
            if (seg.Count >= 2)
            {
                if (DistSq(merged[^1], seg[0]) <= DistSq(merged[^1], seg[^1]))
                {
                    for (int j = 1; j < seg.Count; j++) merged.Add(seg[j]);
                }
                else
                {
                    for (int j = seg.Count - 2; j >= 0; j--) merged.Add(seg[j]);
                }
            }
        }

        return merged;
    }

    /// <summary>Haversine distance in meters between two TrackPoints (local copy for this file).</summary>
    private static double HaversineMRelation(TrackPoint a, TrackPoint b)
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

    private static TrackPoint? EstimateStartFinish(List<TrackPoint> layout)
    {
        if (layout.Count < 10) return null;

        // Find the longest straight section
        int longestStart = 0;
        int longestEnd = 1;
        double longestLen = 0;
        int straightStart = 0;

        for (int i = 1; i <= layout.Count; i++)
        {
            int curr = i < layout.Count ? i : 0;
            double dx = layout[curr].Longitude - layout[straightStart].Longitude;
            double dy = layout[curr].Latitude - layout[straightStart].Latitude;

            // Check curvature at current point
            int prev = curr > 0 ? curr - 1 : layout.Count - 1;
            int prev2 = prev > 0 ? prev - 1 : layout.Count - 1;
            double ddx1 = layout[prev].Longitude - layout[prev2].Longitude;
            double ddy1 = layout[prev].Latitude - layout[prev2].Latitude;
            double ddx2 = layout[curr].Longitude - layout[prev].Longitude;
            double ddy2 = layout[curr].Latitude - layout[prev].Latitude;

            double cross = Math.Abs(ddx1 * ddy2 - ddy1 * ddx2);
            double dot = ddx1 * ddx2 + ddy1 * ddy2;
            double angle = Math.Atan2(cross, dot + 1e-10);

            if (angle > 0.15) // ~8.6 degrees — likely a corner
            {
                double straightLen = CumulativeDist(layout, straightStart, curr);
                if (straightLen > longestLen)
                {
                    longestLen = straightLen;
                    longestStart = straightStart;
                    longestEnd = curr;
                }
                straightStart = curr;
            }
        }

        // Check final straight (wrapping)
        if (straightStart < layout.Count)
        {
            double straightLen = CumulativeDist(layout, straightStart, layout.Count)
                               + CumulativeDist(layout, 0, longestStart);
            if (straightLen > longestLen)
            {
                longestStart = straightStart;
                longestEnd = longestStart;
                longestLen = straightLen;
            }
        }

        if (longestLen <= 0) return null;

        // Midpoint of the longest straight
        double halfLen = longestLen / 2;
        double cum = 0;
        for (int i = longestStart; i != longestEnd && i < layout.Count; i++)
        {
            int next = i + 1 < layout.Count ? i + 1 : 0;
            double dx = layout[next].Longitude - layout[i].Longitude;
            double dy = layout[next].Latitude - layout[i].Latitude;
            double segLen = Math.Sqrt(dx * dx + dy * dy);
            if (cum + segLen >= halfLen)
            {
                double t = segLen > 0 ? (halfLen - cum) / segLen : 0;
                return new TrackPoint(
                    layout[i].Latitude + t * dy,
                    layout[i].Longitude + t * dx);
            }
            cum += segLen;
        }

        return null;
    }

    private static double CumulativeDist(List<TrackPoint> pts, int start, int end)
    {
        double total = 0;
        int max = Math.Min(end, pts.Count - 1);
        for (int i = start + 1; i <= max; i++)
        {
            double dx = pts[i].Longitude - pts[i - 1].Longitude;
            double dy = pts[i].Latitude - pts[i - 1].Latitude;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }

    private static double ComputeTrackLength(List<TrackPoint> layout)
    {
        if (layout.Count < 3) return 0;
        double total = 0;
        for (int i = 1; i < layout.Count; i++)
            total += HaversineM(layout[i - 1], layout[i]);
        total += HaversineM(layout[^1], layout[0]);
        return total;
    }

    private static double[] ComputeSectorBoundaries(List<TrackPoint> layout, double totalLength)
    {
        if (totalLength <= 0 || layout.Count < 3)
            return [0.0, 1.0 / 3.0, 2.0 / 3.0, 1.0];

        double target1 = totalLength / 3.0;
        double target2 = 2.0 * totalLength / 3.0;
        double cum = 0;
        double s1 = 1.0 / 3.0;
        double s2 = 2.0 / 3.0;

        for (int i = 1; i < layout.Count; i++)
        {
            double segLen = HaversineM(layout[i - 1], layout[i]);
            double prevCum = cum;
            cum += segLen;

            if (s1 == 1.0 / 3.0 && cum >= target1)
            {
                double t = segLen > 0 ? (target1 - prevCum) / segLen : 0;
                s1 = ((double)(i - 1) + t) / layout.Count;
            }
            if (s2 == 2.0 / 3.0 && cum >= target2)
            {
                double t = segLen > 0 ? (target2 - prevCum) / segLen : 0;
                s2 = ((double)(i - 1) + t) / layout.Count;
            }
        }

        return [0.0, Math.Clamp(s1, 0.0, 1.0), Math.Clamp(s2, 0.0, 1.0), 1.0];
    }

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

    private static double DistSq(TrackPoint a, TrackPoint b)
    {
        double dlat = a.Latitude - b.Latitude;
        double dlon = a.Longitude - b.Longitude;
        return dlat * dlat + dlon * dlon;
    }

    #endregion

    #region OSM JSON Parsing Types

    private sealed class RelationData
    {
        public long Id { get; }
        public JsonElement? Tags { get; }
        public List<MemberRef>? Members { get; }

        public RelationData(long id, JsonElement el)
        {
            Id = id;
            Tags = TryGetObject(el, "tags");

            if (el.TryGetProperty("members", out var membersArr))
            {
                Members = new List<MemberRef>();
                foreach (var m in membersArr.EnumerateArray())
                {
                    if (TryGetString(m, "type", out var mtype) && mtype == "way" &&
                        TryGetInt64(m, "ref", out var refId))
                    {
                        string role = TryGetString(m, "role", out var r) ? (r ?? "") : "";
                        Members.Add(new MemberRef(refId, role));
                    }
                }
            }
        }
    }

    private sealed class MemberRef
    {
        public long refId;
        public string? role;
        public string type = "way";
        public MemberRef(long id, string? r) { refId = id; role = r; }
    }

    private sealed class ScoredRelation
    {
        public long RelationId;
        public int Score;
        public int RacewayMemberCount;
        public int PitWayCount;
        public bool HasStartFinish;
        public string Name = "";
        public List<string> CornerNames = new();
    }

    #endregion

    #region JSON Helpers

    private static void StaticLog(string msg)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [Relation] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    private static JsonElement? TryGetObject(JsonElement el, string key)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Object)
            return val;
        return null;
    }

    private static string? TryGetTag(JsonElement? tags, string key)
    {
        if (tags == null) return null;
        var t = tags.Value;
        return t.TryGetProperty(key, out var val) ? val.GetString() : null;
    }

    private static bool TryGetString(JsonElement el, string key, out string? value)
    {
        value = null;
        if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
        {
            value = val.GetString();
            return true;
        }
        return false;
    }

    private static bool TryGetInt64(JsonElement el, string key, out long value)
    {
        value = 0;
        if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            value = val.GetInt64();
            return true;
        }
        return false;
    }

    #endregion

    public void Dispose() => _http.Dispose();
}
