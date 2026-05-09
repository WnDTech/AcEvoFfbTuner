using System.Runtime.InteropServices;
using System.Text;

namespace AcEvoFfbTuner.Core.FfbProviders;

internal static class FanatecSdkNative
{
    private const string DllName = "EndorFanatecSdk64_VS2019.dll";

    public static readonly string[] SearchPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Fanatec", "Fanatec Wheel", "fw"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Fanatec", "Fanatec Wheel", "fw"),
    ];

    public static IntPtr TryLoad()
    {
        foreach (var dir in SearchPaths)
        {
            var path = Path.Combine(dir, DllName);
            if (!File.Exists(path)) continue;

            try
            {
                return NativeLibrary.Load(path);
            }
            catch { }
        }

        return IntPtr.Zero;
    }

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSEnumerateInstance2(int index, out IntPtr ppDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSDeviceQueryInterface(out IntPtr ppInterface, IntPtr pDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSDeviceRelease(IntPtr pDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSDeviceCapsGet(IntPtr pInterface, out int caps);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSInterfaceDestroy(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSItemCountGet(IntPtr pInterface, out int count);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSItemSubmitToDevice(IntPtr pInterface);

    // --- Force Feedback ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbSetGain(IntPtr pInterface, int gain);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbGetForceFeedbackState(IntPtr pInterface, out int state);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbSendForceFeedbackCommand(IntPtr pInterface, int command);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbDownloadEffect(IntPtr pInterface, ref FsbEffect effect, out int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbStartEffect(IntPtr pInterface, int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbStopEffect(IntPtr pInterface, int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfbDestroyEffect(IntPtr pInterface, int effectId);

    // --- FullForce (sample-based haptics) ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfSetSampleRate(IntPtr pInterface, int sampleRate);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfGetSampleRate(IntPtr pInterface, out int sampleRate);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfSetSampleBufferSize(IntPtr pInterface, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfAppendSamples(IntPtr pInterface, float[] samples, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfGetBufferedSampleCount(IntPtr pInterface, out int count);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfSamplePlayStart1(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfSamplePlayStart2(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSFfSamplePlayStop(IntPtr pInterface);

    // --- Transducer / Rumble ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSRumbleSetOn(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] bool on);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSTransducerDownloadEffect(IntPtr pInterface, ref FsbTransducerEffect effect, out int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSTransducerStartEffect(IntPtr pInterface, int effectId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSTransducerStopEffect(IntPtr pInterface, int effectId);

    // --- LEDs ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedButtonsRevLedModeEnable(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedButtonsIsRevLedModeEnabled(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedItemColorSet1(IntPtr pInterface, int itemIndex, byte r, byte g, byte b);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedItemColorSet2(IntPtr pInterface, int itemIndex, byte r, byte g, byte b);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedItemSetBrightness(IntPtr pInterface, int itemIndex, byte brightness);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedBrightnessSave(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedDigitCountGet(IntPtr pInterface, out int count);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedDigitSetOn1(IntPtr pInterface, int digitIndex, byte charValue);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedDigitSetOn2(IntPtr pInterface, int digitIndex, byte charValue);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedDigitSetOff(IntPtr pInterface, int digitIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedSetNumber(IntPtr pInterface, int number);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedRimWhiteLEDSet(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] bool on);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSLedRevsMirrorEnable(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] bool enable);

    // --- Utility / Capability Detection ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilHasWheelRimRevLeds(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool hasRevLeds);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilHasWheelRimLedDisplay(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool hasDisplay);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilHasWheelRimRumbleMotors(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool hasMotors);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilHasWheelBaseRevLeds(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool hasRevLeds);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilIsWheelBaseDirectDrive(IntPtr pInterface, [MarshalAs(UnmanagedType.Bool)] out bool isDirectDrive);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilWheelBaseProductNameGet(IntPtr pInterface, StringBuilder name, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilWheelRimProductNameGet(IntPtr pInterface, StringBuilder name, int bufferSize);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSUtilWheelBaseMaxRotationGet(IntPtr pInterface, out int maxRotation);

    // --- Wheel ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelAngleGet(IntPtr pInterface, out int angle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelAngleSet(IntPtr pInterface, int angle);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelAutocenterSet(IntPtr pInterface, int strength);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelMaxTorqueGet(IntPtr pInterface, out int maxTorque);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelStopEffects(IntPtr pInterface);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelModeSet(IntPtr pInterface, int mode);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelSettingEnable(IntPtr pInterface, int setting, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSWheelIsSettingEnabled(IntPtr pInterface, int setting, [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    // --- Telemetry Memory ---

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSTmDataSet(IntPtr pInterface, int slot, IntPtr data, int size);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int FSTmDataReportRead(IntPtr pInterface, int slot, out int report);

    // --- Structs (sizes TBD — adjust when official headers are available) ---

    [StructLayout(LayoutKind.Sequential)]
    public struct FsbEffect
    {
        public int Type;
        public int Direction;
        public int Duration;
        public int StartDelay;
        public int TriggerButton;
        public int TriggerRepeatInterval;
        public int Gain;
        public int SamplePeriod;
        public int EnvelopeAttackLevel;
        public int EnvelopeAttackTime;
        public int EnvelopeFadeLevel;
        public int EnvelopeFadeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FsbTransducerEffect
    {
        public int Type;
        public int Magnitude;
        public int Duration;
        public int Frequency;
    }
}
