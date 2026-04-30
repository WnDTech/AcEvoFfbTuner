namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbDamping
{
    public float SpeedDampingCoefficient { get; set; } = 0.3f;
    public float FrictionLevel { get; set; } = 0.25f;
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

    // Kept as no-ops for profile/UI compatibility. No longer used in Apply().
    public float LowSpeedDampingBoost { get; set; } = 3.0f;
    public float LowSpeedThreshold { get; set; } = 20f;

    private float _previousSteerAngle;
    private float _steerVelocity;
    private float _previousSteerVelocity;
    private bool _steerVelocityInitialized;
    private long _previousTimestamp;
    private float _prevDampingForce;

    public float Apply(float force, float speedKmh, float steerAngle)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float dt = _previousTimestamp > 0 ? (now - _previousTimestamp) / 1000f : 0.003f;
        dt = Math.Clamp(dt, 0.001f, 0.05f);

        // Calculate actual steering velocity (radians/second)
        float rawSteerVel = (steerAngle - _previousSteerAngle) / dt;
        _steerVelocity = _steerVelocity * 0.6f + rawSteerVel * 0.4f;
        _previousSteerAngle = steerAngle;
        _previousTimestamp = now;

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

        // ── Coulomb friction: constant magnitude, opposes motion direction ──
        // Real steering rack friction is approximately constant (dry/Coulomb friction).
        // It doesn't scale with velocity — it's always the same force opposing motion.
        float frictionForce = -Math.Sign(normalizedSteerVel) * FrictionLevel;

        // ── Viscous damping: proportional to steering velocity × car speed ──
        // Represents tire contact patch drag that increases with vehicle speed.
        float dampingForce = -SpeedDampingCoefficient * normalizedSteerVel * speedFactor;

        // ── Inertia: proportional to angular ACCELERATION × car speed ──
        // F = -I × α (moment of inertia × angular acceleration).
        // Resists changes in steering direction — feels like the weight of the wheel.
        float normalizedAccel = Math.Clamp(steerAccel / SteerAccelReference, -1f, 1f);
        float inertiaForce = -InertiaWeight * normalizedAccel * speedFactor;

        float targetDamping = dampingForce + frictionForce + inertiaForce;
        float maxDamp = absForce * 0.2f;
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