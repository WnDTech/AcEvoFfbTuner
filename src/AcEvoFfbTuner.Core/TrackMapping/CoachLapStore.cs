using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Personal-best laps on disk, one file per track + layout + car. A saved lap is only valid for
/// the recorded line it was sampled on (its per-waypoint arrays index that line), so the file
/// stores the waypoint count and a re-recorded line (different count) ignores it.
/// </summary>
public sealed class CoachLapStore
{
    private sealed class Record
    {
        public int Version { get; set; } = 1;
        public string Track { get; set; } = "";
        public string Config { get; set; } = "";
        public string Car { get; set; } = "";
        public int WaypointCount { get; set; }
        public DateTime RecordedAtUtc { get; set; }
        public int LapNumber { get; set; }
        public float LapTimeS { get; set; }
        public float[] TimeS { get; set; } = Array.Empty<float>();
        public float[] SpeedKmh { get; set; } = Array.Empty<float>();
        public float[] Brake { get; set; } = Array.Empty<float>();
        public float[] Gas { get; set; } = Array.Empty<float>();
        public float[] Steer { get; set; } = Array.Empty<float>();
        public float[] Yaw { get; set; } = Array.Empty<float>();
    }

    // NaN marks "waypoint not reached" — must round-trip.
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string Directory { get; }

    public CoachLapStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner", "CoachLaps");
    }

    public static string Key(TrackMap map, string car) =>
        TrackMapKeys.Sanitize($"{map.TrackName}__{map.ConfigName}__{(string.IsNullOrWhiteSpace(car) ? "any" : car.Trim())}");

    private string PathFor(TrackMap map, string car) => System.IO.Path.Combine(Directory, Key(map, car) + ".json");

    /// <summary>The saved personal best for this track/layout/car on this recorded line, or null.</summary>
    public CoachLap? Load(TrackMap map, string car)
    {
        try
        {
            var path = PathFor(map, car);
            if (!File.Exists(path)) return null;
            var r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Json);
            int n = map.Waypoints.Count;
            if (r == null || r.WaypointCount != n || r.TimeS.Length != n || r.LapTimeS <= 0) return null;
            return new CoachLap
            {
                LapNumber = r.LapNumber,
                LapTimeS = r.LapTimeS,
                TimeS = r.TimeS, SpeedKmh = r.SpeedKmh, Brake = r.Brake, Gas = r.Gas,
                Steer = r.Steer.Length == n ? r.Steer : Array.Empty<float>(),
                Yaw = r.Yaw.Length == n ? r.Yaw : Array.Empty<float>(),
                RecordedAtUtc = r.RecordedAtUtc,
                FromEarlierSession = true,
            };
        }
        catch { return null; } // a corrupt file just means "no PB yet"
    }

    public void Save(TrackMap map, string car, CoachLap lap)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var r = new Record
            {
                Track = map.TrackName, Config = map.ConfigName, Car = car,
                WaypointCount = map.Waypoints.Count,
                RecordedAtUtc = lap.RecordedAtUtc == default ? DateTime.UtcNow : lap.RecordedAtUtc,
                LapNumber = lap.LapNumber, LapTimeS = lap.LapTimeS,
                TimeS = lap.TimeS, SpeedKmh = lap.SpeedKmh, Brake = lap.Brake, Gas = lap.Gas,
                Steer = lap.Steer, Yaw = lap.Yaw,
            };
            var path = PathFor(map, car);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
            File.Move(tmp, path, overwrite: true); // atomic replace: never a half-written PB
        }
        catch { /* best effort — the session still has it in memory */ }
    }
}
