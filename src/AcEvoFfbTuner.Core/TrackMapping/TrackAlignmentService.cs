using System.Text.Json;

namespace AcEvoFfbTuner.Core.TrackMapping;

public sealed record TrackAlignment(double CenterLat, double CenterLon, double RotationDeg);

public static class TrackAlignmentService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static async Task<TrackAlignment?> ComputeAlignmentAsync(
        string trackName, List<TrackWaypoint> waypoints)
    {
        if (waypoints.Count < 10) return null;

        var osmGeometry = await FetchOsmGeometry(trackName);
        if (osmGeometry == null || osmGeometry.Count < 10) return null;

        double osmCenterLat = osmGeometry.Average(p => p.Lat);
        double osmCenterLon = osmGeometry.Average(p => p.Lon);
        double cosLat = Math.Cos(osmCenterLat * Math.PI / 180.0);

        var osmMeters = osmGeometry.Select(p => (
            X: (p.Lon - osmCenterLon) * 111320.0 * cosLat,
            Y: (p.Lat - osmCenterLat) * 111320.0
        )).ToList();

        double gameCenterX = waypoints.Average(w => w.X);
        double gameCenterZ = waypoints.Average(w => w.Z);

        var gamePoints = waypoints.Select(w => (X: (double)w.X - gameCenterX, Z: (double)w.Z - gameCenterZ)).ToList();

        int gameStep = Math.Max(1, gamePoints.Count / 100);
        int osmStep = Math.Max(1, osmMeters.Count / 100);
        var subGame = gamePoints.Where((_, i) => i % gameStep == 0).ToList();
        var subOsm = osmMeters.Where((_, i) => i % osmStep == 0).ToList();

        double gameRms = Math.Sqrt(subGame.Average(p => p.X * p.X + p.Z * p.Z));
        double osmRms = Math.Sqrt(subOsm.Average(p => p.X * p.X + p.Y * p.Y));
        if (gameRms < 1.0) return null;
        double scale = osmRms / gameRms;

        double bestAngle = 0;
        double bestError = double.MaxValue;

        for (int deg = 0; deg < 360; deg++)
        {
            double rad = deg * Math.PI / 180.0;
            double cosR = Math.Cos(rad);
            double sinR = Math.Sin(rad);

            double error = 0;
            foreach (var gp in subGame)
            {
                double dx = gp.X * scale;
                double dz = -gp.Z * scale;
                double rx = dx * cosR + dz * sinR;
                double ry = -dx * sinR + dz * cosR;

                double minDist = double.MaxValue;
                foreach (var op in subOsm)
                {
                    double ddx = rx - op.X;
                    double ddy = ry - op.Y;
                    double d = ddx * ddx + ddy * ddy;
                    if (d < minDist) minDist = d;
                }
                error += minDist;
            }

            if (error < bestError)
            {
                bestError = error;
                bestAngle = deg;
            }
        }

        for (double tenthDeg = -20; tenthDeg <= 20; tenthDeg += 0.5)
        {
            double deg = bestAngle + tenthDeg;
            double rad = deg * Math.PI / 180.0;
            double cosR = Math.Cos(rad);
            double sinR = Math.Sin(rad);

            double error = 0;
            foreach (var gp in subGame)
            {
                double dx = gp.X * scale;
                double dz = -gp.Z * scale;
                double rx = dx * cosR + dz * sinR;
                double ry = -dx * sinR + dz * cosR;

                double minDist = double.MaxValue;
                foreach (var op in subOsm)
                {
                    double ddx = rx - op.X;
                    double ddy = ry - op.Y;
                    double d = ddx * ddx + ddy * ddy;
                    if (d < minDist) minDist = d;
                }
                error += minDist;
            }

            if (error < bestError)
            {
                bestError = error;
                bestAngle = deg;
            }
        }

        double avgDistM = Math.Sqrt(bestError / subGame.Count);
        if (avgDistM > 500) return null;

        return new TrackAlignment(osmCenterLat, osmCenterLon, bestAngle);
    }

    private static async Task<List<(double Lat, double Lon)>?> FetchOsmGeometry(string trackName)
    {
        var keywords = ExtractKeywords(trackName);

        foreach (var keyword in keywords)
        {
            var result = await TryFetchOsm(keyword);
            if (result != null && result.Count >= 10) return result;
        }

        return null;
    }

    private static List<string> ExtractKeywords(string trackName)
    {
        var keywords = new List<string>();
        if (!string.IsNullOrWhiteSpace(trackName))
            keywords.Add(trackName);

        var words = trackName.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            if (w.Length >= 3
                && !w.Equals("track", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("circuit", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("international", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("raceway", StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(w);
                break;
            }
        }

        return keywords.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<(double Lat, double Lon)>?> TryFetchOsm(string keyword)
    {
        try
        {
            string escaped = keyword.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string query =
                "[out:json][timeout:10];(" +
                $"way[\"name\"~\"{escaped}\",i][\"highway\"=\"raceway\"];" +
                $"way[\"name\"~\"{escaped}\",i][\"leisure\"=\"sports_centre\"];" +
                $"way[\"name\"~\"{escaped}\",i][\"leisure\"=\"pitch\"];" +
                $"relation[\"name\"~\"{escaped}\",i][\"leisure\"=\"sports_centre\"];" +
                $"relation[\"name\"~\"{escaped}\",i][\"sport\"=\"motor\"];" +
                ");out geom;";

            var content = new StringContent(
                "data=" + Uri.EscapeDataString(query),
                System.Text.Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var response = await _http.PostAsync("https://overpass-api.de/api/interpreter", content);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            return ParseOsmGeometry(json);
        }
        catch { return null; }
    }

    private static List<(double Lat, double Lon)>? ParseOsmGeometry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out var elements)) return null;

            List<(double Lat, double Lon)>? best = null;
            int bestCount = 0;

            foreach (var elem in elements.EnumerateArray())
            {
                if (!elem.TryGetProperty("geometry", out var geom)) continue;
                if (geom.ValueKind != JsonValueKind.Array) continue;

                var points = new List<(double, double)>();
                foreach (var coord in geom.EnumerateArray())
                {
                    double lat = coord.GetProperty("lat").GetDouble();
                    double lon = coord.GetProperty("lon").GetDouble();
                    points.Add((lat, lon));
                }

                if (points.Count > bestCount)
                {
                    bestCount = points.Count;
                    best = points;
                }
            }

            return best;
        }
        catch { return null; }
    }
}
