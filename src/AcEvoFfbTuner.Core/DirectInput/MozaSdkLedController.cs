using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.DirectInput;

public sealed class MozaSdkLedController : IDisposable
{
    private bool _installed;
    private bool _disposed;
    private readonly List<string> _log = new();

    private const int MozaLedCount = 10;
    private const int ShiftIndicatorModeGame = 1;
    private const int ShiftIndicatorSwitchGame = 3;

    public bool IsAvailable => _installed;
    public string DiagnosticLog => string.Join("\n", _log);

    public bool Initialize()
    {
        if (_installed) return true;

        Log("MozaSDK: initializing...");

        if (!ProbeNativeDlls())
        {
            Log("MozaSDK: native DLLs not found or failed to load");
            return false;
        }

        try
        {
            mozaAPI.mozaAPI.installMozaSDK();
            Log("MozaSDK: installMozaSDK() called, waiting for CoAP connection to PitHouse...");

            Thread.Sleep(3500);

            _installed = true;

            var testErr = mozaAPI.ERRORCODE.NORMAL;
            mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorBrightness(ref testErr);
            Log($"MozaSDK: readiness probe → {testErr}");

            if (testErr == mozaAPI.ERRORCODE.PITHOUSENOTREADY)
            {
                Log("MozaSDK: PitHouse not ready, retrying in 3s...");
                Thread.Sleep(3000);
                mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorBrightness(ref testErr);
                Log($"MozaSDK: retry probe → {testErr}");
            }

            if (testErr != mozaAPI.ERRORCODE.NORMAL && testErr != mozaAPI.ERRORCODE.NODEVICES)
            {
                Log($"MozaSDK: installed but PitHouse may not be running (err={testErr})");
            }
            else
            {
                Log($"MozaSDK: installed and connected to PitHouse");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"MozaSDK: install failed: {ex.Message}");
            return false;
        }
    }

    private bool ProbeNativeDlls()
    {
        string[] required = { "Moza_API_C.dll", "Moza_SDK.dll" };
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        foreach (var dll in required)
        {
            string path = Path.Combine(baseDir, dll);
            if (!File.Exists(path))
            {
                Log($"MozaSDK: missing {dll} in {baseDir}");
                return false;
            }

            try
            {
                IntPtr h = LoadLibrary(path);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Log($"MozaSDK: LoadLibrary({dll}) failed, Win32 error {err}");
                    return false;
                }
                Log($"MozaSDK: {dll} loaded OK");
            }
            catch (Exception ex)
            {
                Log($"MozaSDK: LoadLibrary({dll}) exception: {ex.Message}");
                return false;
            }
        }

