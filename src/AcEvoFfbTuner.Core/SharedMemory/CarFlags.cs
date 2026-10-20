using System.Text.RegularExpressions;

namespace AcEvoFfbTuner.Core.SharedMemory;

/// <summary>Flag state of one car, shown as the ring around its Live Map disc.</summary>
public enum CarFlag : byte
{
    None = 0,   // racing normally (green / no flag)
    Yellow,     // under a full-course or local yellow
    Blue,       // being shown the blue flag (about to be lapped)
    Penalty,    // outstanding penalty to serve (black flag)
    Checkered,  // finished
    Retired,    // DNF / DQ
}

/// <summary>
/// Per-game flag decoding. End states win over transient ones (a finished car is never
/// "yellow"), then safety (yellow) over blue over an outstanding penalty.
/// </summary>
public static partial class CarFlags
{
    /// <summary>
    /// LMU / rF2 VehicleScoringInfoV01: mFinishStatus (0 none, 1 finished, 2 DNF, 3 DQ),
    /// mFlag (0 green, 6 blue — the only values the game sends), mUnderYellow (took the
    /// full-course caution), mNumPenalties (outstanding). Local sector yellows are not used:
    /// ScoringInfoV01.mSectorFlag read (11, 11, 1) with no yellow out (2026-10-20) — encoding
    /// unconfirmed.
    /// </summary>
    public static CarFlag FromLmu(sbyte finishStatus, byte flag, bool underYellow, short penalties)
    {
        if (finishStatus == 1) return CarFlag.Checkered;
        if (finishStatus is 2 or 3) return CarFlag.Retired;
        if (underYellow) return CarFlag.Yellow;
        if (flag == 6) return CarFlag.Blue;
        if (penalties > 0) return CarFlag.Penalty;
        return CarFlag.None;
    }

    /// <summary>
    /// RaceRoom r3e_driver_data: finish_status (1 finished, 2 DNF, 3 DNQ, 4 DNS, 5 DQ), the
    /// driver's sector against flags.sector_yellow (documented: 1 = active), and any pending
    /// cut-track penalty. RaceRoom only reports blue flags for the player's own car.
    /// </summary>
    public static CarFlag FromR3e(int finishStatus, bool inYellowSector, bool hasPenalty)
    {
        if (finishStatus == 1) return CarFlag.Checkered;
        if (finishStatus is 2 or 5) return CarFlag.Retired;
        if (inYellowSector) return CarFlag.Yellow;
        if (hasPenalty) return CarFlag.Penalty;
        return CarFlag.None;
    }

    /// <summary>
    /// Race number from an LMU vehicle name: "Cadillac Hertz Team Jota 2026 #12:LM" → "12",
    /// "911GT3R Custom Team 2026 #397" → "397". Empty when the name carries no number.
    /// </summary>
    public static string LmuCarNumber(string vehicleName)
    {
        var m = CarNumberRegex().Match(vehicleName ?? "");
        return m.Success ? m.Groups[1].Value : "";
    }

    [GeneratedRegex(@"#\s*(\d{1,4})")]
    private static partial Regex CarNumberRegex();
}
