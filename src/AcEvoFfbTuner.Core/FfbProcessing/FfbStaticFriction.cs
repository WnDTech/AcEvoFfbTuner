using System;
using System.IO;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// Stationary friction using elastic displacement model.
/// COMPLETELY SEPARATE from the driving FFB pipeline.
/// Called ONLY from TelemetryLoop at standstill (&lt; 0.5 km/h).
///
/// Anti-oscillation:
///   Two decay rates control how displacement relaxes:
///   - ActiveDecay (0.995): slow decay during active turning — unnoticeable
///   - ReturnDecay (0.85): fast decay when spring pulls wheel back — kills oscillation
///   The decay is chosen based on whether steerDelta opposes displacement
///   (spring is winning → fast decay) or matches it (user turning → slow decay).
///   Additionally, zero-crossing kills displacement if it somehow flips sign.
///
/// Output smoothing:
///   EMA on final force smooths quantization from discrete SteerDelta steps.
/// </summary>
public sealed class FfbStaticFriction
{
    // ── Tuning knobs ──────────────────────────────────────────────────────

    /// <summary>Master gain. 0 = disabled, 1 = full stationary friction.</summary>
    public float Gain { get; set; } = 1.0f;

    /// <summary>Normalized force cap to prevent hard clipping.</summary>
    public float MaxForce { get; set; } = 0.85f;

    /// <summary>Max elastic stretch before breakout (normalized steer units).</summary>
    public float MaxElasticStretch { get; set; } = 0.01f;

    /// <summary>Spring stiffness during elastic phase.</summary>
    public float SpringStiffness { get; set; } = 15.0f;

    /// <summary>Kinetic (sliding) friction force level (normalized).</summary>
    public float KineticFrictionBase { get; set; } = 0.20f;

    /// <summary>Wheel load scaling per kN.</summary>
    public float FrictionPerKN { get; set; } = 0.04f;

    /// <summary>Static-to-kinetic drop-off ratio at breakout.</summary>
    public float StaticKineticRatio { get; set; } = 0.85f;

    /// <summary>Engine-off viscous damping coefficient.</summary>
    public float EngineOffDamping { get; set; } = 0.15f;

    /// <summary>Engine-on viscous damping.</summary>
    public float EngineOnDamping { get; set; } = 0.02f;

    /// <summary>Force multiplier when engine is off.</summary>
    public float EngineOffScale { get; set; } = 1.0f;

    /// <summary>Force multiplier when engine is running.</summary>
    public float EngineOnScale { get; set; } = 0.3f;

    /// <summary>Compound boost for slick/racing tyres.</summary>
    public float SlickCompoundBoost { get; set; } = 1.2f;

    /// <summary>Input smoothing alpha for Fy and wheel load.</summary>
    public float InputSmoothAlpha { get; set; } = 0.10f;

    /// <summary>
    /// Per-frame displacement decay during active turning (user steering).
    /// 0.92 = 8%/frame — good balance between natural feel and stability.
    /// Lower values = faster displacement decay = more stable but less natural.
    /// </summary>
    public float ActiveDecay { get; set; } = 0.92f;

    /// <summary>
    /// Per-frame displacement decay when spring is pulling wheel back to center
    /// (steerDelta opposes displacement). 0.65 = 35%/frame — kills oscillation fast.
    /// Lower values = faster oscillation kill but spring force fades faster on return.
    /// </summary>
    public float ReturnDecay { get; set; } = 0.65f;

    /// <summary>Output EMA alpha. 0.35 = responsive but smooth. 1.0 = no smoothing.</summary>
    public float OutputSmoothAlpha { get; set; } = 0.35f;

    /// <summary>
    /// Minimum |steerDelta| to accumulate into displacement.
    /// Set to ~2 quantization steps (0.00035) to prevent the micro-oscillation
    /// feedback loop where our own force pushes the wheel by one LSB step,
    /// which then creates displacement, which creates force, ad infinitum.
    /// During active turning, steerDelta is typically 0.001-0.01+ per frame,
    /// so this gate is transparent to real steering input.
    /// </summary>
    public float SteerDeltaNoiseFloor { get; set; } = 0.00035f;

    // ── State ─────────────────────────────────────────────────────────────
    public float LastComputedForce { get; private set; }
    public float CurrentDisplacement => _displacement;

    private float _displacement;
    private float _prevDisplacement;
    private float _prevSteerAngle;
    private bool _steerInitialized;
    private float _smoothedFyMag;
    private float _smoothedLoadKN;
    private float _smoothedOutput;
    private int _zeroCrossingCooldown;

