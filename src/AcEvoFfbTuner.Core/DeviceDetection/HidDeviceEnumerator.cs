using System.Runtime.InteropServices;
using System.Text;

namespace AcEvoFfbTuner.Core.DeviceDetection;

internal static class HidDeviceEnumerator
{
    private static Guid HidGuid => new(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
    private static readonly IntPtr InvalidHandle = new(-1);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_RW = 0x03;
    private const uint OPEN_EXISTING = 3;

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

    [DllImport("hid.dll", CharSet = CharSet.Auto)]
    private static extern bool HidD_GetProductString(IntPtr dev, byte[] buf, int len);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(IntPtr dev, out IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern bool HidP_GetCaps(IntPtr preparsed, out HidPCaps caps);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(ref IntPtr preparsed);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidDAttributes
    {
        public int Size;
        public ushort Vid;
        public ushort Pid;
        public short Version;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidPCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    public static List<HidDeviceInfo> EnumerateByVid(ushort vendorId)
    {
        var results = new List<HidDeviceInfo>();
        var hidGuid = new Guid(0x4D1E55B2, 0xF16F, 0x11CF, 0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);
        IntPtr hInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hInfo == InvalidHandle) return results;

        try
        {
            uint index = 0;
            var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

            while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref hidGuid, index++, ref iface))
            {
                SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(hInfo, ref iface, buf, size, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(buf + 4)!;
                    string pathLower = path.ToLowerInvariant();
                    string vidHex = $"vid_{vendorId:x4}";

                    if (!pathLower.Contains(vidHex))
                        continue;

                    IntPtr hDev = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (hDev == InvalidHandle) continue;

                    try
                    {
                        if (!HidD_GetAttributes(hDev, out var attrs))
                            continue;

                        if (attrs.Vid != vendorId)
                            continue;

                        string product = GetHidProductString(hDev);

                        IntPtr preparsed = IntPtr.Zero;
                        ushort outLen = 0, featLen = 0, usagePage = 0, usage = 0;
                        if (HidD_GetPreparsedData(hDev, out preparsed))
                        {
                            if (HidP_GetCaps(preparsed, out var caps))
                            {
                                outLen = caps.OutputReportByteLength;
                                featLen = caps.FeatureReportByteLength;
                                usagePage = caps.UsagePage;
                                usage = caps.Usage;
                            }
                            HidD_FreePreparsedData(ref preparsed);
                        }

                        results.Add(new HidDeviceInfo
                        {
                            DevicePath = path,
                            VendorId = attrs.Vid,
                            ProductId = attrs.Pid,
                            ProductString = product,
                            UsagePage = usagePage,
                            Usage = usage,
                            OutputReportByteLength = outLen,
                            FeatureReportByteLength = featLen
                        });
                    }
                    finally
                    {
                        CloseHandle(hDev);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hInfo);
        }

        return results;
    }

    private static string GetHidProductString(IntPtr hDev)
    {
        try
        {
            var buf = new byte[256];
            if (HidD_GetProductString(hDev, buf, buf.Length))
                return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
        }
        catch { }
        return "";
    }
}
