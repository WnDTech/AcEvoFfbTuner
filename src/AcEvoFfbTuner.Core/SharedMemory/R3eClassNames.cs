using System.Text.Json;
using System.Text.RegularExpressions;

namespace AcEvoFfbTuner.Core.SharedMemory;

/// <summary>
/// RaceRoom class id → class name ("GTR 3", "Hypercars", "DTM 2025"…), read once from the
/// game's own GameData/General/r3e-data.json (shared memory only carries the numeric
/// class_id). The install is located through Steam's libraryfolders.vdf. Empty when the
/// game isn't installed locally — callers then fall back to "Class {id}".
/// </summary>
public static partial class R3eClassNames
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> _names =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Get(int classId)
    {
        if (classId <= 0) return "";
        return _names.Value.TryGetValue(classId, out var n) ? n : $"Class {classId}";
    }

    private static IReadOnlyDictionary<int, string> Load()
    {
        var result = new Dictionary<int, string>();
        try
        {
            var file = FindDataFile();
            if (file == null) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("classes", out var classes)) return result;
            foreach (var c in classes.EnumerateObject())
            {
                if (c.Value.TryGetProperty("Id", out var id) && c.Value.TryGetProperty("Name", out var name)
                    && id.TryGetInt32(out var i) && name.GetString() is { Length: > 0 } s)
                    result[i] = s;
            }
        }
        catch { }
        return result;
    }

    private static string? FindDataFile()
    {
        const string rel = @"steamapps\common\raceroom racing experience\Game\GameData\General\r3e-data.json";
        foreach (var lib in SteamLibraries())
        {
            var p = Path.Combine(lib, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string sp && sp.Length > 0) roots.Add(sp.Replace('/', '\\'));
        }
        catch { }
        roots.Add(@"C:\Program Files (x86)\Steam");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (seen.Add(root)) yield return root;
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string text;
            try { if (!File.Exists(vdf)) continue; text = File.ReadAllText(vdf); }
            catch { continue; }
            foreach (Match m in VdfPath().Matches(text))
            {
                var lib = m.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(lib)) yield return lib;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPath();
}
