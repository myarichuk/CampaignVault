using System.Collections.Generic;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using NSubstitute;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>
/// Pins what <c>ApplyAllModifiers</c> does today, so the roll-modifier pipeline can be shown to leave existing rolls unchanged.
/// A test named "quirk" documents behaviour that is kept on purpose or changed deliberately later (with its own test).
/// </summary>
public class ApplyAllModifiersCharacterizationTests
{
    private sealed class Probe(IRollService roll) : Dnd5eRulesetResolver(roll)
    {
        public int Apply(Dnd5eExtension stats, int baseValue, params string[] tags) => ApplyAllModifiers(stats, baseValue, tags);
    }

    private static readonly Probe Resolver = new(Substitute.For<IRollService>());

    private static Dnd5eExtension WithEffects(params (string Stat, float Value)[][] effects)
    {
        var stats = new Dnd5eExtension();
        foreach (var e in effects)
        {
            var fx = new StatusEffect { Name = "fx" };
            foreach (var (stat, value) in e)
                fx.StatModifiers[stat] = value;
            stats.StatusEffects.Add(fx);
        }

        return stats;
    }

    [Fact]
    public void No_effects_leaves_the_base_alone() =>
        Assert.Equal(5, Resolver.Apply(new Dnd5eExtension(), 5, "AttackRoll"));

    [Fact]
    public void A_direct_tag_adds()
    {
        var stats = WithEffects([("AttackRoll", -2)]);
        Assert.Equal(3, Resolver.Apply(stats, 5, "AttackRoll"));
    }

    [Fact]
    public void All_rolls_applies_once_per_effect_even_with_several_tags()
    {
        var stats = WithEffects([("AllRolls", 1)]);
        Assert.Equal(6, Resolver.Apply(stats, 5, "SkillCheck", "Athletics"));
    }

    [Fact]
    public void All_rolls_stacks_across_effects()
    {
        var stats = WithEffects([("AllRolls", 1)], [("AllRolls", 2)]);
        Assert.Equal(8, Resolver.Apply(stats, 5, "AttackRoll"));
    }

    [Theory]
    [InlineData("AC")]
    [InlineData("Defense")]
    public void All_rolls_does_not_touch_armour_class(string tag)
    {
        var stats = WithEffects([("AllRolls", 2)]);
        Assert.Equal(15, Resolver.Apply(stats, 15, tag));
    }

    [Fact]
    public void All_checks_applies_to_skill_checks_only()
    {
        var stats = WithEffects([("AllChecks", -1)]);
        Assert.Equal(4, Resolver.Apply(stats, 5, "SkillCheck", "Stealth"));
        Assert.Equal(5, Resolver.Apply(stats, 5, "SavingThrow", "Wisdom"));
        Assert.Equal(5, Resolver.Apply(stats, 5, "AttackRoll"));
    }

    [Fact]
    public void All_saves_applies_to_saving_throws_only()
    {
        var stats = WithEffects([("AllSaves", -3)]);
        Assert.Equal(2, Resolver.Apply(stats, 5, "SavingThrow", "Wisdom"));
        Assert.Equal(5, Resolver.Apply(stats, 5, "SkillCheck", "Stealth"));
    }

    [Fact]
    public void A_skill_and_the_generic_check_both_apply()
    {
        var stats = WithEffects([("Athletics", 2), ("AllChecks", -1)]);
        Assert.Equal(6, Resolver.Apply(stats, 5, "SkillCheck", "Athletics"));
    }

    [Fact]
    public void The_bonus_is_floored_after_summing()
    {
        var stats = WithEffects([("AttackRoll", -0.5f)]);
        Assert.Equal(4, Resolver.Apply(stats, 5, "AttackRoll")); // 5 + floor(-0.5) = 4
        var up = WithEffects([("AttackRoll", 0.5f)]);
        Assert.Equal(5, Resolver.Apply(up, 5, "AttackRoll"));
    }

    [Theory]
    [InlineData("Athletics", "athletics")]
    [InlineData("Sleight of Hand", "SleightOfHand")]
    [InlineData("sleight_of_hand", "Sleight-of-Hand")]
    public void Skill_names_match_ignoring_case_spaces_and_punctuation(string keyOnEffect, string skillRolled)
    {
        // Deliberate change from the old case-sensitive literal match: the same skill is one skill however it is written.
        var stats = WithEffects([(keyOnEffect, -2)]);
        Assert.Equal(3, Resolver.Apply(stats, 5, "SkillCheck", skillRolled));
    }

    [Fact]
    public void Speed_and_unknown_keys_change_nothing()
    {
        var stats = WithEffects([("Speed", -20), ("Charisma", 4)]);
        Assert.Equal(5, Resolver.Apply(stats, 5, "AttackRoll"));
    }
}
