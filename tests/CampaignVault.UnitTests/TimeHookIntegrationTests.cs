using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Models;
using Xunit;

namespace CampaignVault.Tests;

/// <summary>The time hook through the real container and repository: DI wiring, and the observer's message reaching the caller.</summary>
[Collection("RavenDB")]
public class TimeHookIntegrationTests : IClassFixture<RavenDBFixture>
{
    private readonly RavenDBFixture _fixture;

    public TimeHookIntegrationTests(RavenDBFixture fixture) => _fixture = fixture;

    private async Task<(CampaignRepository repo, Raven.Client.Documents.Session.IAsyncDocumentSession session, string campaign)> SetUpAsync(string campaign)
    {
        var engine = new DefaultSimulationEngine([], null);
        var repo = _fixture.CreateRepository(
            engineOverride: engine,
            overrides: b => b.RegisterInstance(new EncounterResolver(() => 1.0)).As<EncounterResolver>());
        await TestCampaignDefaults.EnsureExistsAsync(_fixture, campaign);
        var session = _fixture.Store.OpenAsyncSession();
        var cs = _fixture.CreateCampaignSession(session, campaign);
        await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
        {
            Id = "chars/hiker", Name = "Hiker", IsPc = true, KeepAlive = true, SystemStats = new Dnd5eExtension()
        });
        await repo.UpsertLocationAsync(cs, new LocationUpsertRequest { Id = "locations/pass", Name = "Pass", Type = LocationType.Wilderness });
        await repo.SaveTimeAsync(cs, new CampaignTime { TotalDaysElapsed = 10 });
        session.Advanced.WaitForIndexesAfterSaveChanges(timeout: System.TimeSpan.FromSeconds(10), throwOnTimeout: false);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (repo, session, campaign);
    }

    [Fact]
    public async Task Travel_ThroughTheRealContainer_DeliversTheBeatHintToTheCaller()
    {
        var (repo, session, campaign) = await SetUpAsync("time-hook-travel");
        using var _ = session;
        ConsequenceBeatObserver.RollOverrideForTests = () => 0.0;
        try
        {
            var result = await repo.StageChangesAsync(_fixture.CreateCampaignSession(session, campaign), [
                new TravelChange
                {
                    CharacterId = "chars/hiker", DestinationLocationId = "locations/pass",
                    TravelCostHoursOverride = 6, TerrainOverride = "mountains"
                }
            ]);
            Assert.True(result.Success);
            Assert.Contains(result.Summary, m => m.Contains("CONSEQUENCE BEAT") && m.Contains("Hiker"));
        }
        finally
        {
            ConsequenceBeatObserver.RollOverrideForTests = null;
        }
    }

    [Fact]
    public async Task AdvanceWorld_ThroughTheRealContainer_DeliversTheBeatHintToTheCaller()
    {
        var (repo, session, campaign) = await SetUpAsync("time-hook-advance");
        using var _ = session;
        ConsequenceBeatObserver.RollOverrideForTests = () => 0.0;
        try
        {
            var result = await repo.AdvanceWorldAsync(session, 0, null, campaign, hours: 6, partyLocationId: "locations/pass");
            Assert.Contains(result.SimulatorEvents, m => m.Contains("CONSEQUENCE BEAT"));
        }
        finally
        {
            ConsequenceBeatObserver.RollOverrideForTests = null;
        }
    }

    [Fact]
    public async Task Off_DeliversNothing()
    {
        var (repo, session, campaign) = await SetUpAsync("time-hook-off");
        using var _ = session;
        var cs = _fixture.CreateCampaignSession(session, campaign);
        var meta = await session.LoadAsync<Campaign>(new CampaignDocumentKeys().Meta(campaign), TestContext.Current.CancellationToken);
        meta.SystemOptions["consequences"] = "off";
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        ConsequenceBeatObserver.RollOverrideForTests = () => 0.0;
        try
        {
            var result = await repo.StageChangesAsync(cs, [
                new TravelChange
                {
                    CharacterId = "chars/hiker", DestinationLocationId = "locations/pass",
                    TravelCostHoursOverride = 6, TerrainOverride = "mountains"
                }
            ]);
            Assert.DoesNotContain(result.Summary, m => m.Contains("CONSEQUENCE BEAT"));
        }
        finally
        {
            ConsequenceBeatObserver.RollOverrideForTests = null;
        }
    }

    [Fact]
    public async Task A_hobbled_hiker_takes_longer_through_the_real_container()
    {
        var (repo, session, campaign) = await SetUpAsync("time-hook-slow");
        using var _ = session;
        var cs = _fixture.CreateCampaignSession(session, campaign);
        var limper = new Dnd5eExtension { Movement = 30 };
        limper.StatusEffects.Add(new StatusEffect { Name = "Hobbled", StatModifiers = { ["Speed"] = -20 } });
        await repo.UpsertCharacterAsync(cs, new CharacterUpsertRequest
        {
            Id = "chars/limper", Name = "Limper", IsPc = true, KeepAlive = true, SystemStats = limper
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await repo.StageChangesAsync(cs, [
            new TravelChange { CharacterId = "chars/limper", DestinationLocationId = "locations/pass", TravelCostHoursOverride = 2 }
        ]);

        Assert.True(result.Success);
        Assert.Contains(result.Summary, m => m.Contains("Limper slows the way") && m.Contains("6h instead of 2h"));
    }
}
