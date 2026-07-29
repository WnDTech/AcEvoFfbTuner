using AcEvoFfbTuner.Core.DirectInput;

namespace AcEvoFfbTuner.Core.PedalInput.Sources;

/// <summary>
/// Reads pedal axis values from the FfbDeviceManager's already-acquired
/// exclusive DirectInput connection. This bypasses the exclusive-access
/// limitation that prevents a second DirectInput handle from reading
/// the wheelbase device simultaneously.
///
/// Only the Y/Z/Rx axes from the wheelbase device are mapped to pedals.
/// Users with separate USB pedals (not connected through the wheelbase)
/// should use DirectInputPedalSource instead.
/// </summary>
public sealed class FfbDevicePedalSource : IPedalInputSource, IDisposable
{
    private readonly FfbDeviceManager _deviceManager;
    private readonly AxisMap _mapping = new();
    private bool _disposed;

    public FfbDevicePedalSource(FfbDeviceManager deviceManager)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
    }

    public SourceType SourceType => SourceType.DirectInput;
    public string DeviceName => "Wheelbase (via FfbDeviceManager)";
    public bool IsAvailable => _deviceManager?.IsDeviceAcquired == true;

    public AxisMap Mapping => _mapping;

    /// <summary>
    /// Reads pedal axes directly from the FfbDeviceManager's exclusive
    /// DirectInput handle. No second DI connection needed.
    /// </summary>
    public bool TryReadRaw(out RawPedalState state)
    {
        state = default;
        if (_disposed) return false;

        var snapshot = _deviceManager.ReadAllAxes();
        if (snapshot == null) return false;

        float ReadMapped(string name) => name switch
        {
            "X" => snapshot.X, "Y" => snapshot.Y, "Z" => snapshot.Z,
            "Rx" => snapshot.RotationX, "Ry" => snapshot.RotationY, "Rz" => snapshot.RotationZ,
            "Slider0" => snapshot.Sliders.Length > 0 ? snapshot.Sliders[0] : 0f,
            "Slider1" => snapshot.Sliders.Length > 1 ? snapshot.Sliders[1] : 0f,
            _ => 0f
        };

        float gas = ReadMapped(_mapping.GasAxis);
        float brake = ReadMapped(_mapping.BrakeAxis);
        float clutch = ReadMapped(_mapping.ClutchAxis);

        if (_mapping.GasInvert) gas = 1f - gas;
        if (_mapping.BrakeInvert) brake = 1f - brake;
        if (_mapping.ClutchInvert) clutch = 1f - clutch;

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

    public void Dispose()
    {
        _disposed = true;
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
