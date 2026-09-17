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
    }

    [Theory]
    [InlineData("restrains", EngagementCategory.Physical)]
    [InlineData("restrained", EngagementCategory.Physical)]
    [InlineData("dragged", EngagementCategory.Physical)]
    [InlineData("drags", EngagementCategory.Physical)]
    [InlineData("drag", EngagementCategory.Physical)]
    [InlineData("grapples", EngagementCategory.Physical)]
    [InlineData("grappled", EngagementCategory.Physical)]
    [InlineData("grapple", EngagementCategory.Physical)]
    [InlineData("embraces", EngagementCategory.Social)]
    [InlineData("embraced", EngagementCategory.Social)]
    [InlineData("embrace", EngagementCategory.Social)]
    [InlineData("treats", EngagementCategory.Medical)]
    [InlineData("treated", EngagementCategory.Medical)]
    [InlineData("watches", EngagementCategory.Attention)]
    [InlineData("watched", EngagementCategory.Attention)]
    public void InferCategory_MatchesRegularInflectionsOfLegacyVerbs(string verb, EngagementCategory expected)
    {
        Assert.Equal(expected, EngagementRelationCatalog.InferCategory(verb));
    }

    [Fact]
    public void InferCategory_UnmappedVerb_DefaultsToSocialNotPhysical()
    {
        // "shoving" isn't in the legacy verb table. The safe default for an unrecognized verb is
        // Social/Soft (visible via EmitsPressure, but never blocks travel or auto-logs as a
        // Physical/Medical history entry) — not Physical/Hard, which would silently gate party
        // movement on a category nobody actually asserted.
        Assert.Equal(EngagementCategory.Social, EngagementRelationCatalog.InferCategory("shoving"));
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
