using AcEvoFfbTuner.Core.Config;

namespace AcEvoFfbTuner.Core.PedalInput;

public sealed class PedalInputManager : IDisposable
{
    private readonly List<IPedalInputSource> _sources = [];
    private IPedalInputSource[] _sourceCache = [];
    private long _sourceVersion;
    private readonly PedalCalibration _calibration = new();
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly SourceType[] PriorityOrder =
    [
        SourceType.ScLink,
        SourceType.Hid,
        SourceType.DirectInput,
        SourceType.Replay,
        SourceType.Keyboard
    ];

    public IReadOnlyList<IPedalInputSource> Sources
    {
        get { lock (_lock) return _sources.ToArray(); }
    }

    public bool IsAnySourceAvailable
    {
        get { lock (_lock) return _sources.Any(s => s.IsAvailable); }
    }


    public void RegisterSource(IPedalInputSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
            _sources.Sort((a, b) =>
            {
                int idxA = Array.IndexOf(PriorityOrder, a.SourceType);
                int idxB = Array.IndexOf(PriorityOrder, b.SourceType);
                return idxA.CompareTo(idxB);
            });
            _sourceCache = _sources.ToArray();
            _sourceVersion++;
        }
    }

    public void UnregisterSource(IPedalInputSource source)
    {
        lock (_lock)
        {
            _sources.Remove(source);
            _sourceCache = _sources.ToArray();
            _sourceVersion++;
        }
    }

    public bool TryGetState(out PedalState state)
    {
        state = default;

        var config = PedalConfigManager.Instance.Config;
        if (!config.Enabled)
            return false;

        var sources = Volatile.Read(ref _sourceCache);

        foreach (var source in sources)
        {
            if (!source.IsAvailable)
                continue;

            if (!source.TryReadRaw(out var raw))
                continue;

            state = _calibration.Apply(raw, config);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Physical pedal state IGNORING the user Enabled toggle. The vJoy bridge
    /// must mirror the real pedals even when the user runs telemetry pedals
    /// (the Enabled=false default) — the toggle only governs whether physical
    /// pedals override game telemetry in the main loop, which is untouched.
    /// </summary>
    public bool TryGetPhysicalState(out PedalState state)    {
        state = default;

        var config = PedalConfigManager.Instance.Config;
        var sources = Volatile.Read(ref _sourceCache);

        foreach (var source in sources)
        {
            if (!source.IsAvailable)
                continue;

            if (!source.TryReadRaw(out var raw))
                continue;

            state = _calibration.Apply(raw, config);
            return true;
        }

        return false;
    }

    public string GetDiagnosticSummary()
    {
        lock (_lock)
        {
            if (_sources.Count == 0)
                return "No pedal input sources registered";
            var lines = _sources.Select(s =>
                $"  [{s.SourceType}] {s.DeviceName} — {(s.IsAvailable ? "available" : "unavailable")}");
            return string.Join("\n", lines);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var source in _sources.OfType<IDisposable>())
                source.Dispose();
            _sources.Clear();
        }
    }

    /// <summary>
    /// Uniform per-axis snapshot across wheelbase + separate-USB sources for
    /// the press-to-detect wizard. Wheelbase axes report DeviceIndex -1.
    /// </summary>
    public List<PedalAxisSample> ReadAllCandidateAxes()
    {
        var list = new List<PedalAxisSample>();
        try
        {
            foreach (var source in Volatile.Read(ref _sourceCache))
            {
                try
                {
                    if (source is Sources.FfbDevicePedalSource wb)
                    {
                        if (!wb.IsAvailable) continue;
                        var axes = wb.ReadAxes();
                        if (axes == null) continue;
                        foreach (var kv in axes)
                            list.Add(new PedalAxisSample { DeviceName = "Wheelbase", DeviceIndex = -1, Axis = kv.Key, Value = kv.Value });
                    }
                    else if (source is Sources.DirectInputPedalSource usb)
                    {
                        var devices = usb.Devices.ToList();
                        for (int i = 0; i < devices.Count; i++)
                        {
                            var axes = usb.ReadDeviceAxes(i);
                            if (axes == null) continue;
                            foreach (var kv in axes)
                                list.Add(new PedalAxisSample { DeviceName = devices[i].ProductName, DeviceIndex = i, Axis = kv.Key, Value = kv.Value });
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}

/// <summary>One readable axis on one pedal hardware device.</summary>
public sealed class PedalAxisSample
{
    public string DeviceName { get; init; } = "";
    /// <summary>-1 = wheelbase device, otherwise the USB device index.</summary>
    public int DeviceIndex { get; init; }
    public string Axis { get; init; } = "";
    public float Value { get; init; }
}

/// <summary>Press-to-detect matching: which axis moved most since baseline.</summary>
public static class PedalAxisDetector
{
    public sealed record MovedAxis(string DeviceName, int DeviceIndex, string Axis, float Delta, float Margin);

    /// <summary>
    /// Returns the axis that moved most, or null when nothing moved clearly.
    /// Requires minDelta travel AND minMargin daylight over the runner-up, so
    /// steering wiggle or jitter can't steal a pedal assignment.
    /// </summary>
    public static MovedAxis? FindMovedAxis(
        IReadOnlyList<PedalAxisSample> baseline,
        IReadOnlyList<PedalAxisSample> current,
        float minDelta = 0.35f,
        float minMargin = 0.15f)
    {
        var baseMap = baseline.ToDictionary(s => (s.DeviceIndex, s.Axis), s => s.Value);
        MovedAxis? best = null, second = null;
        foreach (var s in current)
        {
            if (!baseMap.TryGetValue((s.DeviceIndex, s.Axis), out float b)) continue;
            float delta = Math.Abs(s.Value - b);
            var cand = new MovedAxis(s.DeviceName, s.DeviceIndex, s.Axis, delta, 0f);
            if (best == null || delta > best.Delta) { second = best; best = cand; }
            else if (second == null || delta > second.Delta) { second = cand; }
        }
        if (best == null || best.Delta < minDelta) return null;
        float margin = best.Delta - (second?.Delta ?? 0f);
        if (margin < minMargin) return null;
        return best with { Margin = margin };
    }
}
