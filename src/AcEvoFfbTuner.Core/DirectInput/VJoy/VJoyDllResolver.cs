using System.Runtime.InteropServices;

namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>
/// Locates vJoyInterface.dll and loads it explicitly. A stock vJoy install
/// keeps the DLL under "%ProgramFiles%\vJoy" (x64/x86 subfolders) — NOT on
/// the process load path — so plain DllImport("vJoyInterface.dll") always
/// throws DllNotFoundException even with a healthy install. Once loaded here
/// by full path, subsequent P/Invokes bind to the loaded module.
/// Thread-safe, idempotent, never throws.
/// </summary>
public static class VJoyDllResolver
{
    private const string DllName = "vJoyInterface.dll";
    private static bool _loaded;
    private static readonly object _gate = new();

    public static bool EnsureLoaded()
    {
        if (_loaded) return true;
        lock (_gate)
        {
            if (_loaded) return true;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    NativeLibrary.Load(path);
                    _loaded = true;
                    return true;
                }
                catch { }
            }
            // Last resort: default search (app dir, System32, PATH).
            try
            {
                NativeLibrary.Load(DllName);
                _loaded = true;
                return true;
            }
            catch { }
            return false;
        }
    }

    public static string? LocateDll()
    {
        foreach (var path in CandidatePaths())
        {
            try { if (File.Exists(path)) return path; }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string sub = Environment.Is64BitProcess ? "x64" : "x86";
        var roots = new List<string>();
        try
        {
            string? install = InstallLocation();
            if (!string.IsNullOrEmpty(install)) roots.Add(install);
        }
        catch { }
        try { roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy")); }
        catch { }
        try
        {
            string? x86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(x86)) roots.Add(Path.Combine(x86, "vJoy"));
        }
        catch { }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, sub, DllName);
            yield return Path.Combine(root, DllName);
        }
    }

    private static string? InstallLocation()
    {
        try
        {
            var hives = new (Microsoft.Win32.RegistryHive hive, Microsoft.Win32.RegistryView view)[]
            {
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
            };
            foreach (var (hive, view) in hives)
            {
                try
                {
                    using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                    using var un = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (un == null) continue;
                    foreach (var name in un.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = un.OpenSubKey(name);
                            var display = sub?.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(display) ||
                                !display.Contains("vjoy", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var loc = sub?.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc)) return loc;
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }
}
