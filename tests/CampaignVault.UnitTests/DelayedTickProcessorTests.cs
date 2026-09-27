using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CampaignVault.Data;
using CampaignVault.Events;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

public class DelayedTickProcessorTests
{
    private static Character MakeCharacter(int currentHp = 10, int maxHp = 20, params StatusEffect[] effects) =>
        new()
        {
            Id = "char1",
            Name = "Goblin",
            CurrentHp = currentHp,
            MaxHp = maxHp,
            SystemStats = new Dnd5eExtension { StatusEffects = effects.ToList() },
        };

    private static StatusEffect MakeResidue(string dice = "2d4") =>
        new()
        {
            Name = "AcidArrowResidue",
            Category = "Condition",
            PendingDamage = new PendingEffectDamage { DiceExpression = dice, DamageType = "acid" },
            ExpiresAtOwnTurnStart = true,
            AppliedBy = "system/combat-resolver",
        };

    [Fact]
    public async Task ProcessAsync_NoOwnTurnEffects_DoesNothing()
    {
        var character = MakeCharacter(effects: new StatusEffect { Name = "Frightened", Category = "Condition" });
        var rolls = new FakeRollService();
        var messages = new List<string>();

        var events = await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Empty(events);
        Assert.Empty(messages);
        Assert.Empty(rolls.RecordedRequests);
        Assert.Single(character.SystemStats.StatusEffects);
        Assert.Equal(10, character.CurrentHp);
    }

    [Fact]
    public async Task ProcessAsync_PendingDamage_AppliesHpRemovesAndEmitsEvent()
    {
        var character = MakeCharacter(effects: MakeResidue());
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 5, Summary = "Rolled 5" });
        var messages = new List<string>();

        var events = await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Equal(5, character.CurrentHp);
        Assert.Empty(character.SystemStats.StatusEffects);
        Assert.Single(messages);
        Assert.Contains("dealt 5 acid damage", messages[0]);
        Assert.Equal("delayed-tick", rolls.RecordedRequests.Single().Tag);
        Assert.Equal("2d4", rolls.RecordedRequests.Single().Expression);
        var (topic, data) = Assert.Single(events);
        Assert.Equal(CoreEvents.CharacterDamaged, topic);
        var payload = Assert.IsType<Dictionary<string, object?>>(data);
        Assert.Equal("char1", payload[CoreEvents.Fields.CharacterId]);
        Assert.Equal(5, payload[CoreEvents.Fields.Amount]);
        Assert.Equal(5, payload[CoreEvents.Fields.HpLost]);
        Assert.Equal(5, payload[CoreEvents.Fields.CurrentHp]);
    }

    [Fact]
    public async Task ProcessAsync_LethalTick_ClampsAtZeroAndEmitsDowned()
    {
        var character = MakeCharacter(currentHp: 3, effects: MakeResidue());
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 8, Summary = "Rolled 8" });
        var messages = new List<string>();

        var events = await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Equal(0, character.CurrentHp);
        Assert.Empty(character.SystemStats.StatusEffects);
        Assert.Equal(2, events.Count);
        Assert.Equal(CoreEvents.CharacterDamaged, events[0].Topic);
        Assert.Equal(CoreEvents.CharacterDowned, events[1].Topic);
    }

    [Fact]
    public async Task ProcessAsync_EffectWithoutPendingDamage_ExpiresQuietly()
    {
        var character = MakeCharacter(effects: new StatusEffect
        {
            Name = "HasteFade",
            Category = "Condition",
            ExpiresAtOwnTurnStart = true,
        });
        var rolls = new FakeRollService();
        var messages = new List<string>();

        var events = await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Empty(events);
        Assert.Empty(rolls.RecordedRequests);
        Assert.Empty(character.SystemStats.StatusEffects);
        Assert.Equal(10, character.CurrentHp);
        Assert.Single(messages);
        Assert.Contains("Expired effect 'HasteFade'", messages[0]);
    }

    [Fact]
    public async Task ProcessAsync_NullRollService_WithPendingDamage_ThrowsAndKeepsEffect()
    {
        var character = MakeCharacter(effects: MakeResidue());
        var messages = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DelayedTickProcessor.ProcessAsync(character, null, messages, TestContext.Current.CancellationToken));

        Assert.Single(character.SystemStats.StatusEffects);
    }

    [Fact]
    public async Task ProcessAsync_MaxHpZero_SkipsDamage()
    {
        var character = MakeCharacter(currentHp: 0, maxHp: 0, effects: MakeResidue());
        var rolls = new FakeRollService();
        var messages = new List<string>();

        var events = await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Empty(events);
        Assert.Empty(rolls.RecordedRequests);
        Assert.Empty(character.SystemStats.StatusEffects);
        Assert.Single(messages);
        Assert.Contains("MaxHp is 0", messages[0]);
    }

    [Fact]
    public async Task ProcessAsync_ConcentrationBroken_OnFailedSave()
    {
        var character = MakeCharacter(10, 20,
            MakeResidue(),
            new StatusEffect { Name = "Concentration (Bless)", Category = "Condition" });
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 7, Summary = "Rolled 7" });
        var messages = new List<string>();

        await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        Assert.Equal(4, character.CurrentHp);
        Assert.Empty(character.SystemStats.StatusEffects);
        Assert.Equal(2, messages.Count);
        Assert.Contains("Concentration broken", messages[1]);
        Assert.Equal("concentration", rolls.RecordedRequests[1].Tag);
    }

    [Fact]
    public async Task ProcessAsync_ConcentrationHeld_OnSuccessfulSave()
    {
        var character = MakeCharacter(10, 20,
            MakeResidue(),
            new StatusEffect { Name = "Concentration (Bless)", Category = "Condition" });
        var rolls = new FakeRollService();
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        rolls.NextRolls.Enqueue(new RollOutcome { Result = 15, Summary = "Rolled 15" });
        var messages = new List<string>();

        await DelayedTickProcessor.ProcessAsync(character, rolls, messages, TestContext.Current.CancellationToken);

        var remaining = Assert.Single(character.SystemStats.StatusEffects);
        Assert.Equal("Concentration (Bless)", remaining.Name);
        Assert.Contains("Concentration held", messages[1]);
    }
}
