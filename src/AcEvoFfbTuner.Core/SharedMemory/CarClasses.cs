namespace AcEvoFfbTuner.Core.SharedMemory;

/// <summary>Cross-game car class families used to colour-code the Live Map.</summary>
public enum CarClassCategory
{
    Unknown = 0,
    Hypercar,   // LMH / LMDh / GTP / LMP1 / DPi / Group C — top prototypes
    Lmp2,
    Lmp3,
    Gte,        // GTE / GT2
    Gt3,        // GT3 / LMGT3 / GTD / GT Masters
    Gt4,
    Touring,    // DTM / TCR / WTCR / Group 2/5 / national touring
    Formula,    // open wheel
    Cup,        // one-make cups and trophies
    Other,
}

/// <summary>
/// Maps a game's class name to a <see cref="CarClassCategory"/>. Names differ per game —
/// LMU mVehicleClass ("Hyper", "LMP2", "LMP3", "GTE", "GT3"), RaceRoom r3e-data.json class
/// names ("Hypercars", "P1", "P2", "GTR 3", "GTR 4", "DTM 2025", "Porsche 992 GT3 Cup") —
/// so matching works on normalised whole tokens. One-make cups are checked before GT3 so
/// "Porsche 992 GT3 Cup" is a Cup, not GT3 grid.
/// </summary>
public static class CarClasses
{
    public static CarClassCategory Categorize(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return CarClassCategory.Unknown;
        var n = " " + TrackMapping.TrackNameMatcher.Normalize(className) + " ";
        bool Has(string token) => n.Contains(" " + token + " ", StringComparison.Ordinal);
        bool Starts(string prefix) => n.Contains(" " + prefix, StringComparison.Ordinal);

        if (Starts("hyper") || Has("lmh") || Has("lmdh") || Has("gtp") || Has("lmp1") || Has("p1")
            || Has("dpi") || Has("group c")) return CarClassCategory.Hypercar;
        if (Starts("lmp2") || Has("p2")) return CarClassCategory.Lmp2;
        if (Starts("lmp3") || Has("p3")) return CarClassCategory.Lmp3;
        if (Starts("lmgte") || Has("gte") || Has("gt2") || Has("gtr 2")) return CarClassCategory.Gte;
        if (Has("cup") || Has("trophy") || Has("challenge")) return CarClassCategory.Cup;
        if (Starts("lmgt3") || Has("gt3") || Has("gtr 3") || Has("gtd") || n.Contains(" gt masters", StringComparison.Ordinal))
            return CarClassCategory.Gt3;
        if (Has("gt4") || Has("gtr 4")) return CarClassCategory.Gt4;
        if (Has("dtm") || Has("tcr") || Has("wtcr") || Has("touring") || Has("btcc") || Has("stcc")
            || Has("group 2") || Has("group 5") || Has("nationals")) return CarClassCategory.Touring;
        if (Has("formula") || Has("f1") || Has("f2") || Has("f3") || Has("f4") || Has("fr2") || Has("fr3")
            || Has("frj") || Has("frx") || Starts("fr x") || Has("fr us") || Has("indy") || Has("open wheel"))
            return CarClassCategory.Formula;
        return CarClassCategory.Other;
    }

    /// <summary>Legend label for a category ("Other"/"Unknown" are named by the caller).</summary>
    public static string Label(CarClassCategory c) => c switch
    {
        CarClassCategory.Hypercar => "Hypercar",
        CarClassCategory.Lmp2 => "LMP2",
        CarClassCategory.Lmp3 => "LMP3",
        CarClassCategory.Gte => "GTE",
        CarClassCategory.Gt3 => "GT3",
        CarClassCategory.Gt4 => "GT4",
        CarClassCategory.Touring => "Touring",
        CarClassCategory.Formula => "Formula",
        CarClassCategory.Cup => "Cup",
        CarClassCategory.Other => "Other",
        _ => "Unclassified",
    };
}
