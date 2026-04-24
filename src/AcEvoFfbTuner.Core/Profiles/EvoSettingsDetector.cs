using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.Profiles;

public sealed class EvoDetectedSettings
{
    public float FfbStrength { get; init; }
    public float CarFfbMultiplier { get; init; }
    public int SteerDegrees { get; init; }
    public float RecommendedOutputGain { get; init; }
    public float RecommendedNormalizationScale { get; init; }
    public int RecommendedSteeringLock { get; init; }
    public bool IsValid { get; init; }
}

public static class EvoSettingsDetector
{
    public static EvoDetectedSettings? DetectFromRaw(FfbRawData raw)
    {
        if (raw == null) return null;

        float ffbStrength = raw.FfbStrength;
        float carMult = raw.CarFfbMultiplier;
        int steerDeg = raw.SteerDegrees;

        if (ffbStrength < 0.001f && carMult < 0.001f)
            return null;

        float outputGain = ffbStrength > 0.001f ? Math.Clamp(ffbStrength, 0.1f, 5.0f) : 1.0f;

        float normScale;
        if (carMult > 0.001f)
        {
            normScale = Math.Clamp(250f / carMult, 50f, 2000f);
        }
        else
        {
            normScale = 250f;
        }

        int lockDeg = steerDeg > 90 && steerDeg <= 1440 ? steerDeg : 900;

        return new EvoDetectedSettings
        {
            FfbStrength = ffbStrength,
            CarFfbMultiplier = carMult,
            SteerDegrees = steerDeg,
            RecommendedOutputGain = outputGain,
            RecommendedNormalizationScale = normScale,
            RecommendedSteeringLock = lockDeg,
            IsValid = ffbStrength > 0.001f || carMult > 0.001f
        };
    }

    public static void ApplyToProfile(FfbProfile profile, EvoDetectedSettings settings)
    {
        profile.OutputGain = settings.RecommendedOutputGain;
        profile.NormalizationScale = settings.RecommendedNormalizationScale;
        profile.SteeringLockDegrees = settings.RecommendedSteeringLock;
    }
}
