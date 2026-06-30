using System.Collections.Generic;
using System.Reflection;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Stage 2 regression tests: verifies that YAML-backed pool loading produces
/// the same results as the C# ResourcePoolDefaults oracle.
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
#pragma warning disable CS0618
        var expected = ResourcePoolDefaults.Dnd5e;
#pragma warning restore CS0618
        var actual = Provider.GetPoolsForSystem(RulesetSystem.Dnd5e);

        Assert.Equal(expected.Count, actual.Count);
    }

    [Fact]
    public void Provider_Pf2e_LoadsExpectedPoolCount()
    {
#pragma warning disable CS0618
        var expected = ResourcePoolDefaults.Pf2e;
#pragma warning restore CS0618
        var actual = Provider.GetPoolsForSystem(RulesetSystem.Pathfinder2e);

        Assert.Equal(expected.Count, actual.Count);
    }

    [Fact]
    public void Provider_Fallout2d20_LoadsActionPoints()
    {
        var pools = Provider.GetPoolsForSystem(RulesetSystem.Fallout2d20);

        Assert.True(pools.ContainsKey("action_points"));
        Assert.Equal(10, pools["action_points"].DefaultMax);
        Assert.Equal(RecoveryType.PerTurn, pools["action_points"].Recovery);
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

    // ── ResourcePoolInitializer — YAML parity with defaults ──────────────────

    [Theory]
    [InlineData(RulesetSystem.Dnd5e)]
    [InlineData(RulesetSystem.Pathfinder2e)]
    [InlineData(RulesetSystem.Fallout2d20)]
    public void Initializer_YamlPools_SameResultAsDefaults(RulesetSystem system)
    {
        var wizardClass = system switch
        {
            RulesetSystem.Dnd5e => "Wizard 5",
            RulesetSystem.Pathfinder2e => "Wizard 5",
            _ => "Survivor 5"
        };
        var extension = system switch
        {
            RulesetSystem.Dnd5e => (SystemExtension)new Dnd5eExtension { Level = 5 },
            RulesetSystem.Pathfinder2e => new Pf2eExtension { Level = 5 },
            _ => new Fallout2d20Extension { Level = 5 }
        };

        var charDefault = new Character
        {
            Id = "chars/test-default",
            ClassLevel = wizardClass,
            SystemStats = extension
        };
        var charYaml = new Character
        {
            Id = "chars/test-yaml",
            ClassLevel = wizardClass,
            SystemStats = CloneExtension(extension)
        };

        var sutDefault = new ResourcePoolInitializer();       // uses ResourcePoolDefaults
        var sutYaml = new ResourcePoolInitializer(Provider);  // uses YAML

        sutDefault.InitializePools(charDefault, system, null);
        sutYaml.InitializePools(charYaml, system, null);

        Assert.Equal(charDefault.SystemStats.ResourcePools.Count,
            charYaml.SystemStats.ResourcePools.Count);

        foreach (var (poolName, defaultPool) in charDefault.SystemStats.ResourcePools)
        {
            Assert.True(charYaml.SystemStats.ResourcePools.TryGetValue(poolName, out var yamlPool),
                $"Pool '{poolName}' missing from YAML-loaded result");
            Assert.Equal(defaultPool.Max, yamlPool!.Max);
            Assert.Equal(defaultPool.Recovery, yamlPool.Recovery);
        }
    }

    private static SystemExtension CloneExtension(SystemExtension ext) => ext switch
    {
        Dnd5eExtension d => new Dnd5eExtension { Level = d.Level, ClassLevels = d.ClassLevels },
        Pf2eExtension p => new Pf2eExtension { Level = p.Level },
        Fallout2d20Extension f => new Fallout2d20Extension { Level = f.Level },
        _ => throw new System.NotSupportedException()
    };
}
