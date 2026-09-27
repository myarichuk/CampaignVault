using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using CampaignVault.Tools;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class CombatToolsTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public CombatToolsTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    private CampaignTools CreateTools()
    {
        return TestCampaignToolsFactory.Create(_fixture);
    }

    [Fact]
    public async Task StartCombat_ValidCharacters_InitializesEncounter()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();

        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);

        Assert.True(result.Success, $"StartCombat failed. Error: {result.Error}, Summary: {result.Summary}");
        Assert.NotNull(result.Data);
        Assert.True(result.Data.IsActive);
        Assert.Equal(2, result.Data.Combatants.Count);
        Assert.Equal(loc, result.Data.LocationId);
        Assert.NotNull(result.Data.ActiveTurnId);
        Assert.Equal(1, result.Data.Round);
    }

    [Fact]
    public async Task StartCombat_EmptyList_ReturnsError()
    {
        var store = _store;
        var tools = CreateTools();
        var campaign = "camp_" + Guid.NewGuid();

        var result = await tools.StartCombat("loc1", [], campaignName: campaign);

        Assert.False(result.Success);
        Assert.Contains("Cannot start combat with zero", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCombat_RejectsCombatantFromOtherCampaign()
    {
        var store = _store;
        var tools = CreateTools();
        var foreignId = "char_foreign_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = foreignId,
                Name = "Foreign Enemy",
                CampaignName = "other-campaign",
                CurrentHp = 10
            }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await tools.StartCombat("loc1", [foreignId], campaignName: campaign);

        Assert.False(result.Success);
        Assert.Equal("InvalidInput", result.Error);
        Assert.Contains("not available in campaign", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCombat_AllowsCanonCombatantWithoutCampaignName()
    {
        var store = _store;
        var tools = CreateTools();
        var canonId = "chars/bob_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = canonId, Name = "Bob", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await tools.StartCombat("loc1", [canonId], campaignName: campaign);

        Assert.True(result.Success, result.Summary);
        Assert.Single(result.Data!.Combatants);
    }

    [Fact]
    public async Task StartCombat_DeadCharacters_FiltersOutAndMayFail()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 0 }, TestContext.Current.CancellationToken); // Dead
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = -5 }, TestContext.Current.CancellationToken); // Dead
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await tools.StartCombat("loc1", [c1, c2], campaignName: campaign);

        Assert.False(result.Success);
        Assert.Contains("None of the specified combatants are valid and alive", result.Summary);
    }

    [Fact]
    public async Task StartCombat_MixOfAliveAndZeroHp_DropsZeroHpAndReportsInSummary()
    {
        var store = _store;
        var tools = CreateTools();
        var alive = "char1_" + Guid.NewGuid();
        var downed = "char2_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = alive, Name = "Alice", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character { Id = downed, Name = "Bob", CurrentHp = 0 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await tools.StartCombat("loc1", [alive, downed], campaignName: campaign);

        Assert.True(result.Success, $"StartCombat failed. Error: {result.Error}, Summary: {result.Summary}");
        Assert.Single(result.Data!.Combatants);
        Assert.Equal(alive, result.Data!.Combatants[0].CharacterId);
        Assert.Contains("Dropped 1 combatant(s) with 0 or negative HP", result.Summary);
        Assert.Contains(downed, result.Summary);
    }

    [Fact]
    public async Task NextTurn_SkipsDeadCharacters()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Start combat
        var startResult = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);
        Assert.True(startResult.Success,
            $"StartCombat failed. Error: {startResult.Error}, Summary: {startResult.Summary}");

        var firstCharacterId = startResult.Data!.ActiveTurnId;
        var secondCharacterId = firstCharacterId == c1 ? c2 : c1;

        // Kill the second actor
        using (var session = store.OpenAsyncSession())
        {
            var char2 = await session.LoadAsync<Character>(secondCharacterId, TestContext.Current.CancellationToken);
            char2.CurrentHp = 0;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Advance turn
        var nextResult = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextResult.Success, $"NextTurn failed. Error: {nextResult.Error}, Summary: {nextResult.Summary}");

        // It should have skipped the dead guy and wrapped around back to the first guy, OR
        // it advanced to round 2 and gave the turn to the only alive person.
        Assert.Equal(firstCharacterId, nextResult.Data!.ActiveTurnId);
        Assert.Equal(2, nextResult.Data.Round);
    }

    [Fact]
    public async Task NextTurn_EveryoneDead_EndsCombatOrFails()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Start combat
        var startResult = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);
        Assert.True(startResult.Success,
            $"StartCombat failed. Error: {startResult.Error}, Summary: {startResult.Summary}");

        // Kill EVERYONE
        using (var session = store.OpenAsyncSession())
        {
            var char1 = await session.LoadAsync<Character>(c1, TestContext.Current.CancellationToken);
            var char2 = await session.LoadAsync<Character>(c2, TestContext.Current.CancellationToken);
            char1.CurrentHp = 0;
            char2.CurrentHp = 0;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Advance turn
        var nextResult = await tools.NextTurn(campaignName: campaign);
        Assert.False(nextResult.Success);
        Assert.Equal("CombatEnded", nextResult.Error);
        Assert.Contains("Combat has ended", nextResult.Summary);
        Assert.False(nextResult.Data?.IsActive ?? true);

        using (var session = store.OpenAsyncSession())
        {
            var char1 = await session.LoadAsync<Character>(c1, TestContext.Current.CancellationToken);
            var char2 = await session.LoadAsync<Character>(c2, TestContext.Current.CancellationToken);
            char1.CurrentHp = 10;
            char2.CurrentHp = 10;
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var restart = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);
        Assert.True(restart.Success, restart.Summary);
        Assert.True(restart.Data?.IsActive);
    }

    [Fact]
    public async Task EndCombat_WrapsUpSuccessfully()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await tools.StartCombat(loc, [c1], campaignName: campaign);

        var endResult = await tools.EndCombat(campaignName: campaign);
        Assert.True(endResult.Success, $"EndCombat failed. Error: {endResult.Error}, Summary: {endResult.Summary}");
        Assert.False(endResult.Data!.IsActive);
    }

    [Fact]
    public async Task NextTurn_ExpiresRoundBasedStatusEffects()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = c1,
                Name = "Alice",
                CurrentHp = 10,
                SystemStats = new Dnd5eExtension
                {
                    StatusEffects =
                    [
                        new StatusEffect { Name = "Stunned", ExpiresAtRound = 1 },
                        new StatusEffect { Name = "Poisoned", ExpiresAtRound = 3 }
                    ]
                }
            }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Start combat (Round 1)
        await tools.StartCombat(loc, [c1, c2], campaignName: campaign);

        // Advance turns until round 2
        await tools.NextTurn(campaignName: campaign);
        await tools.NextTurn(campaignName: campaign); // This will transition to Round 2

        using (var session = store.OpenAsyncSession())
        {
            var alice = await session.LoadAsync<Character>(c1, TestContext.Current.CancellationToken);
            Assert.Single(alice.SystemStats.StatusEffects);
            Assert.Equal("Poisoned", alice.SystemStats.StatusEffects[0].Name);
        }
    }

    [Fact]
    public async Task EndCombat_ClearsRoundBasedStatuses()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = c1,
                Name = "Alice",
                CurrentHp = 10,
                SystemStats = new Dnd5eExtension
                {
                    StatusEffects =
                    [
                        new StatusEffect { Name = "Stunned", ExpiresAtRound = 5 }, // Should be removed
                        new StatusEffect { Name = "Cursed", ExpiresAtDay = 10 }, // Should NOT be removed
                        new StatusEffect { Name = "Poisoned", ExpiresAtRound = 10 }
                    ]
                }
            }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await tools.StartCombat(loc, [c1], campaignName: campaign);

        var endResult = await tools.EndCombat(campaignName: campaign);
        Assert.True(endResult.Success, $"EndCombat failed. Error: {endResult.Error}, Summary: {endResult.Summary}");
        Assert.Contains("Cleared effect 'Stunned'", endResult.Summary);
        Assert.Contains("Cleared effect 'Poisoned'", endResult.Summary);

        using (var session = store.OpenAsyncSession())
        {
            var alice = await session.LoadAsync<Character>(c1, TestContext.Current.CancellationToken);
            Assert.Single(alice.SystemStats.StatusEffects);
            Assert.Equal("Cursed", alice.SystemStats.StatusEffects[0].Name);
        }
    }

    [Fact]
    public async Task EndCombat_RecoversEncounterEndPools()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character
            {
                Id = c1,
                Name = "Alice",
                CurrentHp = 10,
                SystemStats = new Dnd5eExtension
                {
                    ResourcePools = new Dictionary<string, CampaignVault.Models.ResourcePool>
                    {
                        ["encounter_pool"] = new() { Current = 2, Max = 5, Recovery = RecoveryType.EncounterEnd }
                    }
                }
            }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await tools.StartCombat(loc, [c1], campaignName: campaign);

        var endResult = await tools.EndCombat(campaignName: campaign);
        Assert.True(endResult.Success, $"EndCombat failed. Error: {endResult.Error}, Summary: {endResult.Summary}");

        using (var session = store.OpenAsyncSession())
        {
            var alice = await session.LoadAsync<Character>(c1, TestContext.Current.CancellationToken);
            Assert.Equal(5, alice.SystemStats.ResourcePools["encounter_pool"].Current);
            Assert.Contains("encounter_pool", endResult.Summary);
        }
    }

    [Fact]
    public async Task NextTurn_OwnTurnStartEffect_AppliesPendingDamageOnce()
    {
        // Full turn-advance wiring for delayed ticks (e.g. Acid Arrow's residue): the seeded
        // effect fires when Bob's turn starts, dealing the rolled damage and removing itself.
        var store = _store;
        var rolls = new FakeRollService();
        // Initiative/derivation rolls may also draw from this service depending on container
        // wiring — over-queue so the tick deterministically rolls 6 wherever it lands.
        for (var i = 0; i < 30; i++)
        {
            rolls.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        }
        var tools = TestCampaignToolsFactory.Create(_fixture, rollService: rolls);
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 20, MaxHp = 20 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character
            {
                Id = c2,
                Name = "Bob",
                CurrentHp = 20,
                MaxHp = 20,
                SystemStats = new Dnd5eExtension
                {
                    StatusEffects =
                    [
                        new StatusEffect
                        {
                            Name = "AcidArrowResidue",
                            Category = "Condition",
                            PendingDamage = new PendingEffectDamage { DiceExpression = "2d4", DamageType = "acid" },
                            ExpiresAtOwnTurnStart = true,
                            AppliedBy = "system/combat-resolver",
                        }
                    ]
                }
            }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await tools.StartCombat(loc, [c1, c2], campaignName: campaign);

        // Initiative order is random, but after two advances Bob has started a turn exactly once
        // in either order — and the tick fires exactly once, when his turn starts.
        var first = await tools.NextTurn(campaignName: campaign);
        Assert.True(first.Success, $"NextTurn failed. Error: {first.Error}, Summary: {first.Summary}");
        var second = await tools.NextTurn(campaignName: campaign);
        Assert.True(second.Success, $"NextTurn failed. Error: {second.Error}, Summary: {second.Summary}");

        Assert.Contains("dealt 6 acid damage", first.Summary + " " + second.Summary);
        var tickRolls = rolls.RecordedRequests.Where(r => r.Tag == "delayed-tick").ToList();
        Assert.Single(tickRolls);
        Assert.Equal("2d4", tickRolls[0].Expression);

        using (var session = store.OpenAsyncSession())
        {
            var bob = await session.LoadAsync<Character>(c2, TestContext.Current.CancellationToken);
            Assert.Equal(14, bob.CurrentHp);
            Assert.Empty(bob.SystemStats.StatusEffects);
        }
    }

    [Fact]
    public async Task StartCombat_OverwriteActive_ClearsAbandonedEffectsAndFiresTicks()
    {
        // Force-restarting abandons the live encounter: its round-based effects are cleared and
        // pending own-turn damage resolves now, instead of leaking into the fresh encounter.
        // (CombatTools is built directly because the facade has no overwriteActive seam.)
        var store = _store;
        var rolls = new FakeRollService();
        for (var i = 0; i < 30; i++)
        {
            rolls.NextRolls.Enqueue(new RollOutcome { Result = 6, Summary = "Rolled 6" });
        }
        var repo = _fixture.CreateRepository();
        var combat = new CombatTools(repo, _fixture.Container.Resolve<CampaignDocumentKeys>(),
            _fixture.Container.Resolve<IRulesetModuleSelector>(), rollService: rolls);
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var loc = "loc1_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 20, MaxHp = 20 }, TestContext.Current.CancellationToken);
            await session.StoreAsync(new Character
            {
                Id = c2,
                Name = "Bob",
                CurrentHp = 20,
                MaxHp = 20,
                SystemStats = new Dnd5eExtension
                {
                    StatusEffects =
                    [
                        new StatusEffect
                        {
                            Name = "AcidArrowResidue",
                            Category = "Condition",
                            PendingDamage = new PendingEffectDamage { DiceExpression = "2d4", DamageType = "acid" },
                            ExpiresAtOwnTurnStart = true,
                            AppliedBy = "system/combat-resolver",
                        },
                        new StatusEffect { Name = "Haste", Category = "Buff", ExpiresAtRound = 5 }
                    ]
                }
            }, TestContext.Current.CancellationToken);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var first = await combat.StartCombat(loc, [c1, c2], campaign);
        Assert.True(first.Success, $"StartCombat failed. Error: {first.Error}, Summary: {first.Summary}");
        var restart = await combat.StartCombat(loc, [c1, c2], campaign, overwriteActive: true);
        Assert.True(restart.Success, $"Overwrite failed. Error: {restart.Error}, Summary: {restart.Summary}");

        Assert.Contains("dealt 6 acid damage", restart.Summary);
        Assert.Contains("Cleared effect 'Haste'", restart.Summary);
        var tickRolls = rolls.RecordedRequests.Where(r => r.Tag == "delayed-tick").ToList();
        Assert.Single(tickRolls);

        using (var session = store.OpenAsyncSession())
        {
            var bob = await session.LoadAsync<Character>(c2, TestContext.Current.CancellationToken);
            Assert.Equal(14, bob.CurrentHp);
            Assert.Empty(bob.SystemStats.StatusEffects);
        }
    }
}
