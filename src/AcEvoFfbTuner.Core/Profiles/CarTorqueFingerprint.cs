using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcEvoFfbTuner.Core.Profiles;

/// <summary>
/// Car Character Normaliser — Phase 1 (measurement only, see docs/car-character-normaliser-spec.md).
///
/// Learns each car's core-torque distribution in the GAME'S NATIVE units, keyed by
/// game + car. Read-only: nothing here feeds back into any pipeline, so every game's
/// FFB is untouched.
///
/// The sampled signal is chosen per game in TelemetryLoop so it does NOT depend on
/// the user's own settings (R3E raw steering_force, not the settings-scaled
/// percentage) — that is what lets fingerprints from different users be pooled.
/// </summary>
public sealed class CarTorqueFingerprint
{
    // Log2 histogram: 8 bins per octave from 2^-14 (~6e-5) to 2^17 (~131k) covers
    // every game's native torque units (LMU Nm, R3E N, EVO finalFF).
    public const int BinsPerOctave = 8;
    public const int MinExp = -14;
    public const int MaxExp = 17;
    public const int BinCount = (MaxExp - MinExp) * BinsPerOctave;

    /// <summary>Valid driving needed before the fingerprint is trusted.</summary>
    public const double ConvergedSeconds = 90;
    public const int ConvergedLaps = 1;
    private const double HalveAboveCount = 2_000_000; // ~100 min at 333 Hz — keeps it tracking setup changes

    public string Game { get; set; } = "";
    public string Car { get; set; } = "";
    public string CarClass { get; set; } = "";
    /// <summary>Which telemetry signal filled the bins (e.g. "r3e.steering_force"). A change resets the bins.</summary>
    public string Signal { get; set; } = "";
    /// <summary>In-game FFB strength when known (EVO), sent with submissions so the server can check it.</summary>
    public float GameStrength { get; set; }
    public double ValidSeconds { get; set; }
    public int Laps { get; set; }
    /// <summary>ValidSeconds at the last successful community submission (0 = never).</summary>
    public double UploadedSeconds { get; set; }
    /// <summary>Slug used for the last submission; a slug-rule change re-submits once.</summary>
    public string UploadedSlug { get; set; } = "";
    public DateTime UpdatedUtc { get; set; }
    public float P50 { get; set; }
    public float P95 { get; set; }
    public float P99 { get; set; }
    public double[] Bins { get; set; } = new double[BinCount];
    /// <summary>LOCAL ONLY (never uploaded): P99 of the pipeline's own input signal
    /// (FfbProcessedData.RawFinalFf). Differs from P99 where the game pre-scales by
    /// per-car / user settings (R3E steering_force_percentage); Phase 2 gains use it.</summary>
    public float InputP99 { get; set; }
    public double[] InputBins { get; set; } = new double[BinCount];

    [JsonIgnore] public bool IsConverged => ValidSeconds >= ConvergedSeconds && Laps >= ConvergedLaps;

