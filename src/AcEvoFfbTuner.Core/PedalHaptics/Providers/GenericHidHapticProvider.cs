using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.PedalHaptics.Providers;

public sealed class GenericHidHapticProvider : IPedalHapticProvider
{
    private IntPtr _hidHandle = InvalidHandle;
    private readonly int _reportLength;
    private readonly byte _reportId;
    private readonly ushort _vendorId;
    private readonly ushort _productId;
    private bool _disposed;

    private const string HidGuidString = "{4D1E55B2-F16F-11CF-88CB-001111000030}";
    private static Guid HidGuid => new(HidGuidString);
    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;

    public GenericHidHapticProvider() { }

    public GenericHidHapticProvider(ushort vendorId, ushort productId, byte reportId = 0x01, int reportLength = 64)
    {
        _vendorId = vendorId;
        _productId = productId;
        _reportId = reportId;
        _reportLength = reportLength;
    }

    public string DeviceName => $"Generic HID (0x{_vendorId:X4}:0x{_productId:X4})";
    public bool IsAvailable => _hidHandle != InvalidHandle && !_disposed;
    public bool IsBrakeSupported => true;
    public bool IsGasSupported => true;
    public bool IsClutchSupported => false;

    public bool Connect(ushort vendorId, ushort productId)
    {
        Disconnect();

        try
        {
            var hidGuid = HidGuid;
            var hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hInfo == InvalidHandle) return false;

            try
            {
                uint index = 0;
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

                while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref hidGuid, index++, ref iface))
                {
                    SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    var buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, buf, size, out _, IntPtr.Zero))
                            continue;

                        var path = Marshal.PtrToStringUni(buf + 4);
                        if (string.IsNullOrEmpty(path)) continue;

                        var hDev = CreateFile(path, GENERIC_READ_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (hDev == InvalidHandle) continue;

                        if (!HidD_GetAttributes(hDev, out var attrs))
                        { CloseHandle(hDev); continue; }

                        if (attrs.VendorID != vendorId || attrs.ProductID != productId)
                        { CloseHandle(hDev); continue; }

                        _hidHandle = hDev;
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(hInfo); }
        }
        catch { }

        return false;
    }

    public void Disconnect()
    {
        if (_hidHandle != InvalidHandle)
        {
            CloseHandle(_hidHandle);
            _hidHandle = InvalidHandle;
        }
    }

    public void SetBrakeHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        SendHapticReport(intensity);
    }

    public void SetGasHaptic(float intensity, HapticSignalType signal)
    {
        if (!IsAvailable) return;
        SendHapticReport(intensity);
    }

    public void SetClutchHaptic(float intensity, HapticSignalType signal) { }

    public void StopAll()
    {
        if (IsAvailable)
            SendHapticReport(0f);
    }

    private void SendHapticReport(float intensity)
    {
        if (_hidHandle == InvalidHandle) return;

        try
        {
            byte[] report = new byte[Math.Max(_reportLength, 2)];
            report[0] = _reportId;
            ushort amplitude = (ushort)Math.Clamp((int)(intensity * 65535f), 0, 65535);
            report[1] = (byte)(amplitude & 0xFF);
            if (_reportLength > 2)
                report[2] = (byte)((amplitude >> 8) & 0xFF);

            HidD_SetOutputReport(_hidHandle, report, report.Length);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid interfaceClassGuid;
        public int flags;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDAttributes
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr handle, IntPtr devInfo, ref Guid ifaceGuid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr handle, ref SpDeviceInterfaceData ifaceData, IntPtr detailBuf, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(IntPtr dev, out HidDAttributes attrs);

    [DllImport("hid.dll")]
    private static extern bool HidD_SetOutputReport(IntPtr dev, byte[] buf, int len);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
