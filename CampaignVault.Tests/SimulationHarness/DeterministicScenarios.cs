using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents;
using System;
using System.Collections.Generic;
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
        var repo = new CampaignRepository(_store);
        using var session = _store.OpenAsyncSession();
        var tools = new CampaignTools(repo);
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
            Mind = new NpcMind { Needs = new Dictionary<string, float> { ["tiredness"] = 50f } }
        });
        await session.SaveChangesAsync();

        // 1. Kickoff
        var kickoff = await simulator.Kickoff(locId);
        Assert.True(kickoff.Success);
        Assert.Equal(NarrativePhase.Exploration, simulator.CurrentPhase);

        // 2. Explore
        var scene = await simulator.Explore(locId);
        Assert.True(scene.Success);
        Assert.Contains(scene.Data!.PresentNPCs, n => n.Id == npcId);
        Assert.Equal(NarrativePhase.Roleplay, simulator.CurrentPhase);

        // 3. Interact
        var context = await simulator.Interact(npcId);
        Assert.True(context.Success);
        Assert.Equal("Barliman Butterbur", context.Data!.Character.Name);
        Assert.Equal(50f, context.Data!.Mind.Needs["tiredness"]);
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
        var finalTiredness = finalContext.Data!.Mind.Needs["tiredness"];
        
        // Calculation: 50 (start) - 20 (resolve) + 8 (1 day simulation at 0.8 rate) = 38
        Assert.Equal(38f, finalTiredness);
    }
}
