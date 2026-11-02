using System.Collections.Generic;
using System.Linq;
using CampaignVault.Data.Context;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>WITNESS_CHANNEL_PLAN.md A3: one row per line of the A0 rules table.</summary>
public class AwarenessResolverTests
{
    private static Character Person(int passivePerception = 12, AwarenessState? state = null, int? stealth = null, params string[] conditions)
    {
        var stats = new Dnd5eExtension();
        stats.Attributes["passivePerception"] = passivePerception;
        foreach (var condition in conditions)
        {
            stats.StatusEffects.Add(new StatusEffect { Name = condition, Category = "Condition", ConditionName = condition });
        }

        return new Character
        {
            Id = "chars/x", Name = "X", SystemStats = stats,
            Awareness = state == null ? null : new Awareness { State = state.Value },
            Concealment = stealth == null ? null : new Concealment { StealthTotal = stealth.Value },
        };
    }

    // ---- spectacles ----

    [Fact]
    public void The_unconscious_perceive_nothing() =>
        Assert.Equal(Noticed.Misses, AwarenessResolver.Perceive(Person(conditions: "unconscious"), Perceptible.Both).Outcome);

    [Fact]
    public void A_sleeper_sleeps_through_a_silent_spectacle_and_wakes_to_a_loud_one()
    {
        Assert.Equal(Noticed.Misses, AwarenessResolver.Perceive(Person(state: AwarenessState.Asleep), Perceptible.Visible).Outcome);

        var loud = AwarenessResolver.Perceive(Person(state: AwarenessState.Asleep), Perceptible.Both);
        Assert.Equal(Noticed.Wakes, loud.Outcome);
        Assert.Equal(Perceptible.Loud, loud.Senses);
    }

    [Fact]
    public void A_deaf_sleeper_is_not_woken_by_noise() =>
        Assert.Equal(Noticed.Misses, AwarenessResolver.Perceive(Person(state: AwarenessState.Asleep, conditions: "deafened"), Perceptible.Both).Outcome);

    [Fact]
    public void Blinded_and_deafened_keep_the_other_sense()
    {
        Assert.Equal("blinded: heard it only", AwarenessResolver.Perceive(Person(conditions: "blinded"), Perceptible.Both).Note);
        Assert.Equal("deafened: saw it only", AwarenessResolver.Perceive(Person(conditions: "deafened"), Perceptible.Both).Note);
    }

    [Fact]
    public void Everyone_sees_an_invisible_casters_fireball_but_not_the_caster() =>
        Assert.Equal(Noticed.EffectNotSource,
            AwarenessResolver.Perceive(Person(), Perceptible.Both, source: Person(conditions: "invisible")).Outcome);

    // ---- a quiet or hidden presence ----

    [Fact]
    public void Someone_in_plain_view_is_seen_by_anyone_awake() =>
        Assert.Equal(Noticed.Perceives, AwarenessResolver.NoticePresence(Person(passivePerception: 8), Person()).Outcome);

    [Theory]
    [InlineData(15, 15, Noticed.Perceives)]
    [InlineData(14, 15, Noticed.Misses)]
    public void A_hiding_source_is_noticed_when_passive_perception_meets_its_stealth(int pp, int stealth, Noticed expected) =>
        Assert.Equal(expected, AwarenessResolver.NoticePresence(Person(pp), Person(stealth: stealth)).Outcome);

    [Fact]
    public void Distraction_costs_five()
    {
        Assert.Equal(Noticed.Misses, AwarenessResolver.NoticePresence(Person(15, AwarenessState.Distracted), Person(stealth: 12)).Outcome);
        Assert.Equal(Noticed.Misses, AwarenessResolver.NoticePresence(Person(15), Person(stealth: 12), absorbed: true).Outcome);
        Assert.Equal(10, AwarenessResolver.PassivePerception(Person(15), absorbed: true));
    }

    [Theory]
    [InlineData("dnd5e", Noticed.Misses)]
    [InlineData("pf2e", Noticed.Wakes)]
    public void A_sleeper_wakes_to_a_quiet_intruder_only_if_their_reduced_score_meets_its_stealth(string system, Noticed expected) =>
        // 18 - 5 = 13 < 14 (dnd5e house rule D6); 18 - 4 = 14 >= 14 (Player Core unconscious).
        Assert.Equal(expected, AwarenessResolver.NoticePresence(Person(18, AwarenessState.Asleep), Person(stealth: 14), system: system).Outcome);

    [Fact]
    public void An_invisible_sneak_is_heard_not_seen_when_the_score_beats_their_quiet() =>
        Assert.Equal(Noticed.EffectNotSource,
            AwarenessResolver.NoticePresence(Person(14), Person(stealth: 12, conditions: "invisible")).Outcome);

    [Fact]
    public void Haggling_searching_and_marked_actions_absorb_the_actor_for_the_beat()
    {
        var ids = AwarenessResolver.AbsorbedIds(
        [
            new EventOccurred { Summary = "Haggling", Category = EventCategory.Conversation, Involved = ["chars/a", "chars/b"] },
            new RulesetAction
            {
                CharacterId = "chars/c", ActionName = "Search the desk", ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Investigation" }
            },
            new RulesetAction
            {
                CharacterId = "chars/d", ActionName = "Pick the lock", ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Sleight of Hand", ["absorbed"] = "true" }
            },
            new RulesetAction
            {
                CharacterId = "chars/e", ActionName = "Look around", ActionType = RulesetActionType.SkillCheck,
                Parameters = new Dictionary<string, string> { ["skill"] = "Perception" }
            },
        ]);

        Assert.Equal(["chars/a", "chars/b", "chars/c", "chars/d"], ids.Order());
    }
}
