namespace AcEvoFfbTuner.Core.FfbProcessing;

public enum TyreCompoundCategory
{
    Unknown = 0,
    DrySlickHard,
    DrySlickMedium,
    DrySlickSoft,
    Intermediate,
    Wet,
    Snow,
    OffRoad
}

public static class TyreCompoundClassifier
{
    public static TyreCompoundCategory Classify(string? compound)
    {
        if (string.IsNullOrWhiteSpace(compound))
            return TyreCompoundCategory.Unknown;

        string c = ExtractCleanName(compound);

        if (c.Contains("Wet", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.Wet;
        if (c.Contains("Rain", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.Wet;
        if (c.Contains("Intermediate", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.Intermediate;
        if (c.Contains("Int", StringComparison.OrdinalIgnoreCase) && !c.Contains("Internal", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.Intermediate;
        if (c.Contains("Snow", StringComparison.OrdinalIgnoreCase) || c.Contains("Winter", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.Snow;
        if (c.Contains("OffRoad", StringComparison.OrdinalIgnoreCase) || c.Contains("Gravel", StringComparison.OrdinalIgnoreCase) || c.Contains("Dirt", StringComparison.OrdinalIgnoreCase))
            return TyreCompoundCategory.OffRoad;

        if (c.Contains("Slick", StringComparison.OrdinalIgnoreCase))
        {
            if (c.Contains("(H)", StringComparison.OrdinalIgnoreCase) || c.Contains("Hard", StringComparison.OrdinalIgnoreCase))
                return TyreCompoundCategory.DrySlickHard;
            if (c.Contains("(M)", StringComparison.OrdinalIgnoreCase) || c.Contains("Medium", StringComparison.OrdinalIgnoreCase))
                return TyreCompoundCategory.DrySlickMedium;
            if (c.Contains("(S)", StringComparison.OrdinalIgnoreCase) || c.Contains("Soft", StringComparison.OrdinalIgnoreCase))
                return TyreCompoundCategory.DrySlickSoft;
            return TyreCompoundCategory.DrySlickMedium;
        }

        if (c.Contains("Dry", StringComparison.OrdinalIgnoreCase))
        {
            if (c.Contains("Hard", StringComparison.OrdinalIgnoreCase))
                return TyreCompoundCategory.DrySlickHard;
            if (c.Contains("Soft", StringComparison.OrdinalIgnoreCase))
                return TyreCompoundCategory.DrySlickSoft;
            return TyreCompoundCategory.DrySlickMedium;
        }

        return TyreCompoundCategory.Unknown;
    }

    public static bool IsWetOrIntermediate(TyreCompoundCategory cat)
        => cat == TyreCompoundCategory.Wet || cat == TyreCompoundCategory.Intermediate;

    public static bool IsDrySlick(TyreCompoundCategory cat)
        => cat is TyreCompoundCategory.DrySlickHard or TyreCompoundCategory.DrySlickMedium or TyreCompoundCategory.DrySlickSoft;

    private static string ExtractCleanName(string raw)
    {
        int lastPrintable = -1;
        for (int i = raw.Length - 1; i >= 0; i--)
        {
            char ch = raw[i];
            if (ch >= ' ' && ch < 127)
            {
                lastPrintable = i;
                break;
            }
        }
        if (lastPrintable < 0) return "";

        int firstAlpha = 0;
        for (int i = 0; i <= lastPrintable; i++)
        {
            char ch = raw[i];
            if (ch >= ' ' && ch < 127)
            {
                firstAlpha = i;
                break;
            }
        }

        string cleaned = raw.Substring(firstAlpha, lastPrintable - firstAlpha + 1).Trim();

        int parenIdx = cleaned.IndexOf('(');
        if (parenIdx > 0 && cleaned.IndexOf(')', parenIdx) > parenIdx)
        {
            string beforeParen = cleaned[..parenIdx].Trim();
            if (beforeParen.Length > 0)
                return cleaned;
        }

        return cleaned;
    }
}
