namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>One lap, sampled at every waypoint of the position map (first pass of each waypoint).</summary>
public sealed class CoachLap
{
    public int LapNumber { get; init; }
    public float LapTimeS { get; init; }
    /// <summary>Seconds since the lap started when the car first reached each waypoint (NaN = not reached).</summary>
    public float[] TimeS { get; init; } = Array.Empty<float>();
    public float[] SpeedKmh { get; init; } = Array.Empty<float>();
    public float[] Brake { get; init; } = Array.Empty<float>();
    public float[] Gas { get; init; } = Array.Empty<float>();
    /// <summary>Steering input (game units, signed) and yaw rate (rad/s, signed) per waypoint.
    /// Empty in personal bests saved before grip hints existed.</summary>
    public float[] Steer { get; init; } = Array.Empty<float>();
    public float[] Yaw { get; init; } = Array.Empty<float>();
    public DateTime RecordedAtUtc { get; init; } = DateTime.UtcNow;
    /// <summary>True for a personal best loaded from disk (set in an earlier session).</summary>
    public bool FromEarlierSession { get; init; }
}

/// <summary>How one corner of the last lap compared with the same corner on the best lap.</summary>
public sealed class CornerCoachResult
{
    public int CornerNumber { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Metres before the apex the brake went on (≥ 15 %). NaN = no braking.</summary>
    public float BrakePointM { get; init; } = float.NaN;
    public float BestBrakePointM { get; init; } = float.NaN;
    public float MinSpeedKmh { get; init; }
    public float BestMinSpeedKmh { get; init; }
    public float ExitSpeedKmh { get; init; }
    public float BestExitSpeedKmh { get; init; }
    /// <summary>Metres after the apex to full throttle (≥ 90 %). NaN = never.</summary>
    public float ThrottlePointM { get; init; } = float.NaN;
    public float BestThrottlePointM { get; init; } = float.NaN;
    /// <summary>Time from this corner's entry to the next corner's entry, minus the best lap's (s). + = slower.</summary>
    public float DeltaS { get; init; }
    /// <summary>Waypoint where the brake went on (−1 = none) — for the map markers.</summary>
    public int BrakeWaypoint { get; init; } = -1;
    public int BestBrakeWaypoint { get; init; } = -1;

    /// <summary>Extra steering for the same line vs the best lap at this corner (%, + = understeer).
    /// NaN = not measurable (no reference with steering data, too slow, too straight).</summary>
    public float UndersteerPct { get; init; } = float.NaN;
    /// <summary>Where a slide was caught with countersteer: "" none, "entry", "mid-corner", "on power".</summary>
    public string OversteerPhase { get; init; } = "";
    /// <summary>How many of the recent laps showed understeer / oversteer here, out of <see cref="HistoryLaps"/>.</summary>
    public int UndersteerLaps { get; set; }
    public int OversteerLaps { get; set; }
    public int HistoryLaps { get; set; }

    /// <summary>Grip hint for this corner, or "".</summary>
    public string GripNote
    {
        get
        {
            var notes = new List<string>();
            string Of(int k) => HistoryLaps > 1 ? $" ({k} of last {HistoryLaps} laps)" : "";
            if (!float.IsNaN(UndersteerPct) && UndersteerPct >= DriverCoach.UndersteerFlagPct)
                notes.Add($"understeer: {UndersteerPct:F0}% more steering than your best for the same line{Of(UndersteerLaps)}");
            if (OversteerPhase.Length > 0)
                notes.Add($"oversteer {OversteerPhase} — caught with countersteer{Of(OversteerLaps)}");
            return string.Join("; ", notes);
        }
    }

    /// <summary>The one most useful sentence for this corner, or "" when it was as good as the best lap.</summary>
    public string Advice
    {
        get
        {
            string grip = GripNote;
            if (DeltaS < 0.05f) return grip;
            var tips = new List<string>();
            if (grip.Length > 0) tips.Add(grip);
            if (!float.IsNaN(BrakePointM) && !float.IsNaN(BestBrakePointM))
            {
                float d = BrakePointM - BestBrakePointM; // + = braked earlier than best
                if (d > 8f) tips.Add($"braked {d:F0} m earlier");
                else if (d < -8f) tips.Add($"braked {-d:F0} m later — overshooting?");
            }
            float vmin = MinSpeedKmh - BestMinSpeedKmh;
            if (vmin < -3f) tips.Add($"{-vmin:F0} km/h slower mid-corner");
            float vexit = ExitSpeedKmh - BestExitSpeedKmh;
            if (vexit < -3f) tips.Add($"{-vexit:F0} km/h slower on exit");
            if (!float.IsNaN(ThrottlePointM) && !float.IsNaN(BestThrottlePointM) && ThrottlePointM - BestThrottlePointM > 10f)
                tips.Add($"full throttle {ThrottlePointM - BestThrottlePointM:F0} m later");
            return tips.Count == 0 ? "lost time on the run to the next corner" : string.Join("; ", tips);
        }
    }
}

/// <summary>Result of comparing the last completed lap with the best lap.</summary>
public sealed class CoachReport
{
    public int LapNumber { get; init; }
    public float LapTimeS { get; init; }
    public float BestLapTimeS { get; init; }
    public int BestLapNumber { get; init; }
    /// <summary>The reference is a saved personal best from an earlier session.</summary>
    public bool BestFromEarlierSession { get; init; }
    public DateTime BestRecordedAtUtc { get; init; }
    public bool IsNewBest { get; init; }
    /// <summary>False for the first valid lap: it becomes the reference and is not compared.</summary>
    public bool HasReference { get; set; } = true;
    public List<CornerCoachResult> Corners { get; init; } = new();
}

/// <summary>
/// Driver Coach: records time, speed, brake and throttle at each waypoint of the position map,
/// keeps the best lap of the session on the current map, and compares every completed lap with
/// it corner by corner (braking point, apex speed, exit speed, throttle point, time per corner).
/// Uses the map's corners — the stable, layout-derived ones once the Live Map is locked.
/// Pure telemetry; never touches FFB.
/// </summary>
public sealed class DriverCoach
{
    public const float BrakeOn = 0.15f;
    public const float FullThrottle = 0.9f;
    /// <summary>A lap must have reached this fraction of waypoints to count (no cuts / pit stops).</summary>
    public const float MinCoverage = 0.9f;
    /// <summary>A frame gap longer than this (pause, alt-tab, reset) invalidates the lap timing.</summary>
    public const double MaxGapS = 1.0;

