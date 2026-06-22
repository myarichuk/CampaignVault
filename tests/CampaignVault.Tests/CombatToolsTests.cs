using System;
using System.Threading.Tasks;
using CampaignVault.Models;
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
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 });
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 });
            await session.SaveChangesAsync();
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
    public async Task StartCombat_DeadCharacters_FiltersOutAndMayFail()
    {
        var store = _store;
        var tools = CreateTools();
        var c1 = "char1_" + Guid.NewGuid();
        var c2 = "char2_" + Guid.NewGuid();
        var campaign = "camp_" + Guid.NewGuid();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 0 }); // Dead
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = -5 }); // Dead
            await session.SaveChangesAsync();
        }

        var result = await tools.StartCombat("loc1", [c1, c2], campaignName: campaign);

        Assert.False(result.Success);
        Assert.Contains("None of the specified combatants are valid and alive", result.Summary);
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
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 });
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // Start combat
        var startResult = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);
        Assert.True(startResult.Success,
            $"StartCombat failed. Error: {startResult.Error}, Summary: {startResult.Summary}");

        var firstActorId = startResult.Data!.ActiveTurnId;
        var secondActorId = firstActorId == c1 ? c2 : c1;

        // Kill the second actor
        using (var session = store.OpenAsyncSession())
        {
            var char2 = await session.LoadAsync<Character>(secondActorId);
            char2.CurrentHp = 0;
            await session.SaveChangesAsync();
        }

        // Advance turn
        var nextResult = await tools.NextTurn(campaignName: campaign);
        Assert.True(nextResult.Success, $"NextTurn failed. Error: {nextResult.Error}, Summary: {nextResult.Summary}");

        // It should have skipped the dead guy and wrapped around back to the first guy, OR
        // it advanced to round 2 and gave the turn to the only alive person.
        Assert.Equal(firstActorId, nextResult.Data!.ActiveTurnId);
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
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 });
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // Start combat
        var startResult = await tools.StartCombat(loc, [c1, c2], campaignName: campaign);
        Assert.True(startResult.Success,
            $"StartCombat failed. Error: {startResult.Error}, Summary: {startResult.Summary}");

        // Kill EVERYONE
        using (var session = store.OpenAsyncSession())
        {
            var char1 = await session.LoadAsync<Character>(c1);
            var char2 = await session.LoadAsync<Character>(c2);
            char1.CurrentHp = 0;
            char2.CurrentHp = 0;
            await session.SaveChangesAsync();
        }

        // Advance turn
        var nextResult = await tools.NextTurn(campaignName: campaign);
        Assert.False(nextResult.Success);
        Assert.Contains("Combat has ended", nextResult.Summary);
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
            await session.StoreAsync(new Character { Id = c1, Name = "Alice", CurrentHp = 10 });
            await session.SaveChangesAsync();
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
            });
            await session.StoreAsync(new Character { Id = c2, Name = "Bob", CurrentHp = 10 });
            await session.SaveChangesAsync();
        }

        // Start combat (Round 1)
        await tools.StartCombat(loc, [c1, c2], campaignName: campaign);

        // Advance turns until round 2
        await tools.NextTurn(campaignName: campaign);
        await tools.NextTurn(campaignName: campaign); // This will transition to Round 2

        using (var session = store.OpenAsyncSession())
        {
            var alice = await session.LoadAsync<Character>(c1);
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
            });
            await session.SaveChangesAsync();
        }

        await tools.StartCombat(loc, [c1], campaignName: campaign);

        var endResult = await tools.EndCombat(campaignName: campaign);
        Assert.True(endResult.Success, $"EndCombat failed. Error: {endResult.Error}, Summary: {endResult.Summary}");
        Assert.Contains("Cleared effect 'Stunned'", endResult.Summary);
        Assert.Contains("Cleared effect 'Poisoned'", endResult.Summary);

        using (var session = store.OpenAsyncSession())
        {
            var alice = await session.LoadAsync<Character>(c1);
            Assert.Single(alice.SystemStats.StatusEffects);
            Assert.Equal("Cursed", alice.SystemStats.StatusEffects[0].Name);
        }
    }
}