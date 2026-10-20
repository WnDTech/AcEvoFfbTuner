using System;
using System.IO;
using AcEvoFfbTuner.Core.FfbProcessing.Models;
using AcEvoFfbTuner.Core.SharedMemory.Structs;

namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// Stationary friction using elastic displacement model.
/// COMPLETELY SEPARATE from the driving FFB pipeline.
/// Called ONLY from TelemetryLoop at standstill (hysteresis: engage &lt; 0.4 km/h,
/// release &gt; 1.2 km/h, crossfaded — see TelemetryLoop stationary blend).
/// Driving pipelines zero their output below 0.5 km/h, so there is no overlap.
///
/// Layers (all synthesized — game physics channels are ~0 at v=0):
///   1. Torsional wind-up (elastic spring + kinetic breakout) — base.
///   2. Stick-slip breakout dither near/after breakaway (BreakoutGain).
///   3. Scrub grain: position-dependent tread/aggregate texture driven by
///      steer travel, amplitude by load × stretch (ScrubGain).
///   4. Load asymmetry + pressure + surface (TireOnMtrl) modulation.
///
/// Anti-oscillation:
///   Two decay rates control how displacement relaxes:
///   - ActiveDecay: slow decay during active turning — unnoticeable
///   - ReturnDecay: fast decay when spring pulls wheel back — kills oscillation
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

    // ── Stationary detail (v2, universal — all games) ─────────────────────
    // All default ON at modest levels: driving pipelines are zeroed at
    // standstill so these cannot leak into driving feel (zero driving impact).
    // Set Gain=0 to disable the whole stationary path as before.

    /// <summary>Stick-slip judder amplitude near/after breakaway (0-1). 0 = off.</summary>
    public float BreakoutGain { get; set; } = 0.35f;

    /// <summary>Stick-slip judder frequency in Hz (20-70). Time-based phase at ~333Hz loop.</summary>
    public float BreakoutFrequencyHz { get; set; } = 42f;

    /// <summary>Scrub grain amplitude (0-1): tread-block/aggregate texture felt
    /// while the tyre twists. Position-dependent (travel-driven), not a buzz. 0 = off.</summary>
    public float ScrubGain { get; set; } = 0.30f;

    /// <summary>Left/right front-load asymmetry feel (0-1). Subtle weight offset
    /// from driver/fuel weight. 0 = off.</summary>
    public float LoadAsymmetryGain { get; set; } = 0.5f;

    /// <summary>Tyre-pressure sensitivity (0-1). Low pressure = heavier + grainier.
    /// Unit-aware (PSI vs kPa auto-detect); invalid/zero pressure = neutral. 0 = off.</summary>
    public float PressureSensitivity { get; set; } = 0.4f;

    /// <summary>Respond to R3E TireOnMtrl surface (grass/dirt/gravel/rumble).
    /// Non-R3E games report none → neutral 1.0.</summary>
    public bool SurfaceResponseEnabled { get; set; } = true;

    /// <summary>
    /// True when the game's centering force points the SAME direction as the
    /// steer angle (column-force games: R3E + LMU, verified on hardware —
    /// positive output pushes LEFT while steer+ is RIGHT, so +sign(steer)
    /// is toward center). EVO/AC/ACC use the opposite convention (default
    /// false). When true, the elastic spring + viscous damping signs are
    /// flipped so stationary friction resists displacement instead of
    /// assisting it (pull-away). Fixed per-game convention, NOT a profile
    /// value — set from the active pipeline type in TelemetryLoop.
    /// </summary>
    public bool CenteringMatchesSteerSign { get; set; } = false;

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
    private float _breakoutPhase;
    private float _smoothedSurface = 1f;

    // Scrub texture state (value noise driven by steer travel).
    private float _scrubCycle;          // 0..1 position within current texture cycle
    private float _scrubPrev;           // random anchor at start of cycle
    private float _scrubNext;           // random anchor at end of cycle
    private float _scrubVel;            // smoothed steer speed (norm/s)
    private uint _scrubRng = 0x9E3779B9u;

    /// <summary>Loop sample rate assumed for steer-speed conversion (physics packets ~333 Hz).</summary>
    private const float SampleRateHz = 333f;
    /// <summary>Steer travel (normalized) per texture cycle. 0.012 ≈ 5° of wheel at 900° lock → ~40 Hz texture at 0.5 norm/s.</summary>
    private const float ScrubCycleNorm = 0.012f;
    /// <summary>Steer speed (normalized/s) at which scrub reaches full amplitude. 0.5 ≈ 225°/s at 900° lock.</summary>
    private const float ScrubSaturateVel = 0.5f;
    /// <summary>Peak scrub amplitude at ScrubGain=1, normalized force. 0.20 → 0.06 at default ScrubGain 0.30.</summary>
    private const float ScrubAmplitude = 0.20f;
    /// <summary>Scrub factor when engine is running (power assist softens, does not erase).</summary>
    private const float ScrubEngineOnFactor = 0.6f;

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
        // Fy is ~0 at standstill; including it keeps the smoother live with
        // zero effect when parked, and adds slope/camber load when present.
        float effectiveLoadKN = _smoothedLoadKN + _smoothedFyMag / 4000f;
        float loadScale = 1f + effectiveLoadKN * FrictionPerKN;
        float surfaceFactor = ComputeSurfaceFactor(raw);
        _smoothedSurface = _smoothedSurface * 0.95f + surfaceFactor * 0.05f;
        float pressureFactor = ComputePressureFactor(raw);
        float detailScale = loadScale * _smoothedSurface * pressureFactor;

        float absDisp = Math.Abs(_displacement);
        float stretchRatio = absDisp / Math.Max(MaxElasticStretch, 0.0001f);

        float forceMagnitude;
        if (stretchRatio < 1.0f)
        {
            forceMagnitude = SpringStiffness * absDisp * detailScale * engineMult * compoundFactor * Gain;
        }
        else
        {
            float kineticForce = KineticFrictionBase * detailScale * engineMult * compoundFactor * Gain;
            float staticPeak = SpringStiffness * MaxElasticStretch * detailScale * engineMult * compoundFactor * Gain;
            forceMagnitude = staticPeak * StaticKineticRatio + kineticForce * (1f - StaticKineticRatio);
        }

        // Restoring spring: oppose the elastic displacement, pulling the wheel
        // back toward the grab point (center). Sign depends on the game's
        // steer convention: EVO reports steer opposite to the device centering
        // direction (opposite-sign centers → negate), while column-force games
        // (R3E/LMU) center with the SAME sign as steer (verified: R3E steer+
        // is RIGHT, device+ pushes LEFT, so +sign(steer) is toward center).
        // Using the wrong polarity here is positive feedback — pushing the
        // wheel away from center at standstill.
        float springPolarity = CenteringMatchesSteerSign ? 1f : -1f;
        float force = springPolarity * CopySignSafe(forceMagnitude, _displacement);

        // ── Viscous damping ─────────────────────────────────────────────
        // Opposes velocity in physical space; game-delta sign maps through the
        // same convention, so it flips with the spring polarity.
        float dampingCoeff = raw.IsEngineRunning ? EngineOnDamping : EngineOffDamping;
        float dampingForce = springPolarity * CopySignSafe(Math.Abs(steerDelta) * dampingCoeff * engineMult * Gain * 100f, steerDelta);
        force += dampingForce;

        // ── Stationary detail: stick-slip + asymmetry ────────────────────
        // Signed into the same polarity frame as the spring so R3E/LMU
        // (CenteringMatchesSteerSign) and EVO/AC/ACC stay consistent.
        // Scrub texture is NOT added here: it is applied after output smoothing
        // (see ComputeScrubTexture) so the EMA cannot erase it.
        float detailForce = 0f;

        // Stick-slip breakout judder: near/after breakaway the patch catches
        // and releases. Time-based phase (~333Hz loop assumption); amplitude
        // peaks at the stretch limit and persists in kinetic sliding.
        if (BreakoutGain > 0.001f && stretchRatio > 0.6f)
        {
            float freq = Math.Clamp(BreakoutFrequencyHz, 20f, 70f);
            _breakoutPhase += MathF.Tau * freq / 333f;
            if (_breakoutPhase > MathF.Tau) _breakoutPhase -= MathF.Tau;
            float breakoutGate = Math.Clamp((stretchRatio - 0.6f) / 0.4f, 0f, 1f);
            float judder = MathF.Sin(_breakoutPhase) * BreakoutGain * 0.028f * detailScale * engineMult * Gain * breakoutGate;
            detailForce += springPolarity * judder;
        }

        // Load asymmetry: subtle constant weight from driver/fuel offset.
        // Small by design (±0.02 max) — directional stiffness would need a
        // per-game steer-direction map; this gives weight without pull-away risk.
        if (LoadAsymmetryGain > 0.001f && raw.WheelLoad is { Length: >= 2 })
        {
            float diffKN = (raw.WheelLoad[0] - raw.WheelLoad[1]) / 1000f;
            if (!float.IsNaN(diffKN) && !float.IsInfinity(diffKN))
            {
                float asym = Math.Clamp(diffKN * 0.08f, -0.02f, 0.02f) * LoadAsymmetryGain;
                detailForce += springPolarity * asym * engineMult * Gain;
            }
        }

        force += detailForce;

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

        // ── Tyre scrub texture (post-smoothing, velocity-driven) ────────
        // Added AFTER the EMA so the grain survives. Not a spring term, so it
        // cannot feed the oscillation loop the spring/cooldown guard against.
        float scrub = ComputeScrubTexture(steerDelta, detailScale, raw.IsEngineRunning);
        LastComputedForce = Math.Clamp(_smoothedOutput + scrub, -MaxForce, MaxForce);
        if (Math.Abs(LastComputedForce) < 0.003f)
            LastComputedForce = 0f;
        float outputForce = LastComputedForce;

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

        return outputForce;
    }

    /// <summary>
    /// Tyre scrub texture: a rough, zero-mean grain that exists only while the
    /// wheel is being turned. Grain frequency is proportional to steer speed
    /// (one texture cycle per <see cref="ScrubCycleNorm"/> of steer travel), so
    /// faster turning gives a finer, busier texture — the way real tread
    /// blocks drag across the contact patch. Value noise (random values per
    /// cycle, smoothly interpolated) avoids a pure-tone buzz.
    /// Amplitude ramps in with steer speed and saturates at
    /// <see cref="ScrubSaturateVel"/>; it fades to zero when the wheel stops.
    /// </summary>
    private float ComputeScrubTexture(float steerDelta, float detailScale, bool engineRunning)
    {
        if (ScrubGain <= 0.001f)
        {
            _scrubVel = 0f;
            return 0f;
        }

        // Steer speed in normalized units per second, EMA-smoothed.
        // Clamp the per-frame delta so a single bad sample cannot spin the cycle loop.
        float stepAbs = Math.Min(Math.Abs(steerDelta), 0.1f);
        _scrubVel = _scrubVel * 0.7f + (stepAbs * SampleRateHz) * 0.3f;

        float speedGate = Math.Clamp(_scrubVel / ScrubSaturateVel, 0f, 1f);
        if (speedGate < 0.001f)
            return 0f;

        // Advance texture by steer travel (not time): stopping freezes the grain.
        _scrubCycle += stepAbs / ScrubCycleNorm;
        while (_scrubCycle >= 1f)
        {
            _scrubCycle -= 1f;
            _scrubPrev = _scrubNext;
            _scrubNext = NextScrubRandom();
        }

        float t = _scrubCycle;
        float smooth = t * t * (3f - 2f * t);
        float grain = _scrubPrev + (_scrubNext - _scrubPrev) * smooth;

        // Engine running: power assist softens the texture but must not erase it.
        float engineFactor = engineRunning ? ScrubEngineOnFactor : 1f;

        return grain * ScrubGain * ScrubAmplitude * detailScale * Gain * speedGate * engineFactor;
    }

    private float NextScrubRandom()
    {
        // xorshift32: allocation-free, deterministic, values in [-1, 1].
        _scrubRng ^= _scrubRng << 13;
        _scrubRng ^= _scrubRng >> 17;
        _scrubRng ^= _scrubRng << 5;
        return (_scrubRng / 4294967295f) * 2f - 1f;
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
        var cat = TyreCompoundClassifier.Classify(compound);
        return cat switch
        {
            TyreCompoundCategory.DrySlickSoft => SlickCompoundBoost,
            TyreCompoundCategory.DrySlickMedium => SlickCompoundBoost,
            TyreCompoundCategory.DrySlickHard => SlickCompoundBoost,
            _ => 1.0f
        };
    }

    /// <summary>
    /// R3E TireOnMtrl surface factor (front-axle average). Non-R3E games leave
    /// TireOnMtrl at 0/none → neutral 1.0. Mapping: tarmac/concrete 1.0,
    /// grass 0.65, dirt 0.75, gravel 0.8, rumble 1.25 (extra grain via scrub).
    /// -1 (N/A) → 1.0. Smoothed by caller.
    /// </summary>
    private float ComputeSurfaceFactor(FfbRawData raw)
    {
        if (!SurfaceResponseEnabled)
            return 1f;
        try
        {
            if (raw.TireOnMtrl is not { Length: >= 2 })
                return 1f;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < 2; i++)
            {
                float f = raw.TireOnMtrl[i] switch
                {
                    1 => 1f,    // tarmac
                    2 => 0.65f, // grass
                    3 => 0.75f, // dirt
                    4 => 0.8f,  // gravel
                    5 => 1.25f, // rumble strip
                    6 => 1f,    // concrete
                    _ => 1f,    // 0 none / -1 N/A / unknown → neutral
                };
                sum += f;
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }
        catch { return 1f; }
    }

    /// <summary>
    /// Unit-aware pressure factor: low pressure = heavier + grainier.
    /// PSI path (&lt;60 avg): ref 30 PSI. kPa path: ref 200 kPa.
    /// Invalid/zero → neutral 1.0.
    /// </summary>
    private float ComputePressureFactor(FfbRawData raw)
    {
        if (PressureSensitivity <= 0.001f)
            return 1f;
        try
        {
            if (raw.WheelsPressure is not { Length: >= 2 })
                return 1f;
            float p0 = raw.WheelsPressure[0];
            float p1 = raw.WheelsPressure[1];
            if (p0 < 1f || p1 < 1f)
                return 1f;
            float avg = (p0 + p1) * 0.5f;
            if (float.IsNaN(avg) || float.IsInfinity(avg))
                return 1f;
            float delta;
            if (avg < 60f)
                delta = (30f - avg) / 15f;      // PSI
            else
                delta = (200f - avg) / 80f;     // kPa
            delta = Math.Clamp(delta, -0.4f, 0.6f);
            return 1f + PressureSensitivity * delta;
        }
        catch { return 1f; }
    }

    /// <summary>
    /// Called while driving (stationary blend = 0) to keep steer tracking fresh
    /// without accumulating displacement. Prevents a stale _prevSteerAngle spike
    /// when the car stops again after the wheel moved at speed.
    /// </summary>
    public void NotifyDriving(float steerAngle)
    {
        _prevSteerAngle = steerAngle;
        _steerInitialized = true;
        _displacement *= 0.9f;
        if (Math.Abs(_displacement) < 0.0001f)
            _displacement = 0f;
        _smoothedOutput = 0f;
        _scrubVel = 0f;
        LastComputedForce = 0f;
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
        _breakoutPhase = 0f;
        _smoothedSurface = 1f;
        _scrubCycle = 0f;
        _scrubPrev = 0f;
        _scrubNext = 0f;
        _scrubVel = 0f;
        _diagFrameCount = 0;
        _diagHeaderWritten = false;
    }
}