    private readonly object _lock = new();
    private TrackMap? _map;
    private int _lap = -1;
    private double _lapStartS, _lastFrameS;
    private bool _lapValid;
    private float[]? _t, _v, _b, _g, _s, _y;
    private int _lastIdx = -1;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public CoachLap? BestLap { get; private set; }

    /// <summary>Live gap to the best lap at the car's current point (s, + = slower); null when
    /// there is no best yet or the current lap is not a timed lap.</summary>
    public float? LiveDeltaS { get; private set; }
    public CoachReport? LatestReport { get; private set; }
    /// <summary>Raised (on the telemetry thread) after each valid lap is analysed.</summary>
    public event Action<CoachReport>? LapAnalysed;

    /// <summary>
    /// Same recorded line: the saved map is re-loaded as a NEW object after every session reset
    /// (pit exit, restart), but its waypoints are identical — keep the best lap across that.
    /// A different track or a re-recorded line (other waypoint count) starts over.
    /// </summary>
    private static bool SameLine(TrackMap? a, TrackMap? b) =>
        a != null && b != null && a.Waypoints.Count == b.Waypoints.Count
        && string.Equals(a.TrackName, b.TrackName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ConfigName, b.ConfigName, StringComparison.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (_lock) { _lap = -1; BestLap = null; LatestReport = null; _bestLoaded = false; _gripHistory.Clear(); }
    }

    /// <summary>Saved personal bests. Null = session-only (tests).</summary>
    public CoachLapStore? Store { get; set; }