    // ── Diagnostic logging (temporary) ────────────────────────────────────
    private static readonly string DiagLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AcEvoFfbTuner", "static_friction_diag.log");
    private int _diagFrameCount;
    private bool _diagHeaderWritten;
    private const int DiagMaxFrames = 3000;

    public float Compute(FfbRawData raw)
    {
        LastComputedForce = 0f;

        if (Gain < 0.001f)
        {
            _smoothedOutput = 0f;
            return 0f;
        }

        // ── Steer delta ────────────────────────────────────────────────
        float steerDelta = 0f;
        if (_steerInitialized)
            steerDelta = raw.SteerAngle - _prevSteerAngle;
        _prevSteerAngle = raw.SteerAngle;
        _steerInitialized = true;

        // ── Smooth physics inputs ───────────────────────────────────────
        float fyMag = (Math.Abs(raw.Fy[0]) + Math.Abs(raw.Fy[1])) * 0.5f;
        _smoothedFyMag = _smoothedFyMag * (1f - InputSmoothAlpha) + fyMag * InputSmoothAlpha;

        float frontLoadKN = (raw.WheelLoad[0] + raw.WheelLoad[1]) * 0.5f / 1000f;
        _smoothedLoadKN = _smoothedLoadKN * (1f - InputSmoothAlpha) + frontLoadKN * InputSmoothAlpha;

        // ── Update elastic displacement ─────────────────────────────────
        _prevDisplacement = _displacement;

        // Zero-crossing cooldown: after displacement crosses zero, suppress
        // accumulation for N frames. This breaks the underdamped spring oscillation:
        //   spring pushes wheel → momentum carries past zero → displacement
        //   accumulates in opposite direction → spring pulls back → repeat forever.
        // By suppressing accumulation after zero-crossing, the wheel's momentum
        // dissipates via the Moza's internal damping without our force re-injecting energy.
        if (_zeroCrossingCooldown > 0)
        {
            _zeroCrossingCooldown--;
            // Don't accumulate displacement — let wheel momentum dissipate naturally
        }
        else
        {
            // Noise gate: ignore sub-step steerDelta (quantization noise).
            // Our force can only cause single-quantization-step movements (~0.000174).
            // By ignoring those, we break the feedback loop:
            //   force → wheel moves 1 step → displacement → force → repeat
            if (Math.Abs(steerDelta) >= SteerDeltaNoiseFloor)
                _displacement += steerDelta;
        }

        // ── Zero-crossing kill + cooldown ────────────────────────────────
        if (_prevDisplacement != 0f && _displacement != 0f
            && Math.Sign(_prevDisplacement) != Math.Sign(_displacement))
        {
            _displacement = 0f;
            _smoothedOutput = 0f;          // Kill residual EMA force
            // Scale cooldown with Gain: higher gain = more force = more momentum = longer settle time.
            // At Gain=0.5 → ~17 frames, Gain=1.0 → 25 frames, Gain=2.0 → 40 frames.
            // This auto-adapts to stronger wheel bases (which typically use higher gain).
            _zeroCrossingCooldown = 10 + (int)(15f * Math.Max(Gain, 0.5f));
        }

        // ── Direction-aware displacement decay ──────────────────────────
        // When steerDelta OPPOSES displacement (spring pulling wheel back),
        // decay aggressively to kill spring energy.
        // When steerDelta matches displacement (active turning), decay slowly.
        bool isReturning = steerDelta != 0f && _displacement != 0f
            && Math.Sign(steerDelta) != Math.Sign(_displacement);

        if (isReturning)
        {
            _displacement *= ReturnDecay;
        }
        else
        {
            _displacement *= ActiveDecay;
        }

        if (Math.Abs(_displacement) < 0.0001f)
            _displacement = 0f;

        // Clip to maximum elastic stretch
        _displacement = Math.Clamp(_displacement, -MaxElasticStretch, MaxElasticStretch);

        // ── Compute force ───────────────────────────────────────────────
        float engineMult = ComputeEngineMultiplier(raw);
        float compoundFactor = ComputeCompoundFactor(raw.TyreCompoundFront);
        float loadScale = 1f + _smoothedLoadKN * FrictionPerKN;

        float absDisp = Math.Abs(_displacement);
        float stretchRatio = absDisp / Math.Max(MaxElasticStretch, 0.0001f);

        float forceMagnitude;
        if (stretchRatio < 1.0f)
        {
            forceMagnitude = SpringStiffness * absDisp * loadScale * engineMult * compoundFactor * Gain;
        }
        else
        {
            float kineticForce = KineticFrictionBase * loadScale * engineMult * compoundFactor * Gain;
            float staticPeak = SpringStiffness * MaxElasticStretch * loadScale * engineMult * compoundFactor * Gain;
            forceMagnitude = staticPeak * StaticKineticRatio + kineticForce * (1f - StaticKineticRatio);
        }

        float force = CopySignSafe(forceMagnitude, _displacement);

        // ── Viscous damping ─────────────────────────────────────────────
        float dampingCoeff = raw.IsEngineRunning ? EngineOnDamping : EngineOffDamping;
        float dampingForce = -CopySignSafe(Math.Abs(steerDelta) * dampingCoeff * engineMult * Gain * 100f, steerDelta);
        force += dampingForce;

        // ── Clamp ───────────────────────────────────────────────────────
        float clamped = Math.Clamp(force, -MaxForce, MaxForce);
        if (Math.Abs(clamped) < 0.003f)
            clamped = 0f;

        // ── Output EMA smoothing ────────────────────────────────────────
        if (OutputSmoothAlpha >= 0.999f)
            _smoothedOutput = clamped;
        else
            _smoothedOutput = _smoothedOutput * (1f - OutputSmoothAlpha) + clamped * OutputSmoothAlpha;

        // ── Cooldown override: force output to 0 during zero-crossing cooldown ──
        // During cooldown, displacement is 0 but the viscous damping term can
        // still produce non-zero force (it's based on steerDelta). This re-seeds
        // the EMA, which keeps force alive during cooldown, which keeps the wheel
        // moving, which defeats the whole purpose of the cooldown.
        // Fix: unconditionally zero the output during cooldown.
        if (_zeroCrossingCooldown > 0)
            _smoothedOutput = 0f;

        LastComputedForce = _smoothedOutput;

        // ── Diagnostic logging (first N frames only) ────────────────────
        if (_diagFrameCount < DiagMaxFrames)
        {
            _diagFrameCount++;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DiagLogPath)!);
                if (!_diagHeaderWritten)
                {
                    File.AppendAllText(DiagLogPath,
                        "Frame,SteerAngle,SteerDelta,Displacement,PrevDisp,IsReturning,Cooldown," +
                        "ForceMag,FrictionForce,DampingForce,Clamped,Smoothed\n");
                    _diagHeaderWritten = true;
                }
                File.AppendAllText(DiagLogPath,
                    $"{_diagFrameCount},{raw.SteerAngle:F6},{steerDelta:F6},{_displacement:F6},{_prevDisplacement:F6},{(isReturning?1:0)},{_zeroCrossingCooldown}," +
                    $"{forceMagnitude:F6},{CopySignSafe(forceMagnitude,_displacement):F6},{dampingForce:F6},{clamped:F6},{_smoothedOutput:F6}\n");
            }
            catch { }
        }

        return _smoothedOutput;
    }

    private static float CopySignSafe(float magnitude, float signSource)
    {
        if (Math.Abs(magnitude) < 0.0001f)
            return 0f;
        return Math.Sign(signSource) * magnitude;
    }

    private float ComputeEngineMultiplier(FfbRawData raw)
    {
        if (raw.IsEngineRunning)
            return EngineOnScale;
        if (raw.IsIgnitionOn)
        {
            bool isElectric = raw.EngineType == AcEvoEngineType.AcevoElectricMotor
                           || raw.EngineType == AcEvoEngineType.AcevoHybrid;
            return isElectric ? EngineOnScale : EngineOffScale;
        }
        return EngineOffScale;
    }

    private float ComputeCompoundFactor(string compound)
    {
        if (string.IsNullOrEmpty(compound))
            return 1.0f;
        if (compound.Contains("Slick", StringComparison.OrdinalIgnoreCase)
            || compound.Contains("Racing", StringComparison.OrdinalIgnoreCase))
            return SlickCompoundBoost;
        return 1.0f;
    }

    public void Reset()
    {
        LastComputedForce = 0f;
        _displacement = 0f;
        _prevDisplacement = 0f;
        _prevSteerAngle = 0f;
        _steerInitialized = false;
        _smoothedFyMag = 0f;
        _smoothedLoadKN = 0f;
        _smoothedOutput = 0f;
        _zeroCrossingCooldown = 0;
        _diagFrameCount = 0;
        _diagHeaderWritten = false;
    }
}
