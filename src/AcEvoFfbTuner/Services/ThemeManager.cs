using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AcEvoFfbTuner.Services;

public static class ThemeManager
{
    public const string DefaultTheme = "Onyx";

    public static readonly string[] ThemeNames = ["Onyx", "Slate", "Obsidian", "Graphite", "Amber", "Crimson", "Jade", "Cobalt", "Rose", "Charcoal", "Workshop", "WorkshopDark"];

    private static readonly Uri[] ThemeUris = ThemeNames
        .Select(name => new Uri($"Themes/{name}.xaml", UriKind.Relative))
        .ToArray();

    public static string CurrentTheme { get; private set; } = DefaultTheme;

    public static void ApplyTheme(string themeName)
    {
        if (string.IsNullOrEmpty(themeName))
            themeName = DefaultTheme;

        if (!ThemeNames.Contains(themeName))
            themeName = DefaultTheme;

        var app = Application.Current;
        if (app == null) return;

        var merged = app.Resources.MergedDictionaries;

        foreach (var uri in ThemeUris)
        {
            var existing = merged.FirstOrDefault(r => r.Source == uri);
            if (existing != null)
                merged.Remove(existing);
        }

        var newTheme = new ResourceDictionary
        {
            Source = new Uri($"Themes/{themeName}.xaml", UriKind.Relative)
        };
        merged.Add(newTheme);

        CurrentTheme = themeName;
    }

    // Hex colours from code-behind, resolved against the active theme. The first pass of the
    // XAML theming mapped these hexes to named Ui* keys; every other hex has a UiC_<AARRGGBB> key.
    // Classic themes hold the exact original colour, so their look is unchanged.
    private static readonly Dictionary<string, string> NamedHexKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#1AF0883E"] = "UiAccentDim",
        ["#1AFFFFFF"] = "UiHover",
        ["#22FFFFFF"] = "UiHover2",
        ["#FF00E676"] = "UiGoodBright",
        ["#FF0D1117"] = "UiBgDeep",
        ["#FF161B22"] = "UiPanel",
        ["#FF1C2128"] = "UiCard",
        ["#FF21262D"] = "UiInput",
        ["#FF30363D"] = "UiBorder",
        ["#FF333355"] = "UiTrack",
        ["#FF484F58"] = "UiBorderStrong",
        ["#FF4CAF50"] = "UiGood",
        ["#FF4FC3F7"] = "UiSky",
        ["#FF56D4A0"] = "UiTeal",
        ["#FF79C0FF"] = "UiInfo",
        ["#FF7EE787"] = "UiLime",
        ["#FF8B949E"] = "UiTextDim",
        ["#FFC9D1D9"] = "UiTextSoft",
        ["#FFD2A8FF"] = "UiPurple",
        ["#FFE08040"] = "UiAccent2",
        ["#FFE53935"] = "UiBad",
        ["#FFE6EDF3"] = "UiText",
        ["#FFF0883E"] = "UiAccent",
        ["#FFFF7B72"] = "UiSalmon",
        ["#FFFF9800"] = "UiOrange",
        ["#FFFFD600"] = "UiWarn",
    };

    public static SolidColorBrush Brush(string hex)
    {
        var key = NamedHexKeys.TryGetValue(hex, out var named) ? named : "UiC_" + hex.TrimStart('#').ToUpperInvariant();
        // Always a new, unfrozen brush: callers (e.g. LedVuMeter) mutate .Color in place,
        // and theme resources are shared and read-only.
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return new SolidColorBrush(b.Color);
        return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
    }

    public static System.Windows.Media.Color Color(string hex) => Brush(hex).Color;
}