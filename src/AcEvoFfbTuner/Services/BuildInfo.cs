using System.Reflection;

namespace AcEvoFfbTuner.Services;

/// <summary>Identity of the running build. release.yml stamps the FULL tag into
/// InformationalVersion ("1.29.0-beta.1"); the assembly version is numeric-only
/// (System.Version cannot parse the suffix), so the beta marker lives here.
/// Stable and local builds carry a plain "1.28.1" and are never beta.</summary>
public static class BuildInfo
{
    /// <summary>Exact release tag without the leading "v", e.g. "1.29.0-beta.1"
    /// or "1.28.1". Empty when the attribute is missing.</summary>
    public static string ReleaseTag { get; } = ReadReleaseTag();

    /// <summary>True for Test Drive (beta) builds — drives the BETA corner
    /// ribbon and the version label. Never true for a stable release.</summary>
    public static bool IsBeta => ReleaseTag.Contains("-beta", StringComparison.OrdinalIgnoreCase);

    private static string ReadReleaseTag()
    {
        try
        {
            var raw = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            int plus = raw.IndexOf('+');
            if (plus >= 0) raw = raw[..plus];
            return raw.Trim().TrimStart('v', 'V');
        }
        catch
        {
            return "";
        }
    }
}
