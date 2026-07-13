using System;
using System.Collections.Generic;
using System.Reflection;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Stage 2 regression tests: verifies YAML-backed pool loading and initializer behaviour.
/// </summary>
public class ResourcePoolStage2Tests
{
    private static readonly ResourcePoolProvider Provider = new(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cv_stage2_test_" + System.Guid.NewGuid()),
        typeof(ResourcePoolProvider).Assembly);

    // ── ResourcePoolTemplate.Merge ────────────────────────────────────────────

    [Fact]
    public void Merge_ChildKeyWins_ParentFillsGaps()
    {
        var parent = new ResourcePoolTemplate
        {
            Name = "parent",
            Recovery = RecoveryType.LongRest,
            DefaultMax = 4,
            LevelToMaxMap = new() { { "1", 2 }, { "2", 3 }, { "5", 4 } }
        };
        var child = new ResourcePoolTemplate
        {
            Name = "child",
            Inherits = ["parent"],
            LevelToMaxMap = new() { { "1", 1 } } // overrides level 1 only
        };

        var merged = ResourcePoolTemplate.Merge(child, parent);

        Assert.Equal(1, merged.LevelToMaxMap!["1"]);  // child wins
        Assert.Equal(3, merged.LevelToMaxMap["2"]);   // parent fills gap
        Assert.Equal(4, merged.LevelToMaxMap["5"]);   // parent fills gap
        Assert.Equal(RecoveryType.LongRest, merged.Recovery); // inherited from parent
        Assert.Equal(4, merged.DefaultMax);            // inherited from parent
    }

    [Fact]
    public void Merge_ChildScalarsWin_OverParent()
    {
        var parent = new ResourcePoolTemplate
        {
            Name = "parent",
            Recovery = RecoveryType.LongRest,
            DefaultMax = 4,
            Description = "parent desc"
        };
        var child = new ResourcePoolTemplate
        {
            Name = "child",
            Recovery = RecoveryType.ShortRest,
            DefaultMax = 2,
            Description = "child desc"
        };

        var merged = ResourcePoolTemplate.Merge(child, parent);

        Assert.Equal(RecoveryType.ShortRest, merged.Recovery);
        Assert.Equal(2, merged.DefaultMax);
        Assert.Equal("child desc", merged.Description);
    }

    [Fact]
    public void Merge_ParentLevelMapDeepCopied_NotShared()
    {
        var parent = new ResourcePoolTemplate
        {
            Name = "parent",
            LevelToMaxMap = new() { { "1", 10 }, { "2", 20 } }
        };
        var child = new ResourcePoolTemplate { Name = "child", Inherits = ["parent"] };

        var merged = ResourcePoolTemplate.Merge(child, parent);
        merged.LevelToMaxMap!["1"] = 999; // mutate the merged map

        // Original parent map should not be affected
        Assert.Equal(10, parent.LevelToMaxMap["1"]);
    }

    // ── ResourcePoolProvider — YAML loading ───────────────────────────────────

    [Fact]
    public void Provider_Dnd5e_LoadsExpectedPoolCount()
    {
        var actual = Provider.GetPoolsForSystem(RulesetSystem.Dnd5e);

        // 9 spell slot levels + 10 class-specific pools (including gold, pact_magic)
        Assert.Equal(19, actual.Count);
        Assert.True(actual.ContainsKey("spell_slots_1"));
        Assert.True(actual.ContainsKey("font_of_magic"));
    }

    [Fact]
    public void Provider_Pf2e_LoadsExpectedPoolCount()
    {
        var actual = Provider.GetPoolsForSystem(RulesetSystem.Pathfinder2e);

        // 10 spell slot ranks + focus_points + bon_mot + gold (no recall_knowledge — investigator not in ORC set)
        Assert.Equal(13, actual.Count);
        Assert.False(actual.ContainsKey("recall_knowledge"));
    }

    [Fact]
    public void Provider_Dnd5e_NoSystemCrossContamination()
    {
        var dnd5e = Provider.GetPoolsForSystem(RulesetSystem.Dnd5e);
        var pf2e = Provider.GetPoolsForSystem(RulesetSystem.Pathfinder2e);

        // Both have spell_slots_1 but with different DefaultMax (dnd5e=4, pf2e=1)
        Assert.True(dnd5e.ContainsKey("spell_slots_1"));
        Assert.True(pf2e.ContainsKey("spell_slots_1"));
        Assert.NotEqual(dnd5e["spell_slots_1"].DefaultMax, pf2e["spell_slots_1"].DefaultMax);
    }

    // ── ResourcePoolInitializer — YAML-backed pools ────────────────────────────

    [Theory]
    [InlineData(RulesetSystem.Dnd5e, 4, 3)]   // wizard 5: 4×1st, 3×2nd
    [InlineData(RulesetSystem.Pathfinder2e, 1, 1)]
    public void Initializer_YamlPools_WizardLevel5_CreatesExpectedSpellSlots(
        RulesetSystem system, int expectedSlots1, int expectedSlots2)
    {
        var character = new Character
        {
            Id = "chars/test-yaml",
            ClassLevel = "Wizard 5",
            SystemStats = system switch
            {
                RulesetSystem.Dnd5e => (SystemExtension)new Dnd5eExtension { Level = 5 },
                RulesetSystem.Pathfinder2e => new Pf2eExtension { Level = 5 },
                _ => throw new System.NotSupportedException()
            }
        };

        var sut = new ResourcePoolInitializer(Provider);
        sut.InitializePools(character, system, null);

        Assert.Equal(expectedSlots1, character.SystemStats.ResourcePools["spell_slots_1"].Max);
        Assert.Equal(expectedSlots2, character.SystemStats.ResourcePools["spell_slots_2"].Max);
    }

    [Fact]
    public void Initializer_WithoutProvider_Throws()
    {
        var sut = new ResourcePoolInitializer();
        var character = new Character
        {
            Id = "chars/no-provider",
            ClassLevel = "Wizard 1",
            SystemStats = new Dnd5eExtension { Level = 1 }
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            sut.InitializePools(character, RulesetSystem.Dnd5e, null));

        Assert.Contains("ResourcePoolProvider", ex.Message);
    }

}
