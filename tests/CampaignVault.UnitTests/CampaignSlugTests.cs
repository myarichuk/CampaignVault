using System;
using CampaignVault.Data;
using Xunit;

namespace CampaignVault.Tests;

public class CampaignSlugTests
{
    [Theory]
    [InlineData("Dragon Heist", "dragon-heist")]
    [InlineData("dragon_heist", "dragon-heist")]
    [InlineData("  SWORD-COAST ", "sword-coast")]
    [InlineData("curse/of/strahd", "curse-of-strahd")]
    [InlineData("double--hyphen", "double-hyphen")]
    public void Canonicalize_NormalizesConsistently(string input, string expected)
    {
        Assert.Equal(expected, CampaignSlug.Canonicalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Canonicalize_RejectsEmpty(string? input)
    {
        Assert.Throws<ArgumentException>(() => CampaignSlug.Canonicalize(input!));
    }

    [Fact]
    public void TryCanonicalize_ReturnsFalseForMissingInput()
    {
        Assert.False(CampaignSlug.TryCanonicalize(null, out var slug));
        Assert.Equal(string.Empty, slug);
    }
}
