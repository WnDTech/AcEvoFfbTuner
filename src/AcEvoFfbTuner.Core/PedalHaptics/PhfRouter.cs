using AcEvoFfbTuner.Core.Config;

namespace AcEvoFfbTuner.Core.PedalHaptics;

/// <summary>
/// Routes PHF effects to pedals by each effect's user-selected route mask
/// (1 = throttle, 2 = brake, 4 = clutch). Sums the effects on each pedal and
/// picks that pedal's vibration frequency from its strongest effect.
/// </summary>
public static class PhfRouter
{
    /// <param name="Key">Effect key (<see cref="PhfEffectKeys"/>).</param>
    /// <param name="Value">Strength contribution (0..~255 scale).</param>
    /// <param name="Hz">Frequency for this effect.</param>
    /// <param name="Left">Kerbs only: left-side value (else null).</param>
    /// <param name="Right">Kerbs only: right-side value (else null).</param>
    /// <param name="ClutchValue">Value to use when routed to the clutch
    /// (e.g. engine scaled by clutch position); null = <paramref name="Value"/>.</param>
    public readonly record struct Effect(string Key, double Value, int Hz,
        double? Left = null, double? Right = null, double? ClutchValue = null);

    public sealed class Mix
    {
        public double Brake, Throttle, Clutch;
        public int BrakeHz, ThrottleHz, ClutchHz;
    }

    public const int Throttle = 0b001, Brake = 0b010, Clutch = 0b100;

    public static Mix Route(IEnumerable<Effect> effects, Func<string, int> routeOf, int idleHz)
    {
        var mix = new Mix { BrakeHz = idleHz, ThrottleHz = idleHz, ClutchHz = idleHz };
        double bTop = 0, tTop = 0, cTop = 0;

        foreach (var e in effects)
        {
            int mask = routeOf(e.Key);
            if (mask == 0) continue;

            double brakeV, throttleV, clutchV = e.ClutchValue ?? e.Value;
            if (e.Left.HasValue || e.Right.HasValue)
            {
                double l = e.Left ?? 0, r = e.Right ?? 0, any = Math.Max(l, r);
                bool split = (mask & Brake) != 0 && (mask & Throttle) != 0;
                // Both pedals selected: left side → brake, right side → throttle.
                // Single pedal: it feels a kerb on either side.
                brakeV = split ? l : any;
                throttleV = split ? r : any;
                clutchV = e.ClutchValue ?? any;
            }
            else
            {
                brakeV = throttleV = e.Value;
            }

            if ((mask & Brake) != 0 && brakeV > 0)
            {
                mix.Brake += brakeV;
                if (brakeV > bTop) { bTop = brakeV; mix.BrakeHz = e.Hz; }
            }
            if ((mask & Throttle) != 0 && throttleV > 0)
            {
                mix.Throttle += throttleV;
                if (throttleV > tTop) { tTop = throttleV; mix.ThrottleHz = e.Hz; }
            }
            if ((mask & Clutch) != 0 && clutchV > 0)
            {
                mix.Clutch += clutchV;
                if (clutchV > cTop) { cTop = clutchV; mix.ClutchHz = e.Hz; }
            }
        }
        return mix;
    }
}
