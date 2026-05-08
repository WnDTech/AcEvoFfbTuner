namespace AcEvoFfbTuner.Core.FfbProviders;

public sealed class HapticData
{
    public float VibrationIntensity { get; set; }
    public int VibrationFrequencyHz { get; set; } = 80;
    public float AbsRumble { get; set; }
    public float TcRumble { get; set; }
    public float RpmFrequency { get; set; }
}
