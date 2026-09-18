namespace AcEvoFfbTuner.Core.FfbProcessing;

/// <summary>
/// A model available through the OpenCode Go AI gateway (or a compatible endpoint).
/// Prices are USD per 1M tokens. Used for binding in the AI Coach settings dropdown.
/// </summary>
public sealed record AiModelInfo(
    string Id,
    string DisplayName,
    decimal? InputPricePerMTok,
    decimal? OutputPricePerMTok,
    long? ContextWindow,
    bool IsLegacy)
{
    public bool HasPricing => InputPricePerMTok is not null && OutputPricePerMTok is not null;

    public string PriceLabel => HasPricing
        ? $"${InputPricePerMTok:0.###} in / ${OutputPricePerMTok:0.###} out per 1M tokens"
        : "Price not listed";

    public string ContextLabel => ContextWindow is { } ctx
        ? $"{FormatContext(ctx)} context"
        : "";

    public override string ToString() => Id;

    private static string FormatContext(long ctx)
    {
        if (ctx >= 1_000_000)
            return $"{ctx / 1_000_000.0:0.#}M";
        // Exact KiB multiples (262144, 204800, ...) read cleaner as 256K/200K than 262K/205K.
        if (ctx % 1024 == 0)
            return $"{ctx / 1024}K";
        return $"{ctx / 1000.0:0}K";
    }
}

/// <summary>
/// Price/context catalog for OpenCode Go gateway models, sourced from models.dev
/// ("opencode-go" provider, api https://opencode.ai/zen/go/v1). The live model list
/// is fetched from the gateway each time; this table layers prices and context sizes
/// onto the served IDs. Unknown IDs (e.g. brand-new models) stay selectable but show
/// "Price not listed".
/// </summary>
public static class OpenCodeGoModelCatalog
{
    private sealed record Spec(string Id, string DisplayName, decimal InputPerMTok, decimal OutputPerMTok, long ContextWindow, bool IsLegacy);

    // Order = relevance: current/recommended models first, legacy compatibility models last.
    private static readonly List<Spec> KnownModels =
    [
        new("qwen3.7-max", "Qwen3.7 Max", 2.5m, 7.5m, 1_000_000, false),
        new("longcat-2.0", "LongCat-2.0", 0.3m, 1.2m, 1_000_000, false),
        new("deepseek-v4-flash-vision-exp", "DeepSeek V4 Flash Vision Exp", 0.22m, 0.66m, 1_000_000, false),
        new("qwen3.6-plus", "Qwen3.6 Plus", 0.5m, 3m, 1_000_000, false),
        new("muse-spark-1.2-contributor", "Muse Spark 1.2 Contributor", 0.1m, 0.2m, 1_048_576, false),
        new("minimax-m2.7", "MiniMax-M2.7", 0.3m, 1.2m, 204_800, false),
        new("kimi-k2.6", "Kimi K2.6", 0.95m, 4m, 262_144, false),
        new("glm-5.2", "GLM-5.2", 1.4m, 4.4m, 1_000_000, false),
        new("minimax-m2.5", "MiniMax-M2.5", 0.3m, 1.2m, 204_800, true),
        new("minimax-m3", "MiniMax-M3", 0.3m, 1.2m, 1_000_000, false),
        new("deepseek-v4-flash", "DeepSeek V4 Flash", 0.22m, 0.66m, 1_000_000, false),
        new("kimi-k2.7-code", "Kimi K2.7 Code", 0.95m, 4m, 262_144, false),
        new("grok-4.5", "Grok 4.5", 2m, 6m, 500_000, false),
        new("hy3", "Hy3", 0.14m, 0.58m, 256_000, false),
        new("hy3-preview", "Hy3 Preview", 0.14m, 0.58m, 256_000, false),
        new("hy4-preview", "Hy4 Preview", 0.834m, 2.501m, 1_024_000, false),
        new("omen-alpha", "Omen Alpha", 0.2m, 0.66m, 500_000, false),
        new("gpt-5.6-luna", "GPT-5.6 Luna", 0.2m, 1.2m, 1_050_000, false),
        new("kimi-k3", "Kimi K3", 3m, 15m, 1_048_576, false),
        new("glm-5.3-flash", "GLM-5.3 Flash", 0.075m, 0.25m, 1_000_000, false),
        new("muse-spark-1.3-contributor", "Muse Spark 1.3 Contributor", 0.1m, 0.2m, 1_048_576, false),
        new("mimo-v2-pro", "MiMo V2 Pro", 1m, 3m, 1_048_576, true),
        new("qwen3.8-flash", "Qwen3.8 Flash", 0.15m, 0.47m, 1_000_000, false),
        new("grok-4.6", "Grok 4.6", 2m, 6m, 500_000, false),
        new("glm-5", "GLM-5", 1m, 3.2m, 202_752, true),
        new("mimo-v2-omni", "MiMo V2 Omni", 0.4m, 2m, 262_144, true),
        new("qwen3.8-max", "Qwen3.8 Max", 2m, 6m, 1_000_000, false),
        new("kimi-k2.5", "Kimi K2.5", 0.6m, 3m, 262_144, true),
        new("glm-5.1", "GLM-5.1", 1.4m, 4.4m, 202_752, true),
        new("qwen3.7-plus", "Qwen3.7 Plus", 0.4m, 1.6m, 1_000_000, false),
        new("deepseek-v4-pro", "DeepSeek V4 Pro", 0.66m, 1.98m, 1_000_000, false),
        new("glm-5.3", "GLM-5.3", 1.4m, 4.4m, 1_000_000, false),
        new("mimo-v2.5", "MiMo V2.5", 0.14m, 0.28m, 1_000_000, false),
        new("mimo-v2.5-pro", "MiMo V2.5 Pro", 0.435m, 0.87m, 1_048_576, false),
        new("qwen3.5-plus", "Qwen3.5 Plus", 0.2m, 1.2m, 262_144, true)
    ];

    /// <summary>
    /// Looks up price/context info for a model ID served by the gateway.
    /// Returns null for unknown (probably brand-new) model IDs.
    /// </summary>
    public static AiModelInfo? Lookup(string id)
    {
        var spec = KnownModels.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return spec is null ? null : spec.ToInfo();
    }

    /// <summary>
    /// The known catalog as model info, in relevance order. Used as the offline
    /// fallback when the gateway's live /models endpoint cannot be reached.
    /// </summary>
    public static IReadOnlyList<AiModelInfo> BuiltinModels() => KnownModels.Select(m => m.ToInfo()).ToList();

    private static AiModelInfo ToInfo(this Spec s) => new(s.Id, s.DisplayName, s.InputPerMTok, s.OutputPerMTok, s.ContextWindow, s.IsLegacy);
}