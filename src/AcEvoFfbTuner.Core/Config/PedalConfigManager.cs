using System.Text.Json;

namespace AcEvoFfbTuner.Core.Config;

public sealed class PedalConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "pedal_config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Lazy<PedalConfigManager> _instance = new(() => new PedalConfigManager());
    public static PedalConfigManager Instance => _instance.Value;

    private readonly ReaderWriterLockSlim _lock = new();
    private PedalConfig _config;

    private PedalConfigManager()
    {
        _config = LoadInternal();
    }

    public PedalConfig Config
    {
        get
        {
            _lock.EnterReadLock();
            try { return _config; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void Save(PedalConfig config)
    {
        _lock.EnterWriteLock();
        try
        {
            _config = config;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private PedalConfig LoadInternal()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"PedalConfigManager: file NOT found at {ConfigPath}");
                return new PedalConfig();
            }

            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<PedalConfig>(json, JsonOptions);
            if (config == null)
            {
                Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"PedalConfigManager: deserialized NULL from {ConfigPath}");
                return new PedalConfig();
            }
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways(
                $"PedalConfigManager: loaded {ConfigPath} — gasAxis={config.GasAxis} brakeAxis={config.BrakeAxis} clutchAxis={config.ClutchAxis}");
            return config;
        }
        catch (Exception ex)
        {
            Core.DirectInput.VJoy.VJoyFeeder.LogAlways($"PedalConfigManager: FAILED to load {ConfigPath}: {ex.Message}");
            return new PedalConfig();
        }
    }
}
