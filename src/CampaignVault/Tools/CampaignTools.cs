using CampaignVault.Data;
using CampaignVault.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Raven.Client.Exceptions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using CampaignVault.Rulesets;
using System.Threading.RateLimiting;
// ReSharper disable UnusedMember.Global

namespace CampaignVault.Tools;

internal static class ToolErrors
{
    public const string NotFound = "NotFound";
    public const string StateDrift = "StateDriftConflict";
    public const string InternalError = "InternalError";
    public const string RateLimitExceeded = "RateLimitExceeded";
    public const string BadRequest = "BadRequest";
}

[McpServerToolType]
public class CampaignTools
{
    private readonly CampaignRepository _repository;
    private readonly INpcBehaviorSynthesizer _behaviorSynthesizer;
    private readonly IRulesetResolverSelector _rulesetSelector;
    private readonly CampaignDocumentKeys _keys;
    private readonly ICurrentCampaignContext _currentCampaign;

    private static readonly RateLimiter _commitRateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 10000, // Large enough for parallel xUnit test suites, still guards against infinite loops
        TokensPerPeriod = 1000, 
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        AutoReplenishment = true
    });

    // Modern / DI constructor (all services provided)
    public CampaignTools(
        CampaignRepository repository,
        INpcBehaviorSynthesizer behaviorSynthesizer,
        IRulesetResolverSelector rulesetSelector,
        CampaignDocumentKeys keys,
        ICurrentCampaignContext currentCampaign)
    {
        _repository = repository;
        _behaviorSynthesizer = behaviorSynthesizer;
        _rulesetSelector = rulesetSelector;
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign ?? new CurrentCampaignContext();
    }

    private string EffectiveCampaign(string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName;
        return string.IsNullOrWhiteSpace(_currentCampaign.CurrentCampaignName) ? "default" : _currentCampaign.CurrentCampaignName;
    }

    /// <summary>
    /// Central helper for campaign lifecycle. Ensures both the Campaign meta document
    /// and its corresponding CampaignConfig exist. Used by CreateCampaign, SelectCampaign,
    /// and SetActiveSystem to keep meta creation logic in one place and enforce lock-in semantics.
    /// </summary>
    private async Task<Campaign> GetOrCreateCampaignMetaAsync(IAsyncDocumentSession session, string normalizedName, RulesetSystem defaultSystem, string? displayName = null, bool forceLock = false)
    {
        var campaignId = _keys.Meta(normalizedName);
        var campaign = await session.LoadAsync<Campaign>(campaignId);
        if (campaign == null)
        {
            campaign = new Campaign
            {
                Id = campaignId,
                Name = normalizedName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedName : displayName,
                System = defaultSystem,
                IsSystemLocked = forceLock
            };
            await session.StoreAsync(campaign, campaignId);

            var configId = _keys.Config(normalizedName);
            var config = await session.LoadAsync<CampaignConfig>(configId);
            if (config == null)
            {
                config = new CampaignConfig
                {
                    Id = configId,
                    ActiveSystem = defaultSystem
                };
                await session.StoreAsync(config, configId);
            }
        }
        return campaign;
    }

    private async Task<ToolResult<T>> ExecuteAsync<T>(Func<IAsyncDocumentSession, Task<ToolResult<T>>> action, bool saveChanges = true)
    {
        using var session = _repository.OpenSession();
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
        _repository.SanitizeForToolResponse(result.Data);

        return result;
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the current time, active rumors, recent history, and current party location in one view. Respects the currently selected campaign (via select_campaign).")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is (string type)")] string partyLocationId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        // Pure read: skip SaveChanges to avoid unnecessary write transactions and reduce surface for
        // RavenDB "active async task" / serialization issues during disposal.
        return ExecuteAsync(async session => {
            var time = await _repository.GetTimeAsync(session, effective);
            
            // Widen rumor search for kickoff
            var spreading = await _repository.QueryRumorsAsync(session, null, null, RumorState.Spreading, 3, effective);
            var peak = await _repository.QueryRumorsAsync(session, null, null, RumorState.Peak, 3, effective);
            var rumors = peak.Concat(spreading).ToList();

            var events = await _repository.QueryEventsAsync(session, null, null, 5, effective);
            var location = await _repository.GetLocationAsync(session, partyLocationId, effective);
            
            var pressure = new List<string>();
            foreach (var r in rumors.Where(r => time.TotalDaysElapsed - r.LastStateChangeDay > 5))
            {
                pressure.Add($"Rumor '{r.Subject}' has been spreading for {time.TotalDaysElapsed - r.LastStateChangeDay} days without resolution.");
            }

            var agingEvents = await _repository.QueryEventsAsync(session, null, EventCategory.Unresolved, 5, effective);
            foreach (var e in agingEvents)
            {
                pressure.Add($"Unresolved thread: '{e.Summary}' ({time.TotalDaysElapsed - e.DayLogged} days old).");
            }
            
            var charPressure = await _repository.GetCharacterPressureAsync(session, effective);
            pressure.AddRange(charPressure);

            var locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var view = new WorldStateView(time, rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), events, locSummary, pressure);
            return new ToolResult<WorldStateView>(true, view, $"Authoritative world state retrieved for session start (campaign: {effective}).");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("EXPLORATION TOOL: Call this whenever entering a new room, building, or region. Returns the location description, present NPCs (with behavioral summaries), visible items, and local rumors. Respects the currently selected campaign.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var scene = await _repository.GetSceneAsync(session, locationId, effective);
            return new ToolResult<SceneView>(true, scene, $"Scene details for {locationId} (campaign: {effective}) retrieved.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(@"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world. 
Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove, ruleset_action). 
Use ActivityChange liberally to keep get_scene in sync with your narrative. Respects the currently selected campaign.

Supported types for $type: hp, item, status, statusremove, event, rumor, relationship, need, attribute, mood, activity, ruleset_action.

=== RECOMMENDED PATTERNS (copy-paste friendly) ===

1) Basic Narrative Update:
[
  { ""$type"": ""event"", ""category"": ""Narrative"", ""summary"": ""The party discovered the hidden door."" },
  { ""$type"": ""activity"", ""characterId"": ""chars/guard1"", ""newLocationId"": ""locations/cellar"", ""newActivity"": ""Searching the cellar"" }
]

2) Combat & Mechanics (ruleset_action):
Use ruleset_action to trigger attacks or skill checks. The correct math and properties depend on the ActiveSystem.
D&D 5e Example:
{ ""$type"": ""ruleset_action"", ""actorId"": ""bob"", ""targetIds"": [""goblin1""], ""actionType"": ""Attack"", ""parameters"": { ""bonus"": ""5"", ""damageDice"": ""1d8+3"" } }

