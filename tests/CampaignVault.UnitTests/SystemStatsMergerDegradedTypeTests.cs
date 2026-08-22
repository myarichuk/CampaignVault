using CampaignVault.Models;
using CampaignVault.Rulesets;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// SystemStatsMerger.Merge used to deserialize its result into target.GetType() — if `target` was
/// (or had degraded to) the base SystemExtension type, that silently discarded every ruleset-specific
/// field (ArmorClass, ability scores, hitDie, skillModifiers, ...) that had just been correctly
/// merged into the loose JSON, and the next merge would repeat the loss forever, since the output
/// fed back in as the next call's target. These tests pin the fixed behavior: the merge result's type
/// now follows `activeSystem`, so a single correct character_update self-heals a previously-degraded
/// character rather than perpetuating the degradation.
/// </summary>
public class SystemStatsMergerDegradedTypeTests
{
    [Fact]
    public void Merge_UpgradesDegradedBaseTarget_ToDnd5eExtension_WhenSourceCarriesDnd5eStats()
    {
        // Simulates a character loaded from a still-corrupted document: SystemStats collapsed to the
        // base type, with none of the dnd5e-specific fields the commit is trying to (re)establish.
        var degradedTarget = new SystemExtension();
        var source = new Dnd5eExtension { ArmorClass = 15, Dexterity = 14, HitDie = "d10", Level = 3 };

        var result = SystemStatsMerger.Merge(degradedTarget, source, RulesetSystem.Dnd5e);

        var dnd5e = Assert.IsType<Dnd5eExtension>(result);
        Assert.Equal(15, dnd5e.ArmorClass);
        Assert.Equal(14, dnd5e.Dexterity);
        Assert.Equal("d10", dnd5e.HitDie);
        Assert.Equal(3, dnd5e.Level);
    }

    [Fact]
    public void Merge_UpgradesDegradedBaseTarget_ToPf2eExtension_WhenSourceCarriesPf2eStats()
    {
        var degradedTarget = new SystemExtension();
        var source = new Pf2eExtension { ArmorClass = 18, DexterityMod = 3, ClassHpPerLevel = 10, Level = 5 };

        var result = SystemStatsMerger.Merge(degradedTarget, source, RulesetSystem.Pathfinder2e);

        var pf2e = Assert.IsType<Pf2eExtension>(result);
        Assert.Equal(18, pf2e.ArmorClass);
        Assert.Equal(3, pf2e.DexterityMod);
        Assert.Equal(10, pf2e.ClassHpPerLevel);
        Assert.Equal(5, pf2e.Level);
    }

    [Fact]
    public void Merge_PreservesAlreadyCorrectType_WhenTargetIsAlreadyDnd5eExtension()
    {
        var target = new Dnd5eExtension { ArmorClass = 12, Constitution = 14 };
        var source = new Dnd5eExtension { HitDie = "d8", Level = 2 };

        var result = SystemStatsMerger.Merge(target, source, RulesetSystem.Dnd5e);

        var dnd5e = Assert.IsType<Dnd5eExtension>(result);
        Assert.Equal(12, dnd5e.ArmorClass);
        Assert.Equal(14, dnd5e.Constitution);
        Assert.Equal("d8", dnd5e.HitDie);
        Assert.Equal(2, dnd5e.Level);
    }
}
