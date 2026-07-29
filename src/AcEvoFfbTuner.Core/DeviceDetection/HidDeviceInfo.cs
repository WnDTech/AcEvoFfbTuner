namespace AcEvoFfbTuner.Core.DeviceDetection;

public sealed record HidDeviceInfo
{
    public string DevicePath { get; init; } = string.Empty;
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public string ProductString { get; init; } = string.Empty;
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort OutputReportByteLength { get; init; }
    public ushort FeatureReportByteLength { get; init; }
}
