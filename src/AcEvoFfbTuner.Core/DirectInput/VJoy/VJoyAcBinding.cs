using System.Diagnostics;

namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>Result of the one-click AC controls binding write.</summary>
public sealed class AcBindingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public int VJoyIndex { get; init; } = -1;
    public string? BackupPath { get; init; }
}

/// <summary>
/// Writes the AC1 vJoy bindings into the game's controls.ini so the user
/// never hand-binds axes in Content Manager:
/// STEER/THROTTLE/BRAKES/CLUTCH → vJoy device index with axles 0/1/2/3
/// (matching VJoyFeeder: X=steer, Y=gas, Z=brake, Rx=clutch).
///
/// Safety: line-preserving edit (comments, button bindings and all other
/// keys untouched — only JOY=/AXLE= in the four sections plus the vJoy
/// CONTROLLERS entry change), timestamp-free single backup
/// (controls.ini.ffbtuner-bak), refuses while AC is running, idempotent
/// (re-running reuses the existing vJoy entry, never duplicates).
/// </summary>
public static class VJoyAcBinding
{
    public static string DefaultControlsIniPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Assetto Corsa", "cfg", "controls.ini");

    public static bool IsAcRunning()
    {
        try
        {
            return Process.GetProcessesByName("acs").Length > 0
                || Process.GetProcessesByName("AssettoCorsa").Length > 0;
        }
        catch { return false; }
    }

    public static AcBindingResult WriteBindings(string iniPath, Guid instanceGuid, Guid productGuid)
        => WriteBindings(iniPath, instanceGuid, productGuid, IsAcRunning(), null);

    public static AcBindingResult WriteBindings(
        string iniPath, Guid instanceGuid, Guid productGuid, bool acRunning)
        => WriteBindings(iniPath, instanceGuid, productGuid, acRunning, null);

