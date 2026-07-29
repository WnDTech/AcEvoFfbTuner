using DI = SharpDX.DirectInput;

namespace AcEvoFfbTuner.Core.PedalInput.Sources;

public sealed class DirectInputPedalSource : IPedalInputSource, IDisposable
{
    private DI.DirectInput? _directInput;
    private readonly List<PedalDeviceHandle> _allDevices = [];
    private readonly AxisMap _axisMapping = new();
    private int _activeDeviceIndex;
    private bool _disposed;
    private bool _initialized;

    public SourceType SourceType => SourceType.DirectInput;

    public string DeviceName => _allDevices.Count > 0
        ? _activeDeviceIndex >= 0 && _activeDeviceIndex < _allDevices.Count
            ? _allDevices[_activeDeviceIndex].ProductName
            : $"DirectInput ({_allDevices.Count} device{( _allDevices.Count != 1 ? "s" : "")})"
        : "DirectInput (no pedals)";

    public bool IsAvailable => _allDevices.Count > 0;

    /// <summary>Number of detected DirectInput devices with pedal-like axes.</summary>
    public int DeviceCount => _allDevices.Count;

    /// <summary>Index of the currently selected device. -1 means auto.</summary>
    public int ActiveDeviceIndex
    {
        get => _activeDeviceIndex;
        set => _activeDeviceIndex = Math.Clamp(value, -1, _allDevices.Count - 1);
    }

    public IReadOnlyList<DeviceInfo> Devices => _allDevices.Select(d => new DeviceInfo
    {
        Index = _allDevices.IndexOf(d),
        ProductName = d.ProductName,
        InstanceGuid = d.InstanceGuid,
        AxisCount = d.AxisCount,
        IsFfbCapable = d.IsFfbCapable
    }).ToArray();

