using System.Diagnostics;

namespace AcEvoFfbTuner.Services;

public sealed class ConflictingAppResult
{
    public List<ConflictingApp> DetectedApps { get; } = new();
    public bool HasConflicts => DetectedApps.Count > 0;
}

public sealed class ConflictingApp
{
    public string ProcessName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Reason { get; init; } = "";
}

public static class ConflictingAppDetector
{
    private static readonly (string processName, string displayName, string reason)[] KnownConflictingApps =
    {
        ("fanalab", "FanaLab", "Sends competing FFB commands to Fanatec wheelbases, overriding this app's tuned output."),
        ("mozapithouse", "Moza Pit House", "Controls Moza wheelbase FFB settings and sends DirectInput effects that conflict with this app."),
        ("pithouse", "Moza Pit House", "Controls Moza wheelbase FFB settings and sends DirectInput effects that conflict with this app."),
        ("true_drive", "Simucube True Drive", "Manages Simucube wheelbase FFB directly — both apps will fight for exclusive device control."),
        ("simucube", "Simucube True Drive", "Manages Simucube wheelbase FFB directly — both apps will fight for exclusive device control."),
        ("irffb", "irFFB", "Intercepts and modifies DirectInput FFB signals, causing double-processing of force data."),
        ("simcommander", "SimCommander", "SimXperience FFB engine that sends its own force effects to DirectInput devices."),
        ("simcommander64", "SimCommander", "SimXperience FFB engine that sends its own force effects to DirectInput devices."),
        ("vrs", "VRS DirectForce", "Controls VRS wheelbase FFB settings and can override DirectInput force commands."),
        ("vrsdirectforce", "VRS DirectForce", "Controls VRS wheelbase FFB settings and can override DirectInput force commands."),
        ("ffbleditor", "FFB Editor / LUT Generator", "Modifies FFB lookup tables and may interfere with this app's force processing pipeline."),
        ("ffpostpro", "FFB Post Processor", "Post-processes FFB signals in real-time, conflicting with this app's pipeline."),
        ("accffbfix", "ACC FFB Fix", "Patches and modifies FFB output, causing unpredictable force叠加 with this app."),
        ("simhub", "SimHub", "Can send haptic/FFB effects to wheel devices that interfere with this app's output."),
        ("simhub.desktop", "SimHub", "Can send haptic/FFB effects to wheel devices that interfere with this app's output."),
        ("crewchief", "Crew Chief", "May send DirectInput vibration effects to the wheel that conflict with this app's FFB."),
        ("crewchief.v4", "Crew Chief", "May send DirectInput vibration effects to the wheel that conflict with this app's FFB."),
    };

    private static string[]? _cachedProcessNames;
    private static DateTime _lastCacheTime = DateTime.MinValue;

    public static ConflictingAppResult Detect()
    {
        var processNames = GetProcessNames();
        var result = new ConflictingAppResult();

        foreach (var (processName, displayName, reason) in KnownConflictingApps)
        {
            if (processNames.Contains(processName))
            {
                if (result.DetectedApps.All(a => a.DisplayName != displayName))
                {
                    result.DetectedApps.Add(new ConflictingApp
                    {
                        ProcessName = processName,
                        DisplayName = displayName,
                        Reason = reason
                    });
                }
            }
        }

        return result;
    }

    private static HashSet<string> GetProcessNames()
    {
        if (_cachedProcessNames != null && (DateTime.UtcNow - _lastCacheTime).TotalSeconds < 5)
            return new HashSet<string>(_cachedProcessNames, StringComparer.OrdinalIgnoreCase);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try { names.Add(proc.ProcessName.ToLowerInvariant()); }
                    catch { }
                }
            }
        }
        catch { }

        _cachedProcessNames = names.ToArray();
        _lastCacheTime = DateTime.UtcNow;
        return names;
    }
}
