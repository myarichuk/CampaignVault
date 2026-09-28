using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CampaignVault.Data.Templates;
using CampaignVault.Models;
using CampaignVault.Services;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Phase 1 (summoning): SpellDefinition.Summon shape — YAML deserialization via the
/// embedded spell corpus, Merge inheritance, and handbook creature-link integrity.
/// </summary>
public class SummonEffectTests
{
    private static readonly SpellDefinitionProvider Spells = new(
        Path.Combine(Path.GetTempPath(), "cv_summon_test_" + Guid.NewGuid()),
        typeof(SpellDefinitionProvider).Assembly);

    private static readonly CreatureDefinitionProvider Creatures = new(
        Path.Combine(Path.GetTempPath(), "cv_summon_creature_test_" + Guid.NewGuid()),
        typeof(CreatureDefinitionProvider).Assembly);

    [Fact]
    public void Summon_AnimateDead_HasSummonEffect()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("animate_dead", out var animateDead));
        Assert.NotNull(animateDead.Summon);
        var summon = animateDead.Summon!;
        Assert.Equal(["skeleton", "zombie"], summon.Creatures);
        Assert.Null(summon.InlineSeed);
        Assert.Equal(1, summon.CountAtSlotLevel![3]);
        Assert.Equal(3, summon.CountAtSlotLevel[4]);
        Assert.Equal(13, summon.CountAtSlotLevel[9]);
        Assert.Equal(4, summon.RetainCountAtSlotLevel![3]);
        Assert.Equal(16, summon.RetainCountAtSlotLevel[9]);
        Assert.Equal(1, summon.DurationDays);
        Assert.Null(summon.DurationRounds);
        Assert.Null(summon.ControlCap);
        Assert.Equal(SummonDisposition.Loyal, summon.Disposition);
    }

    [Fact]
    public void Summon_AnimateDead_CreaturesResolveInCatalog()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("animate_dead", out var animateDead));
        foreach (var creature in animateDead.Summon!.Creatures)
        {
            Assert.True(
                Creatures.TryGet(RulesetSystem.Dnd5e, creature, out _),
                $"Summon creature ref '{creature}' resolves no CreatureDefinition.");
        }
    }

    [Fact]
    public void Summon_NonSummonSpells_HaveNullSummon()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("fireball", out var fireball));
        Assert.Null(fireball.Summon);
        Assert.True(spells.TryGetValue("magic_missile", out var missile));
        Assert.Null(missile.Summon);
    }

    [Fact]
    public void Merge_ChildSummonWins_OverParent()
    {
        var parent = new SpellDefinition
        {
            Name = "base",
            Summon = new SummonEffect
            {
                Creatures = ["skeleton"],
                Disposition = SummonDisposition.Loyal,
            },
        };
        var child = new SpellDefinition
        {
            Name = "sub",
            Inherits = ["base"],
            Summon = new SummonEffect
            {
                Creatures = ["zombie"],
                Disposition = SummonDisposition.Hostile,
            },
        };

        var merged = SpellDefinition.Merge(child, parent);

        Assert.NotNull(merged.Summon);
        Assert.Equal(["zombie"], merged.Summon!.Creatures);
        Assert.Equal(SummonDisposition.Hostile, merged.Summon.Disposition);
    }

    [Fact]
    public void Merge_NullChildSummon_KeepsParent()
    {
        var parent = new SpellDefinition
        {
            Name = "base",
            Summon = new SummonEffect { Creatures = ["skeleton"] },
        };
        var child = new SpellDefinition { Name = "sub", Inherits = ["base"] };

        var merged = SpellDefinition.Merge(child, parent);

        Assert.NotNull(merged.Summon);
        Assert.Equal(["skeleton"], merged.Summon!.Creatures);
    }

    [Fact]
    public void SummonEffect_Disposition_DefaultsLoyal()
    {
        Assert.Equal(SummonDisposition.Loyal, new SummonEffect().Disposition);
    }

    [Fact]
    public void Summon_ConjureAnimals_HasChoiceCounts()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("conjure_animals", out var conjure));
        var summon = Assert.IsType<SummonEffect>(conjure.Summon);
        Assert.Null(summon.CountAtSlotLevel);
        Assert.NotNull(summon.CountChoicesAtSlotLevel);
        Assert.Equal([1, 2, 4, 8], summon.CountChoicesAtSlotLevel[3]);
        Assert.Equal([2, 4, 8, 16], summon.CountChoicesAtSlotLevel[5]);
        Assert.Equal([3, 6, 12, 24], summon.CountChoicesAtSlotLevel[7]);
        Assert.Equal(600, summon.DurationRounds);
        Assert.Equal(SummonDisposition.Neutral, summon.Disposition);
        Assert.Equal(["giant_spider"], summon.Creatures);
    }

    [Fact]
    public void Summon_ConjureElemental_CreaturesResolveHostile()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("conjure_elemental", out var conjure));
        var summon = Assert.IsType<SummonEffect>(conjure.Summon);
        Assert.Equal(1, summon.CountAtSlotLevel![5]);
        Assert.Equal(1, summon.CountAtSlotLevel[9]);
        Assert.Equal(600, summon.DurationRounds);
        Assert.Equal(SummonDisposition.Hostile, summon.Disposition);
        foreach (var creature in summon.Creatures)
        {
            Assert.True(
                Creatures.TryGet(RulesetSystem.Dnd5e, creature, out _),
                $"Summon creature ref '{creature}' resolves no CreatureDefinition.");
        }
    }

    [Fact]
    public void Summon_AnimateObjects_HasSmallSeed()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Dnd5e);

        Assert.True(spells.TryGetValue("animate_objects", out var animated));
        var summon = Assert.IsType<SummonEffect>(animated.Summon);
        Assert.Equal(10, summon.CountAtSlotLevel![5]);
        Assert.Equal(18, summon.CountAtSlotLevel[9]);
        Assert.Equal(10, summon.DurationRounds);
        Assert.Equal(SummonDisposition.Loyal, summon.Disposition);
        var seed = Assert.IsType<SummonStatSeed>(summon.InlineSeed);
        Assert.Equal(25, seed.Hp);
        Assert.Equal(16, seed.Defense);
        Assert.NotEmpty(seed.Attacks);
    }

    [Fact]
    public void Summon_Pf2eSummonUndead_ResolvesSkeletonGuard()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(spells.TryGetValue("summon_undead", out var summon));
        var effect = Assert.IsType<SummonEffect>(summon.Summon);
        Assert.Equal(["skeleton_guard"], effect.Creatures);
        Assert.Equal(1, effect.CountAtSlotLevel![1]);
        Assert.Equal(10, effect.DurationRounds);
        Assert.Equal(SummonDisposition.Loyal, effect.Disposition);
        Assert.True(
            Creatures.TryGet(RulesetSystem.Pathfinder2e, "skeleton_guard", out _),
            "Summon creature ref 'skeleton_guard' resolves no pf2e CreatureDefinition.");
    }

    [Fact]
    public void Summon_Pf2eRouseSkeletons_HasRank3Count()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(spells.TryGetValue("rouse_skeletons", out var rouse));
        var effect = Assert.IsType<SummonEffect>(rouse.Summon);
        Assert.Equal(["skeleton_guard"], effect.Creatures);
        Assert.Equal(1, effect.CountAtSlotLevel![3]);
        Assert.Equal(10, effect.DurationRounds);
    }

    [Fact]
    public void Summon_Pf2eSummonFiend_UsesGuidanceSeed()
    {
        var spells = Spells.GetSpellsForSystem(RulesetSystem.Pathfinder2e);

        Assert.True(spells.TryGetValue("summon_fiend", out var fiend));
        var effect = Assert.IsType<SummonEffect>(fiend.Summon);
        Assert.Empty(effect.Creatures);
        Assert.NotNull(effect.InlineSeed);
        Assert.NotEmpty(effect.InlineSeed.Attacks);
        Assert.Equal(1, effect.CountAtSlotLevel![5]);
    }
}
