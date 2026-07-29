using System.Diagnostics;

namespace AcEvoFfbTuner.Core.PedalHaptics;

public sealed class PedalHapticManager : IDisposable
{
    private readonly List<IPedalHapticProvider> _providers = [];
    private readonly PedalHapticSignals _signals = new();
    private readonly PedalHapticRouteConfig _config;

    private Thread? _hapticThread;
    private volatile bool _running;
    private bool _disposed;

    private const int HapticIntervalMs = 16; // ~60Hz

    public PedalHapticManager(PedalHapticRouteConfig config)
    {
        _config = config;
    }

    public PedalHapticSignals Signals => _signals;
    public PedalHapticRouteConfig Config => _config;
    public bool IsRunning => _running;

    public IReadOnlyList<IPedalHapticProvider> Providers => _providers;

    public void RegisterProvider(IPedalHapticProvider provider)
    {
        lock (_providers) _providers.Add(provider);
    }

    public void UnregisterProvider(IPedalHapticProvider provider)
    {
        lock (_providers) _providers.Remove(provider);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _hapticThread = new Thread(HapticLoop)
        {
            Name = "Pedal Haptic",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        _hapticThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _hapticThread?.Join(1000);
        _hapticThread = null;

        lock (_providers)
        {
            foreach (var p in _providers)
                p.StopAll();
        }
    }

    private void HapticLoop()
    {
        var sw = new Stopwatch();
        sw.Start();

        while (_running)
        {
            long startTicks = sw.ElapsedTicks;

            try
            {
                ProcessHapticTick();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PedalHapticManager] Error: {ex.Message}");
            }

            long elapsedTicks = sw.ElapsedTicks - startTicks;
            long targetTicks = HapticIntervalMs * Stopwatch.Frequency / 1000;
            long remainingTicks = targetTicks - elapsedTicks;

            if (remainingTicks > 0)
            {
                int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
                else
                    Thread.Yield();
            }
        }
    }

    private void ProcessHapticTick()
    {
        if (!_config.Enabled)
        {
            lock (_providers)
            {
                foreach (var p in _providers)
                    p.StopAll();
            }
            return;
        }

        IPedalHapticProvider[] providers;
        lock (_providers) providers = _providers.ToArray();

        if (providers.Length == 0)
            return;

        float abs = _signals.AbsModulation;
        float scrub = _signals.ScrubModulation;
        float rearSlip = _signals.RearSlipModulation;
        float roadForce = _signals.RoadForceModulation;
        float tc = _signals.TcRumble;
        float brakePressure = _signals.BrakePressureLevel;

        float brakeIntensity = 0f;
        float gasIntensity = 0f;

        foreach (var route in _config.Routes)
        {
            float signalValue = route.Signal.ToLowerInvariant() switch
            {
                "abs" => abs,
                "tc" => tc,
                "curb" => roadForce,
                "road" => roadForce,
                "scrub" => scrub,
                "rearslip" => rearSlip,
                "brakepressure" => brakePressure,
                _ => 0f
            };

            float routed = signalValue * route.Gain;

            switch (route.TargetPedal.ToLowerInvariant())
            {
                case "brake":
                    brakeIntensity = Math.Max(brakeIntensity, routed);
                    break;
                case "gas":
                    gasIntensity = Math.Max(gasIntensity, routed);
                    break;
                case "both":
                    brakeIntensity = Math.Max(brakeIntensity, routed);
                    gasIntensity = Math.Max(gasIntensity, routed);
                    break;
            }
        }

        float speedFade = Math.Clamp(_signals.SpeedKmh / 10f, 0f, 1f);
        brakeIntensity *= speedFade;
        gasIntensity *= speedFade;

        foreach (var provider in providers)
        {
            if (!provider.IsAvailable) continue;

            if (provider.IsBrakeSupported)
                provider.SetBrakeHaptic(brakeIntensity * _config.BrakeHapticGain, HapticSignalType.Abs);
            if (provider.IsGasSupported)
                provider.SetGasHaptic(gasIntensity * _config.GasHapticGain, HapticSignalType.Tc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        lock (_providers)
        {
            foreach (var p in _providers.OfType<IDisposable>())
                p.Dispose();
            _providers.Clear();
        }
    }
}