    private string _car = "";
    private bool _bestLoaded;

    /// <summary>Per-frame feed from the telemetry loop.</summary>
    public void Update(TrackMap? map, int lapNumber, int waypoint, float speedKmh, float brake, float gas, bool inPit,
        string car = "", float steer = 0f, float yawRate = 0f)
    {
        CoachReport? report = null;
        lock (_lock)
        {
            if (!ReferenceEquals(map, _map)) SetMapLocked(map);
            if (!string.Equals(car ?? "", _car, StringComparison.OrdinalIgnoreCase))
            {
                // A different car has a different personal best — and this lap is not comparable.
                _car = car ?? "";
                BestLap = null; LatestReport = null; _bestLoaded = false; _lap = -1; _t = null; _gripHistory.Clear();
            }
            if (!_bestLoaded && _map != null && Store != null)
            {
                _bestLoaded = true;
                BestLap = Store.Load(_map, _car);
            }
            if (_map == null || _map.Waypoints.Count < 50) return;
            int n = _map.Waypoints.Count;
            double now = _clock.Elapsed.TotalSeconds;

            if (lapNumber != _lap)
            {
                // Only a lap that began at a start/finish crossing seen live is a timed lap. The
                // first lap observed (joined mid-lap, out lap, standing start) is not — it showed
                // as a 2:20 "best" with 5 km/h at Abbey (Silverstone, 2026-11-08).
                bool crossed = _lap >= 0 && lapNumber == _lap + 1;
                if (crossed) report = CompleteLap(now);
                StartLap(lapNumber, now, n, crossed);
            }
            if (_t == null || waypoint < 0 || waypoint >= n) return;

            if (now - _lastFrameS > MaxGapS || inPit) _lapValid = false;
            _lastFrameS = now;

            float ts = (float)(now - _lapStartS);
            if (float.IsNaN(_t[waypoint]))
            {
                // Fill skipped waypoints (frames are coarser than ~3 m waypoints at speed) by
                // linear time interpolation from the previous one, forward only.
                if (_lastIdx >= 0)
                {
                    int gap = ((waypoint - _lastIdx) % n + n) % n;
                    if (gap > 1 && gap < 40 && !float.IsNaN(_t[_lastIdx]))
                    {
                        for (int k = 1; k < gap; k++)
                        {
                            int i = (_lastIdx + k) % n;
                            if (!float.IsNaN(_t[i])) continue;
                            float f = (float)k / gap;
                            _t[i] = _t[_lastIdx] + (ts - _t[_lastIdx]) * f;
                            _v[i] = _v![_lastIdx] + (speedKmh - _v[_lastIdx]) * f;
                            _b![i] = _b[_lastIdx] + (brake - _b[_lastIdx]) * f;
                            _g![i] = _g[_lastIdx] + (gas - _g[_lastIdx]) * f;
                            _s![i] = _s[_lastIdx] + (steer - _s[_lastIdx]) * f;
                            _y![i] = _y[_lastIdx] + (yawRate - _y[_lastIdx]) * f;
                        }
                    }
                }
                _t[waypoint] = ts;
                _v![waypoint] = speedKmh;
                _b![waypoint] = brake;
                _g![waypoint] = gas;
                _s![waypoint] = steer;
                _y![waypoint] = yawRate;
            }
            _lastIdx = waypoint;

            // Running delta to the best lap at this point of the lap (+ = slower). Only on a
            // timed lap: an out lap / joined-mid-lap time has no common start with the best.
            var best = BestLap;
            float bt = best != null && waypoint < best.TimeS.Length ? best.TimeS[waypoint] : float.NaN;
            LiveDeltaS = _lapValid && !float.IsNaN(bt) && ts > 1f ? ts - bt : null;
        }
        if (report != null) LapAnalysed?.Invoke(report);
    }

    private void SetMapLocked(TrackMap? map)
    {
        _map = map;
        _lap = -1;
        _t = null;
        // A reset clears the map for a moment (null) — keep the best lap through that and
        // compare the next map with the last real one.
        if (map == null) return;
        if (!SameLine(map, _lastLine)) { BestLap = null; LatestReport = null; _bestLoaded = false; _gripHistory.Clear(); }
        _lastLine = map;
    }

