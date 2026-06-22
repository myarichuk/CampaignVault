using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using CampaignVault.Data;
using CampaignVault.Data.Pressure;
using CampaignVault.Data.Pressure.Contributors;
using CampaignVault.Models;
using CampaignVault.Rulesets;
using Raven.Client.Documents;
using Xunit;

namespace CampaignVault.Tests;

[Collection("RavenDB")]
public class Phase9ExtensibilityTests : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;
    private readonly RavenDBFixture _fixture;

    public Phase9ExtensibilityTests(RavenDBFixture fixture)
    {
        _store = fixture.Store;
        _fixture = fixture;
    }

    [Fact]
    public async Task PressureOrchestrator_ScopeIsolation_WorldDoesNotIncludeSceneOnlyContributors()
    {
        var repo = _fixture.CreateRepository();
        using var session = _store.OpenAsyncSession();

        var locId = "locations/scope-test-" + Guid.NewGuid();
        await repo.UpsertLocationAsync(session, new Location
        {
            Id = locId,
            Name = "Empty Room",
            Type = LocationType.Room,
            Exits = [],
            PointsOfInterest = [],
            AmbientCrowd = null
        }, "scope-test");
        await session.SaveChangesAsync();

        var time = await repo.GetTimeAsync(session, "scope-test");
        var config = await repo.GetCampaignConfigAsync(session, "scope-test");
        var scene = await repo.GetSceneAsync(session, locId, "scope-test");

        var rollSvc = new DefaultRollService();
        var selector = new RulesetModuleSelector([
            new Dnd5eRulesetResolver(rollSvc),
            new Pf2eRulesetResolver(rollSvc),
            new Fallout2d20RulesetResolver(rollSvc),
            new NarrativeRulesetResolver(rollSvc)
        ]);
        var pm = new PressureManager(new CampaignDocumentKeys());
        var orchestrator = new PressureOrchestrator(_fixture.Container.Resolve<IEnumerable<IPressureContributor>>(), pm,
            selector);

        var worldCtx = new PressureContext("scope-test", time, config, session,
            Scene: scene, RequestedLocationId: locId);
        var worldPressures = await orchestrator.CollectAndCapAsync(PressureScope.World, worldCtx);
        var worldText = string.Join(" | ", worldPressures);

        Assert.DoesNotContain("flavor details", worldText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FlavorVacuum", worldText, StringComparison.OrdinalIgnoreCase);

        var sceneCtx = new PressureContext("scope-test", time, config, session,
            Scene: scene, RequestedLocationId: locId);
        var scenePressures = await orchestrator.CollectAndCapAsync(PressureScope.Scene, sceneCtx);
        var sceneText = string.Join(" | ", scenePressures);

        Assert.Contains("flavor details", sceneText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgingRumorPressureContributor_RespectsRumorAgingPressureDays_ConfigOverride()
    {
        using var session = _store.OpenAsyncSession();
        var rumor = new Rumor
        {
            Id = "rumors/aging-" + Guid.NewGuid(),
            Subject = "Old Gossip",
            LastStateChangeDay = 1,
            State = RumorState.Spreading,
            CampaignName = "config-test"
        };
        await session.StoreAsync(rumor);
        await session.SaveChangesAsync();

        var time = new CampaignTime { TotalDaysElapsed = 10 };
        var config = new CampaignConfig { RumorAgingPressureDays = 20 };
        var contributor = new AgingRumorPressureContributor();
        var ctx = new PressureContext("config-test", time, config, session, ActiveRumors: [rumor]);

        var pressures = (await contributor.EvaluateAsync(ctx)).ToList();
        Assert.Empty(pressures);

        var strictConfig = new CampaignConfig { RumorAgingPressureDays = 5 };
        pressures = (await contributor.EvaluateAsync(new PressureContext("config-test", time, strictConfig, session,
            ActiveRumors: [rumor]))).ToList();
        Assert.Single(pressures);
        Assert.Contains("Old Gossip", pressures[0].Text);
    }
}