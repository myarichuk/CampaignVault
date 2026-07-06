using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class RestThenAdvanceWorldIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public RestThenAdvanceWorldIntegrationTests(RavenDBFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RestThenAdvanceWorld_RecoversSpellSlots()
    {
        var engine = new DefaultSimulationEngine([new ResourceRecoveryRule(NullLogger<ResourceRecoveryRule>.Instance)], null);
        var repo = _fixture.CreateRepository(
            engineOverride: engine,
            overrides: b => b.RegisterInstance(new EncounterResolver(() => 1.0)).As<EncounterResolver>());

        const string campaign = "rest-then-advance";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/rest-test";
        const string locId = "locations/inn";

        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = charId,
            Name = "Rest Test",
            KeepAlive = true,
            CurrentLocationId = locId,
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["spell_slots_1"] = new() { Current = 0, Max = 4, Recovery = RecoveryType.LongRest }
                }
            }
        }, campaign);

        await repo.UpsertLocationAsync(session, new LocationUpsertRequest { Id = locId, Name = "Inn", Type = LocationType.Room }, campaign);
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 10 }, campaign);
        await session.SaveChangesAsync();

        await repo.StageChangesAsync(session, [
            new RestChange
            {
                CharacterId = charId,
                LocationId = locId,
                IntendedHours = 8,
                SecurityModifier = 100,
                RestType = RestType.LongRest
            }
        ], campaign);
        await session.SaveChangesAsync();

        // Pool must still be empty immediately after rest — proves resource pool recovery
        // is deferred to the next advance_world call, not applied at rest time (P2-3).
        var afterRest = await session.LoadAsync<Character>(charId);
        Assert.Equal(0, afterRest.SystemStats!.ResourcePools["spell_slots_1"].Current);
        Assert.NotNull(afterRest.LastRestedDay);
        Assert.Equal(RestType.LongRest, afterRest.LastRestType);

        await repo.AdvanceWorldAsync(session, 0, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();

        var afterAdvance = await session.LoadAsync<Character>(charId);
        Assert.Equal(4, afterAdvance.SystemStats!.ResourcePools["spell_slots_1"].Current);
        Assert.Equal(afterRest.LastRestedDay, afterAdvance.LastRestRecoveredDay);
        Assert.Equal(afterAdvance.LastRestedDay, afterAdvance.SystemStats.ResourcePools["spell_slots_1"].LastRecoveredDay);
    }

    [Fact]
    public async Task AdvanceWorld_RecoversDailyPool_OncePerDay()
    {
        var engine = new DefaultSimulationEngine([new ResourceRecoveryRule(NullLogger<ResourceRecoveryRule>.Instance)], null);
        var repo = _fixture.CreateRepository(engineOverride: engine);

        const string campaign = "daily-pool-recovery";
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);

        using var session = _fixture.Store.OpenAsyncSession();
        const string charId = "chars/daily-test";

        await repo.UpsertCharacterAsync(session, new CharacterUpsertRequest
        {
            Id = charId,
            Name = "Daily Test",
            KeepAlive = true,
            SystemStats = new Dnd5eExtension
            {
                ResourcePools = new Dictionary<string, ResourcePool>
                {
                    ["daily_pool"] = new() { Current = 0, Max = 5, Recovery = RecoveryType.Daily }
                }
            }
        }, campaign);
        await repo.SaveTimeAsync(session, new CampaignTime { TotalDaysElapsed = 10 }, campaign);
        await session.SaveChangesAsync();

        await repo.AdvanceWorldAsync(session, 0, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();

        var afterFirst = await session.LoadAsync<Character>(charId);
        Assert.Equal(5, afterFirst.SystemStats!.ResourcePools["daily_pool"].Current);
        Assert.Equal(10, afterFirst.SystemStats.ResourcePools["daily_pool"].LastRecoveredDay);

        await repo.StageChangesAsync(session, [
            new ResourceChange { CharacterId = charId, PoolName = "daily_pool", Delta = -3 }
        ], campaign);
        await session.SaveChangesAsync();

        // Same day: must NOT recover again.
        await repo.AdvanceWorldAsync(session, 0, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();

        var sameDay = await session.LoadAsync<Character>(charId);
        Assert.Equal(2, sameDay.SystemStats!.ResourcePools["daily_pool"].Current);

        // Next day: recovers again.
        await repo.AdvanceWorldAsync(session, 1, TimeOfDay.Noon, campaign);
        await session.SaveChangesAsync();

        var nextDay = await session.LoadAsync<Character>(charId);
        Assert.Equal(5, nextDay.SystemStats!.ResourcePools["daily_pool"].Current);
        Assert.Equal(11, nextDay.SystemStats.ResourcePools["daily_pool"].LastRecoveredDay);
    }
}
