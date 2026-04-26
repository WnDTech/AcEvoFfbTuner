using System.Text.Json;
using System.Text.Json.Serialization;
using AcEvoFfbTuner.Core.FfbProcessing;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class ProfileManager
{
    private static readonly string ProfilesDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "Profiles");

    private static readonly string AppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner");

    private static readonly string LastProfilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "last_profile.txt");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly List<FfbProfile> _profiles = new();
    private FfbProfile? _activeProfile;

    public IReadOnlyList<FfbProfile> Profiles => _profiles.AsReadOnly();
    public FfbProfile? ActiveProfile => _activeProfile;

    public event Action? ProfilesChanged;
    public event Action<FfbProfile>? ActiveProfileChanged;

    public void Initialize()
    {
        Directory.CreateDirectory(ProfilesDirectory);

        if (!Directory.GetFiles(ProfilesDirectory, "*.json").Any())
            CreateDefaultProfiles();
        else
            EnsureDefaultProfilesExist();

        LoadAllProfiles();

        string? lastProfileName = null;
        if (File.Exists(LastProfilePath))
        {
            lastProfileName = File.ReadAllText(LastProfilePath).Trim();
            _activeProfile = _profiles.FirstOrDefault(p => p.Name == lastProfileName);
        }

        _activeProfile ??= _profiles.FirstOrDefault();
        if (_activeProfile != null)
            ActiveProfileChanged?.Invoke(_activeProfile);
    }

    public void LoadProfileIntoPipeline(FfbProfile profile, FfbPipeline pipeline)
    {
        profile.ApplyToPipeline(pipeline);
    }

    public FfbProfile SaveProfileFromPipeline(FfbPipeline pipeline, string name)
    {
        var existing = _profiles.FirstOrDefault(p => p.Name == name);
        if (existing != null)
        {
            existing.UpdateFromPipeline(pipeline);
            SaveProfile(existing);
            return existing;
        }

        var profile = FfbProfile.CreateFromPipeline(pipeline, name);
        _profiles.Add(profile);
        SaveProfile(profile);
        ProfilesChanged?.Invoke();
        return profile;
    }

    public void SetActiveProfile(FfbProfile profile)
    {
        _activeProfile = profile;
        ActiveProfileChanged?.Invoke(profile);
        SaveLastProfileName(profile.Name);
    }

    private void SaveLastProfileName(string name)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(LastProfilePath, name);
        }
        catch { }
    }

    public void DeleteProfile(FfbProfile profile)
    {
        _profiles.Remove(profile);
        var filePath = GetProfileFilePath(profile.Name);
        if (File.Exists(filePath))
            File.Delete(filePath);

        if (_activeProfile == profile)
        {
            _activeProfile = _profiles.FirstOrDefault();
            if (_activeProfile != null)
                ActiveProfileChanged?.Invoke(_activeProfile);
        }

        ProfilesChanged?.Invoke();
    }

    public void RenameProfile(FfbProfile profile, string newName)
    {
        var oldFilePath = GetProfileFilePath(profile.Name);
        if (File.Exists(oldFilePath))
            File.Delete(oldFilePath);

        profile.Name = newName;
        SaveProfile(profile);
        ProfilesChanged?.Invoke();
    }

    public void SaveProfile(FfbProfile profile)
    {
        var filePath = GetProfileFilePath(profile.Name);
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public void ExportProfile(FfbProfile profile, string filePath)
    {
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public FfbProfile? ImportProfile(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            var profile = JsonSerializer.Deserialize<FfbProfile>(json, JsonOptions);
            if (profile == null) return null;

            profile.Migrate();

            _profiles.Add(profile);
            SaveProfile(profile);
            ProfilesChanged?.Invoke();
            return profile;
        }
        catch
        {
            return null;
        }
    }

    private void CreateDefaultProfiles()
    {
        foreach (var name in FfbProfile.AllDefaultNames)
        {
            var profile = FfbProfile.GetDefaultProfile(name);
            SaveProfile(profile);
        }
    }

    private void EnsureDefaultProfilesExist()
    {
        foreach (var name in FfbProfile.AllDefaultNames)
        {
            var filePath = GetProfileFilePath(name);
            if (!File.Exists(filePath))
            {
                var profile = FfbProfile.GetDefaultProfile(name);
                SaveProfile(profile);
            }
        }
    }

    private void LoadAllProfiles()
    {
        _profiles.Clear();
        foreach (var file in Directory.GetFiles(ProfilesDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<FfbProfile>(json, JsonOptions);
                if (profile != null)
                {
                    int prevVersion = profile.Version;
                    profile.Migrate();
                    if (profile.Version > prevVersion)
                        SaveProfile(profile);
                    _profiles.Add(profile);
                }
            }
            catch { }
        }
    }

    private static string GetProfileFilePath(string name)
    {
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(ProfilesDirectory, $"{safeName}.json");
    }
}
