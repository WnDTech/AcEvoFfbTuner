namespace AcEvoFfbTuner.Core.FfbProcessing;

public sealed class FfbDamping
{
    public float SpeedDampingCoefficient { get; set; } = 0.3f;
    public float FrictionLevel { get; set; } = 0.25f;
    public float InertiaWeight { get; set; } = 0.1f;
    public float MaxSpeedReference { get; set; } = 300f;
    public float SteerVelocityReference { get; set; } = 10f;

    /// <summary>
    /// Minimum normalized steer velocity below which all damping forces are zeroed.
    /// Prevents residual damping from EMA tail when the wheel is stationary,
    /// which causes the "bounce back" / notchy feel at center.
    /// </summary>
    public float VelocityDeadzone { get; set; } = 0.02f;

    /// <summary>
    /// Low-speed damping multiplier. At 0 km/h, damping is this many times stronger
    /// to prevent the wheel from "hunting" left-to-right when stationary or creeping.
    /// </summary>
    public float LowSpeedDampingBoost { get; set; } = 3.0f;

    /// <summary>
    /// Speed (km/h) below which the low-speed damping boost starts fading in.
    /// </summary>
    public float LowSpeedThreshold { get; set; } = 20f;

    private float _previousSteerAngle;
    private float _steerVelocity;
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

        float normalizedSteerVel = Math.Clamp(_steerVelocity / SteerVelocityReference, -1f, 1f);
        float absSteerVel = Math.Abs(normalizedSteerVel);

        if (absSteerVel < VelocityDeadzone)
            return force;

        // ── Inverse speed factor: damping is STRONGEST at low speed ──
        // At 0 km/h: full boost. At LowSpeedThreshold+: normal damping.
        // This prevents "hunting" oscillation when the car is slow/stationary.
        float lowSpeedBlend = speedKmh < LowSpeedThreshold
            ? 1.0f - (speedKmh / LowSpeedThreshold)
            : 0f;
        float dampingMultiplier = 1.0f + lowSpeedBlend * (LowSpeedDampingBoost - 1.0f);

        float speedFactor = Math.Clamp(speedKmh / MaxSpeedReference, 0f, 1f);
        float absForce = Math.Abs(force);
        float forceRatio = Math.Clamp(absForce / 0.1f, 0f, 1f);

        // ── Velocity-based friction: resists steering MOVEMENT, not position ──
        // opposes the direction the wheel is physically turning
        float frictionForce = -FrictionLevel * normalizedSteerVel * dampingMultiplier * forceRatio;

        // ── Viscous damping: proportional to steering velocity × car speed ──
        // increases naturally as the car goes faster (more tire contact force)
        float dampingForce = -SpeedDampingCoefficient * normalizedSteerVel * speedFactor * dampingMultiplier;

        // ── Inertia: resists changes in steering direction (weight of the wheel) ──
        float inertiaForce = -InertiaWeight * normalizedSteerVel * speedFactor * forceRatio * dampingMultiplier;

        float targetDamping = dampingForce + frictionForce + inertiaForce;
        float maxDamp = absForce * 0.4f;
        _prevDampingForce = _prevDampingForce + (targetDamping - _prevDampingForce) * 0.4f;
        _prevDampingForce = Math.Clamp(_prevDampingForce, -maxDamp, maxDamp);

        return force + _prevDampingForce;
    }

    public void Reset()
    {
        _previousSteerAngle = 0f;
        _steerVelocity = 0f;
        _previousTimestamp = 0;
        _prevDampingForce = 0f;
    }
}