        return true;
    }

    public bool ConfigureShiftIndicator(
        int brightness = 100,
        int switchMode = 2,
        int displayMode = 1,
        string[]? colors = null,
        int[]? rpmThresholds = null)
    {
        if (!_installed) return false;

        try
        {
            var err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorBrightness(brightness);
            Log($"MozaSDK: setBrightness({brightness}) → {err}");
            bool brightnessOk = err == mozaAPI.ERRORCODE.NORMAL;

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorSwitch(switchMode);
            Log($"MozaSDK: setSwitch({switchMode}) → {err}");

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorMode(displayMode);
            Log($"MozaSDK: setMode({displayMode}) → {err}");

            if (colors != null && colors.Length == MozaLedCount)
            {
                var colorList = new List<string>(colors);
                err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorColor(colorList);
                Log($"MozaSDK: setColor([{colors.Length} entries]) → {err}");
            }

            if (rpmThresholds != null && rpmThresholds.Length == MozaLedCount)
            {
                var rpmList = new List<int>(rpmThresholds);
                err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorLightRpm(rpmList);
                Log($"MozaSDK: setRpm([{rpmThresholds.Length} entries]) → {err}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"MozaSDK: configure failed: {ex.Message}");
            return false;
        }
    }

    public bool SetShiftIndicatorColors(string[] colors)
    {
        if (!_installed || colors == null || colors.Length != MozaLedCount)
            return false;

        try
        {
            var err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorColor(new List<string>(colors));
            if (err != mozaAPI.ERRORCODE.NORMAL)
            {
                Log($"MozaSDK: setColor failed → {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"MozaSDK: setColor exception: {ex.Message}");
            return false;
        }
    }

    public bool SetShiftIndicatorRpm(int[] rpmPercentages)
    {
        if (!_installed || rpmPercentages == null || rpmPercentages.Length != MozaLedCount)
            return false;

        try
        {
            var err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorLightRpm(new List<int>(rpmPercentages));
            if (err != mozaAPI.ERRORCODE.NORMAL)
            {
                Log($"MozaSDK: setRpm failed → {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"MozaSDK: setRpm exception: {ex.Message}");
            return false;
        }
    }

    public int GetBrightness()
    {
        if (!_installed) return -1;
        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;
            return mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorBrightness(ref err);
        }
        catch { return -1; }
    }

    public int GetIndicatorSwitch()
    {
        if (!_installed) return -1;
        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;
            return mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorSwitch(ref err);
        }
        catch { return -1; }
    }

    public string[]? GetIndicatorColors()
    {
        if (!_installed) return null;
        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;
            var list = mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorColor(ref err);
            return err == mozaAPI.ERRORCODE.NORMAL ? list?.ToArray() : null;
        }
        catch { return null; }
    }

    public int[]? GetIndicatorRpm()
    {
        if (!_installed) return null;
        try
        {
            var err = mozaAPI.ERRORCODE.NORMAL;
            var list = mozaAPI.mozaAPI.getSteeringWheelShiftIndicatorLightRpm(ref err);
            return err == mozaAPI.ERRORCODE.NORMAL ? list?.ToArray() : null;
        }
        catch { return null; }
    }

    public bool TestAllLedsOn()
    {
        if (!_installed) return false;

        try
        {
            var allRed = new string[MozaLedCount];
            Array.Fill(allRed, "#FFFF0000");

            var lowRpm = new int[MozaLedCount];
            for (int i = 0; i < MozaLedCount; i++)
                lowRpm[i] = i + 1;

            var err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorBrightness(100);
            Log($"MozaSDK TEST: setBrightness(100) → {err}");

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorSwitch(2);
            Log($"MozaSDK TEST: setSwitch(2) → {err}");

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorMode(1);
            Log($"MozaSDK TEST: setMode(1) → {err}");

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorColor(new List<string>(allRed));
            Log($"MozaSDK TEST: setColor(all red) → {err}");

            err = mozaAPI.mozaAPI.setSteeringWheelShiftIndicatorLightRpm(new List<int>(lowRpm));
            Log($"MozaSDK TEST: setRpm(1..10%) → {err}");

            Log("MozaSDK TEST: all LEDs should be RED now (if PitHouse reads RPM from game)");
            return true;
        }
        catch (Exception ex)
        {
            Log($"MozaSDK TEST: failed: {ex.Message}");
            return false;
        }
    }

    public static string[] BuildRpmGradientColors()
    {
        return new string[]
        {
            "#FF00CE00",
            "#FF00CE00",
            "#FF00CE00",
            "#FFFFCC00",
            "#FFFFCC00",
            "#FFFFCC00",
            "#FFFF6600",
            "#FFFF6600",
            "#FFFF0606",
            "#FFFF0606",
        };
    }

    public static int[] BuildDefaultRpmThresholds()
    {
        return new int[] { 50, 60, 70, 80, 85, 90, 93, 96, 98, 100 };
    }

    private void Log(string msg)
    {
        _log.Add(msg);
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "moza_sdk.log"), $"{DateTime.Now:HH:mm:ss.fff} | {msg}\n");
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string path);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_installed)
        {
            try
            {
                mozaAPI.mozaAPI.removeMozaSDK();
                Log("MozaSDK: removed");
            }
            catch (Exception ex)
            {
                Log($"MozaSDK: remove failed: {ex.Message}");
            }
            _installed = false;
        }
    }
}