    /// <summary>Store file-name slug. The app is the source of truth: it is sent with each
    /// submission and the server only validates its format. Accents are folded (é→e) so
    /// "Mégane" becomes "megane", not "m-gane".</summary>
    public static string Slug(string car)
    {
        var folded = new System.Text.StringBuilder(car.Length);
        foreach (char ch in car.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                folded.Append(ch);
        var sb = new System.Text.StringBuilder(car.Length);
        bool dash = false;
        foreach (char ch in folded.ToString().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        return sb.ToString().TrimEnd('-');
    }

    public static int BinOf(float magnitude)
    {
        double e = Math.Log2(magnitude);
        int bin = (int)Math.Floor((e - MinExp) * BinsPerOctave);
        return Math.Clamp(bin, 0, BinCount - 1);
    }

    public void Add(float magnitude, double dtSeconds, float inputMagnitude = float.NaN)
    {
        if (!float.IsFinite(magnitude) || magnitude <= 0f) return;
        if (Bins.Length != BinCount) Bins = new double[BinCount];
        Bins[BinOf(magnitude)] += 1;
        if (float.IsFinite(inputMagnitude) && inputMagnitude > 0f)
        {
            if (InputBins == null || InputBins.Length != BinCount) InputBins = new double[BinCount];
            InputBins[BinOf(inputMagnitude)] += 1;
        }
        ValidSeconds += dtSeconds;
    }

    public void Reset(string signal)
    {
        Signal = signal;
        Bins = new double[BinCount];
        InputBins = new double[BinCount];
        ValidSeconds = 0; Laps = 0; UploadedSeconds = 0;
        P50 = P95 = P99 = InputP99 = 0;
    }

    /// <summary>Recomputes P50/P95/P99; halves counts when large so old setups fade out.</summary>
    public void Recompute()
    {
        double total = 0;
        foreach (var b in Bins) total += b;
        if (total <= 0) return;
        P50 = Percentile(Bins, 0.50, total);
        P95 = Percentile(Bins, 0.95, total);
        P99 = Percentile(Bins, 0.99, total);
        double inTotal = 0;
        if (InputBins != null) foreach (var b in InputBins) inTotal += b;
        if (inTotal > 0) InputP99 = Percentile(InputBins!, 0.99, inTotal);
        if (total > HalveAboveCount)
        {
            for (int i = 0; i < Bins.Length; i++) Bins[i] *= 0.5;
            if (InputBins != null) for (int i = 0; i < InputBins.Length; i++) InputBins[i] *= 0.5;
        }
        UpdatedUtc = DateTime.UtcNow;
    }

    public static float Percentile(double[] bins, double q, double total)
    {
        double target = q * total, cum = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            if (bins[i] <= 0) continue;
            if (cum + bins[i] >= target)
            {
                double frac = (target - cum) / bins[i];          // position inside the bin
                double e = MinExp + (i + frac) / BinsPerOctave;  // linear in log space
                return (float)Math.Pow(2, e);
            }
            cum += bins[i];
        }
        return (float)Math.Pow(2, MaxExp);
    }
}

/// <summary>Pooled fingerprint downloaded from the community store (no bins kept).</summary>
public sealed class CommunityFingerprint
{
    public string Game { get; set; } = "";
    public string Car { get; set; } = "";
    public string CarClass { get; set; } = "";
    public string Signal { get; set; } = "";
    public int Contributors { get; set; }
    public double TotalSeconds { get; set; }
    public float P50 { get; set; }
    public float P95 { get; set; }
    public float P99 { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Local time this copy was fetched; drives the re-check interval.</summary>
    public DateTime FetchedUtc { get; set; }
}

/// <summary>
/// Thread-safe store + sampler. <see cref="AddSample"/> runs on the telemetry thread
/// (cheap: one log2 + an array increment); saving is pushed off-thread.
/// </summary>
public sealed class CarTorqueFingerprintStore
{
    public const float MinSpeedKmh = 40f;
    private const double SaveEveryValidSeconds = 60;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Dictionary<string, CarTorqueFingerprint> _cars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommunityFingerprint> _community = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly string _communityPath;
    private CarTorqueFingerprint? _active;
    private int _lastLap = int.MinValue;
    private long _lastSampleTicks;
    private double _unsavedSeconds;
    private int _recomputeCounter;
    private int _saving;

    public CarTorqueFingerprintStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AcEvoFfbTuner", "car-fingerprints.json");
        _communityPath = Path.Combine(Path.GetDirectoryName(_path)!,
            Path.GetFileNameWithoutExtension(_path) + "-community.json");
        Load();
    }

    public static string Key(string game, string car) => $"{game}|{car}";

    /// <summary>Raised (telemetry thread) when the car being measured changes.</summary>
    public event Action<string, string>? ActiveCarChanged;

    /// <summary>Feed one telemetry frame. Invalid frames only reset the dt clock.</summary>
    public void AddSample(string game, string car, string carClass, string signal, float rawTorque,
        float speedKmh, bool inPitLane, bool stale, int currentLap, float gameStrength = 0f,
        float inputTorque = float.NaN)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _lastSampleTicks == 0 ? 0
            : Math.Min((now - _lastSampleTicks) / (double)System.Diagnostics.Stopwatch.Frequency, 0.05);
        _lastSampleTicks = now;

        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(car)) return;

