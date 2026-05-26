using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Session;

namespace CampaignVault.Tools;

internal static class ToolErrors
{
    public const string NotFound = "NotFound";
    public const string StateDrift = "StateDriftConflict";
    public const string InternalError = "InternalError";
}

[McpServerToolType]
public class CampaignTools(CampaignRepository repository)
{
    private async Task<ToolResult<T>> ExecuteAsync<T>(Func<IAsyncDocumentSession, Task<ToolResult<T>>> action)
    {
        using var session = repository.OpenSession();
        ToolResult<T> result;

        try
        {
            result = await action(session);
        }
        catch (ConcurrencyException)
        {
            return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "State changed mid-operation. Re-fetch and retry.");
        }
        catch (Exception ex)
        {
            return new ToolResult<T>(false, Error: ToolErrors.InternalError, Summary: ex.Message);
        }

        if (!result.Success) return result;

        try
        {
            await session.SaveChangesAsync();
        }
        catch (ConcurrencyException)
        {
            return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "Commit failed due to concurrent modification. Re-fetch and retry.");
        }

        return result;
    }

    [McpServerTool]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the current time, active rumors, recent history, and current party location in one view.")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is.")] string partyLocationId)
    {
        return ExecuteAsync(async session => {
            var time = await repository.GetTimeAsync(session);
            
            // Widen rumor search for kickoff
            var spreading = await repository.QueryRumorsAsync(session, null, null, RumorState.Spreading, 3);
            var peak = await repository.QueryRumorsAsync(session, null, null, RumorState.Peak, 3);
            var rumors = peak.Concat(spreading).ToList();

            var events = await repository.QueryEventsAsync(session, null, null, 5);
            var location = await repository.GetLocationAsync(session, partyLocationId);
            
            var pressure = new List<string>();
            foreach (var r in rumors.Where(r => time.TotalDaysElapsed - r.LastStateChangeDay > 5))
            {
                pressure.Add($"Rumor '{r.Subject}' has been spreading for {time.TotalDaysElapsed - r.LastStateChangeDay} days without resolution.");
            }

            var agingEvents = await repository.QueryEventsAsync(session, null, "unresolved", 5);
            foreach (var e in agingEvents)
            {
                pressure.Add($"Unresolved thread: '{e.Summary}' ({time.TotalDaysElapsed - e.DayLogged} days old).");
            }

            LocationSummary? locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var view = new WorldStateView(time, rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), events, locSummary, pressure);
            return new ToolResult<WorldStateView>(true, view, "Authoritative world state retrieved for session start.");
        });
    }

    [McpServerTool]
    [Description("EXPLORATION TOOL: Call this whenever entering a new room, building, or region. Returns the location description, present NPCs (with behavioral summaries), visible items, and local rumors.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId)
    {
        return ExecuteAsync(async session => {
            var scene = await repository.GetSceneAsync(session, locationId);
            return new ToolResult<SceneView>(true, scene, $"Scene details for {locationId} retrieved.");
        });
    }

    [McpServerTool]
    [Description("UNIVERSAL WRITE TOOL: ALWAYS call this at the end of a combat, conversation, or discovery to atomically update the world. Accepts a batch of changes (HP, Items, Events, Rumors, Relationships).")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Array of world changes to apply.")] WorldChange[] changes,
        [Description("Narrative summary of what happened (for the log).")] string narrative)
    {
        if (changes == null || changes.Length == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));
        }

        return ExecuteAsync(async session => {
            var result = await repository.CommitChangesAsync(session, changes);
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Type = "scene-commit" });
            return new ToolResult<CommitResult>(true, result, $"World updated with {changes.Length} changes.");
        });
    }

    [McpServerTool]
    [Description("TIME PASSAGE: Call this for travel, long rests, or downtime. Fast-forwards the world clock and runs background simulations (rumor decay, NPC needs). Returns narrative updates on what changed while the party was away.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Number of days to skip.")] int days, 
        [Description("The resulting time of day.")] TimeOfDay timeOfDay,
        [Description("Summary of the rest or travel activity.")] string narrative)
    {
        if (days < 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest", Summary: "Cannot advance a negative number of days."));
        }

        return ExecuteAsync(async session => {
            var result = await repository.AdvanceWorldAsync(session, days, timeOfDay);
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Type = "timeskip" });
            return new ToolResult<AdvanceResult>(true, result, $"Advanced {days} days. {result.SimulatorEvents.Count} simulation events triggered.");
        });
    }

    [McpServerTool]
    [Description("ROLEPLAY TOOL: Deep dive into an NPC's psychological state. Returns their relationships, goals, fears, knowledge, and current emotional mood.")]
    public Task<ToolResult<NpcContextView>> GetNpcContext(string characterId)
    {
        return ExecuteAsync(async session => {
            var npc = await repository.GetCharacterAsync(session, characterId);
            if (npc == null) return new ToolResult<NpcContextView>(false, Error: "NotFound");

            // Query events involving the NPC, then explicitly sanitize Details using the central helper
            // so complex JsonElement values never leak to the LLM (was missing before).
            var npcEvents = await session.Advanced.AsyncDocumentQuery<Event>()
                .WhereEquals("Involved", characterId)
                .OrderByDescending(x => x.Timestamp)
                .Take(10)
                .ToListAsync();

            foreach (var ev in npcEvents)
            {
                repository.SanitizeEvent(ev);   // reuses the central sanitization logic
            }

            var context = new NpcContextView
            {
                Character = npc,
                Mind = npc.Mind,
                RecentInteractions = npcEvents
            };

            return new ToolResult<NpcContextView>(true, context, $"Psychological context for {npc.Name} retrieved.");
        });
    }

    [McpServerTool]
    [Description("UNIFIED SEARCH: Search across Lore, Characters, Locations, and Items in one shot. Use this when searching for anything by name or keyword.")]
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(string query)
    {
        return ExecuteAsync(async session => {
            var results = await repository.UnifiedSearchAsync(session, query);
            return new ToolResult<IEnumerable<object>>(true, results, $"Found {results.Count()} matches.");
        });
    }

    [McpServerTool]
    [Description("HISTORY RECALL: Semantic search over past events. Use this to remember 'what happened last time we were here' or recall specific plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(string query, int limit = 5)
    {
        return ExecuteAsync(async session => {
            var results = await repository.QueryEventsAsync(session, query, null, limit);
            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events.");
        });
    }

    // --- Configuration Tools (Genuine state setup) ---

    [McpServerTool]
    [Description("Directly create or overwrite a character/NPC. For updates, use 'Commit'.")]
    public Task<ToolResult<Character>> UpsertCharacter(Character c) => ExecuteAsync(async s => { await repository.UpsertCharacterAsync(s, c); return new ToolResult<Character>(true, c); });

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Register a new location on the world map. For first-time setup only.")]
    public Task<ToolResult<Location>> UpsertLocation(Location l) => ExecuteAsync(async s => { await repository.UpsertLocationAsync(s, l); return new ToolResult<Location>(true, l); });

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Use SearchWorld to check it doesn't already exist before calling this.")]
    public Task<ToolResult<Lore>> UpsertLore(Lore l) => ExecuteAsync(async s => { await repository.UpsertLoreAsync(s, l); return new ToolResult<Lore>(true, l); });
}

public class NpcContextView
{
    public Character Character { get; set; } = default!;
    public NpcMind Mind { get; set; } = default!;
    public IEnumerable<Event> RecentInteractions { get; set; } = [];
}
