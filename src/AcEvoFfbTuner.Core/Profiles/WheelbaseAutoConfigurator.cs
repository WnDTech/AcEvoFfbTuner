namespace AcEvoFfbTuner.Core.Profiles;

public enum WheelCharacteristics
{
    DirectDrive,
    BeltDriven,
    GearDriven
}

public static class WheelbaseAutoConfigurator
{
    private static readonly (string ProfileName, float TorqueNm)[] BuiltInProfilesByTorque =
    {
        ("Default - Logitech G29/G920", 2.5f),
        ("Default - Thrustmaster T300/TX", 4.5f),
        ("Default - Fanatec CSL DD 5Nm", 5.0f),
        ("Moza R5 - Final Stable Baseline", 5.5f),
        ("Default - Fanatec CSL DD 8Nm", 8.0f),
        ("Default - Moza R9", 9.0f),
        ("Default - Fanatec ClubSport DD", 15.0f),
        ("Default - Simagic Alpha", 15.0f),
        ("Default - Simucube 2 Pro", 25.0f),
    };

    public static WheelCharacteristics DetectWheelType(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return WheelCharacteristics.BeltDriven;
        var n = deviceName.ToUpperInvariant();

        if (n.Contains("G29") || n.Contains("G920") || n.Contains("G27") || n.Contains("DFGT") || n.Contains("DRIVING FORCE"))
            return WheelCharacteristics.GearDriven;

        if (n.Contains("T150") || n.Contains("T300") || n.Contains("TX ") || n.Contains("TMX") || n.Contains("T248"))
            return WheelCharacteristics.BeltDriven;

        return WheelCharacteristics.DirectDrive;
    }

    public static bool DetectForceInversion(string deviceName)
    {
        return DirectInput.FfbDeviceManager.DetectForceInversion(deviceName);
    }

    public static FfbProfile SelectNearestProfile(float torqueNm, string deviceName)
    {
        string nearest = FindNearestProfileName(torqueNm);
        var profile = FfbProfile.GetDefaultProfile(nearest);

        profile.Name = $"Auto - {deviceName} ({torqueNm:F1}Nm)";
        profile.WheelMaxTorqueNm = torqueNm;
        profile.ForceInvertEnabled = DetectForceInversion(deviceName);

        return profile;
    }

    public static FfbProfile GenerateProfile(float torqueNm, string deviceName, EvoDetectedSettings? evoSettings)
    {
        string nearest = FindNearestProfileName(torqueNm);
        var profile = FfbProfile.GetDefaultProfile(nearest);

        profile.Name = $"Auto - {deviceName} ({torqueNm:F1}Nm)";
        profile.WheelMaxTorqueNm = torqueNm;
        profile.ForceInvertEnabled = DetectForceInversion(deviceName);

        if (evoSettings != null && evoSettings.IsValid)
        {
            profile.SteeringLockDegrees = evoSettings.RecommendedSteeringLock;
            EvoSettingsDetector.ApplyToProfile(profile, evoSettings);
        }

        return profile;
    }

    private static string FindNearestProfileName(float torqueNm)
    {
        string nearest = BuiltInProfilesByTorque[0].ProfileName;
        float minDiff = float.MaxValue;

        foreach (var (name, torque) in BuiltInProfilesByTorque)
        {
            float diff = Math.Abs(torque - torqueNm);
            if (diff < minDiff)
            {
                minDiff = diff;
                nearest = name;
            }
        }

        return nearest;
    }
}