        bool save = false, changed = false;
        lock (_lock)
        {
            if (_active == null || !string.Equals(_active.Car, car, StringComparison.OrdinalIgnoreCase)
                || _active.Game != game)
            {
                if (_active != null) { _active.Recompute(); save = true; }
                string key = Key(game, car);
                if (!_cars.TryGetValue(key, out var fp))
                    _cars[key] = fp = new CarTorqueFingerprint { Game = game, Car = car, Signal = signal };
                _active = fp;
                _lastLap = int.MinValue;
                changed = true;
            }
            if (_active.Signal != signal) _active.Reset(signal);
            if (!string.IsNullOrEmpty(carClass)) _active.CarClass = carClass;
            if (gameStrength > 0f) _active.GameStrength = gameStrength;

            // Lap counting: one lap per +1 step of the game's lap counter.
            if (currentLap != _lastLap)
            {
                if (_lastLap != int.MinValue && currentLap == _lastLap + 1) _active.Laps++;
                _lastLap = currentLap;
            }

            if (!(stale || inPitLane || speedKmh < MinSpeedKmh || !float.IsFinite(rawTorque)))
            {
                _active.Add(Math.Abs(rawTorque), dt, Math.Abs(inputTorque));
                _unsavedSeconds += dt;
                if (++_recomputeCounter >= 333) { _recomputeCounter = 0; _active.Recompute(); }
                if (_unsavedSeconds >= SaveEveryValidSeconds) { _active.Recompute(); save = true; }
            }
        }
        if (changed) ActiveCarChanged?.Invoke(game, car);
        if (save) SaveAsync();
    }

    /// <summary>Copy of the fingerprint being learned right now (null when no car).</summary>
    public CarTorqueFingerprint? Active
    {
        get { lock (_lock) return _active == null ? null : Clone(_active, withBins: false); }
    }

    public CommunityFingerprint? GetCommunity(string game, string car)
    {
        lock (_lock) return _community.TryGetValue(Key(game, car), out var c) ? c : null;
    }

    public void SetCommunity(CommunityFingerprint c)
    {
        lock (_lock) _community[Key(c.Game, c.Car)] = c;
        SaveAsync();
    }

    /// <summary>Converged fingerprints with enough NEW data since the last submission
    /// (first time, then each time valid driving grows by half). Copies with bins.</summary>
    public List<CarTorqueFingerprint> UploadCandidates(Func<string, bool> gameAllowed)
    {
        lock (_lock)
            return _cars.Values
                .Where(f => f.IsConverged && f.P99 > 0 && gameAllowed(f.Game)
                            && (f.UploadedSeconds <= 0 || f.ValidSeconds >= f.UploadedSeconds * 1.5
                                || f.UploadedSlug != CarTorqueFingerprint.Slug(f.Car)))
                .Select(f => Clone(f, withBins: true)).ToList();
    }

    public void MarkUploaded(string game, string car, double validSeconds)
    {
        lock (_lock)
            if (_cars.TryGetValue(Key(game, car), out var f))
            {
                f.UploadedSeconds = validSeconds;
                f.UploadedSlug = CarTorqueFingerprint.Slug(car);
            }
        SaveAsync();
    }

    /// <summary>
    /// Reference P99 for ratio readouts: median P99 of converged cars in the same game and
    /// class (falls back to the whole game). Community fingerprints (same signal) fill in
    /// cars not driven locally. Returns 0 when fewer than two cars are known.
    /// </summary>
    public float ReferenceP99(string game, string carClass)
    {
        lock (_lock)
        {
            var pool = new Dictionary<string, (string cls, float p99)>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _community.Values.Where(c => c.Game == game && c.P99 > 0))
                pool[c.Car] = (c.CarClass, c.P99);
            foreach (var f in _cars.Values.Where(f => f.Game == game && f.IsConverged && f.P99 > 0))
                pool[f.Car] = (f.CarClass, f.P99); // local measurement wins

            var all = pool.Values.ToList();
            var cls = string.IsNullOrEmpty(carClass) ? new List<(string cls, float p99)>()
                : all.Where(v => string.Equals(v.cls, carClass, StringComparison.OrdinalIgnoreCase)).ToList();
            var use = cls.Count >= 2 ? cls : all;
            if (use.Count < 2) return 0f;
            var p = use.Select(v => v.p99).OrderBy(v => v).ToList();
            return p.Count % 2 == 1 ? p[p.Count / 2] : 0.5f * (p[p.Count / 2 - 1] + p[p.Count / 2]);
        }
    }

    /// <summary>
    /// Phase 2: P99 of the pipeline INPUT signal for a car. Local measurement when the car is
    /// learned here; otherwise the community P99 converted with this user's own input/shared
    /// ratio (median over their learned cars of the game — exact for LMU where both signals are
    /// the same, an estimate for R3E where the game pre-scales per car). 0 = unknown.
    /// </summary>
    public float EstimatedInputP99(string game, string car)
    {
        lock (_lock) return EstimateLocked(game, car, out _);
    }

    /// <summary>Phase 2 reference: median estimated input P99 of the cars in the same class
    /// (falls back to the whole game); 0 when fewer than two cars are known.</summary>
    public float ReferenceInputP99(string game, string carClass)
    {
        lock (_lock)
        {
            var cars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _community.Values.Where(c => c.Game == game)) cars[c.Car] = c.CarClass;
            foreach (var f in _cars.Values.Where(f => f.Game == game && f.IsConverged)) cars[f.Car] = f.CarClass;
            var vals = new List<(string cls, float p)>();
            foreach (var (car, cls) in cars)
            {
                float p = EstimateLocked(game, car, out _);
                if (p > 0) vals.Add((cls, p));
            }
            var inClass = string.IsNullOrEmpty(carClass) ? new List<(string cls, float p)>()
                : vals.Where(v => string.Equals(v.cls, carClass, StringComparison.OrdinalIgnoreCase)).ToList();
            var use = inClass.Count >= 2 ? inClass : vals;
            if (use.Count < 2) return 0f;
            var p99 = use.Select(v => v.p).OrderBy(v => v).ToList();
            return p99.Count % 2 == 1 ? p99[p99.Count / 2] : 0.5f * (p99[p99.Count / 2 - 1] + p99[p99.Count / 2]);
        }
    }

    private float EstimateLocked(string game, string car, out bool local)
    {
        local = false;
        if (_cars.TryGetValue(Key(game, car), out var f) && f.IsConverged && f.InputP99 > 0)
        {
            local = true;
            return f.InputP99;
        }
        if (!_community.TryGetValue(Key(game, car), out var c) || c.P99 <= 0) return 0f;
        var ratios = _cars.Values
            .Where(x => x.Game == game && x.IsConverged && x.InputP99 > 0 && x.P99 > 0 && x.Signal == c.Signal)
            .Select(x => x.InputP99 / x.P99).OrderBy(r => r).ToList();
        if (ratios.Count == 0) return 0f;
        float factor = ratios.Count % 2 == 1 ? ratios[ratios.Count / 2]
            : 0.5f * (ratios[ratios.Count / 2 - 1] + ratios[ratios.Count / 2]);
        return c.P99 * factor;
    }

    public IReadOnlyList<CarTorqueFingerprint> All
    {
        get { lock (_lock) return _cars.Values.Select(f => Clone(f, withBins: false)).ToList(); }
    }

    public void SaveNow()
    {
        string json, community;
        lock (_lock)
        {
            _active?.Recompute();
            _unsavedSeconds = 0;
            json = JsonSerializer.Serialize(_cars.Values.ToList(), JsonOpts);
            community = JsonSerializer.Serialize(_community.Values.ToList(), JsonOpts);
        }
        WriteAtomic(_path, json);
        WriteAtomic(_communityPath, community);
    }

    private static void WriteAtomic(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* measurement data — never break the telemetry loop over it */ }
    }

    private void SaveAsync()
    {
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        Task.Run(() => { try { SaveNow(); } finally { Interlocked.Exchange(ref _saving, 0); } });
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path)
                && JsonSerializer.Deserialize<List<CarTorqueFingerprint>>(File.ReadAllText(_path)) is { } list)
            {
                foreach (var f in list)
                {
                    if (string.IsNullOrEmpty(f.Game) || string.IsNullOrEmpty(f.Car)) continue;
                    if (f.Bins == null || f.Bins.Length != CarTorqueFingerprint.BinCount)
                        f.Bins = new double[CarTorqueFingerprint.BinCount];
                    if (f.InputBins == null || f.InputBins.Length != CarTorqueFingerprint.BinCount)
                        f.InputBins = new double[CarTorqueFingerprint.BinCount];
                    _cars[Key(f.Game, f.Car)] = f;
                }
            }
        }
        catch { /* corrupt file → start fresh; next save overwrites it */ }
        try
        {
            if (File.Exists(_communityPath)
                && JsonSerializer.Deserialize<List<CommunityFingerprint>>(File.ReadAllText(_communityPath)) is { } cl)
                foreach (var c in cl)
                    if (!string.IsNullOrEmpty(c.Game) && !string.IsNullOrEmpty(c.Car))
                        _community[Key(c.Game, c.Car)] = c;
        }
        catch { }
    }

    private static CarTorqueFingerprint Clone(CarTorqueFingerprint f, bool withBins) => new()
    {
        Game = f.Game, Car = f.Car, CarClass = f.CarClass, Signal = f.Signal, GameStrength = f.GameStrength,
        ValidSeconds = f.ValidSeconds, Laps = f.Laps, UploadedSeconds = f.UploadedSeconds, UploadedSlug = f.UploadedSlug,
        UpdatedUtc = f.UpdatedUtc, P50 = f.P50, P95 = f.P95, P99 = f.P99, InputP99 = f.InputP99,
        Bins = withBins ? (double[])f.Bins.Clone() : Array.Empty<double>(),
    };
}
