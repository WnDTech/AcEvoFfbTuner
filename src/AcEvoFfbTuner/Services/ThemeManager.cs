using System.Linq;
using System.Windows;

namespace AcEvoFfbTuner.Services;

public static class ThemeManager
{
    public const string DefaultTheme = "Onyx";

    public static readonly string[] ThemeNames = ["Onyx", "Slate", "Obsidian", "Graphite", "Amber", "Crimson", "Jade", "Cobalt", "Rose", "Charcoal"];

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
}
