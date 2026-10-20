using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>vJoy device status (vJoyInterface).</summary>
public enum VjdStat : uint
{
    Own = 0,
    Free = 1,
    Busy = 2,
    Miss = 3
}

/// <summary>HID usages used for the racing axes.</summary>
public enum HidUsage : uint
{
    X = 0x30,
    Y = 0x31,
    Z = 0x32,
    Rx = 0x33,
    Ry = 0x34,
    Rz = 0x35,
    Sl0 = 0x36,
    Sl1 = 0x37
}

/// <summary>Minimal vJoy native surface (mockable for tests).</summary>
public interface IVJoyNative
{
    bool DriverEnabled();
    VjdStat GetStatus(uint deviceId);
    bool Acquire(uint deviceId);
    void Relinquish(uint deviceId);
    bool SetAxis(int value, uint deviceId, HidUsage axis);
    bool SetButton(bool pressed, uint deviceId, int buttonId1Based);
    bool Reset(uint deviceId);
    /// <summary>Buttons the device reports (for clamping the mirror range).</summary>
    int GetButtonCount(uint deviceId);
    /// <summary>Discrete + continuous POV hats (preserved across reconfigures).</summary>
    int GetDiscPovCount(uint deviceId);
    int GetContPovCount(uint deviceId);
    /// <summary>Whether an axis exists on the device (config preservation).</summary>
    bool AxisExists(uint deviceId, HidUsage axis);
}

/// <summary>P/Invoke binding to vJoyInterface.dll (ships with the vJoy driver).
/// Every call is exception-safe: without the driver installed all calls fail
/// closed and report DriverPresent=false — never crash the app.
/// NOTE: a stock vJoy install does NOT put the interface DLL on the load
/// path (it lives under C:\Program Files\vJoy), so VJoyDllResolver loads it
/// from its install location before the first call.</summary>
public sealed class VJoyNative : IVJoyNative
{
    private const string Dll = "vJoyInterface.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern bool vJoyEnabled();
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetVJDStatus(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int AcquireVJD(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern void RelinquishVJD(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern bool SetAxis(int Value, uint rID, uint Axis);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern bool SetBtn(bool Value, uint rID, byte nBtn);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern bool ResetVJD(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int GetVJDButtonNumber(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int GetVJDDiscPovNumber(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern int GetVJDContPovNumber(uint rID);
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern bool GetVJDAxisExist(uint rID, uint Axis);

    public bool DriverPresent { get; private set; } = true;

    private bool Guard(Func<bool> call)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return call();
        }
        catch (DllNotFoundException) { DriverPresent = false; return false; }
        catch (EntryPointNotFoundException) { DriverPresent = false; return false; }
        catch { return false; }
    }

    public bool DriverEnabled() => Guard(vJoyEnabled);
    public VjdStat GetStatus(uint deviceId)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return (VjdStat)GetVJDStatus(deviceId);
        }
        catch { DriverPresent = false; return VjdStat.Miss; }
    }
    public bool Acquire(uint deviceId) => Guard(() => AcquireVJD(deviceId) != 0);
    public void Relinquish(uint deviceId) { try { RelinquishVJD(deviceId); } catch { DriverPresent = false; } }
    public bool SetAxis(int value, uint deviceId, HidUsage axis) => Guard(() => SetAxis(value, deviceId, (uint)axis));
    public bool SetButton(bool pressed, uint deviceId, int buttonId1Based)
        => Guard(() => SetBtn(pressed, deviceId, (byte)buttonId1Based));
    public bool Reset(uint deviceId) => Guard(() => ResetVJD(deviceId));
    public int GetButtonCount(uint deviceId)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return GetVJDButtonNumber(deviceId);
        }
        catch { return -1; }
    }
    public int GetDiscPovCount(uint deviceId)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return GetVJDDiscPovNumber(deviceId);
        }
        catch { return -1; }
    }
    public int GetContPovCount(uint deviceId)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return GetVJDContPovNumber(deviceId);
        }
        catch { return -1; }
    }
    public bool AxisExists(uint deviceId, HidUsage axis)
    {
        try
        {
            VJoyDllResolver.EnsureLoaded();
            return GetVJDAxisExist(deviceId, (uint)axis);
        }
        catch { return false; }
    }
}
