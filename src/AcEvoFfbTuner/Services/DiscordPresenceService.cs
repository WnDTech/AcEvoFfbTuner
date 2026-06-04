using DiscordRPC;
using DiscordRPC.Logging;
using AcEvoFfbTuner.Core;
using AcEvoFfbTuner.Core.SharedMemory;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
namespace AcEvoFfbTuner.Services;
/// <summary>
/// Discord Rich Presence integration.
/// Shows car, track, speed, gear, and FFB output in the user's Discord status.
/// Runs on a 5-second poll timer — no event subscription management needed.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    private const string ClientId = "1512187437130973214";
    private DiscordRpcClient? _client;
    private TelemetryLoop? _loop;
    private Timer? _pollTimer;
    private bool _initialized;
    private bool _disposed;
    private string _gameName = "";
    private float _lastSpeed;
    private float _lastForce;
    private int _lastGear;
    private string _lastCar = "";
    private string _lastTrack = "";
    private bool _wasConnected;
    private DateTime _sessionStart = DateTime.UtcNow;
    private string _lastLaptimeString = "";
    private int _prevTotalLapCount;
    private int _maxCurrentLapTimeMs;
    private int _storedLapTimeMs;
    private int _totalLapCount;
    private float _npos;
    private int _currentSector;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public void Attach(TelemetryLoop loop)
    {
        _loop = loop;
        if (!_initialized) return;
        _lastCar = "";
        _lastTrack = "";
        _lastSpeed = 0;
        _lastForce = 0;
        _lastGear = 0;
        _lastLaptimeString = "";
        _prevTotalLapCount = 0;
        _maxCurrentLapTimeMs = 0;
        _storedLapTimeMs = 0;
        _totalLapCount = 0;
        _npos = 0;
        _currentSector = 0;
        _wasConnected = false;
        _pollTimer?.Change(TimeSpan.Zero, PollInterval);
    }
    public void Detach()
    {
        _loop = null;
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }
    public void Initialize()
    {
        if (_initialized) return;
        try
        {
            _client = new DiscordRpcClient(ClientId)
            {
                Logger = new NullLogger(),
                SkipIdenticalPresence = false
            };
            _client.OnReady += (_, msg) =>
            {
                System.Diagnostics.Debug.WriteLine($"[DiscordRPC] Ready: {msg.User.Username}");
                _initialized = true;
            };
            _client.OnConnectionFailed += (_, _) =>
            {
                System.Diagnostics.Debug.WriteLine("[DiscordRPC] Connection failed");
                _initialized = false;
            };
            _client.OnError += (_, msg) =>
            {
                System.Diagnostics.Debug.WriteLine($"[DiscordRPC] Error: {msg.Message}");
            };
            _client.OnClose += (_, _) =>
            {
                System.Diagnostics.Debug.WriteLine("[DiscordRPC] Connection closed");
                _initialized = false;
            };
            _client.Initialize();
            _pollTimer = new Timer(OnPoll, null, TimeSpan.FromSeconds(1), PollInterval);
            System.Diagnostics.Debug.WriteLine("[DiscordRPC] Service initialized");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscordRPC] Init failed: {ex.Message}");
            _initialized = false;
        }
    }
    public void Shutdown()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        try
        {
            if (_client != null)
            {
                _client.ClearPresence();
                _client.Dispose();
                _client = null;
            }
        }
        catch { }
        _initialized = false;
        _loop = null;
    }
    private void OnPoll(object? state)
    {
        if (_disposed) return;
        if (_client == null || !_initialized) return;
        if (_loop == null) return;
        var raw = _loop.LatestRaw;
        var proc = _loop.LatestProcessed;
        var readerField = typeof(TelemetryLoop).GetField("_reader",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var reader = readerField?.GetValue(_loop);
        var gameName = reader switch
        {
            RaceroomSharedMemoryReader => "RaceRoom",
            AssettoCorsaSharedMemoryReader => "Assetto Corsa",
            _ => "AC EVO"
        };
        var car = _loop.DetectedCarModel;
        var track = _loop.DetectedTrackName;
        var connected = _loop.IsGameConnected && _loop.LatestRaw?.IsEngineRunning == true;
        if (raw != null && proc != null)
        {
            _lastSpeed = raw.SpeedKmh;
            _lastForce = Math.Abs(proc.MainForce);
            _lastGear = raw.Gear;
            // Track lap completions via CurrentLapTimeMs resets
            int curLapTimeMs = _loop.CurrentLapTimeMs;
            int totalLapCount = _loop.TotalLapCount;
            if (totalLapCount > _prevTotalLapCount)
            {
                if (_maxCurrentLapTimeMs > 1000)
                    _storedLapTimeMs = _maxCurrentLapTimeMs;
                _maxCurrentLapTimeMs = 0;
            }
            _prevTotalLapCount = totalLapCount;
            if (curLapTimeMs > _maxCurrentLapTimeMs)
                _maxCurrentLapTimeMs = curLapTimeMs;
            _totalLapCount = totalLapCount;
            _npos = _loop.Npos;
            _currentSector = _loop.CurrentSector;
        }
        _gameName = gameName;
        _lastCar = car;
        _lastTrack = track;
        if (!connected)
        {
            if (_wasConnected)
            {
                _wasConnected = false;
                try { _client.ClearPresence(); } catch { }
            }
            return;
        }
            if (!_wasConnected) _sessionStart = DateTime.UtcNow;
            _wasConnected = true;
        UpdatePresence();
    }
    private void UpdatePresence()
    {
        if (_client == null || !_initialized) return;
        try
        {
            var details = string.IsNullOrEmpty(_lastCar)
                ? _gameName
                : $"{FormatCarName(_lastCar)} @ {FormatTrackName(_lastTrack)}";
            var gearStr = _gameName switch
            {
                "AC EVO" when _lastGear == 0 => "R",
                "AC EVO" when _lastGear == 1 => "N",
                "AC EVO" => (_lastGear - 1).ToString(),
                _ when _lastGear < 0 => "R",
                _ when _lastGear == 0 => "N",
                _ => _lastGear.ToString()
            };
            // Format lap time: ms -> m:ss.xxx
            var lapStr = _storedLapTimeMs > 0
                ? $"L{_totalLapCount} {_storedLapTimeMs / 60000}:{(_storedLapTimeMs % 60000) / 1000:00}.{_storedLapTimeMs % 1000:000} S{_currentSector}"
                : "";
            var forcePercent = Math.Clamp((int)(_lastForce * 100), 0, 100);
            var state = string.IsNullOrEmpty(lapStr)
                ? $"{_lastSpeed:F0} km/h  \u00b7  G{gearStr}"
                : $"{lapStr}  \u00b7  {_lastSpeed:F0} km/h  \u00b7  G{gearStr}";
            var presence = new RichPresence
            {
                Details = Truncate(details, 128),
                State = Truncate(state, 128),
                Timestamps = new Timestamps(_sessionStart),
                Assets = new Assets
                {
                    LargeImageKey = "app_icon",
                    LargeImageText = $"AC EVO FFB Tuner {GitHash.Commit}",
                    SmallImageKey = forcePercent > 90 ? "clipping" :
                                    _lastSpeed < 1 ? "stopped" : "driving",
                    SmallImageText = forcePercent > 90 ? "High FFB" :
                                     _lastSpeed < 1 ? "Stationary" : "On Track"
                }
            };
            _client.SetPresence(presence);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiscordRPC] SetPresence failed: {ex.Message}");
        }
    }
    private static string FormatCarName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var parts = raw.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpper(parts[i][0]) + parts[i][1..];
        }
        return string.Join(" ", parts);
    }
    private static string FormatTrackName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (raw.Length > 0)
            return char.ToUpper(raw[0]) + raw[1..];
        return raw;
    }
    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }
}