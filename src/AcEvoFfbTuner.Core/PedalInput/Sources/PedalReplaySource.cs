using System.Diagnostics;
using System.Globalization;

namespace AcEvoFfbTuner.Core.PedalInput.Sources;

public sealed class PedalReplaySource : IPedalInputSource, IDisposable
{
    private readonly List<(float timeMs, float gas, float brake, float clutch)> _frames = [];
    private readonly Stopwatch _stopwatch = new();
    private readonly object _lock = new();
    private int _currentIndex;
    private bool _paused;
    private bool _disposed;
    private float _timeScale = 1.0f;

    public SourceType SourceType => SourceType.Replay;
    public string DeviceName => $"CSV Replay ({_frameCount} frames)";
    public bool IsAvailable => _frames.Count > 0 && !_disposed;

    public float TimeScale
    {
        get => _timeScale;
        set => _timeScale = Math.Clamp(value, 0.1f, 10f);
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public int CurrentFrame => _currentIndex;
    public int TotalFrames => _frameCount;

    private int _frameCount;

    public bool LoadFromCsv(string filePath)
    {
        lock (_lock)
        {
            _frames.Clear();
            _currentIndex = 0;
            _stopwatch.Reset();
        }

        try
        {
            if (!File.Exists(filePath))
                return false;

            var lines = File.ReadAllLines(filePath);
            if (lines.Length < 2) return false;

            var header = lines[0].Split(',');
            int timeIdx = Array.IndexOf(header, "Time");
            int gasIdx = Array.IndexOf(header, "Gas");
            int brakeIdx = Array.IndexOf(header, "Brake");

            if (gasIdx < 0 && brakeIdx < 0) return false;

            var frames = new List<(float timeMs, float gas, float brake, float clutch)>();
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 2) break;

                float timeMs = timeIdx >= 0 && timeIdx < parts.Length
                    ? float.TryParse(parts[timeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : (i - 1) * 3f
                    : (i - 1) * 3f;
                float gas = gasIdx >= 0 && gasIdx < parts.Length && float.TryParse(parts[gasIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : 0f;
                float brake = brakeIdx >= 0 && brakeIdx < parts.Length && float.TryParse(parts[brakeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 0f;

                frames.Add((timeMs, gas, brake, 0f));
            }

            lock (_lock)
            {
                _frames.Clear();
                _frames.AddRange(frames);
                _frameCount = _frames.Count;
            }

            return _frameCount > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PedalReplaySource] Load error: {ex.Message}");
            return false;
        }
    }

    public bool LoadFromSnapshotCsvData(Profiles.SnapshotCsvData csvData)
    {
        lock (_lock)
        {
            _frames.Clear();
            _currentIndex = 0;
            _stopwatch.Reset();
        }

        try
        {
            var lines = csvData.CsvLines;
            if (lines.Count < 2) return false;

            var header = lines[0].Split(',');
            int timeIdx = Array.IndexOf(header, "Time");
            int gasIdx = Array.IndexOf(header, "Gas");
            int brakeIdx = Array.IndexOf(header, "Brake");

            if (gasIdx < 0 && brakeIdx < 0) return false;

            var frames = new List<(float timeMs, float gas, float brake, float clutch)>();
            for (int i = 1; i < lines.Count; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 2) break;

                float timeMs = timeIdx >= 0 && timeIdx < parts.Length
                    ? float.TryParse(parts[timeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : (i - 1) * 3f
                    : (i - 1) * 3f;
                float gas = gasIdx >= 0 && gasIdx < parts.Length && float.TryParse(parts[gasIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : 0f;
                float brake = brakeIdx >= 0 && brakeIdx < parts.Length && float.TryParse(parts[brakeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 0f;

                frames.Add((timeMs, gas, brake, 0f));
            }

            lock (_lock)
            {
                _frames.Clear();
                _frames.AddRange(frames);
                _frameCount = _frames.Count;
            }

            return _frameCount > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PedalReplaySource] Load error: {ex.Message}");
            return false;
        }
    }

    public bool TryReadRaw(out RawPedalState state)
    {
        state = default;
        if (_frames.Count == 0 || _disposed) return false;

        lock (_lock)
        {
            if (_paused)
            {
                if (_currentIndex < _frames.Count)
                {
                    var f = _frames[_currentIndex];
                    state = new RawPedalState
                    {
                        GasRaw = f.gas,
                        BrakeRaw = f.brake,
                        ClutchRaw = f.clutch,
                        Source = SourceType.Replay,
                        TimestampTicks = Stopwatch.GetTimestamp()
                    };
                    return true;
                }
                return false;
            }

            if (!_stopwatch.IsRunning)
                _stopwatch.Start();

            double elapsedMs = _stopwatch.Elapsed.TotalMilliseconds * _timeScale;

            int idx = _frames.Count - 1;
            for (int i = _currentIndex; i < _frames.Count; i++)
            {
                if (_frames[i].timeMs > elapsedMs)
                {
                    idx = i > 0 ? i - 1 : 0;
                    break;
                }
                idx = i;
            }

            _currentIndex = Math.Min(idx + 1, _frames.Count - 1);

            if (idx >= 0 && idx < _frames.Count)
            {
                var f = _frames[idx];
                state = new RawPedalState
                {
                    GasRaw = f.gas,
                    BrakeRaw = f.brake,
                    ClutchRaw = f.clutch,
                    Source = SourceType.Replay,
                    TimestampTicks = Stopwatch.GetTimestamp()
                };
                return true;
            }

            if (_currentIndex >= _frames.Count - 1)
                _stopwatch.Reset();

            return false;
        }
    }

    public void Restart()
    {
        lock (_lock)
        {
            _currentIndex = 0;
            _stopwatch.Reset();
            _paused = false;
        }
    }

    public void StepForward()
    {
        lock (_lock)
        {
            if (_currentIndex < _frames.Count - 1)
                _currentIndex++;
            _paused = true;
        }
    }

    public void StepBackward()
    {
        lock (_lock)
        {
            if (_currentIndex > 0)
                _currentIndex--;
            _paused = true;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock) _frames.Clear();
    }
}