Pathfinder 2e Example (Strike):
{ ""$type"": ""ruleset_action"", ""actorId"": ""bob"", ""targetIds"": [""goblin1""], ""actionType"": ""Strike"", ""parameters"": { ""bonus"": ""7"", ""damageDice"": ""1d8+4"", ""mapPenalty"": ""0"" } }

Fallout 2d20 Example (Skill Test):
{ ""$type"": ""ruleset_action"", ""actorId"": ""bob"", ""actionType"": ""SkillTest"", ""parameters"": { ""target"": ""12"", ""complicationRange"": ""19"" } }")]
    public Task<ToolResult<CommitResult>> Commit(
        [Description("Array of world changes. Each item must be a JSON object with a '$type' discriminator.")] WorldChange[] changes,
        [Description("Narrative summary of what happened (for the log and world pressure).")] string narrative,
        [Description("Optional campaign name. Falls back to currently selected campaign.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);

        if (changes.Length == 0)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.BadRequest, Summary: "Commit requires at least one change."));
        }

        if (changes.Length > 50)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: $"Commit rejected: Too many changes in a single batch ({changes.Length}). Maximum allowed is 50."));
        }

        if (!_commitRateLimiter.AttemptAcquire().IsAcquired)
        {
            return Task.FromResult(new ToolResult<CommitResult>(false, Error: ToolErrors.RateLimitExceeded, Summary: "Commit rate limit exceeded. Please wait a few seconds before making more world changes."));
        }

        return ExecuteAsync(async session => {
            var result = await _repository.StageChangesAsync(session, changes, effective);
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.SceneCommit });
            var msg = $"World updated with {changes.Length} changes.";
            return new ToolResult<CommitResult>(true, result, msg);
        });
    }

    /// <summary>
    /// Fallback for callers (or future clients) that can only easily emit a raw JSON string for the changes batch.
    /// Parses to WorldChange[] and delegates to the primary MCP Commit implementation.
    /// Not exposed as an MCP tool.
    /// </summary>
    public Task<ToolResult<CommitResult>> Commit(string changesJson, string narrative, string? campaignName = null)
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

        return Commit(elements, narrative, campaignName); // respects context + explicit override
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("TIME PASSAGE: Call this for travel, long rests, or downtime. Fast-forwards the world clock and runs background simulations (rumor decay, NPC needs). Returns narrative updates on what changed while the party was away. Respects the currently selected campaign.")]
    public Task<ToolResult<AdvanceResult>> AdvanceWorld(
        [Description("Number of days to skip.")] int days, 
        [Description("The resulting time of day.")] TimeOfDay timeOfDay,
        [Description("Summary of the rest or travel activity.")] string narrative,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        if (days < 0)
        {
            return Task.FromResult(new ToolResult<AdvanceResult>(false, Error: "BadRequest", Summary: "Cannot advance a negative number of days."));
        }

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var result = await _repository.AdvanceWorldAsync(session, days, timeOfDay, effective);
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), Summary = narrative, Category = EventCategory.Timeskip });

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
    [Description("ROLEPLAY TOOL: Deep dive into an NPC's psychological state. Returns their relationships, goals, fears, knowledge, and current emotional mood. Respects the currently selected campaign for need descriptors etc.")]
    public Task<ToolResult<NpcContextView>> GetNpcContext(
        string characterId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
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
                _repository.SanitizeEvent(ev);   // reuses the central sanitization logic
            }

            var behavioralSummary = _behaviorSynthesizer.GenerateSummary(npc, null, npcEvents);

            var knownNeeds = npc.Needs?.ActiveNeeds ?? new Dictionary<string, float>();
            // Merge global + per-NPC descriptors (per-NPC wins) for full context
            var globalDescriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
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

            return new ToolResult<NpcContextView>(true, context, $"Psychological context for {npc.Name} retrieved (campaign: {effective}).");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("UNIFIED SEARCH: Search across Lore, Characters, Locations, and Items in one shot. Use this when searching for anything by name or keyword. (Campaign context is recorded for future per-campaign scoping.)")]
    public Task<ToolResult<IEnumerable<object>>> SearchWorld(
        string query,
        [Description("Optional campaign name. Falls back to currently selected (for future namespacing).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        // Pure read + the previous parallel query pattern was a major source of "active async tasks on dispose".
        return ExecuteAsync(async session => {
            var results = await _repository.UnifiedSearchAsync(session, query, effective);
            return new ToolResult<IEnumerable<object>>(true, results, $"Found {results.Count()} matches (campaign: {effective}).");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("HISTORY RECALL: Semantic search over past events. Use this to remember 'what happened last time we were here' or recall specific plot points.")]
    public Task<ToolResult<IEnumerable<Event>>> RecallHistory(
        string query, 
        int limit = 5,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var results = await _repository.QueryEventsAsync(session, query, null, limit, effective);
            return new ToolResult<IEnumerable<Event>>(true, results, $"Retrieved {results.Count()} historical events (campaign: {effective}).");
        }, saveChanges: false);
    }

    // --- Configuration Tools (Genuine state setup) ---

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Directly create or overwrite a character/NPC.

Use this to seed or update full NPC records, including rich psychological data.

STRONGLY encouraged to populate:
- Mind.Wants, Mind.Fears, Mind.Knows
- Detailed backstory in Notes
- Schedule + Routines + StateModifiers
- Mind.NeedDescriptors (human-readable explanations for any custom needs)
- Equipment via Items (set HolderId to the character)

This is the best opportunity to create deep, simulatable NPCs.")]
    public Task<ToolResult<Character>> UpsertCharacter(
        [Description("The full Character object to create or replace. Strongly typed.")] Character character,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertCharacterAsync(s, character);
            return new ToolResult<Character>(true, character, $"Character upserted (campaign context: {effective}).");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"WORLD BUILDER TOOL: Register a new location on the world map. For first-time setup only.

Define hierarchical locations with exits, parent relationships, and rich metadata.")]
    public Task<ToolResult<Location>> UpsertLocation(
        [Description("The full Location object to create or replace. Strongly typed.")] Location location,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLocationAsync(s, location);
            return new ToolResult<Location>(true, location, $"Location upserted (campaign context: {effective}).");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("WORLD BUILDER TOOL: Create or update a lore entry. Always use SearchWorld first to check whether similar lore already exists.")]
    public Task<ToolResult<Lore>> UpsertLore(
        [Description("The full Lore object to create or replace. Strongly typed.")] Lore lore,
        [Description("Optional campaign name (for future per-campaign scoping of entities).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async s =>
        {
            await _repository.UpsertLoreAsync(s, lore);
            return new ToolResult<Lore>(true, lore, $"Lore upserted (campaign context: {effective}).");
        });
    }

    // --- Needs Discoverability Tools ---

    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Returns all known needs for an NPC along with their current values and any descriptors. Use this to understand what psychological or physical drives an NPC has before roleplaying or making changes. The needs system is open — you are encouraged to invent new narrative-appropriate needs.")]
    public Task<ToolResult<NpcNeedsView>> GetNpcNeeds(
        string characterId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var npc = await _repository.GetCharacterAsync(session, characterId, effective);
            if (npc == null) return new ToolResult<NpcNeedsView>(false, Error: "NotFound");

            // Merge global descriptors (from DefineNeedDescriptor) with per-NPC ones.
            // Per-NPC descriptors take precedence on conflicts.
            var globalDescriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
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

            return new ToolResult<NpcNeedsView>(true, view, $"Needs for {npc.Name} retrieved (campaign: {effective}).");
        }, saveChanges: false);
    }

    [McpServerTool]
    [Description("WORLD BUILDER TOOL: Define or update a descriptor for a need type for the current/selected campaign. Automatically merged into get_npc_needs, get_npc_context, and get_scene results (per-NPC descriptors override). Use get_need_descriptors to list defined ones for the campaign. Example: needName='homesickness', descriptor='Longing for home and family. High values cause distraction, poor rest, and risk of emotional outbursts.'")]
    public Task<ToolResult<string>> DefineNeedDescriptor(string needName, string descriptor, string? campaignName = null)
    {
        if (string.IsNullOrWhiteSpace(needName) || string.IsNullOrWhiteSpace(descriptor))
            return Task.FromResult(new ToolResult<string>(false, Error: "BadRequest", Summary: "needName and descriptor are required."));

        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            await _repository.SetNeedDescriptorAsync(session, needName, descriptor, effective);
            return new ToolResult<string>(true, $"Descriptor for '{needName}' stored for campaign '{effective}'.", $"Descriptor persisted for campaign '{effective}'.");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("DISCOVERABILITY TOOL: Lists all defined need descriptors for the current (or specified) campaign.")]
    public Task<ToolResult<Dictionary<string, string>>> GetNeedDescriptors(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var descriptors = await _repository.GetGlobalNeedDescriptorsAsync(session, effective);
            return new ToolResult<Dictionary<string, string>>(true, descriptors, 
                descriptors.Count > 0 
                    ? $"Retrieved {descriptors.Count} need descriptors for campaign '{effective}'."
                    : $"No need descriptors defined yet for campaign '{effective}'.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Get the current campaign configuration.
Returns the ruleset and system-specific options (e.g., house rules). Respects the currently selected campaign.")]
    public Task<ToolResult<CampaignConfig>> GetConfig(
        [Description("Optional campaign name. Falls back to the currently selected campaign (via select_campaign).")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            var config = await _repository.GetCampaignConfigAsync(session, effective);
            return new ToolResult<CampaignConfig>(true, config, $"Campaign configuration retrieved for '{effective}'.");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"RULES CONFIG TOOL: Set the active ruleset system for a campaign.
Respects lock-in (cannot change system once locked). Use this to define house rules or system options.

Example: set_active_system(RulesetSystem.Pf2e, { ""mapEnabled"": ""true"" })")]
    public Task<ToolResult<CampaignConfig>> SetActiveSystem(
        [Description("The active TTRPG ruleset system.")] RulesetSystem activeSystem,
        [Description("Optional dictionary of system options and house rules.")] Dictionary<string, string>? systemOptions = null,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);

        return ExecuteAsync(async session =>
        {
            var campaign = await GetOrCreateCampaignMetaAsync(session, effective, activeSystem, forceLock: false);

            if (campaign.IsSystemLocked && campaign.System != activeSystem)
            {
                return new ToolResult<CampaignConfig>(
                    false,
                    Error: "SystemLocked",
                    Summary: $"The ruleset for campaign '{effective}' is locked to {campaign.System}. Cannot change to {activeSystem}.");
            }

            var config = await _repository.GetCampaignConfigAsync(session, effective);
            config.ActiveSystem = activeSystem;
            config.SystemOptions = systemOptions ?? [];
            await _repository.UpsertCampaignConfigAsync(session, config, effective);

            if (!campaign.IsSystemLocked)
            {
                campaign.System = activeSystem;
                campaign.IsSystemLocked = true;
            }

            return new ToolResult<CampaignConfig>(true, config, $"Active ruleset for '{effective}' set to '{activeSystem}' (locked).");
        });
    }

    // --- Combat & Dispatch Tools ---

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Starts a new combat encounter at the specified location.
Rolls initiative for all combatants based on the active ruleset system and establishes the turn order. If a combat is already active, it is overwritten. Respects the currently selected campaign.

Example: start_combat(""locations/tavern"", [""chars/pc1"", ""chars/pc2"", ""monsters/goblin1""])")]
    public Task<ToolResult<CombatEncounter>> StartCombat(
        [Description("The location ID where combat is happening.")] string locationId,
        [Description("List of character IDs participating in combat.")] string[] combatantIds,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session =>
        {
            if (combatantIds == null || combatantIds.Length == 0)
            {
                return new ToolResult<CombatEncounter>(false, Error: "InvalidInput", Summary: "Cannot start combat with zero combatants.");
            }

            var uniqueIds = combatantIds.Distinct().ToList();
            var loadedCharacters = await session.LoadAsync<Character>(uniqueIds);
            var validCharacters = loadedCharacters.Values.Where(c => c != null && c.CurrentHp > 0).ToList();

            if (validCharacters.Count == 0)
            {
                return new ToolResult<CombatEncounter>(false, Error: "InvalidInput", Summary: "None of the specified combatants are valid and alive.");
            }

            var config = await _repository.GetCampaignConfigAsync(session, effective);
            var resolver = _rulesetSelector.GetResolver(config.ActiveSystem);

            var combatants = new List<CombatantState>();
            foreach (var character in validCharacters)
            {
                var initiative = await resolver.RollInitiativeAsync(character);
                combatants.Add(new CombatantState
                {
                    CharacterId = character.Id,
                    Initiative = initiative,
                    HasActedThisRound = false
                });
            }

            // Sort by highest initiative first
            combatants = combatants.OrderByDescending(c => c.Initiative).ToList();

            var encounter = new CombatEncounter
            {
                Id = _keys.CombatCurrent(effective),
                LocationId = locationId,
                Round = 1,
                Combatants = combatants,
                ActiveTurnId = combatants.FirstOrDefault()?.CharacterId,
                IsActive = true
            };

            await session.StoreAsync(encounter, encounter.Id);

            return new ToolResult<CombatEncounter>(true, encounter, $"Combat started at {locationId} with {combatants.Count} combatants.");
        });
    }


    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Advances the turn order to the next combatant.
If all combatants have acted, advances to the next round. Skips dead combatants (HP <= 0).
Round-based status effects naturally expire during this transition when their round duration ends.
Respects the currently selected campaign.")]
    public Task<ToolResult<CombatEncounter>> NextTurn(
        [Description("Optional. If provided, the command will fail if the current active turn does not match this ID. Helps prevent accidental double-advancing.")] string? expectedActiveTurnId = null,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        var combatId = _keys.CombatCurrent(effective);

        return ExecuteAsync(async session =>
        {
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounter>(false, Error: "NotFound", Summary: "No active combat encounter.");
            }

            if (!string.IsNullOrWhiteSpace(expectedActiveTurnId) && encounter.ActiveTurnId != expectedActiveTurnId)
            {
                return new ToolResult<CombatEncounter>(false, Error: "StateDrift", Summary: $"Expected active turn to be '{expectedActiveTurnId}' but it was '{encounter.ActiveTurnId}'. The combat state has drifted.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);

            // Mark current actor as having acted
            var current = encounter.Combatants.FirstOrDefault(c => c.CharacterId == encounter.ActiveTurnId);
            if (current != null)
            {
                current.HasActedThisRound = true;
            }

            var expiredMessages = new List<string>();

            // Find next who hasn't acted and is alive
            CombatantState? GetNextAliveUnacted() => encounter.Combatants.FirstOrDefault(c => 
                !c.HasActedThisRound && 
                characters.TryGetValue(c.CharacterId, out var character) && character != null && character.CurrentHp > 0);

            var next = GetNextAliveUnacted();
            
            if (next == null)
            {
                // Verify if anyone is actually alive
                if (!encounter.Combatants.Any(c => characters.TryGetValue(c.CharacterId, out var character) && character != null && character.CurrentHp > 0))
                {
                     return new ToolResult<CombatEncounter>(false, Error: "CombatEnded", Summary: "No valid and alive combatants remain. Combat has ended or cannot proceed.");
                }

                // New round
                encounter.Round++;
                foreach (var c in encounter.Combatants) c.HasActedThisRound = false;
                next = GetNextAliveUnacted(); // Retrieve the first alive person again

                // Expire round-based status effects
                foreach (var character in characters.Values.Where(c => c != null))
                {
                    if (character.SystemStats?.StatusEffects != null)
                    {
                        var effects = character.SystemStats.StatusEffects;
                        var toRemove = effects.Where(e => e.ExpiresAtRound.HasValue && e.ExpiresAtRound.Value <= encounter.Round).ToList();
                        foreach (var effect in toRemove)
                        {
                            effects.Remove(effect);
                            expiredMessages.Add($"Expired effect '{effect.Name}' on '{character.Name}'.");
                        }
                    }
                }
            }

            encounter.ActiveTurnId = next?.CharacterId;
            await session.StoreAsync(encounter, encounter.Id);

            var summary = $"Advanced to turn of {encounter.ActiveTurnId} (Round {encounter.Round}).";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"COMBAT TOOL: Ends the current active combat encounter and wraps up the state.
Aggressively clears all round-based status effects (e.g., 'until end of combat' effects) from all combatants.
Day-based effects remain active. Respects the currently selected campaign.")]
    public Task<ToolResult<CombatEncounter>> EndCombat(
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        var combatId = _keys.CombatCurrent(effective);

        return ExecuteAsync(async session =>
        {
            var encounter = await session.LoadAsync<CombatEncounter>(combatId);
            if (encounter == null || !encounter.IsActive)
            {
                return new ToolResult<CombatEncounter>(false, Error: "NotFound", Summary: "No active combat encounter to end.");
            }

            var characterIds = encounter.Combatants.Select(c => c.CharacterId).ToList();
            var characters = await session.LoadAsync<Character>(characterIds);
            var expiredMessages = new List<string>();

            // Clear all round-based status effects when combat ends.
            // This implements "until end of combat" semantics for effects created with ExpiresAtRound.
            // Day-based effects (ExpiresAtDay) are handled separately by StatusExpiryRule during advance_world.
            // Note: This is intentionally aggressive — all round-tied effects are removed on combat end.
            foreach (var character in characters.Values.Where(c => c != null))
            {
                if (character.SystemStats?.StatusEffects != null)
                {
                    var effects = character.SystemStats.StatusEffects;
                    var toRemove = effects.Where(e => e.ExpiresAtRound.HasValue).ToList();
                    foreach (var effect in toRemove)
                    {
                        effects.Remove(effect);
                        expiredMessages.Add($"Cleared effect '{effect.Name}' on '{character.Name}'.");
                    }
                }
            }

            encounter.IsActive = false;
            encounter.ActiveTurnId = null;

            await session.StoreAsync(encounter, encounter.Id);

            var summary = "Combat encounter ended.";
            if (expiredMessages.Count > 0)
            {
                summary += " " + string.Join(" ", expiredMessages);
            }

            return new ToolResult<CombatEncounter>(true, encounter, summary);
        });
    }

    // --- Dedicated Campaign Management Tools ---

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Creates a new campaign with a name and initial ruleset.
The ruleset is immediately locked for this campaign, preventing accidental system changes later.
Automatically selects the newly created campaign as the current one.

Example: create_campaign(""dragonheist"", RulesetSystem.Dnd5e, ""Waterdeep: Dragon Heist"")")]
    public Task<ToolResult<Campaign>> CreateCampaign(
        [Description("Unique name/slug for the campaign (e.g. 'dragonheist', 'curse-of-strahd').")] string name,
        [Description("Initial ruleset system. This will be locked.")] RulesetSystem initialSystem,
        [Description("Optional human-friendly display name.")] string? displayName = null)
    {
        var normalized = name.Trim().ToLowerInvariant();

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);
            if (existing != null)
            {
                return new ToolResult<Campaign>(false, Error: "AlreadyExists", Summary: $"Campaign '{normalized}' already exists.");
            }

            var campaign = await GetOrCreateCampaignMetaAsync(session, normalized, initialSystem, displayName, forceLock: true);

            // Select it immediately for convenience
            _currentCampaign.SetCurrent(normalized);

            return new ToolResult<Campaign>(true, campaign, $"Campaign '{normalized}' created and locked to {initialSystem}. Now selected as current.");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Lists all existing campaigns in the database.
Useful for discovering existing worlds to join before calling select_campaign.")]
    public Task<ToolResult<List<Campaign>>> ListCampaigns()
    {
        return ExecuteAsync(async session =>
        {
            // Query all Campaign documents (they live under campaigns/*/meta)
            var campaigns = await session.Query<Campaign>()
                .Where(c => c.Id.StartsWith("campaigns/"))
                .ToListAsync();

            return new ToolResult<List<Campaign>>(true, campaigns, $"Found {campaigns.Count} campaign(s).");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN TOOL: Selects a campaign as the current one for this session.
Most tools will use this campaign context automatically, meaning you don't need to specify 'campaignName' on subsequent tool calls.

Example: select_campaign(""dragonheist"")")]
    public Task<ToolResult<string>> SelectCampaign(
        [Description("Name of the campaign to select.")] string campaignName)
    {
        if (string.IsNullOrWhiteSpace(campaignName))
        {
            return Task.FromResult(new ToolResult<string>(false, Error: "InvalidArgument", Summary: "campaignName is required."));
        }

        var normalized = campaignName.Trim().ToLowerInvariant();
        _currentCampaign.SetCurrent(normalized);

        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(normalized);
            var existing = await session.LoadAsync<Campaign>(campaignId);

            if (existing == null)
            {
                // Auto-create a minimal campaign entry so lock-in and per-campaign state can work
                await GetOrCreateCampaignMetaAsync(session, normalized, RulesetSystem.Dnd5e, forceLock: false);

                return new ToolResult<string>(true, normalized, 
                    $"Campaign '{normalized}' selected (new minimal campaign created with D&D 5e as default system).");
            }

            return new ToolResult<string>(true, normalized, $"Campaign '{normalized}' is now selected as current.");
        });
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"CAMPAIGN DISCOVERABILITY: Returns the currently active campaign context (name, lock-in status, and active ruleset).
Use this if you are unsure which campaign you are currently in or if you need to know the active ruleset system (e.g., Dnd5e, Pf2e) before using ruleset_actions in combat.")]
    public Task<ToolResult<Campaign>> GetCurrentCampaign()
    {
        var effective = EffectiveCampaign(null);
        return ExecuteAsync(async session =>
        {
            var campaignId = _keys.Meta(effective);
            var campaign = await session.LoadAsync<Campaign>(campaignId);
            if (campaign == null)
                return new ToolResult<Campaign>(false, Error: "NotFound", Summary: $"Campaign '{effective}' meta document not found. The campaign might not be initialized yet.");
            return new ToolResult<Campaign>(true, campaign, $"Currently selected campaign: {effective}");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(@"SYSTEM DISCOVERABILITY: Returns a comprehensive DM manual. Call this if you forget how to use the tools, how to write ruleset_actions, how StatusEffects work, or the core gameplay loop.")]
    public Task<ToolResult<string>> GetHelp()
    {
        string manual = @"# CampaignVault DM Manual

Welcome to the CampaignVault engine. Your role as the AI DM is to drive the narrative while letting the MCP engine handle the persistence, math, and simulation.

## Core Gameplay Loop
1. **Start of Session**: Call `get_world_state` to sync with the campaign clock, rumors, events, and immediate **WorldPressure**.
2. **Exploration**: Call `get_scene` when the party enters a new location to see NPCs, items, and local descriptions.
3. **Action & Consequence**: Describe the narrative to the player. When something permanent happens (combat, taking an item, changing a relationship), call `commit`.
4. **Time Skips**: Use `advance_world` when the party rests or travels. This triggers background simulations (NPC routines, needs accumulation, rumor decay).

## The Commit Tool
`commit` is your universal write tool. It takes an array of JSON mutations (`$type`). It is atomic. NEVER forget to `commit` the outcome of a narrative beat.

Supported `$type`s: `hp`, `item`, `status`, `statusremove`, `event`, `rumor`, `relationship`, `need`, `attribute`, `mood`, `activity`, `ruleset_action`.

## Ruleset Actions (Combat & Skill Checks)
Instead of rolling dice yourself, use `$type: ruleset_action` inside `commit`. The engine will calculate hits, crits, and modifiers automatically based on the `ActiveSystem`.

**D&D 5e / PF2e Example:**
```json
{
  ""$type"": ""ruleset_action"",
  ""actorId"": ""chars/gimli"",
  ""targetIds"": [""chars/goblin""],
  ""actionType"": ""Attack"",
  ""parameters"": { ""bonus"": ""5"", ""damageDice"": ""1d8+3"" }
}
```

## Status Effects & Stat Modifiers
Do not just narrate ""he is crippled"". Apply a status effect via `commit` so the system knows! You can embed mechanical modifiers that the engine will mathematically enforce on all future `ruleset_action` calls.

**Status Example:**
```json
{
  ""$type"": ""status"",
  ""characterId"": ""chars/gimli"",
  ""effect"": {
    ""name"": ""Crippled Arm"",
    ""category"": ""Injury"",
    ""affectedPart"": ""LeftArm"",
    ""statModifiers"": {
      ""AttackRoll"": -2,
      ""SkillCheck"": -1
    },
    ""recoveryHint"": ""Requires a DC 15 Medicine check or a long rest.""
  }
}
```
**Canonical Modifiers:** `AttackRoll`, `DamageRoll`, `AC`, `Defense`, `Initiative`, `SkillCheck`, `AllRolls`, `AllChecks`.

## World Pressure
When you call `get_world_state` or `advance_world`, the engine returns a `pressure` array. This contains characters who are dying, statuses that are festering, or rumors that have gone unresolved for days. **It is your job to inject these pressures into the narrative.**
";
        return Task.FromResult(new ToolResult<string>(true, manual, "Help manual retrieved."));
    }
}

