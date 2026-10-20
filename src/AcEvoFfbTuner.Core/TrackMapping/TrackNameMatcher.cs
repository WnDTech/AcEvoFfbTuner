using System.Globalization;
using System.Text;

namespace AcEvoFfbTuner.Core.TrackMapping;

/// <summary>
/// Normalised, word-boundary matching of game track names against gazetteer names.
/// Games append layout variants ("Circuit de la Sarthe Mulsanne"), use file-style ids
/// ("ks_silverstone") and drop accents ("Nurburgring"), so lookups compare normalised
/// forms and accept a key that occurs as whole words inside the game name (or vice versa).
/// </summary>
public static class TrackNameMatcher
{
    /// <summary>
    /// Lower-case, accents removed, '_' '-' '.' ',' '/' → space, whitespace collapsed. With
    /// <paramref name="splitDigits"/>, also a space between letters and digits: AC track ids glue
    /// years on ("ks_monza66", "ks_silverstone1967"). Off by default — class names need "gt3" whole.
    /// </summary>
    public static string Normalize(string? name, bool splitDigits = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var decomposed = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool lastSpace = false;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            char c = ch switch { 'ß' => 's', _ => ch };
            if (char.IsLetterOrDigit(c))
            {
                if (splitDigits && !lastSpace && sb.Length > 0 && char.IsDigit(c) != char.IsDigit(sb[^1]))
                    sb.Append(' ');
                sb.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>True when <paramref name="needle"/> occurs in <paramref name="haystack"/> on word boundaries.</summary>
    public static bool ContainsWords(string haystack, string needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            bool startOk = idx == 0 || haystack[idx - 1] == ' ';
            int end = idx + needle.Length;
            bool endOk = end == haystack.Length || haystack[end] == ' ';
            if (startOk && endOk) return true;
            idx++;
        }
        return false;
    }

    /// <summary>
    /// Best key for a normalised query: exact equality wins; otherwise the LONGEST key
    /// found as whole words in the query (key ≥ <paramref name="minKeyLength"/>), or the
    /// longest key containing the whole query (query ≥ <paramref name="minQueryLengthForReverse"/>).
    /// </summary>
    public static T? BestMatch<T>(string normQuery, IReadOnlyList<(string key, T value)> keys,
        int minKeyLength, int minQueryLengthForReverse) where T : class
    {
        if (normQuery.Length == 0) return null;
        T? best = null;
        int bestLen = 0;
        foreach (var (key, value) in keys)
        {
            if (key.Length == normQuery.Length && key == normQuery) return value;
            if (key.Length <= bestLen) continue;
            bool forward = key.Length >= minKeyLength && ContainsWords(normQuery, key);
            bool reverse = !forward && normQuery.Length >= minQueryLengthForReverse && ContainsWords(key, normQuery);
            if (forward || reverse)
            {
                best = value;
                bestLen = key.Length;
            }
        }
        return best;
    }

    // Words that never identify a venue on their own: kinds of track and layout descriptors.
    private static readonly HashSet<string> NonVenueWords = new(StringComparer.Ordinal)
    {
        "circuit", "circuito", "international", "internacional", "raceway", "race", "racing", "track",
        "grand", "prix", "park", "speedway", "speedpark", "autodromo", "autodrome", "autodrom", "ring",
        "motor", "motorsport", "motorsports", "motorbana", "arena", "national", "club", "layout", "course",
        "short", "long", "full", "classic", "endurance", "school", "outer", "inner", "north", "south",
        "east", "west", "street", "city", "road", "the", "della",
    };

    // A name only gets the single-word fallback when it reads like a real circuit's name, so
    // fictional AC/R3E maps ("ks_highlands", "Twin Forest") don't latch onto a real venue.
    private static readonly string[] CircuitWords =
        { "circuit", "circuito", "ring", "raceway", "motorbana", "autodrome", "autodromo", "autodrom", "speedway", "international" };

    /// <summary>
    /// Venue-identifying words of a normalised name for the single-word fallback (≥ 5 letters,
    /// not generic or layout words), longest first; empty unless the name reads like a circuit.
    /// </summary>
    public static IEnumerable<string> DistinctiveWords(string normName)
    {
        var words = normName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!words.Any(w => CircuitWords.Contains(w))) return Array.Empty<string>();
        return words.Where(w => w.Length >= 5 && !w.All(char.IsDigit) && !NonVenueWords.Contains(w))
            .Distinct().OrderByDescending(w => w.Length);
    }

    /// <summary>Distance in meters between two WGS84 points (equirectangular; fine below ~100 km).</summary>
    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        double k = 111320.0 * Math.Cos((lat1 + lat2) * 0.5 * Math.PI / 180.0);
        double dx = (lon2 - lon1) * k, dy = (lat2 - lat1) * 111320.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