    public bool Initialize()
    {
        if (_initialized) return true;
        if (_disposed) return false;

        try
        {
            _directInput = new DI.DirectInput();
            EnumerateAllDevices();
            _initialized = true;
            return _allDevices.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectInputPedalSource] Init error: {ex.Message}");
            return false;
        }
    }

    public void RefreshDevices()
    {
        _allDevices.Clear();
        _initialized = false;
        Initialize();
    }

    private static readonly string[] NonPedalPatterns =
    [
        "throttle", "flight", "joystick", "yoke", "stick", "Q-Eng", "quadrant",
        "hotas", "sidestick", "collective", "rudder",
        "controller", "gamepad", "xbox", "playstation", "dual", "wireless",
        "keyboard", "mouse",
        "winwing", "fcu", "efis", "ctrl", "airbus", "boeing", "tca"
    ];

    private readonly List<string> _enumLog = [];

    public string GetEnumerationLog() => string.Join("\n", _enumLog);

    private void EnumerateAllDevices()
    {
        if (_directInput == null) return;

        _enumLog.Clear();
        _enumLog.Add($"PedalSource: Scanning GameControl devices...");

        var allInstances = _directInput.GetDevices(DI.DeviceClass.GameControl, DI.DeviceEnumerationFlags.AllDevices);
        var ffbGuids = new HashSet<Guid>(
            _directInput.GetDevices(DI.DeviceClass.GameControl, DI.DeviceEnumerationFlags.ForceFeedback)
                .Select(d => d.InstanceGuid));

        _enumLog.Add($"  Total GameControl devices: {allInstances.Count}");

        foreach (var instance in allInstances)
        {
            try
            {
                string name = (instance.ProductName ?? "").ToUpperInvariant();
                bool isFfb = ffbGuids.Contains(instance.InstanceGuid);

                if (isFfb)
                {
                    _enumLog.Add($"  SKIP (FFB): \"{instance.ProductName}\"");
                    continue;
                }

                if (!IsPedalDevice(name))
                {
                    _enumLog.Add($"  SKIP (name): \"{instance.ProductName}\"");
                    continue;
                }

                var joystick = new DI.Joystick(_directInput, instance.InstanceGuid);
                joystick.SetCooperativeLevel(IntPtr.Zero, DI.CooperativeLevel.NonExclusive | DI.CooperativeLevel.Background);

                _allDevices.Add(new PedalDeviceHandle
                {
                    Joystick = joystick,
                    ProductName = instance.ProductName,
                    InstanceGuid = instance.InstanceGuid,
                    IsFfbCapable = isFfb
                });
                _enumLog.Add($"  ADDED: \"{instance.ProductName}\"");
            }
            catch (Exception ex)
            {
                _enumLog.Add($"  ERROR: \"{instance.ProductName}\": {ex.Message}");
            }
        }

        _enumLog.Add($"  Final candidates: {_allDevices.Count}");
        for (int i = 0; i < _allDevices.Count; i++)
            _enumLog.Add($"    [{i}] \"{_allDevices[i].ProductName}\"");
    }

    private static bool IsPedalDevice(string name)
    {
        // Explicit non-pedal patterns (flight sim gear, gamepads, etc.)
        foreach (var pattern in NonPedalPatterns)
        {
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                // Exception: if name ALSO contains a pedal-like term, it might be pedals
                bool hasPedalKeyword = name.Contains("BRAKE") || name.Contains("CLUTCH") || name.Contains("ACCEL") || name.Contains("PEDAL") || name.Contains("LOAD");
                if (!hasPedalKeyword)
                    return false;
            }
        }
        return true;
    }

    public bool TryReadRaw(out RawPedalState state)
    {
        state = default;
        if (!_initialized && !Initialize())
            return false;

        if (_allDevices.Count == 0)
            return false;

        PedalDeviceHandle? target;

        if (_activeDeviceIndex >= 0 && _activeDeviceIndex < _allDevices.Count)
        {
            target = _allDevices[_activeDeviceIndex];
        }
        else
        {
            // Auto: pick the device with the most axes (likely wheelbase+pedals)
            target = _allDevices.OrderByDescending(d => d.AxisCount).FirstOrDefault();
        }

        if (target == null) return false;

        try
        {
            if (!target.Acquired)
            {
                try { target.Joystick.Acquire(); target.Acquired = true; }
                catch { /* NonExclusive acquire may fail alongside Exclusive — skip */ }
            }

            target.Joystick.Poll();
            var js = target.Joystick.GetCurrentState();

            float valX = NormalizeAxis(js.X);
            float valY = NormalizeAxis(js.Y);
            float valZ = NormalizeAxis(js.Z);
            float valRx = NormalizeAxis(js.RotationX);
            float valRy = NormalizeAxis(js.RotationY);
            float valRz = NormalizeAxis(js.RotationZ);
            float valSl0 = js.Sliders is { Length: > 0 } ? NormalizeAxis(js.Sliders[0]) : 0f;
            float valSl1 = js.Sliders is { Length: > 1 } ? NormalizeAxis(js.Sliders[1]) : 0f;

            float ReadMapped(string name) => name switch
            {
                "X" => valX, "Y" => valY, "Z" => valZ,
                "Rx" => valRx, "Ry" => valRy, "Rz" => valRz,
                "Slider0" => valSl0, "Slider1" => valSl1,
                _ => 0f
            };

            float gas = ReadMapped(_axisMapping.GasAxis);
            float brake = ReadMapped(_axisMapping.BrakeAxis);
            float clutch = ReadMapped(_axisMapping.ClutchAxis);

            if (_axisMapping.GasInvert) gas = 1f - gas;
            if (_axisMapping.BrakeInvert) brake = 1f - brake;
            if (_axisMapping.ClutchInvert) clutch = 1f - clutch;

            state = new RawPedalState
            {
                GasRaw = gas,
                BrakeRaw = brake,
                ClutchRaw = clutch,
                Source = SourceType.DirectInput,
                TimestampTicks = System.Diagnostics.Stopwatch.GetTimestamp()
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>User-configurable axis-to-pedal mapping.</summary>
    public AxisMap Mapping => _axisMapping;

    /// <summary>Reads raw axis values from every detected device (for diagnostic display).</summary>
    public Dictionary<string, DeviceAxisSnapshot> ReadAllDeviceAxes()
    {
        var result = new Dictionary<string, DeviceAxisSnapshot>();

        foreach (var dev in _allDevices)
        {
            try
            {
                if (!dev.Acquired)
                {
                    try { dev.Joystick.Acquire(); dev.Acquired = true; }
                    catch { }
                }

                dev.Joystick.Poll();
                var js = dev.Joystick.GetCurrentState();

                result[dev.ProductName] = new DeviceAxisSnapshot
                {
                    X = NormalizeAxis(js.X),
                    Y = NormalizeAxis(js.Y),
                    Z = NormalizeAxis(js.Z),
                    RotationX = NormalizeAxis(js.RotationX),
                    RotationY = NormalizeAxis(js.RotationY),
                    RotationZ = NormalizeAxis(js.RotationZ),
                    Sliders = js.Sliders?.Select(NormalizeAxis).ToArray() ?? [],
                    DeviceName = dev.ProductName,
                    AxisCount = dev.AxisCount,
                    IsFfbCapable = dev.IsFfbCapable
                };
            }
            catch { }
        }

        return result;
    }

    private static int CountAxes(DI.JoystickState state)
    {
        int count = 0;
        if (Math.Abs(state.X) > 1) count++;
        if (Math.Abs(state.Y) > 1) count++;
        if (Math.Abs(state.Z) > 1) count++;
        if (Math.Abs(state.RotationX) > 1) count++;
        if (Math.Abs(state.RotationY) > 1) count++;
        if (Math.Abs(state.RotationZ) > 1) count++;
        if (state.Sliders is { Length: > 0 })
            count += state.Sliders.Count(s => Math.Abs(s) > 1);
        return count;
    }

    private static float NormalizeAxis(int raw)
    {
        if (raw >= 0)
            return raw / 65535f;
        return (raw + 32768f) / 32767f;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pd in _allDevices)
        {
            try { pd.Joystick.Dispose(); }
            catch { }
        }
        _allDevices.Clear();
        _directInput?.Dispose();
        _directInput = null;
    }

    public sealed class DeviceInfo
    {
        public int Index { get; init; }
        public string ProductName { get; init; } = "";
        public Guid InstanceGuid { get; init; }
        public int AxisCount { get; init; }
        public bool IsFfbCapable { get; init; }
    }

    public sealed class DeviceAxisSnapshot
    {
        public string DeviceName { get; init; } = "";
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
        public float RotationX { get; init; }
        public float RotationY { get; init; }
        public float RotationZ { get; init; }
        public float[] Sliders { get; init; } = [];
        public int AxisCount { get; init; }
        public bool IsFfbCapable { get; init; }
    }

    private sealed class PedalDeviceHandle
    {
        public DI.Joystick Joystick { get; init; } = null!;
        public string ProductName { get; init; } = "";
        public Guid InstanceGuid { get; init; }
        public bool IsFfbCapable { get; init; }
        public bool Acquired { get; set; }
        public int AxisCount
        {
            get
            {
                if (_axisCount >= 0) return _axisCount;
                try
                {
                    if (!Acquired) { try { Joystick.Acquire(); Acquired = true; } catch { } }
                    Joystick.Poll();
                    _axisCount = CountAxes(Joystick.GetCurrentState());
                }
                catch { _axisCount = 0; }
                return _axisCount;
            }
        }
        private int _axisCount = -1;
    }

    public sealed class AxisMap
    {
        public string GasAxis { get; set; } = "Y";
        public string BrakeAxis { get; set; } = "Z";
        public string ClutchAxis { get; set; } = "Rx";
        public bool GasInvert { get; set; }
        public bool BrakeInvert { get; set; }
        public bool ClutchInvert { get; set; }

        public static readonly string[] AvailableAxes = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider0", "Slider1"];
    }
}