    private TrackMap? _lastLine;

    private void StartLap(int lapNumber, double now, int n, bool startedAtLine)
    {
        _lap = lapNumber;
        _lapStartS = now;
        _lastFrameS = now;
        _lapValid = startedAtLine && lapNumber > 0;
        _lastIdx = -1;
        _t = new float[n]; Array.Fill(_t, float.NaN);
        _v = new float[n]; _b = new float[n]; _g = new float[n]; _s = new float[n]; _y = new float[n];
    }

    private CoachReport? CompleteLap(double now)
    {
        if (_t == null || !_lapValid || _map == null) return null;
        int reached = _t.Count(x => !float.IsNaN(x));
        if (reached < _t.Length * MinCoverage) return null;

        var lap = new CoachLap
        {
            LapNumber = _lap,
            LapTimeS = (float)(now - _lapStartS),
            TimeS = _t, SpeedKmh = _v!, Brake = _b!, Gas = _g!, Steer = _s!, Yaw = _y!,
        };
        _t = null;

        bool newBest = BestLap == null || lap.LapTimeS < BestLap.LapTimeS;
        var reference = BestLap ?? lap; // first valid lap: its own figures, no comparison
        var report = Compare(_map, lap, reference, newBest);
        report.HasReference = BestLap != null;
        ApplyHistory(report);
        if (newBest)
        {
            BestLap = lap;
            if (Store != null)
            {
                var store = Store; var map = _map; var car = _car;
                Task.Run(() => store.Save(map, car, lap)); // off the telemetry thread
            }
        }
        LatestReport = report;
        return report;
    }

    // Grip flags of the last few timed laps per corner — a hint that repeats is a habit (or a
    // setup / FFB issue), a one-off is just a moment. Cleared with the best lap (new car / line).
    public const int HistoryDepth = 4;
    private readonly Dictionary<int, Queue<(bool under, bool over)>> _gripHistory = new();

    private void ApplyHistory(CoachReport report)
    {
        foreach (var c in report.Corners)
        {
            if (!_gripHistory.TryGetValue(c.CornerNumber, out var q)) _gripHistory[c.CornerNumber] = q = new();
            q.Enqueue((!float.IsNaN(c.UndersteerPct) && c.UndersteerPct >= UndersteerFlagPct, c.OversteerPhase.Length > 0));
            while (q.Count > HistoryDepth) q.Dequeue();
            c.HistoryLaps = q.Count;
            c.UndersteerLaps = q.Count(x => x.under);
            c.OversteerLaps = q.Count(x => x.over);
        }
    }

