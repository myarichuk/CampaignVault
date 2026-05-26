using CampaignVault.Data;
using CampaignVault.Models;
using CampaignVault.Tools;
using Raven.Client.Documents.Session;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CampaignVault.Tests.SimulationHarness;

public enum NarrativePhase
{
    Kickoff,
    Exploration,
    Roleplay,
    Resolution,
    Downtime
}

public class LlmSimulator(CampaignTools tools, IAsyncDocumentSession session)
{
    public NarrativePhase CurrentPhase { get; private set; } = NarrativePhase.Kickoff;
    public string? CurrentLocationId { get; set; }
    public string? TargetCharacterId { get; set; }
    
    public async Task<ToolResult<WorldStateView>> Kickoff(string locationId)
    {
        CurrentLocationId = locationId;
        var result = await tools.GetWorldState(locationId);
        CurrentPhase = NarrativePhase.Exploration;
        return result;
    }

    public async Task<ToolResult<SceneView>> Explore(string locationId)
    {
        CurrentLocationId = locationId;
        var result = await tools.GetScene(locationId);
        CurrentPhase = NarrativePhase.Roleplay;
        return result;
    }

    public async Task<ToolResult<NpcContextView>> Interact(string characterId)
    {
        TargetCharacterId = characterId;
        var result = await tools.GetNpcContext(characterId);
        CurrentPhase = NarrativePhase.Resolution;
        return result;
    }

    public async Task<ToolResult<CommitResult>> Resolve(WorldChange[] changes, string narrative)
    {
        var result = await tools.Commit(changes, narrative);
        CurrentPhase = NarrativePhase.Downtime;
        return result;
    }

    public async Task<ToolResult<AdvanceResult>> Rest(int days, TimeOfDay timeOfDay, string narrative)
    {
        var result = await tools.AdvanceWorld(days, timeOfDay, narrative);
        CurrentPhase = NarrativePhase.Exploration; // Cycle back to exploration
        return result;
    }

    public async Task SaveChangesAsync()
    {
        await session.SaveChangesAsync();
    }
}
