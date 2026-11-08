namespace AcEvoFfbTuner.Core.PedalHaptics;

/// <summary>
/// Splits a game's single shared kerb-vibration value into left/right sides
/// using per-wheel suspension movement (frame-to-frame |Δ travel|).
/// Wheel order is FL, FR, RL, RR in every reader.
///
/// Weighting: the busier side gets the full kerb value, the quieter side
/// gets it in proportion to its share — a kerb under one side only lands
/// on that side's pedal; a kerb hit square-on (both sides equal) lands on
/// both. With no suspension data the value goes to both sides unchanged.
/// </summary>
public sealed class KerbSideSplitter
{
    private readonly float[] _prev = new float[4];
    private bool _hasPrev;

    /// <summary>Below this total movement per frame the split is ignored and
    /// the shared value goes to both sides (no usable side evidence).</summary>
    public const float MinActivity = 1e-5f;

    public (float left, float right) Split(float shared, float[]? suspensionTravel)
    {
        if (suspensionTravel == null || suspensionTravel.Length < 4)
        {
            _hasPrev = false;
            return (shared, shared);
        }

        float left = 0f, right = 0f;
        if (_hasPrev)
        {
            left = Math.Abs(suspensionTravel[0] - _prev[0]) + Math.Abs(suspensionTravel[2] - _prev[2]);
            right = Math.Abs(suspensionTravel[1] - _prev[1]) + Math.Abs(suspensionTravel[3] - _prev[3]);
        }
        for (int i = 0; i < 4; i++) _prev[i] = suspensionTravel[i];
        bool hadPrev = _hasPrev;
        _hasPrev = true;

        if (shared <= 0f) return (0f, 0f);

        float total = left + right;
        if (!hadPrev || total < MinActivity) return (shared, shared);

        float wLeft = left / total;
        return (shared * Math.Clamp(wLeft * 2f, 0f, 1f),
                shared * Math.Clamp((1f - wLeft) * 2f, 0f, 1f));
    }

    public void Reset() => _hasPrev = false;
}
