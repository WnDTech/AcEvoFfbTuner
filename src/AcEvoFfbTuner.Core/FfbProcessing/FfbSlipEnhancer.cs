using AcEvoFfbTuner.Core.FfbProcessing.Models;

namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbSlipEnhancer
{
    public float SlipRatioGain { get; set; } = 0.0f;
    public float SlipAngleGain { get; set; } = 0.0f;
    public float SlipThreshold { get; set; } = 0.05f;
    public bool UseFrontOnly { get; set; } = true;

    /// <summary>
    /// Slip angle (radians) at which Mz peaks for the typical GT3 tire.
    /// Real values: 0.05-0.10 rad (~3-6°). Default 0.08 rad (~4.6°).
    /// Below this angle, Mz enhancement boosts (approaching peak bite).
    /// Above this angle, Mz enhancement reduces (post-peak lightening warning).
    /// </summary>
    public float PeakSlipAngle { get; set; } = 0.08f;

    /// <summary>
    /// How strongly the slip-angle curve shape modulates the force.
    /// 0.0 = disabled (no modulation), 1.0 = full Pacejka-like shaping.
    /// Adds to the physics model's existing Mz — use conservatively (0.1-0.3).
    /// </summary>
    public float SlipAngleShapeGain { get; set; } = 0.0f;

    private float _smSlipForce;
    private float _smShapeForce;

    public float Apply(float force, FfbRawData raw)
    {
        if (SlipRatioGain < 0.001f && SlipAngleGain < 0.001f && SlipAngleShapeGain < 0.001f)
            return force;

        int startIdx = UseFrontOnly ? 0 : 0;
        int endIdx = UseFrontOnly ? 2 : 4;

        float avgSlipRatio = 0f;
        float avgSlipAngle = 0f;
        int count = 0;

        for (int i = startIdx; i < endIdx; i++)
        {
            if (Math.Abs(raw.SlipRatio[i]) > SlipThreshold)
                avgSlipRatio += raw.SlipRatio[i];
            if (Math.Abs(raw.SlipAngle[i]) > SlipThreshold)
                avgSlipAngle += raw.SlipAngle[i];
            count++;
        }

        if (count > 0)
        {
            avgSlipRatio /= count;
            avgSlipAngle /= count;
        }

        // ── Legacy linear slip enhancement (SlipRatioGain + SlipAngleGain) ──
        float slipForce = (avgSlipRatio * SlipRatioGain + avgSlipAngle * SlipAngleGain);
        float maxSlip = Math.Max(Math.Abs(force) * 0.3f, 0.1f);
        slipForce = Math.Clamp(slipForce, -maxSlip, maxSlip);

        _smSlipForce = _smSlipForce * 0.65f + slipForce * 0.35f;
        _smSlipForce = Math.Clamp(_smSlipForce, -maxSlip, maxSlip);

        // ── Mz-curve-shaped enhancement based on slip angle ──
        // Models the real Pacejka Mz characteristic:
        //   - Linear region (α < peak): slight boost, emphasizing the "bite"
        //   - At peak (α ≈ peak): maximum enhancement
        //   - Post-peak (α > peak): reduction, creating the "going light" warning
        //   - Full slide (α >> peak): strong reduction
        float shapeForce = 0f;
        if (SlipAngleShapeGain > 0.001f && PeakSlipAngle > 0.001f)
        {
            float absSlipAngle = Math.Abs(avgSlipAngle);
            float normalizedAlpha = absSlipAngle / PeakSlipAngle;

            // Pacejka-like Mz curve shape using a simple approximation:
            // shape(x) = x * exp(-x) peaks at x=1 (the peak slip angle)
            // This gives: boost before peak, maximum at peak, dropoff after peak.
            float shape;
            if (normalizedAlpha < 0.001f)
            {
                shape = 0f;
            }
            else
            {
                shape = normalizedAlpha * MathF.Exp(-normalizedAlpha);
            }

            // shape peaks at ~0.368. Normalize so the peak contribution = SlipAngleShapeGain
            float peakShape = 1f / MathF.Exp(-1f); // ≈ 2.718, so shape * 2.718 peaks at 1.0
            shapeForce = shape * peakShape * SlipAngleShapeGain * Math.Sign(force);

            // Clamp to prevent excessive modulation
            shapeForce = Math.Clamp(shapeForce, -Math.Abs(force) * 0.15f, Math.Abs(force) * 0.15f);
        }

        _smShapeForce = _smShapeForce * 0.70f + shapeForce * 0.30f;

        return force + _smSlipForce + _smShapeForce;
    }
}