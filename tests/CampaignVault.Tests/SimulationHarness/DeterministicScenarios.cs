using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CampaignVault.Tests.SimulationHarness;

[Collection("RavenDB")]
public class DeterministicScenarios : IClassFixture<RavenDBFixture>
{
    private readonly IDocumentStore _store;

    public DeterministicScenarios(RavenDBFixture fixture)
    {
        _store = fixture.Store;
    }

    [Fact]
    public async Task Scenario_TheTavernRest_Workflow()
    {
        var engine = new DefaultSimulationEngine(
            new ISimulationRule[] { new NeedsAccumulationRule(), new RumorDecayRule(), new ScheduleEvaluationRule() },
            null);
        var repo = new CampaignRepository(_store, engine, 
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CampaignRepository>.Instance,
            new DefaultBehaviorSynthesizer());
        using var session = _store.OpenAsyncSession();
        var tools = new CampaignTools(repo, new DefaultBehaviorSynthesizer(), new CampaignVault.Rulesets.RulesetResolverSelector(new[] { new CampaignVault.Rulesets.Dnd5eRulesetResolver(new CampaignVault.Data.DefaultRollService()) }));
        var simulator = new LlmSimulator(tools, session);

        // Setup: Location and NPC
        var locId = "locations/tavern-" + Guid.NewGuid();
        var npcId = "npcs/innkeeper-" + Guid.NewGuid();
        
        await repo.UpsertLocationAsync(session, new Location { Id = locId, Name = "The Prancing Pony", Type = LocationType.Building });
        await repo.UpsertCharacterAsync(session, new Character 
        { 
            Id = npcId, 
            Name = "Barliman Butterbur",
            Schedule = new Schedule { DefaultLocationId = locId, Routines = [new Routine { Activity = "Serving", Condition = "Evening", LocationId = locId }] },
            Needs = new NeedsProfile { ActiveNeeds = new Dictionary<string, float> { ["tiredness"] = 50f } }
        });
        await session.SaveChangesAsync();

        // Wait for indexes (with timeout to prevent CI hangs)
        var indexWaitStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - indexWaitStart).TotalSeconds < 10)
        {
            var stats = _store.Maintenance.Send(new Raven.Client.Documents.Operations.GetStatisticsOperation());
            if (stats.Indexes.All(x => x.IsStale == false)) break;
            await Task.Delay(30);
        }
        if ((DateTime.UtcNow - indexWaitStart).TotalSeconds >= 10)
            throw new TimeoutException("Indexes did not become non-stale within 10s");

        // Small time advance so ScheduleEvaluationRule runs and populates CurrentActivity / CurrentLocationId.
        // This exercises the new dynamic presence behavior (Phase 1 goal).
        await simulator.Rest(0, TimeOfDay.Evening, "A few hours pass as the party arrives.");
        await simulator.SaveChangesAsync();

        // 1. Kickoff
        var kickoff = await simulator.Kickoff(locId);
        Assert.True(kickoff.Success);
        Assert.Equal(NarrativePhase.Exploration, simulator.CurrentPhase);

        // 2. Explore — use direct repo call on this session for reliable presence check after simulation
        // (tool path opens new sessions; direct call is more deterministic in tests)
        var directScene = await repo.GetSceneAsync(session, locId);
        Assert.Contains(directScene.PresentNPCs, n => n.Id == npcId);

        var scene = await simulator.Explore(locId);
        Assert.True(scene.Success);
        Assert.Equal(NarrativePhase.Roleplay, simulator.CurrentPhase);

        // 3. Interact
        var context = await simulator.Interact(npcId);
        Assert.True(context.Success);
        Assert.Equal("Barliman Butterbur", context.Data!.Character.Name);
        Assert.Equal(50f, context.Data!.Needs.ActiveNeeds["tiredness"]);
        Assert.Equal(NarrativePhase.Resolution, simulator.CurrentPhase);

        // 4. Resolve (Reduce tiredness narratively)
        var resolve = await simulator.Resolve([
            new NeedChange { CharacterId = npcId, Need = "tiredness", Delta = -20f }
        ], "The party helps Barliman with the dishes, letting him sit down for a bit.");
        Assert.True(resolve.Success);
        await simulator.SaveChangesAsync();
        Assert.Equal(NarrativePhase.Downtime, simulator.CurrentPhase);

        // 5. Rest (Time passes)
        var rest = await simulator.Rest(1, TimeOfDay.Morning, "A long night's sleep at the inn.");
        Assert.True(rest.Success);
        await simulator.SaveChangesAsync();
        Assert.Equal(NarrativePhase.Exploration, simulator.CurrentPhase);

        // Final Assertion: Verify state survived and simulation applied
        var finalContext = await simulator.Interact(npcId);
        var finalTiredness = finalContext.Data!.Needs.ActiveNeeds["tiredness"];
        
        // Calculation: 50 (start) - 20 (resolve) + 8 (1 day simulation at 0.8 rate) = 38
        Assert.Equal(38f, finalTiredness);
    }
}