    /// <summary>Corner-by-corner comparison of <paramref name="lap"/> with <paramref name="best"/>.</summary>
    public static CoachReport Compare(TrackMap map, CoachLap lap, CoachLap best, bool isNewBest = false)
    {
        int n = map.Waypoints.Count;
        var cum = map.GetCumulativeDistances();
        float len = map.TrackLengthM > 0 ? map.TrackLengthM : cum[^1];
        var corners = map.Corners.OrderBy(c => c.CornerNumber).ToList();
        var results = new List<CornerCoachResult>();

        float Dist(int from, int to) { float d = cum[to] - cum[from]; return d < 0 ? d + len : d; }
        int Wrap(int i) => ((i % n) + n) % n;

        // Steering-vs-rotation sign convention differs per game; learn it from this lap's normal
        // cornering (most turning samples have steer and yaw in the "normal" relation).
        int lapSign = NormalSteerYawSign(lap);
        bool bestComparable = !ReferenceEquals(lap, best) && best.Steer.Length == n && best.Yaw.Length == n;

        for (int ci = 0; ci < corners.Count; ci++)
        {
            var c = corners[ci];
            var prev = corners[(ci - 1 + corners.Count) % corners.Count];
            var next = corners[(ci + 1) % corners.Count];

            int approachFrom = corners.Count > 1 ? prev.EndWaypointIndex : Wrap(c.StartWaypointIndex - n / 4);
            int apex = c.ApexWaypointIndex;
            int exitIdx = c.EndWaypointIndex;
            int nextEntry = next.StartWaypointIndex;

            var (bp, bpIdx) = BrakePoint(lap, approachFrom, apex, n, Dist);
            var (bbp, bbpIdx) = BrakePoint(best, approachFrom, apex, n, Dist);

            results.Add(new CornerCoachResult
            {
                CornerNumber = c.CornerNumber,
                Label = c.Label,
                BrakePointM = bp, BestBrakePointM = bbp,
                BrakeWaypoint = bpIdx, BestBrakeWaypoint = bbpIdx,
                MinSpeedKmh = MinSpeed(lap, c.StartWaypointIndex, exitIdx, n),
                BestMinSpeedKmh = MinSpeed(best, c.StartWaypointIndex, exitIdx, n),
                ExitSpeedKmh = lap.SpeedKmh[exitIdx],
                BestExitSpeedKmh = best.SpeedKmh[exitIdx],
                ThrottlePointM = ThrottlePoint(lap, apex, nextEntry, n, Dist),
                BestThrottlePointM = ThrottlePoint(best, apex, nextEntry, n, Dist),
                DeltaS = Segment(lap, c.StartWaypointIndex, nextEntry) - Segment(best, c.StartWaypointIndex, nextEntry),
                UndersteerPct = bestComparable ? UndersteerVsBest(lap, best, c.StartWaypointIndex, exitIdx, n) : float.NaN,
                OversteerPhase = OversteerPhaseAt(lap, lapSign, c.StartWaypointIndex, apex, Wrap(exitIdx + 15), n),
            });
        }

        return new CoachReport
        {
            LapNumber = lap.LapNumber,
            LapTimeS = lap.LapTimeS,
            // The REFERENCE lap compared against (the previous best when this lap is a new best).
            BestLapTimeS = best.LapTimeS,
            BestLapNumber = best.LapNumber,
            BestFromEarlierSession = best.FromEarlierSession,
            BestRecordedAtUtc = best.RecordedAtUtc,
            IsNewBest = isNewBest,
            Corners = results,
        };
    }

    /// <summary>Time between two waypoints within one lap; crossing the lap start wraps by the lap time.</summary>
    private static float Segment(CoachLap lap, int from, int to)
    {
        if (from == to) return lap.LapTimeS; // single-corner map: the segment is the whole lap
        float a = lap.TimeS[from], b = lap.TimeS[to];
        if (float.IsNaN(a) || float.IsNaN(b)) return 0f;
        float d = b - a;
        return d < 0 ? d + lap.LapTimeS : d;
    }

    private static (float m, int idx) BrakePoint(CoachLap lap, int from, int apex, int n, Func<int, int, float> dist)
    {
        int span = ((apex - from) % n + n) % n;
        for (int k = 0; k <= span; k++)
        {
            int i = (from + k) % n;
            if (lap.Brake[i] >= BrakeOn) return (dist(i, apex), i);
        }
        return (float.NaN, -1);
    }

    private static float ThrottlePoint(CoachLap lap, int apex, int to, int n, Func<int, int, float> dist)
    {
        int span = ((to - apex) % n + n) % n;
        for (int k = 0; k <= span; k++)
        {
            int i = (apex + k) % n;
            if (lap.Gas[i] >= FullThrottle) return dist(apex, i);
        }
        return float.NaN;
    }

    // ── Grip analysis (game-independent: steering, yaw rate and speed only) ──

    /// <summary>Flag understeer at this much extra steering for the same line vs the best lap.</summary>
    public const float UndersteerFlagPct = 20f;
    private const float MinCornerSpeedKmh = 40f;
    private const float MinYawRate = 0.08f;   // rad/s — below this the car is barely turning
    private const int MinCountersteerPts = 3;  // ≈ 10 m of opposite lock — not a twitch

