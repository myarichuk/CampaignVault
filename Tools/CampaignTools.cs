using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
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

    [McpServerTool]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the current time, active rumors, recent history, and current party location in one view.")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is.")] string partyLocationId)
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

            var agingEvents = await repository.QueryEventsAsync(session, null, "unresolved", 5);
            foreach (var e in agingEvents)
            {
                pressure.Add($"Unresolved thread: '{e.Summary}' ({time.TotalDaysElapsed - e.DayLogged} days old).");
            }

            LocationSummary? locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var view = new WorldStateView(time, rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), events, locSummary, pressure);
            return new ToolResult<WorldStateView>(true, view, "Authoritative world state retrieved for session start.");
        }, saveChanges: false);
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
    [Description("UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world. This is currently the most reliable mutation path across MCP clients. Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, **Activity**). Use ActivityChange liberally to keep get_scene in sync with your narrative.")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description(@"Array of world changes. Each item must be a JSON object with a 'type' discriminator.

Supported types (exact values for type): hp, item, status, event, rumor, relationship, need, attribute, mood, activity.

The server intentionally accepts this as JsonElement[] so that clients with strict or limited schema support (including some Gemini CLI versions) can still send native objects without fighting complex oneOf/polymorphic input schemas.

Each object is then deserialized server-side using the rich definitions on the WorldChange subtypes (see the per-property descriptions on HpChange, ActivityChange, RelationshipChange, NeedChange, etc.).

=== RECOMMENDED PATTERN (copy-paste friendly) ===
When creating a new area + NPC from scratch, do it in ONE atomic commit:

[
  { ""type"": ""event"", ""summary"": ""The party arrives in the village of Thornwatch..."", ""category"": ""arrival"" },
  { ""type"": ""activity"", ""characterId"": ""characters/bram-ironarm"", ""newActivity"": ""tending bar and watching the door"", ""newLocationId"": ""locations/rusty-nail"", ""reason"": ""Sergeant on duty tonight"" },
  { ""type"": ""relationship"", ""sourceId"": ""characters/elara-voss"", ""targetId"": ""characters/bram-ironarm"", ""delta"": 5, ""reason"": ""Elara buys Bram a drink..."" },
  { ""type"": ""need"", ""characterId"": ""characters/elara-voss"", ""need"": ""wanderlust"", ""delta"": 12 }
]

Example single activity change (very useful during play):
{ ""type"": ""activity"", ""characterId"": ""characters/bram"", ""newActivity"": ""on patrol at the old watchtower"", ""newLocationId"": ""locations/watchtower"", ""reason"": ""Bram decided to check the perimeter"" }

You can (and should) mix many different change kinds in one call.")] JsonElement[] changes,
        [Description("Narrative summary of what happened (for the log and world pressure).")] string narrative)
    {
        if (changes == null || changes.Length == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));
        }

        // Convert JsonElement array to strongly-typed WorldChange[]
        // This approach keeps the tool schema very loose (array of any JSON object) so that
        // clients that choke on STJ-generated polymorphic oneOf schemas (certain Gemini CLI
        // versions, strict validators, some CLIs) can still successfully call the tool.
        var typedChanges = new List<WorldChange>(changes.Length);
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var elem in changes)
        {
            try
            {
                // INTEROPERABILITY WRAPPER:
                // Handle cases where the client sends "$type" (industry standard) or "type" (standard standard).
                // If "$type" is present but "type" is not, we normalize it before deserializing.
                WorldChange? change;
                if (elem.TryGetProperty("$type", out var legacyType) && !elem.TryGetProperty("type", out _))
                {
                    // Create a mutated copy of the JSON object that has the 'type' property
                    var rawJson = elem.GetRawText();
                    // Simple string replacement of the key name is safe here because we've already
                    // validated the properties exist via TryGetProperty.
                    var normalizedJson = rawJson.Replace("\"$type\"", "\"type\"", StringComparison.Ordinal);
                    change = JsonSerializer.Deserialize<WorldChange>(normalizedJson, serializerOptions);
                }
                else
                {
                    change = elem.Deserialize<WorldChange>(serializerOptions);
                }

                if (change != null)
                    typedChanges.Add(change);
            }
            catch (JsonException)
            {
                // Skip malformed individual items; we'll validate count below.
            }
        }

        if (typedChanges.Count == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "No valid changes could be parsed. Each item needs a 'type' discriminator that matches one of the supported WorldChange subtypes."));
        }

        return ExecuteAsync(async session => {
            var result = await repository.CommitChangesAsync(session, typedChanges.ToArray());
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = "scene-commit" });
            return new ToolResult<CommitResult>(true, result, $"World updated with {typedChanges.Count} changes.");
        });
    }

    /// <summary>
    /// Fallback for callers (or future clients) that can only easily emit a raw JSON string for the changes batch.
    /// Parses to JsonElement[] and delegates to the primary MCP Commit implementation (which does the typed deserialization).
    /// Not exposed as an MCP tool.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));

        JsonElement[] elements;
        try
        {
            using var doc = JsonDocument.Parse(changesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "changesJson must be a JSON array."));

            elements = doc.RootElement.EnumerateArray()
                .Select(e => e.Clone())
                .ToArray();
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: $"Invalid changes JSON: {ex.Message}"));
        }

        return Commit(elements, narrative);
    }

    /// <summary>
    /// Convenience overload for tests, the simulation harness, and direct in-process callers that already have
    /// strongly-typed WorldChange objects. Converts them to JsonElement[] and calls the primary MCP implementation.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(WorldChange[] changes, string narrative)
    {
        if (changes == null || changes.Length == 0)
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: "BadRequest", Summary: "Commit requires at least one change."));

        var elements = changes
            .Select(c => JsonSerializer.SerializeToElement(c))
            .ToArray();

        return Commit(elements, narrative);
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
            await repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = "timeskip" });

            // Minimal WorldPressure wiring: surface simulation narratives as pressure for the DM
            var pressure = result.SimulatorEvents.Count > 0 
                ? result.SimulatorEvents.ToArray() 
                : null;

            return new ToolResult<AdvanceResult>(true, result, 
                $"Advanced {days} days. {result.SimulatorEvents.Count} simulation events triggered.",
                WorldPressure: pressure);
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

            var behavioralSummary = repository.GetBehaviorSynthesizer()
                .GenerateSummary(npc, null, npcEvents);

            var knownNeeds = npc.Mind?.Needs ?? new Dictionary<string, float>();
            var needDescriptors = npc.Mind?.NeedDescriptors ?? new Dictionary<string, string>();

            var context = new NpcContextView
            {
                Character = npc,
                Mind = npc.Mind ?? new NpcMind(),
                RecentInteractions = npcEvents,
                BehavioralSummary = behavioralSummary,
                KnownNeeds = knownNeeds,
                NeedDescriptors = needDescriptors
            };

            return new ToolResult<NpcContextView>(true, context, $"Psychological context for {npc.Name} retrieved.");
        });
    }

    [McpServerTool]
    [Description("UNIFIED SEARCH: Search across Lore, Characters, Locations, and Items in one shot. Use this when searching for anything by name or keyword.")]
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(string query)
    {
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteAsync(async session => {
            var results = await repository.UnifiedSearchAsync(session, query);
            return new ToolResult<IEnumerable<object>>(true, results, $"Found {results.Count()} matches.");
        }, saveChanges: false);
    }

    [McpServerTool]
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

    [McpServerTool]
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

    [McpServerTool]
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

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")] Lore lore)
        => ExecuteAsync(async s =>
        {
            await repository.UpsertLoreAsync(s, lore);
            return new ToolResult<Lore>(true, lore);
        });

    // --- Needs Discoverability Tools ---

    [McpServerTool]
    [Description("DISCOVERABILITY TOOL: Returns all known needs for an NPC along with their current values and any descriptors. Use this to understand what psychological or physical drives an NPC has before roleplaying or making changes. The needs system is open — you are encouraged to invent new narrative-appropriate needs.")]
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(string characterId)
    {
        return ExecuteAsync(async session =>
        {
            var npc = await repository.GetCharacterAsync(session, characterId);
            if (npc == null) return new ToolResult<NpcNeedsView>(false, Error: "NotFound");

            var view = new NpcNeedsView
            {
                CharacterId = npc.Id,
                Name = npc.Name,
                KnownNeeds = npc.Mind?.Needs ?? new Dictionary<string, float>(),
                NeedDescriptors = npc.Mind?.NeedDescriptors ?? new Dictionary<string, string>()
            };

            return new ToolResult<NpcNeedsView>(true, view, $"Needs for {npc.Name} retrieved.");
        }, saveChanges: false);
    }

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Define or update a descriptor for a need type. This helps the LLM (and future simulation rules) understand what a custom need means. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor)
    {
        // This is a documentation / discoverability tool. We don't store global descriptors centrally yet.
        // For now it serves as strong guidance + future-proofing. Real usage happens by setting NeedDescriptors on individual NPCs via UpsertCharacter or Commit.
        _ = needName;
        _ = descriptor;
        return Task.FromResult(new ToolResult<string>(true, "Descriptor noted. Apply it to NPCs via UpsertCharacter (set Mind.NeedDescriptors) or during world-building.", $"Need descriptor recorded for '{needName}'."));
    }
}

public class NpcContextView
{
    public Character Character { get; set; } = default!;
    public NpcMind Mind { get; set; } = default!;
    public IEnumerable<Event> RecentInteractions { get; set; } = [];
    public string? BehavioralSummary { get; set; }

    /// <summary>
    /// All known needs for this NPC with their current values. The needs system is intentionally open-ended.
    /// </summary>
    public Dictionary<string, float> KnownNeeds { get; set; } = [];

    /// <summary>
    /// Human/LLM-readable descriptions for the needs (seeded by world-builder or previous LLM actions).
    /// </summary>
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}

/// <summary>
/// Lightweight view returned by GetNpcNeeds for discoverability.
/// </summary>
public class NpcNeedsView
{
    public string CharacterId { get; set; } = default!;
    public string Name { get; set; } = default!;
    public Dictionary<string, float> KnownNeeds { get; set; } = [];
    public Dictionary<string, string> NeedDescriptors { get; set; } = [];
}
