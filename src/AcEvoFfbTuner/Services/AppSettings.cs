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
        catch { }
    }
}