    /// <summary>
    /// Steering per unit of path curvature (|steer| ÷ (|yaw| ÷ v)), median over a corner. Same car,
    /// same corner: needing more of it than on the best lap = the front is not delivering the
    /// rotation the steering asks for (understeer). Units cancel in the ratio.
    /// </summary>
    private static float SteerPerCurvature(CoachLap lap, int from, int to, int n)
    {
        var vals = new List<float>();
        int span = ((to - from) % n + n) % n;
        for (int k = 0; k <= span; k++)
        {
            int i = (from + k) % n;
            float v = lap.SpeedKmh[i] / 3.6f, yaw = MathF.Abs(lap.Yaw[i]), st = MathF.Abs(lap.Steer[i]);
            if (lap.SpeedKmh[i] < MinCornerSpeedKmh || yaw < MinYawRate || st < 1e-4f) continue;
            vals.Add(st / (yaw / v));
        }
        if (vals.Count < 5) return float.NaN;
        vals.Sort();
        return vals[vals.Count / 2];
    }

    private static float UndersteerVsBest(CoachLap lap, CoachLap best, int from, int to, int n)
    {
        float a = SteerPerCurvature(lap, from, to, n), b = SteerPerCurvature(best, from, to, n);
        if (float.IsNaN(a) || float.IsNaN(b) || b <= 0) return float.NaN;
        return (a / b - 1f) * 100f;
    }

    /// <summary>+1 or −1: the sign of steer×yaw while cornering normally on this lap (0 = unknown).</summary>
    private static int NormalSteerYawSign(CoachLap lap)
    {
        if (lap.Steer.Length != lap.Yaw.Length || lap.Steer.Length == 0) return 0;
        int pos = 0, neg = 0;
        for (int i = 0; i < lap.Steer.Length; i++)
        {
            if (lap.SpeedKmh[i] < MinCornerSpeedKmh || MathF.Abs(lap.Yaw[i]) < MinYawRate * 1.5f) continue;
            float p = lap.Steer[i] * lap.Yaw[i];
            if (p > 0) pos++; else if (p < 0) neg++;
        }
        if (pos + neg < 50) return 0;
        return pos >= neg ? 1 : -1;
    }

    /// <summary>
    /// Countersteer = steering opposite to the car's rotation while it is clearly rotating: the
    /// rear is stepping out. Returns where it happened first: "entry" (before the apex, braking),
    /// "on power" (after the apex, throttle ≥ 50 %), else "mid-corner"; "" when none.
    /// </summary>
    private static string OversteerPhaseAt(CoachLap lap, int normalSign, int from, int apex, int to, int n)
    {
        if (normalSign == 0 || lap.Steer.Length != n) return "";
        int span = ((to - from) % n + n) % n;
        int run = 0, firstIdx = -1;
        for (int k = 0; k <= span; k++)
        {
            int i = (from + k) % n;
            bool counter = lap.SpeedKmh[i] >= MinCornerSpeedKmh
                && MathF.Abs(lap.Yaw[i]) >= MinYawRate * 1.5f
                && MathF.Abs(lap.Steer[i]) > 1e-3f
                && MathF.Sign(lap.Steer[i] * lap.Yaw[i]) == -normalSign;
            if (counter)
            {
                if (run == 0) firstIdx = i;
                if (++run >= MinCountersteerPts)
                {
                    bool beforeApex = ((firstIdx - from) % n + n) % n < ((apex - from) % n + n) % n;
                    if (beforeApex) return lap.Brake[firstIdx] >= BrakeOn ? "on entry under braking" : "on entry";
                    return lap.Gas[firstIdx] >= 0.5f ? "on power" : "mid-corner";
                }
            }
            else run = 0;
        }
        return "";
    }

    private static float MinSpeed(CoachLap lap, int from, int to, int n)
    {
        int span = ((to - from) % n + n) % n;
        float min = float.MaxValue;
        for (int k = 0; k <= span; k++)
        {
            float v = lap.SpeedKmh[(from + k) % n];
            if (v > 0 && v < min) min = v;
        }
        return min == float.MaxValue ? 0 : min;
    }
}
