using AcEvoFfbTuner.Core.FfbProcessing;
using FluentAssertions;

namespace AcEvoFfbTuner.Tests;

public class OpenCodeGoModelCatalogTests
{
    [Fact]
    public void Lookup_KnownModel_ReturnsPriceAndContext()
    {
        var info = OpenCodeGoModelCatalog.Lookup("deepseek-v4-flash");

        info.Should().NotBeNull();
        info!.InputPricePerMTok.Should().Be(0.22m);
        info.OutputPricePerMTok.Should().Be(0.66m);
        info.ContextWindow.Should().Be(1_000_000);
        info.HasPricing.Should().BeTrue();
    }

    [Fact]
    public void Lookup_UnknownModel_ReturnsNull()
    {
        OpenCodeGoModelCatalog.Lookup("some-brand-new-model").Should().BeNull();
    }

    [Fact]
    public void PriceLabel_FormatsLikeModelPicker()
    {
        var info = OpenCodeGoModelCatalog.Lookup("kimi-k3")!;

        info.PriceLabel.Should().Be("$3 in / $15 out per 1M tokens");
    }

    [Fact]
    public void PriceLabel_UnpricedModel_ShowsNotListed()
    {
        var info = new AiModelInfo("custom-id", "custom-id", null, null, null, false);

        info.PriceLabel.Should().Be("Price not listed");
        info.ContextLabel.Should().Be("");
        info.HasPricing.Should().BeFalse();
    }

    [Theory]
    [InlineData(1_000_000, "1M context")]
    [InlineData(1_048_576, "1M context")]
    [InlineData(262_144, "256K context")]
    [InlineData(204_800, "200K context")]
    [InlineData(500_000, "500K context")]
    [InlineData(512_000, "500K context")]
    [InlineData(256_000, "250K context")]
    public void ContextLabel_FormatsCompactly(long window, string expected)
    {
        new AiModelInfo("id", "Name", 1m, 2m, window, false).ContextLabel.Should().Be(expected);
    }

    [Fact]
    public void ToString_ReturnsId_ForEditableComboBoxText()
    {
        OpenCodeGoModelCatalog.Lookup("glm-5.3-flash")!.ToString().Should().Be("glm-5.3-flash");
    }

    [Fact]
    public void BuiltinModels_ContainsDefaultModel_WithUniqueIdsAndSaneValues()
    {
        var models = OpenCodeGoModelCatalog.BuiltinModels();

        models.Should().NotBeEmpty();
        models.Should().ContainSingle(m => m.Id == "deepseek-v4-flash");
        models.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        models.Should().OnlyContain(m =>
            !string.IsNullOrWhiteSpace(m.DisplayName)
            && m.InputPricePerMTok > 0
            && m.OutputPricePerMTok > 0
            && m.ContextWindow > 0);
    }
}
