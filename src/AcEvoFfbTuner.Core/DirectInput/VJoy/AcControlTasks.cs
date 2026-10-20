namespace AcEvoFfbTuner.Core.DirectInput.VJoy;

/// <summary>
/// Racing tasks assignable to wheel buttons through the vJoy bridge.
/// Each task names its controls.ini section; the binding writer points that
/// section at the vJoy device + slot. Curated to what drivers actually press:
/// paddles, pits, lights, wipers, camera, indicators, look-back, horn, DRS.
/// </summary>
public sealed class AcControlTask
{
    public string DisplayName { get; init; } = "";
    public string Section { get; init; } = "";

    public static readonly IReadOnlyList<AcControlTask> All = new List<AcControlTask>
    {
        new() { DisplayName = "Gear up", Section = "GEARUP" },
        new() { DisplayName = "Gear down", Section = "GEARDN" },
        new() { DisplayName = "Pit limiter", Section = "__EXT_PIT_LIMITER" },
        new() { DisplayName = "Headlights", Section = "ACTION_HEADLIGHTS" },
        new() { DisplayName = "Change camera", Section = "ACTION_CHANGE_CAMERA" },
        new() { DisplayName = "Look back", Section = "GLANCEBACK" },
        new() { DisplayName = "Indicators left", Section = "__EXT_TURNSIGNAL_LEFT" },
        new() { DisplayName = "Indicators right", Section = "__EXT_TURNSIGNAL_RIGHT" },
        new() { DisplayName = "Hazards", Section = "__EXT_HAZARDS" },
        new() { DisplayName = "Wipers", Section = "__EXT_WIPERS_1" },
        new() { DisplayName = "DRS", Section = "DRS" },
        new() { DisplayName = "KERS", Section = "KERS" },
        new() { DisplayName = "Horn", Section = "ACTION_HORN" },
        new() { DisplayName = "Brake bias +", Section = "BALANCEUP" },
        new() { DisplayName = "Brake bias −", Section = "BALANCEDN" },
        new() { DisplayName = "TC +", Section = "TCUP" },
        new() { DisplayName = "TC −", Section = "TCDN" },
        new() { DisplayName = "ABS +", Section = "ABSUP" },
        new() { DisplayName = "ABS −", Section = "ABSDN" },
        new() { DisplayName = "Engine brake +", Section = "ENGINE_BRAKE_UP" },
        new() { DisplayName = "Engine brake −", Section = "ENGINE_BRAKE_DN" },
        new() { DisplayName = "Turbo +", Section = "TURBOUP" },
        new() { DisplayName = "Turbo −", Section = "TURBODN" },
        new() { DisplayName = "MGU-K delivery +", Section = "MGUK_DELIVERY_UP" },
        new() { DisplayName = "MGU-K recovery +", Section = "MGUK_RECOVERY_UP" },
        new() { DisplayName = "Engine start", Section = "STARTER" },
        new() { DisplayName = "Look left", Section = "GLANCELEFT" },
        new() { DisplayName = "Look right", Section = "GLANCERIGHT" },
        new() { DisplayName = "Headlight flash", Section = "ACTION_HEADLIGHTS_FLASH" },
        new() { DisplayName = "Wipers faster", Section = "__EXT_WIPERS_MORE" },
        new() { DisplayName = "Wipers off", Section = "__EXT_WIPERS_OFF" },
    };
}
