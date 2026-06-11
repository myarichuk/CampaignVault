using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class EngagementRelationCatalogTests
{
    [Theory]
    [InlineData(EngagementCategory.Physical, null, EngagementRestrictionLevel.Hard, true, true)]
    [InlineData(EngagementCategory.Social, null, EngagementRestrictionLevel.Soft, false, true)]
    [InlineData(EngagementCategory.Attention, null, EngagementRestrictionLevel.None, false, false)]
    [InlineData(EngagementCategory.Social, EngagementRestrictionLevel.Hard, EngagementRestrictionLevel.Hard, true, true)]
    public void Catalog_UsesCategoryDefaults(
        EngagementCategory category,
        EngagementRestrictionLevel? overrideLevel,
        EngagementRestrictionLevel expectedLevel,
        bool blocksTravel,
        bool emitsPressure)
    {
        var relation = new EngagementRelation
        {
            TargetId = "characters/elara",
            Category = category,
            Verb = "doing something",
            RestrictionLevel = overrideLevel
        };

        Assert.Equal(expectedLevel, EngagementRelationCatalog.GetRestrictionLevel(relation));
        Assert.Equal(blocksTravel, EngagementRelationCatalog.BlocksTravel(relation));
        Assert.Equal(emitsPressure, EngagementRelationCatalog.EmitsPressure(relation));
    }

    [Fact]
    public void InferCategory_ReadsLegacyVerbs()
    {
        Assert.Equal(EngagementCategory.Physical, EngagementRelationCatalog.InferCategory("Grappling"));
        Assert.Equal(EngagementCategory.Social, EngagementRelationCatalog.InferCategory("Embracing"));
        Assert.Equal(EngagementCategory.Physical, EngagementRelationCatalog.InferCategory("shoving"));
    }

    [Fact]
    public void FormatDescription_UsesVerbPhrase()
    {
        var relation = new EngagementRelation
        {
            TargetId = "characters/elara",
            Category = EngagementCategory.Social,
            Verb = "ranting at"
        };

        var text = EngagementRelationCatalog.FormatDescription("Bram", relation);
        Assert.Contains("Bram", text);
        Assert.Contains("ranting at", text);
        Assert.Contains("characters/elara", text);
    }

    [Fact]
    public void CustomVerb_DoesNotRequireEnum()
    {
        var relation = new EngagementRelation
        {
            TargetId = "characters/pc",
            Category = EngagementCategory.Social,
            Verb = "buying a round for"
        };

        Assert.True(EngagementRelationCatalog.EmitsPressure(relation));
        Assert.False(EngagementRelationCatalog.BlocksTravel(relation));
    }
}