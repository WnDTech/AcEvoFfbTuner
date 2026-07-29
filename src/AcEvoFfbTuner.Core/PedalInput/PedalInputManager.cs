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
}
