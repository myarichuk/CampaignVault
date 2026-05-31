using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Raven.Client.Exceptions;
using Raven.Client.Documents.Session;
// ReSharper disable UnusedMember.Global

namespace CampaignVault.Tools;

internal static class ToolErrors
{
    public const string NotFound = "NotFound";
    public const string StateDrift = "StateDriftConflict";
    public const string InternalError = "InternalError";
}

[McpServerToolType]
public class CampaignTools(CampaignRepository repository, INpcBehaviorSynthesizer behaviorSynthesizer)
{
    private async Task<ToolResult<T>> ExecuteAsync<T>(Func<IAsyncDocumentSession, Task<ToolResult<T>>> action, bool saveChanges = true)
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

        if (saveChanges)
        {
            try
            {
                await session.SaveChangesAsync();
            }
            catch (ConcurrencyException)
            {
                return new ToolResult<T>(false, Error: ToolErrors.StateDrift, Summary: "Commit failed due to concurrent modification. Re-fetch and retry.");
            }
        }

        // Final sanitizing step on every tool response.
        // This guarantees that even if a polluted entity reached this point (legacy data,
        // unsanitized query path, etc.), nothing containing a live or dead JsonElement
        // will be serialized by the MCP layer's System.Text.Json when sending the response.
        repository.SanitizeForToolResponse(result.Data);

