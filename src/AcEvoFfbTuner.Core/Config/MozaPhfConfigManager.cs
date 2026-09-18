using System.Text.Json;

namespace AcEvoFfbTuner.Core.Config;

public sealed class MozaPhfConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "moza_phf_config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Lazy<MozaPhfConfigManager> _instance = new(() => new MozaPhfConfigManager());
    public static MozaPhfConfigManager Instance => _instance.Value;

    private readonly ReaderWriterLockSlim _lock = new();
    private MozaPhfConfig _config;

    private MozaPhfConfigManager()
    {
        _config = LoadInternal();
    }

    public MozaPhfConfig Config
    {
        get
        {
            _lock.EnterReadLock();
            try { return _config; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void Save(MozaPhfConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            _config = config;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static MozaPhfConfig LoadInternal()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new MozaPhfConfig();

            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<MozaPhfConfig>(json, JsonOptions) ?? new MozaPhfConfig();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MozaPhfConfigManager] Failed to load config: {ex.Message}");
            return new MozaPhfConfig();
        }
    }
}
