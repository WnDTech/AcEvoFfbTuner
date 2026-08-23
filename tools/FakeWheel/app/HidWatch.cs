using System.Runtime.InteropServices;

namespace FakeWheelApp;

/// <summary>
/// Enumerates HID device interface paths and reports whether a device with the
/// RS50 identity (VID 046D / PID C276) is present. The fake wheel's VHF child
/// devices appear exactly like the real USB HID instances to the raw-HID APIs.
/// </summary>
internal sealed class HidWatch : IDisposable
{
    private const int DIGCF_PRESENT = 0x2;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private static readonly Guid GuidHid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public nuint Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, nint hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint infoSet, IntPtr deviceInfoData, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint infoSet, ref SpDeviceInterfaceData data, IntPtr detailBuffer, int detailBufferSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint infoSet);

    public bool Rs50Present()
    {
        var devs = IntPtr.Zero;
        try
        {
            var guid = GuidHid;
            devs = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == new IntPtr(-1))
            {
                return false;
            }

            var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (var index = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref guid, index, ref data); index++)
            {
                _ = SetupDiGetDeviceInterfaceDetail(devs, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0)
                {
                    continue;
                }

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // Path starts right after cbSize (x86: 4, x64: 8).
                    Marshal.WriteInt32(buffer, IntPtr.Size);
                    if (SetupDiGetDeviceInterfaceDetail(devs, ref data, buffer, required, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + IntPtr.Size);
                        if (!string.IsNullOrEmpty(path) &&
                            path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase) &&
                            path.Contains("pid_c276", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            if (devs != IntPtr.Zero)
            {
                SetupDiDestroyDeviceInfoList(devs);
            }
        }

        return false;
    }

    public void Dispose()
    {
    }
}