namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbDamping
{
    /// <summary>
    /// Pure viscous damping coefficient. Always active regardless of vehicle speed.
    /// Represents steering column friction / hydraulic fluid resistance.
    /// This is the PRIMARY defense against DD wheel oscillation.
    /// </summary>
    public float ViscousCoefficient { get; set; } = 0.15f;

    /// <summary>
    /// Gyroscopic (speed-sensitive) damping coefficient.
    /// Represents tire contact patch drag that increases with vehicle speed.
    /// Scaled by speedFactor = speedKmh / MaxSpeedReference.
    /// </summary>
    public float SpeedDampingCoefficient { get; set; } = 0.3f;

    /// <summary>
    /// Coulomb (dry) friction level. Constant magnitude opposing motion direction.
    /// Represents steering rack dry friction. Always active.
    /// Uses smooth tanh transition to avoid hard sign flip at zero velocity.
    /// </summary>
    public float FrictionLevel { get; set; } = 0.25f;

    /// <summary>
    /// Steering inertia weight. Scaled by speed (gyroscopic effect of rotating tires).
    /// Resists angular acceleration — feels like the weight of the wheel assembly.
    /// </summary>
    public float InertiaWeight { get; set; } = 0.1f;

    public float MaxSpeedReference { get; set; } = 300f;
    public float SteerVelocityReference { get; set; } = 10f;

    /// <summary>
    /// Reference angular acceleration for normalizing steer acceleration.
    /// Typical steering acceleration: ~20 rad/s² for aggressive maneuvers.
    /// </summary>
    public float SteerAccelReference { get; set; } = 20f;

    /// <summary>
    /// Minimum normalized steer velocity below which all damping forces are zeroed.
    /// Prevents residual damping from EMA tail when the wheel is stationary.
    /// </summary>
    public float VelocityDeadzone { get; set; } = 0.02f;

    // Minimum damping floors: prevent completely undamped wheel regardless of profile settings.
    // These represent the irreducible physical friction of a real steering column.
    private const float MinViscousCoefficient = 0.04f;
    private const float MinFrictionLevel = 0.02f;

    // No-ops kept for profile JSON backward compat.
    public float LowSpeedDampingBoost { get; set; } = 1.0f;
    public float LowSpeedThreshold { get; set; } = 20f;

    /// <summary>
    /// Absolute floor for the damping authority cap. The proportional cap
    /// (|force|*0.25) goes to zero at center where oscillation starts, so an
    /// absolute floor restores stability on-center. Default 0 = legacy
    /// behavior (EVO unchanged). LMU sets 0.03 in its constructor.
    /// </summary>
    public float CenterDampFloor { get; set; } = 0f;

    /// <summary>
    /// Straight-line stability boost. When speed exceeds StraightSpeedKmh and
    /// |steer| is below StraightSteerNorm, viscous+gyro damping scale by
    /// (1+StraightGain). Default 0 = disabled (zero-impact for all games).
    /// </summary>
    public float StraightGain { get; set; } = 0f;
    public float StraightSpeedKmh { get; set; } = 80f;
    public float StraightSteerNorm { get; set; } = 0.02f;

    private float _previousSteerAngle;
    private float _steerVelocity;
    private float _previousSteerVelocity;
    private bool _steerVelocityInitialized;
    private long _previousTimestamp;
    private float _prevDampingForce;

    public float Apply(float force, float speedKmh, float steerAngle)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float wallDt = _previousTimestamp > 0 ? (now - _previousTimestamp) / 1000f : 0.003f;
        wallDt = Math.Clamp(wallDt, 0.001f, 0.05f);
        float out_ = Apply(force, speedKmh, steerAngle, wallDt);
        _previousTimestamp = now;
        return out_;
    }

    /// <summary>
    /// Deterministic-dt overload for pipelines with a known physics rate
    /// (e.g. LMU 1/250). Wall-clock overload above is preserved for EVO/R3E.
    /// </summary>
    public float Apply(float force, float speedKmh, float steerAngle, float dt)
    {
        dt = Math.Clamp(dt, 0.001f, 0.05f);

        // Calculate actual steering velocity (radians/second).
        // NOTE: deterministic overload does NOT touch _previousTimestamp;
        // the wall-clock overload owns it. This keeps LMU (fixed dt) and
        // EVO (wall dt) from corrupting each other's timing.
        float rawSteerVel = (steerAngle - _previousSteerAngle) / dt;
        _steerVelocity = _steerVelocity * 0.6f + rawSteerVel * 0.4f;
        _previousSteerAngle = steerAngle;

        // Calculate steering acceleration (radians/second²)
        float steerAccel = 0f;
        if (_steerVelocityInitialized)
        {
            steerAccel = (_steerVelocity - _previousSteerVelocity) / dt;
        }
        _previousSteerVelocity = _steerVelocity;
        _steerVelocityInitialized = true;

        float normalizedSteerVel = Math.Clamp(_steerVelocity / SteerVelocityReference, -1f, 1f);
        float absSteerVel = Math.Abs(normalizedSteerVel);

        if (absSteerVel < VelocityDeadzone)
            return force;

        float speedFactor = Math.Clamp(speedKmh / MaxSpeedReference, 0f, 1f);
        float absForce = Math.Abs(force);

        // ── Pure viscous damping: proportional to steering velocity ──
        // ALWAYS ACTIVE — represents steering column friction (thick hydraulic fluid,
        // heavy grease in the column). This is the PRIMARY oscillation defense.
        // Real steering rack friction exists regardless of how fast the car is driving.
        // Floor ensures minimum damping even when profile sets coefficient to 0.
        float effectiveViscous = Math.Max(ViscousCoefficient, MinViscousCoefficient);
        float pureViscousForce = -normalizedSteerVel * effectiveViscous;

        // ── Coulomb friction: constant magnitude, opposes motion direction ──
        // Real steering rack friction is approximately constant (dry/Coulomb friction).
        // Smooth tanh transition avoids hard sign flip at zero velocity.
        // ALWAYS ACTIVE — independent of vehicle speed.
        // Floor prevents completely frictionless center feel.
        float effectiveFriction = Math.Max(FrictionLevel, MinFrictionLevel);
        float frictionForce = -MathF.Tanh(normalizedSteerVel * 10f) * effectiveFriction;

        // ── Gyroscopic damping: proportional to velocity × car speed ──
        // Represents tire contact patch drag that increases with vehicle speed.
        // As speed increases, the tires resist rapid changes in yaw.
        float gyroscopicForce = -SpeedDampingCoefficient * normalizedSteerVel * speedFactor;

        // ── Inertia: proportional to angular ACCELERATION × car speed ──
        // F = -I × α (moment of inertia × angular acceleration).
        // Speed-scaled because tire rotational inertia scales with vehicle speed.
        float normalizedAccel = Math.Clamp(steerAccel / SteerAccelReference, -1f, 1f);
        float inertiaForce = -InertiaWeight * normalizedAccel * speedFactor;

        // Straight-line boost (opt-in): extra viscous+gyro authority at speed
        // near center. Default StraightGain=0 = legacy behavior.
        if (StraightGain > 0.001f && speedKmh >= StraightSpeedKmh
            && Math.Abs(steerAngle) <= StraightSteerNorm)
        {
            float boost = 1f + StraightGain;
            pureViscousForce *= boost;
            gyroscopicForce *= boost;
        }

        float targetDamping = pureViscousForce + frictionForce + gyroscopicForce + inertiaForce;
        // Absolute floor restores on-center authority where |force|*0.25 → 0.
        // Default CenterDampFloor=0 preserves legacy behavior; LMU opts into 0.03.
        float maxDamp = Math.Max(absForce * 0.25f, CenterDampFloor);
        _prevDampingForce = _prevDampingForce + (targetDamping - _prevDampingForce) * 0.4f;
        _prevDampingForce = Math.Clamp(_prevDampingForce, -maxDamp, maxDamp);

        return force + _prevDampingForce;
    }

    public void Reset()
    {
        _previousSteerAngle = 0f;
        _steerVelocity = 0f;
        _previousSteerVelocity = 0f;
        _steerVelocityInitialized = false;
        _previousTimestamp = 0;
        _prevDampingForce = 0f;
    }
}
