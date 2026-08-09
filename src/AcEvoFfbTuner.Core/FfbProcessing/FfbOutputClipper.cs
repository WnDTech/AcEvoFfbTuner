namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbOutputClipper
{
    public float SoftClipThreshold { get; set; } = 0.8f;

    public float Process(float force, out bool isClipping)
    {
        float absForce = Math.Abs(force);

        isClipping = absForce > SoftClipThreshold;

        if (absForce > SoftClipThreshold)
        {
            // Clamp overshoot to the soft range. Without this, SoftClipThreshold = 1.0
            // gives range = 0 → division by zero → softAmount = -Infinity → the output
            // sign INVERTS (e.g. -1.4 Nm input pinned to +1.0) — the wheel yanks in
            // the opposite direction whenever the force exceeds the threshold.
            float range = Math.Max(1.0f - SoftClipThreshold, 0.001f);
            float overshoot = Math.Min(absForce - SoftClipThreshold, range);
            float softAmount = 1.0f - (float)Math.Sqrt(overshoot / range) * 0.5f;
            force = Math.Sign(force) * (SoftClipThreshold + overshoot * softAmount);
        }

        return Math.Clamp(force, -1f, 1f);
    }
}
