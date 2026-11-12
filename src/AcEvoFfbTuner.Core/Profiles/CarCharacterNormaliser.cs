namespace AcEvoFfbTuner.Core.Profiles;

/// <summary>
/// Car Character Normaliser — Phase 2 maths (docs/car-character-normaliser-spec.md).
///
/// The user's profile is treated as tuned on a "median" car of the class. Each other car's
/// peak relative to that reference is ratio^keep:
///   keep = 0  → every car has the same peak (today's auto-normalisation),
///   keep = 1  → the real torque difference is kept unchanged (gain 1),
///   between   → differences are compressed but preserved.
/// Heavier cars are capped at maxPeakRatio × the reference peak so they cannot clip much
/// harder than the car the profile was tuned on.
/// </summary>
public static class CarCharacterNormaliser
{
    public const float MinGain = 0.25f;
    public const float MaxGain = 4f;
    /// <summary>Max change per second while driving, so the wheel never steps.</summary>
    public const float SlewPerSecond = 0.02f;

    public static float TargetGain(float carP99, float referenceP99, float keep, float maxPeakRatio)
    {
        if (!(carP99 > 0f) || !(referenceP99 > 0f) || !float.IsFinite(carP99) || !float.IsFinite(referenceP99))
            return 1f;
        keep = Math.Clamp(keep, 0f, 1f);
        float ratio = carP99 / referenceP99;
        float peakRel = MathF.Min(MathF.Pow(ratio, keep), Math.Max(maxPeakRatio, 0.5f));
        return Math.Clamp(peakRel / ratio, MinGain, MaxGain);
    }

    /// <summary>Moves current toward target by at most SlewPerSecond·dt (relative), or holds
    /// while the wheel is heavily loaded; snaps when parked (no force to step).</summary>
    public static float Step(float current, float target, float dtSeconds, bool hold, bool parked)
    {
        if (parked) return target;
        if (hold) return current;
        float maxStep = Math.Max(current, 0.01f) * SlewPerSecond * Math.Max(dtSeconds, 0f);
        return current + Math.Clamp(target - current, -maxStep, maxStep);
    }
}
