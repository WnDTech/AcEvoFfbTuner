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
    public bool AutoDetectGame { get; set; } = true;
    public bool AutoSwitchProfiles { get; set; } = true;
    public bool ProfileLocked { get; set; }
    public string ThemeName { get; set; } = ThemeManager.DefaultTheme;
    public bool VoiceEnabled { get; set; } = true;
    public int VoiceVolume { get; set; } = 75;
    public string? VoiceName { get; set; }
    public bool UseEdgeTts { get; set; } = true;
    public string GoogleTtsLanguage { get; set; } = "en";
    public string? OpenAiApiKey { get; set; }
    public string OpenAiModel { get; set; } = "deepseek-v4-flash";
    public string AiBaseUrl { get; set; } = "https://opencode.ai/zen/go/v1";
    public string FeedbackRelayUrl { get; set; } = "http://127.0.0.1:8090";

    // Profile Hub — used by Share-to-Hub and Browse-Hub features
    public string HubApiBaseUrl { get; set; } = "https://ffbtuner.wndtech.tips/api/hub.php";
    public string HubApiKey { get; set; } = "d0fbf9a40df1393eac2c2e0c2ed4563e319f4ba4b3b6a22c3ac8d358d6e93e4b";
    public string HubAuthorName { get; set; } = "";
    public string HubAuthorId { get; set; } = "";

    /// <summary>True once the WER LocalDumps registry keys for this exe have
    /// been requested (one-time elevated setup) — Windows then writes a
    /// minidump for every crash, even ones that kill the in-process filter.</summary>
    public bool WerLocalDumpsConfigured { get; set; }

    /// <summary>Persisted Logitech wheel FFB strength in Nm (1-8). Applied to
    /// the wheel at every connect — the wheel's desktop profile loads defaults
    /// (5 Nm) otherwise, and without a deliberate write the wheel stays there
    /// until a slider is touched.</summary>
    public float LogitechFfbStrengthNm { get; set; } = 8.0f;

    /// <summary>Persisted Logitech wheel rotation in degrees (90-2700).
    /// Applied to the wheel at every connect alongside the strength.</summary>
    public int LogitechRotationDegrees { get; set; } = 1080;

    /// <summary>Which wheel profile the app writes settings to: -1 = keep the
    /// wheel's current mode (default — never yanks the user out of their
    /// chosen slot), 0 = desktop mode (G HUB profile — live changes, but
    /// resets on wheel restart), 1-5 = onboard slots (settings stored in the
    /// wheel, persist across restarts).</summary>
    public int LogitechProfileSlot { get; set; } = -1;

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
