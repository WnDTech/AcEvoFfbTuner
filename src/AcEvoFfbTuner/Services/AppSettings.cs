using System.IO;
using System.Text.Json;

namespace AcEvoFfbTuner.Services;

public sealed class AppSettings
{
    public bool SplashScreenEnabled { get; set; } = true;
    public string? CustomStartupSoundPath { get; set; }
    public string? LastRecordingDeviceId { get; set; }
    public int SnapshotButtonComboIndex { get; set; }
    public int PanicButtonComboIndex { get; set; }
    public string? PanicDeviceInstanceId { get; set; }
    public string? LastSeenVersion { get; set; }
    public bool StartMinimised { get; set; }
    public bool AutoConnect { get; set; }
    public bool AutoStart { get; set; }
    public string? LastConnectedDeviceInstanceId { get; set; }
    public bool PerCarAutoLoadEnabled { get; set; } = true;
    public string DefaultStartPage { get; set; } = "Home";
    public bool TooltipsEnabled { get; set; } = true;
    public bool AutoProfileUpgrade { get; set; }
    public string ThemeName { get; set; } = ThemeManager.DefaultTheme;
    public bool VoiceEnabled { get; set; } = true;
    public int VoiceVolume { get; set; } = 75;
    public string? VoiceName { get; set; }
    public bool UseEdgeTts { get; set; } = true;
    public string GoogleTtsLanguage { get; set; } = "en";

    private static readonly string BasePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcEvoFfbTuner");

    private static readonly string FilePath = Path.Combine(BasePath, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(BasePath);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(Path.Combine(BasePath, "settings_error.log"),
                    $"{DateTime.Now}: Failed to save settings.json to '{FilePath}': {ex}");
            }
            catch { }
        }
    }
}
