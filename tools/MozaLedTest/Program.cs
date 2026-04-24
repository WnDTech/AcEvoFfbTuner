using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MozaLedTest;

class Program
{
    const byte FrameStart = 0x7E;
    const byte ChecksumMagic = 0x0D;
    const byte OldWriteGroup = 0x41;
    const byte WheelSettingsWriteGroup = 0x3F;

    static readonly byte[] WheelIdCandidates = { 0x17, 0x15, 0x13 };

    static readonly IntPtr InvalidHandle = new(-1);
    static IntPtr _handle = new(-1);

    const uint GENERIC_WRITE = 0x40000000;
    const uint GENERIC_READ = 0x80000000;
    const uint FILE_SHARE_RW = 0x03;
    const uint OPEN_EXISTING = 3;
    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;

    static readonly Guid ComPortInterfaceGuid = new(0x86E0D1E0, 0x8089, 0x11D0, 0x9C, 0xE4, 0x08, 0x00, 0x3E, 0x30, 0x1F, 0x73);
    static readonly Guid UsbSerialGuid = new(0xA5DCBF10, 0x6530, 0x11D2, 0x90, 0x1F, 0x00, 0xC0, 0x4F, 0xB9, 0x51, 0xED);
    static readonly Guid PortsClassGuid = new(0x4D36E978, 0xE325, 0x11CE, 0xBF, 0xC1, 0x08, 0x00, 0x2B, 0xE1, 0x03, 0x18);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr handle, IntPtr devInfo, ref Guid ifaceGuid, uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr handle, ref SpDeviceInterfaceData ifaceData, IntPtr detailBuf, int detailSize, out int requiredSize, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    static void Main(string[] args)
    {
        Console.WriteLine("=== Moza ES LED Test (fixed protocol) ===");
        Console.WriteLine();

        if (System.Diagnostics.Process.GetProcessesByName("PitHouse").Length > 0
            || System.Diagnostics.Process.GetProcessesByName("pithouse").Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("PitHouse is running! Close it first, then retry.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("PitHouse not detected. Scanning for Moza MI_00 serial...");

        string? serialPath = FindMozaSerialPath();
        if (serialPath == null)
        {
            Console.WriteLine("No MI_00 path found. Trying WMI COM port...");
            serialPath = FindMozaComPort();
        }

        if (serialPath == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: No Moza serial device found.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"Found: {serialPath}");
        Console.Write("Opening... ");

        IntPtr hDev = CreateFile(serialPath, GENERIC_WRITE | GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (hDev == InvalidHandle)
            hDev = CreateFile(serialPath, GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (hDev == InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED (err={err})");
            if (err == 5) Console.WriteLine("Access denied - PitHouse may have just started.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("OK");
        _handle = hDev;

        Console.WriteLine();
        Console.WriteLine("Testing all 3 candidate wheel device IDs:");
        Console.WriteLine("  0x17 (23) = new-protocol wheel");
        Console.WriteLine("  0x15 (21) = alternate wheel");
        Console.WriteLine("  0x13 (19) = ES wheel / R5 base (shared)");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("ES wheel wake-up: flash all LEDs then off...");
        Console.ResetColor();
        SendOldTelemetry(0x13, 0x3FF);
        Thread.Sleep(200);
        SendOldTelemetry(0x13, 0);
        Thread.Sleep(200);
        SendOldTelemetry(0x15, 0x3FF);
        Thread.Sleep(200);
        SendOldTelemetry(0x15, 0);
        Thread.Sleep(200);
        SendOldTelemetry(0x17, 0x3FF);
        Thread.Sleep(200);
        SendOldTelemetry(0x17, 0);
        Thread.Sleep(500);

        Console.WriteLine();
        Console.WriteLine("Now trying all-LEDs-on for each device ID...");
        Console.WriteLine();

        foreach (byte devId in WheelIdCandidates)
        {
            Console.Write($"  Device 0x{devId:X2}: ");
            SendOldTelemetry(devId, 0x3FF);
            Thread.Sleep(800);
            SendOldTelemetry(devId, 0);
            Thread.Sleep(200);
        }

        Console.WriteLine();
        Console.WriteLine("Did any of those light up the LEDs? If so, note the device ID.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  [1-9]=N LEDs   [0]=off   [A]=all on   [R]=sweep   [F]=flash");
        Console.WriteLine("  [C]=set color (R,G,B)   [G]=green-red gradient   [B]=brightness (0-15)");
        Console.WriteLine("  [Q]=quit");
        Console.WriteLine();

        byte currentDev = 0x13;

        bool running = true;
        Console.CancelKeyPress += (_, e) => { e.Cancel = false; running = false; };

        while (running)
        {
            Console.Write($"[{currentDev:X2}]> ");
            string? input = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (input == null || input == "Q") break;

            switch (input)
            {
                case "W":
                    Console.WriteLine("  Wake-up flash...");
                    SendOldTelemetry(currentDev, 0x3FF);
                    Thread.Sleep(200);
                    SendOldTelemetry(currentDev, 0);
                    break;
                case "0":
                    SendOldTelemetry(currentDev, 0);
                    break;
                case "A":
                    SendOldTelemetry(currentDev, 0x3FF);
                    break;
                case "F":
                    for (int f = 0; f < 20; f++)
                    {
                        SendOldTelemetry(currentDev, f % 2 == 0 ? 0x3FF : 0);
                        Thread.Sleep(100);
                    }
                    break;
                case "R":
                    for (int sweep = 0; sweep < 2; sweep++)
                    {
                        for (int n = 0; n <= 10; n++)
                        {
                            int bitmask = n > 0 ? (1 << n) - 1 : 0;
                            bitmask &= 0x3FF;
                            SendOldTelemetry(currentDev, bitmask);
                            Thread.Sleep(150);
                        }
                    }
                    break;
                case "C":
                    Console.Write("  Enter R,G,B (0-255 each, e.g. 255,0,0): ");
                    string? colorInput = Console.ReadLine()?.Trim();
                    if (colorInput != null)
                    {
                        var parts = colorInput.Split(',');
                        if (parts.Length == 3 && byte.TryParse(parts[0], out byte cr) && byte.TryParse(parts[1], out byte cg) && byte.TryParse(parts[2], out byte cb))
                        {
                            for (byte idx = 0; idx < 10; idx++)
                            {
                                SendFrame(0x3F, currentDev, new byte[] { 0x15, 0x00, idx, cr, cg, cb });
                                Thread.Sleep(20);
                            }
                            Console.WriteLine($"  Set all 10 LEDs to RGB({cr},{cg},{cb})");
                        }
                        else Console.WriteLine("  Invalid format. Use R,G,B");
                    }
                    break;
                case "G":
                    for (byte idx = 0; idx < 10; idx++)
                    {
                        float t = idx / 9f;
                        byte r = (byte)(t * 255);
                        byte g = (byte)((1 - t) * 255);
                        SendFrame(0x3F, currentDev, new byte[] { 0x15, 0x00, idx, r, g, 0 });
                        Thread.Sleep(20);
                    }
                    Console.WriteLine("  Gradient: green -> yellow -> red");
                    break;
                case "B":
                    Console.Write("  Brightness 0-15 (default 15): ");
                    string? bInput = Console.ReadLine()?.Trim();
                    if (bInput != null && int.TryParse(bInput, out int bri))
                    {
                        bri = Math.Clamp(bri, 0, 15);
                        SendFrame(0x3F, currentDev, new byte[] { 0x14, 0x00, (byte)bri });
                        Console.WriteLine($"  Brightness set to {bri}/15");
                    }
                    break;
                default:
                    if (int.TryParse(input, out int ledCount) && ledCount >= 1 && ledCount <= 9)
                    {
                        int bitmask = (1 << ledCount) - 1;
                        bitmask &= 0x3FF;
                        SendOldTelemetry(currentDev, bitmask);
                    }
                    break;
            }
        }

        Console.WriteLine("Restoring default (LEDs off)...");
        SendOldTelemetry(currentDev, 0);
        Thread.Sleep(100);

        CloseHandle(_handle);
        Console.WriteLine("Done.");
    }

    static void SendOldTelemetry(byte deviceId, int bitmask)
    {
        byte[] payload = new byte[6];
        payload[0] = 0xFD;
        payload[1] = 0xDE;
        payload[2] = (byte)((bitmask >> 24) & 0xFF);
        payload[3] = (byte)((bitmask >> 16) & 0xFF);
        payload[4] = (byte)((bitmask >> 8) & 0xFF);
        payload[5] = (byte)(bitmask & 0xFF);
        SendFrame(OldWriteGroup, deviceId, payload);
    }

    static void SendFrame(byte group, byte device, byte[] payload)
    {
        int n = payload.Length;
        int frameLen = 1 + 1 + 1 + 1 + n + 1;
        var frame = new byte[frameLen];
        int pos = 0;

        frame[pos++] = FrameStart;
        frame[pos++] = (byte)n;
        frame[pos++] = group;
        frame[pos++] = device;
        Buffer.BlockCopy(payload, 0, frame, pos, n);
        pos += n;

        byte checksum = ChecksumMagic;
        for (int i = 0; i < pos; i++)
            checksum = (byte)((checksum + frame[i]) % 256);
        frame[pos] = checksum;

        bool ok = WriteFile(_handle, frame, frame.Length, out int written, IntPtr.Zero);
        var hex = string.Join(" ", frame.Select(b => b.ToString("X2")));
        Console.WriteLine($"  TX: ok={ok} wr={written} [{hex}]");
    }

    static string? FindMozaSerialPath()
    {
        foreach (var guid in new[] { ComPortInterfaceGuid, UsbSerialGuid, PortsClassGuid })
        {
            Guid g = guid;
            IntPtr hInfo = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (hInfo == InvalidHandle) continue;

            try
            {
                uint i = 0;
                var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };

                while (SetupDiEnumDeviceInterfaces(hInfo, IntPtr.Zero, ref g, i++, ref iface))
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

                        if (pathLower.Contains("vid_346e") && pathLower.Contains("mi_00"))
                            return path;
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
        }

        return null;
    }

    static string? FindMozaComPort()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, PNPDeviceID FROM Win32_SerialPort");
            foreach (var obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? pnpId = obj["PNPDeviceID"]?.ToString();
                if (pnpId != null && pnpId.ToLowerInvariant().Contains("vid_346e"))
                {
                    Console.WriteLine($"  WMI match: {deviceId} PNP={pnpId}");
                    return $"\\\\.\\{deviceId}";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WMI scan failed: {ex.Message}");
        }

        try
        {
            using var searcher2 = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_346E%MI_00%'");
            foreach (var obj in searcher2.Get())
            {
                string? name = obj["Name"]?.ToString();
                if (name != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(name, @"COM\d+");
                    if (match.Success)
                    {
                        Console.WriteLine($"  WMI PnP match: {name}");
                        return $"\\\\.\\{match.Value}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  WMI PnP scan failed: {ex.Message}");
        }

        return null;
    }
}