        return result;
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the current time, active rumors, recent history, and current party location in one view.")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is (string type)")] string partyLocationId)
    {
        // Pure read: skip SaveChanges to avoid unnecessary write transactions and reduce surface for
        // RavenDB "active async task" / serialization issues during disposal.
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

            var agingEvents = await repository.QueryEventsAsync(session, null, EventCategory.Unresolved, 5);
            foreach (var e in agingEvents)
            {
                pressure.Add($"Unresolved thread: '{e.Summary}' ({time.TotalDaysElapsed - e.DayLogged} days old).");
            }

            var locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var view = new WorldStateView(time, rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), events, locSummary, pressure);
            return new ToolResult<WorldStateView>(true, view, "Authoritative world state retrieved for session start.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("EXPLORATION TOOL: Call this whenever entering a new room, building, or region. Returns the location description, present NPCs (with behavioral summaries), visible items, and local rumors.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId)
    {
        return ExecuteAsync(async session => {
            var scene = await repository.GetSceneAsync(session, locationId);
            return new ToolResult<SceneView>(true, scene, $"Scene details for {locationId} retrieved.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description("UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world. Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove). Use ActivityChange liberally to keep get_scene in sync with your narrative.")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description(@"Array of world changes. Each item must be a JSON object with a '$type' discriminator.

Supported types (exact values for $type): hp, item, status, statusremove, event, rumor, relationship, need, attribute, mood, activity.

=== RECOMMENDED PATTERN (copy-paste friendly) ===
When creating a new area + NPC from scratch, do it in ONE atomic commit:

[
  { ""$type"": ""event"", ""summary"": ""The party arrives in the village of Thornwatch..."", ""category"": ""Arrival"" },
  { ""$type"": ""activity"", ""characterId"": ""characters/bram-ironarm"", ""newActivity"": ""tending bar and watching the door"", ""newLocationId"": ""locations/rusty-nail"", ""reason"": ""Sergeant on duty tonight"" },
  { ""$type"": ""relationship"", ""sourceId"": ""characters/elara-voss"", ""targetId"": ""characters/bram-ironarm"", ""delta"": 5, ""reason"": ""Elara buys Bram a drink..."" },
  { ""$type"": ""need"", ""characterId"": ""characters/elara-voss"", ""need"": ""wanderlust"", ""delta"": 12 }
]

You can (and should) mix many different change kinds in one call.")] WorldChange[] changes,
        [Description("Narrative summary of what happened (for the log and world pressure).")] string narrative)
    {
        if (changes.Length == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));
        }

        return ExecuteAsync(async session => {
            var result = await repository.StageChangesAsync(session, changes);
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.SceneCommit });
            var msg = $"World updated with {changes.Length} changes.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
    }

    /// <summary>
    /// Fallback for callers (or future clients) that can only easily emit a raw JSON string for the changes batch.
    /// Parses to WorldChange[] and delegates to the primary MCP Commit implementation.
    /// Not exposed as an MCP tool.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));

        WorldChange[] elements;
        try
        {
            var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowOutOfOrderMetadataProperties = true };
            elements = JsonSerializer.Deserialize<WorldChange[]>(changesJson, serializerOptions) ?? [];
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: $"Invalid changes JSON: {ex.Message}"));
        }

        return Commit(elements, narrative);
    }

    [McpServerTool(UseStructuredContent = true)]
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
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.Timeskip });

            // Minimal WorldPressure wiring: surface simulation narratives as pressure for the DM
            var pressure = result.SimulatorEvents.Count > 0 
                ? result.SimulatorEvents.ToArray() 
                : null;

            return new ToolResult<AdvanceResult>(true, result, 
                $"Advanced {days} days. {result.SimulatorEvents.Count} simulation events triggered.",
                WorldPressure: pressure);
        });
    }

    [McpServerTool(UseStructuredContent = true)]
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

            var behavioralSummary = behaviorSynthesizer.GenerateSummary(npc, null, npcEvents);

            var knownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>();
            // Merge global + per-NPC descriptors (per-NPC wins) for full context
            var globalDescriptors = await repository.GetGlobalNeedDescriptorsAsync(session);
            var npcDescriptors = npc.Needs?.NeedDescriptors ?? new Dictionary<string, string>();
            var mergedDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcDescriptors)
            {
                mergedDescriptors[kv.Key] = kv.Value;
            }

            var context = new NpcContextView
            {
                Character = npc,
                Psychology = npc.Psychology ?? new PsychologyProfile(),
                Social = npc.Social ?? new SocialProfile(),
                Needs = npc.Needs ?? new NeedsProfile(),
                SystemStats = npc.SystemStats ?? new SystemExtension(),
                RecentInteractions = npcEvents,
                BehavioralSummary = behavioralSummary,
                KnownNeeds = knownNeeds,
                NeedDescriptors = mergedDescriptors
            };

            return new ToolResult<NpcContextView>(true, context, $"Psychological context for {npc.Name} retrieved.");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("UNIFIED SEARCH: Search across Lore, Characters, Locations, and Items in one shot. Use this when searching for anything by name or keyword.")]
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(string query)
    {
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteAsync(async session => {
            var results = await repository.UnifiedSearchAsync(session, query);
            return new ToolResult<IEnumerable<object>>(true, results, $"Found {results.Count()} matches.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("HISTORY RECALL: Semantic search over past events. Use this to remember 'what happened last time we were here' or recall specific plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(string query, int limit = 5)
    {
        return ExecuteAsync(async session => {
            var results = await repository.QueryEventsAsync(session, query, null, limit);
            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events.");
        }, saveChanges: false);
    }

    // --- Configuration Tools (Genuine state setup) ---

    // Strongly-typed versions are preferred for schema quality and LLM understanding.
    // However, as of late May 2026, Grok Web's client still calls these tools using the
    // original legacy parameter names from the first version of this server ("c" and "l").
    // This is almost certainly a caching / non-dynamic tool schema issue on their side.
    // The descriptions below document this quirk so the LLM knows what's happening.

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

STRONGLY encouraged to populate:
- Mind.Wants, Mind.Fears, Mind.Knows
- Detailed backstory in Notes
- Schedule + Routines + StateModifiers
- Mind.NeedDescriptors (human-readable explanations for any custom needs)
- Equipment via Items (set HolderId to the character)

This is the best opportunity to create deep, simulatable NPCs.

**Note for Grok Web users (as of May 2026):** Grok Web's client may still send this tool using the legacy parameter name 'c' instead of 'character'. If you get a 'missing required parameter' error, try sending the Character object under the key 'c'.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The full Character object to create or replace. Strongly typed.")] Character character)
        => ExecuteAsync(async s =>
        {
            await repository.UpsertCharacterAsync(s, character);
            return new ToolResult<Character>(true, character);
        });

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Register a new location on the world map. For first-time setup only.

Define hierarchical locations with exits, parent relationships, and rich metadata.

**Note for Grok Web users (as of May 2026):** Grok Web's client may still send this tool using the legacy parameter name 'l' instead of 'location'. If you get a 'missing required parameter' error, try sending the Location object under the key 'l'.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The full Location object to create or replace. Strongly typed.")] Location location)
        => ExecuteAsync(async s =>
        {
            await repository.UpsertLocationAsync(s, location);
            return new ToolResult<Location>(true, location);
        });

    [McpServerTool(UseStructuredContent = true)]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")] Lore lore)
        => ExecuteAsync(async s =>
        {
            await repository.UpsertLoreAsync(s, lore);
            return new ToolResult<Lore>(true, lore);
        });

    // --- Needs Discoverability Tools ---

    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Returns all known needs for an NPC along with their current values and any descriptors. Use this to understand what psychological or physical drives an NPC has before roleplaying or making changes. The needs system is open — you are encouraged to invent new narrative-appropriate needs.")]
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId)
    {
        return ExecuteAsync(async session =>
        {
            var npc = await repository.GetCharacterAsync(session, characterId);
            if (npc == null) return new ToolResult<NpcNeedsView>(false, Error: "NotFound");

            // Merge global descriptors (from DefineNeedDescriptor) with per-NPC ones.
            // Per-NPC descriptors take precedence on conflicts.
            var globalDescriptors = await repository.GetGlobalNeedDescriptorsAsync(session);
            var npcDescriptors = npc.Needs?.NeedDescriptors ?? new Dictionary<string, string>();
            var mergedDescriptors = new Dictionary<string, string>(globalDescriptors, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in npcDescriptors)
            {
                mergedDescriptors[kv.Key] = kv.Value;
            }

            var view = new NpcNeedsView
            {
                CharacterId = npc.Id,
                Name = npc.Name,
                KnownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>(),
                NeedDescriptors = mergedDescriptors
            };

            return new ToolResult<NpcNeedsView>(true, view, $"Needs for {npc.Name} retrieved.");
        }, saveChanges: false);
    }

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Define or update a descriptor for a need type. Stored globally and automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list all globally defined ones. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor)
    {
        if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
            return Task.FromResult(new ToolResult<string>(false, Error: "BadRequest", Summary: "needName and descriptor are required."));

        return ExecuteAsync(async session =>
        {
            await repository.SetNeedDescriptorAsync(session, needName, descriptor);
            return new ToolResult<string>(true, $"Descriptor for '{needName}' stored globally.", $"Global descriptor persisted for '{needName}'. It will now appear (merged) in get_need_descriptors, get_npc_needs, get_npc_context, and get_scene.");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Lists all globally defined need descriptors (created via define_need_descriptor). Use this to see what shared descriptors exist before assigning them to specific NPCs.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors()
    {
        return ExecuteAsync(async session =>
        {
            var descriptors = await repository.GetGlobalNeedDescriptorsAsync(session);
            return new ToolResult<Dictionary<string, string>>(true, descriptors, 
                descriptors.Count > 0 
                    ? $"Retrieved {descriptors.Count} global need descriptors."
                    : "No global need descriptors have been defined yet. Use define_need_descriptor to create some.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("RULES CONFIG TOOL: Get the current campaign configuration including the active ruleset system and any active house-rules/system options.")]
    public Task<ToolResult<CampaignConfig>> GetConfig()
    {
        return ExecuteAsync(async session =>
        {
            var config = await repository.GetCampaignConfigAsync(session);
            return new ToolResult<CampaignConfig>(true, config, "Campaign configuration retrieved.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("RULES CONFIG TOOL: Set the active ruleset system (e.g. Dnd5e, Pathfinder2e, Fallout2d20) and optional system-specific configuration options.")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")] RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")] Dictionary<string, string>? systemOptions = null)
    {
        return ExecuteAsync(async session =>
        {
            var config = await repository.GetCampaignConfigAsync(session);
            config.ActiveSystem = activeSystem;
            config.SystemOptions = systemOptions ?? [];
            await repository.UpsertCampaignConfigAsync(session, config);
            return new ToolResult<CampaignConfig>(true, config, $"Active ruleset system updated to '{activeSystem}'.");
        });
    }
}