    /// <param name="taskButtons">Task section → vJoy slot (1-based). Written
    /// as JOY=vjoyIdx + BUTTON=slot-1 (AC buttons are 0-based). Keyboard and
    /// Xbox lines are preserved; missing sections are created.</param>
    public static AcBindingResult WriteBindings(
        string iniPath, Guid instanceGuid, Guid productGuid, bool acRunning,
        IReadOnlyDictionary<string, int>? taskButtons)
    {
        if (acRunning)
            return new AcBindingResult { Success = false, Message = "Close Assetto Corsa first — it holds controls.ini" };
        if (!File.Exists(iniPath))
            return new AcBindingResult { Success = false, Message = $"controls.ini not found: {iniPath}" };

        string inst = instanceGuid.ToString().ToUpperInvariant();
        string prod = productGuid.ToString().ToUpperInvariant();

        string[] lines;
        try { lines = File.ReadAllLines(iniPath); }
        catch (Exception ex) { return new AcBindingResult { Success = false, Message = $"read failed: {ex.Message}" }; }

        var output = new List<string>(lines);
        int ctrlStart = FindSection(output, "CONTROLLERS");
        if (ctrlStart < 0)
            return new AcBindingResult { Success = false, Message = "controls.ini has no [CONTROLLERS] section" };

        int ctrlEnd = NextSection(output, ctrlStart);
        int maxIdx = -1;
        int vjoyIdx = -1;
        for (int i = ctrlStart + 1; i < ctrlEnd; i++)
        {
            string key = KeyOf(output[i]);
            if (key.StartsWith("CON", StringComparison.OrdinalIgnoreCase) && !key.StartsWith("__", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(key.Substring(3), out int idx))
                {
                    maxIdx = Math.Max(maxIdx, idx);
                    string val = ValueOf(output[i]);
                    if (val.Contains("vjoy", StringComparison.OrdinalIgnoreCase))
                        vjoyIdx = idx;
                }
            }
            if (key.StartsWith("__IGUID", StringComparison.OrdinalIgnoreCase)
                && ValueOf(output[i]).Equals(inst, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(key.Substring(7), out int idx2))
                    vjoyIdx = idx2;
            }
        }

        if (vjoyIdx < 0)
        {
            vjoyIdx = maxIdx + 1;
            output.Insert(ctrlEnd, $"PGUID{vjoyIdx}={prod}");
            output.Insert(ctrlEnd, $"__IGUID{vjoyIdx}={inst}");
            output.Insert(ctrlEnd, $"CON{vjoyIdx}=vJoy Device");
        }
        else
        {
            // Refresh GUIDs on the existing entry (DI instance GUIDs can
            // change across reboots/driver updates).
            SetKey(output, ctrlStart, NextSection(output, ctrlStart), $"__IGUID{vjoyIdx}", inst);
            SetKey(output, ctrlStart, NextSection(output, ctrlStart), $"PGUID{vjoyIdx}", prod);
        }

        var targets = new (string section, int axle)[]
        {
            ("STEER", 0), ("THROTTLE", 1), ("BRAKES", 2), ("CLUTCH", 3)
        };
        foreach (var (section, axle) in targets)
        {
            int s = FindSection(output, section);
            if (s < 0)
                return new AcBindingResult { Success = false, Message = $"controls.ini has no [{section}] section" };
            int e = NextSection(output, s);
            SetKey(output, s, e, "JOY", vjoyIdx.ToString());
            SetKey(output, s, e, "AXLE", axle.ToString());
        }

        int tasksWritten = 0;
        if (taskButtons != null)
        {
            foreach (var kv in taskButtons)
            {
                if (kv.Value < 1) continue;
                int s = FindSection(output, kv.Key);
                if (s < 0)
                {
                    // Stock files always carry these sections; create if a
                    // trimmed file lacks one (same JOY/BUTTON shape).
                    output.Add($"[{kv.Key}]");
                    output.Add($"JOY={vjoyIdx}");
                    output.Add($"BUTTON={kv.Value - 1}");
                    tasksWritten++;
                    continue;
                }
                int e = NextSection(output, s);
                SetKey(output, s, e, "JOY", vjoyIdx.ToString());
                SetKey(output, s, e, "BUTTON", (kv.Value - 1).ToString());
                tasksWritten++;
            }
        }

        try
        {
            string backup = iniPath + ".ffbtuner-bak";
            File.Copy(iniPath, backup, overwrite: true);
            File.WriteAllLines(iniPath, output);
            return new AcBindingResult
            {
                Success = true,
                Message = $"AC axes → vJoy device (controller {vjoyIdx})" +
                    (tasksWritten > 0 ? $" + {tasksWritten} button task{(tasksWritten == 1 ? "" : "s")}" : "") +
                    $"; backup: {Path.GetFileName(backup)}",
                VJoyIndex = vjoyIdx,
                BackupPath = backup
            };
        }
        catch (Exception ex)
        {
            return new AcBindingResult { Success = false, Message = $"write failed: {ex.Message}" };
        }
    }

    private static int FindSection(List<string> lines, string name)
    {
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Trim().Equals($"[{name}]", StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int NextSection(List<string> lines, int from)
    {
        for (int i = from + 1; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) return i;
        }
        return lines.Count;
    }

    private static string KeyOf(string line)
    {
        int eq = line.IndexOf('=');
        return eq < 0 ? "" : line.Substring(0, eq).Trim();
    }

    private static string ValueOf(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 0) return "";
        string v = line.Substring(eq + 1);
        int comment = v.IndexOf(';');
        return (comment >= 0 ? v.Substring(0, comment) : v).Trim();
    }

    private static void SetKey(List<string> lines, int from, int to, string key, string value)
    {
        for (int i = from + 1; i < to; i++)
        {
            if (KeyOf(lines[i]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                int eq = lines[i].IndexOf('=');
                string tail = "";
                int comment = lines[i].IndexOf(';', eq + 1);
                if (comment >= 0) tail = " " + lines[i].Substring(comment);
                lines[i] = lines[i].Substring(0, eq + 1) + value + tail;
                return;
            }
        }
        lines.Insert(to, $"{key}={value}");
    }
}
