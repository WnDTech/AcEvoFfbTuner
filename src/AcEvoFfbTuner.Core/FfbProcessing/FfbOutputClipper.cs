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
            float overshoot = absForce - SoftClipThreshold;
            float range = 1.0f - SoftClipThreshold;
            float softAmount = 1.0f - (float)Math.Sqrt(overshoot / range) * 0.5f;
            force = Math.Sign(force) * (SoftClipThreshold + overshoot * softAmount);
        }

        return Math.Clamp(force, -1f, 1f);
    }
}
