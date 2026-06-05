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
    private readonly IPressureManager _pressureManager;

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
        ICurrentCampaignContext currentCampaign,
        IPressureManager? pressureManager = null)
    {
        _repository = repository;
        _behaviorSynthesizer = behaviorSynthesizer;
        _rulesetSelector = rulesetSelector;
        _keys = keys ?? new CampaignDocumentKeys();
        _currentCampaign = currentCampaign ?? new CurrentCampaignContext();
        _pressureManager = pressureManager ?? new PressureManager(_keys);
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

    private void AddQuestDeadlinePressures(
        List<WorldPressureItem> pressures,
        IEnumerable<(string Id, string Title, int? DeadlineDay)> questInfos,
        int currentDay)
    {
        foreach (var (id, title, deadline) in questInfos.Where(x => x.DeadlineDay.HasValue))
        {
            var daysLeft = deadline!.Value - currentDay;
            if (daysLeft > 0 && daysLeft <= 3)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, id,
                    $"Quest '{title}' deadline in {daysLeft} days (Day {deadline}). Progress or fail it: [ {{\"$type\": \"quest_progress\", \"questId\": \"{id}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"...\" }} ] (or Failed).",
                    "Quest:ApproachingDeadline"));
            }
            else if (daysLeft <= 0)
            {
                pressures.Add(new WorldPressureItem(PressureSeverity.Simulation, id, $"Quest '{title}' deadline passed. Engine may have auto-failed objectives.", "Quest:MissedDeadline"));
            }
        }
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("KICKOFF TOOL: Call this at the start of every session to get the current time, active rumors, recent history, and current party location in one view. Respects the currently selected campaign (via select_campaign).")]
    public Task<ToolResult<WorldStateView>> GetWorldState(
        [Description("The current ID of the location where the party is (string type)")] string partyLocationId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        // We now save changes on reads because FilterAndCapAsync needs to persist PressureCooldowns.
        // The underlying repository methods are safe (e.g., GetSceneAsync only marks visited if explicitly requested).
        return ExecuteAsync(async session => {
            var time = await _repository.GetTimeAsync(session, effective);
            
            // Widen rumor search for kickoff
            var spreading = await _repository.QueryRumorsAsync(session, null, null, RumorState.Spreading, 3, effective);
            var peak = await _repository.QueryRumorsAsync(session, null, null, RumorState.Peak, 3, effective);
            var rumors = peak.Concat(spreading).ToList();

            var events = await _repository.QueryEventsAsync(session, null, null, 5, effective);
            var location = await _repository.GetLocationAsync(session, partyLocationId, effective);
            
            var pressure = new List<WorldPressureItem>();
            foreach (var r in rumors.Where(r => time.TotalDaysElapsed - r.LastStateChangeDay > 5))
            {
                pressure.Add(new WorldPressureItem(PressureSeverity.Simulation, r.Id, 
                    $"Rumor '{r.Subject}' has been spreading for {time.TotalDaysElapsed - r.LastStateChangeDay} days without resolution. " +
                    "Consider evolving or resolving via commit: [ { \"$type\": \"rumor\", \"rumorId\": \"...\", \"newState\": \"Fading|Resolved\", \"newText\": \"...\" } ]",
                    "Rumor:Aging"));
            }

            var agingEvents = await _repository.QueryEventsAsync(session, null, EventCategory.Unresolved, 5, effective);
            foreach (var e in agingEvents)
            {
                pressure.Add(new WorldPressureItem(PressureSeverity.Simulation, e.Id, 
                    $"Unresolved thread: '{e.Summary}' ({time.TotalDaysElapsed - e.DayLogged} days old). " +
                    "Resolve or advance via commit e.g. [ { \"$type\": \"event\", \"category\": \"Resolution\", \"summary\": \"...resolved...\", \"involved\": [\"" + (e.Involved?.FirstOrDefault() ?? "ids...") + "\"] } ] or convert to rumor.",
                    "Event:Unresolved"));
            }
            
            var charPressure = await _repository.GetCharacterPressureAsync(session, effective);
            // Enhance a few char pressures with copy-paste hints for common cases (reduces need to recall exact JSON shape)
            foreach (var cp in charPressure)
            {
                if (cp.Text.Contains("critically wounded") || cp.Text.Contains("dying"))
                {
                    pressure.Add(cp with { Text = cp.Text + " Example fix in commit: [ { \"$type\": \"hp\", \"characterId\": \"chars/xxx\", \"delta\": 10 }, { \"$type\": \"status\", \"characterId\": \"chars/xxx\", \"status\": \"Stable\" } ]" });
                }
                else if (cp.Text.Contains("desperate need"))
                {
                    pressure.Add(cp with { Text = cp.Text + " Satisfy via: [ { \"$type\": \"need\", \"characterId\": \"chars/xxx\", \"need\": \"hunger\", \"delta\": -30 } ] (negative = satisfy). Consider schedule_change if this NPC is important." });
                }
                else
                {
                    pressure.Add(cp);
                }
            }

            // Dangling items
            var allItems = await session.Query<Item>().Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2))).Where(i => i.CampaignName == effective).Take(100).ToListAsync();
            foreach (var item in allItems)
            {
                if ((string.IsNullOrEmpty(item.CampaignName) || item.CampaignName == effective) && !string.IsNullOrEmpty(item.HolderId))
                {
                    var holderExists = await session.Advanced.ExistsAsync(item.HolderId);
                    if (!holderExists)
                    {
                        pressure.Add(new WorldPressureItem(PressureSeverity.EngineWarning, item.Id,
                            $"Item '{item.Name}' is held by '{item.HolderId}' which no longer exists (likely GC'd). " +
                            "Use item_transfer to move it to a valid location or character:\n" +
                            "[ { \"$type\": \"item_transfer\", \"itemId\": \"" + item.Id + "\", \"newHolderId\": \"locations/some_valid_location\" } ]",
                            "Item:DanglingHolder"));
                    }
                }
            }

            // Never-visited locations with transients
            var transients = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => c.CampaignName == effective && c.Schedule == null && c.KeepAlive == false)
                .Take(50)
                .ToListAsync();
            var transientLocIds = transients.Select(c => c.CurrentLocationId).Where(id => !string.IsNullOrEmpty(id)).Distinct();
            foreach (var locId in transientLocIds)
            {
                var l = await session.LoadAsync<Location>(locId);
                if (l != null && l.LastVisitedDay == null)
                {
                    pressure.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, l.Id,
                        $"Location '{l.Name}' has never been visited but has transient NPCs. " +
                        "Consider visiting this location or setting keepAlive: true on important NPCs so they are not silently evicted.",
                        "Location:NeverVisitedTransients"));
                }
            }

            // Phase 7.4 quest/travel pressures (global view)
            var worldActiveQuests = await _repository.GetActiveQuestsAsync(session, effective, 10);
            AddQuestDeadlinePressures(pressure, worldActiveQuests.Select(q => (q.Id, q.Title, q.DeadlineDay)), (int)time.TotalDaysElapsed);

            // En-route / interrupted travel
            var stuck = await session.Query<Character>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(2)))
                .Where(c => (string.IsNullOrEmpty(c.CampaignName) || c.CampaignName == effective) 
                            && c.CurrentActivity != null 
                            && (c.CurrentActivity.StartsWith("Travel interrupted en route") || c.CurrentActivity.StartsWith("interrupted en route")))
                .Take(5)
                .ToListAsync();
            foreach (var s in stuck)
            {
                pressure.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, s.Id,
                    $"Character '{s.Name}' is stuck: '{s.CurrentActivity}'. Narrate the encounter resolution then commit e.g. [ {{\"$type\": \"activity\", \"characterId\": \"{s.Id}\", \"newActivity\": \"...resolved...\", \"updateLocation\": false }}, {{\"$type\": \"travel\", \"characterId\": \"{s.Id}\", \"destinationLocationId\": \"...\", \"encounterRiskModifier\": -20 }} ] to continue.",
                    "Travel:Interrupted"));
            }

            var finalPressures = await _pressureManager.FilterAndCapAsync(session, effective, (int)time.TotalDaysElapsed, pressure);
            
            var suggestedExamples = new List<string>();
            var questPressureTriggered = finalPressures.Any(p => p.Contains("Quest", StringComparison.OrdinalIgnoreCase) || p.Contains("deadline", StringComparison.OrdinalIgnoreCase));
            if (questPressureTriggered && worldActiveQuests.Any())
            {
                var q = worldActiveQuests.First();
                suggestedExamples.Add($"[ {{ \"$type\": \"quest_progress\", \"questId\": \"{q.Id}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"We completed the objective.\" }} ]");
            }
            var stuckChar = stuck.FirstOrDefault();
            if (stuckChar != null && finalPressures.Any(p => p.Contains("Travel", StringComparison.OrdinalIgnoreCase) || p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
            {
                suggestedExamples.Add($"[ {{ \"$type\": \"activity\", \"characterId\": \"{stuckChar.Id}\", \"newActivity\": \"Resolved the ambush and continued\", \"updateLocation\": false }}, {{ \"$type\": \"travel\", \"characterId\": \"{stuckChar.Id}\", \"destinationLocationId\": \"locations/actual-dest\", \"encounterRiskModifier\": -30 }} ]");
            }

            var worldActiveFactions = await _repository.GetActiveFactionsAsync(session, effective, 10);
            
            var locSummary = location != null ? new LocationSummary(location.Id, location.Name, location.Type) : null;
            
            var travelEvent = events.FirstOrDefault(e => e.Summary.Contains("travel", StringComparison.OrdinalIgnoreCase) || e.Summary.Contains("en route", StringComparison.OrdinalIgnoreCase) || e.Summary.Contains("interrupted", StringComparison.OrdinalIgnoreCase));

            var view = new WorldStateView(
                time, 
                rumors.Select(r => new RumorSummary(r.Subject, r.CurrentText, r.State)), 
                events, 
                locSummary, 
                finalPressures,
                worldActiveQuests.Select(q => new ActiveQuestSummary(q.Id, q.Title, q.Objectives.Count(o => o.State == QuestState.Open || o.State == QuestState.InProgress), q.Objectives.Count, q.Urgency, q.DeadlineDay, q.GiverId)),
                worldActiveFactions.Select(f => new FactionPresenceSummary(f.Id, f.Name, f.InfluenceLevel, FactionStance.Neutral, null, f.TerritoryLocationIds.Count)),
                travelEvent?.Summary,
                suggestedExamples
            );
            return new ToolResult<WorldStateView>(true, view, $"Authoritative world state retrieved for session start (campaign: {effective}).");
        }, saveChanges: true);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("EXPLORATION TOOL: Call this whenever entering a new room, building, or region. Returns the location description, present NPCs (with behavioral summaries), visible items, and local rumors. Respects the currently selected campaign.\nSet 'partyPresent=true' ONLY if the party is physically entering or spending time here. Leave false if just looking around for pressures to prevent messing up the simulation's character eviction logic.")]
    public Task<ToolResult<SceneView>> GetScene(
        [Description("The unique ID of the location.")] string locationId,
        [Description("Set to true if the party is physically entering or spending time here (prevents cleanup).")] bool partyPresent = false,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var scene = await _repository.GetSceneAsync(session, locationId, effective, markVisited: partyPresent);
            var pressures = new List<WorldPressureItem>();
            var loc = scene.Location;

            if (!scene.IsLocationAnchored)
            {
                var suggestions = await _repository.SuggestLocationsAsync(session, locationId, effective);
                if (suggestions.Any())
                {
                    var names = string.Join(", ", suggestions.Select(s => $"'{s.Id}' ({s.Name})"));
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, locationId,
                        $"Location '{locationId}' not found. Did you mean one of these: {names}? " +
                        "If so, use the correct ID. If it is truly new, use `location_create`:\n" +
                        "[\n  {\n    \"$type\": \"location_create\",\n    \"locationId\": \"" + locationId + "\",\n    " +
                        "\"name\": \"...\",\n    \"description\": \"...\",\n    \"connectedFromLocationId\": \"...\",\n    " +
                        "\"connectionDescription\": \"...\"\n  }\n]",
                        "Location:Hallucinated"));
                }
                else
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, locationId,
                        $"You requested '{locationId}' but it does not exist in the database! " +
                        "You are hallucinating. Use the `commit` tool immediately:\n" +
                        "[\n  {\n    \"$type\": \"location_create\",\n    \"locationId\": \"" + locationId + "\",\n    " +
                        "\"name\": \"...\",\n    \"description\": \"...\",\n    \"connectedFromLocationId\": \"...\",\n    " +
                        "\"connectionDescription\": \"...\"\n  }\n]",
                        "Location:Hallucinated"));
                }
            }
            else
            {
                if (loc.Exits.Count == 0 && loc.Type != LocationType.Region)
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, loc.Id,
                        $"This location has no Exits. The players are soft-locked. " +
                        "Use `location_update` to add an exit back:\n" +
                        "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", " +
                        "\"addExit\": { \"targetLocationId\": \"locations/previous_area\", \"description\": \"...\" } } ]",
                        "Location:NoExits"));
                }
                if (!scene.PresentNPCs.Any() && !string.IsNullOrWhiteSpace(loc.AmbientCrowd))
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, loc.Id,
                        $"This location is currently empty, but expects '{loc.AmbientCrowd}'. " +
                        "Consider spawning flavorful transient NPCs via `character_create` inside `commit`.",
                        "Location:EmptyExpectsCrowd"));
                }

                // Additional laziness / integrity mitigations (Phase 7 prep + Phase 6 amplification)
                // 1. Missing reverse link from parent (one-way door, even if not created via auto-link path).
                if (!string.IsNullOrEmpty(loc.ParentLocationId))
                {
                    // Cheap extra load; parent is often warm from prior calls or include paths.
                    // In real use, this surfaces immediately on get_scene so LLM can fix with location_update on the *parent*.
                    try
                    {
                        // We can't easily access raw session here without changing Execute, so do a repo call (it will sanitize).
                        // Note: GetLocationAsync is lightweight.
                        var parentLoc = await _repository.GetLocationAsync(session, loc.ParentLocationId, effective);
                        if (parentLoc != null && !parentLoc.Exits.Any(e => e.TargetLocationId == loc.Id))
                        {
                            pressures.Add(new WorldPressureItem(PressureSeverity.EngineWarning, parentLoc.Id,
                                $"This location has a ParentLocationId but the parent has no matching exit back to it (one-way link / broken connectivity). " +
                                "Fix with location_update on the parent:\n" +
                                "[ { \"$type\": \"location_update\", \"locationId\": \"" + parentLoc.Id + "\", " +
                                "\"addExit\": { \"targetLocationId\": \"" + loc.Id + "\", \"description\": \"... (back to " + loc.Name + ")\" } } ]",
                                "Location:MissingReverseLink"));
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Pressure check error: {ex.Message}"); }
                }

                // 2. Flavor vacuum: scene has no PoIs, no AmbientCrowd, no present NPCs, and is not a pure Region.
                // Nudges LLM to use lightweight non-persistent flavor instead of forcing character_create for every bar patron.
                if (loc.Type != LocationType.Region && loc.PointsOfInterest.Count == 0 && string.IsNullOrWhiteSpace(loc.AmbientCrowd) && !scene.PresentNPCs.Any())
                {
                    pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, loc.Id,
                        $"This location lacks flavor details (no PointsOfInterest, no AmbientCrowd). " +
                        "For a lively scene without DB bloat, use location_update (or include in location_create) to add PoIs/AmbientCrowd. Example:\n" +
                        "[ { \"$type\": \"location_update\", \"locationId\": \"" + loc.Id + "\", " +
                        "\"addPointOfInterest\": \"A half-empty mug on the bar\", \"ambientCrowd\": \"3-6 locals nursing drinks\" } ]",
                        "Location:FlavorVacuum"));
                }

                // 3. Dead-end room with exits that may need promotion or travel hints (future spatial).
                if (loc.Exits.Count > 0 && loc.Type == LocationType.Room && string.IsNullOrWhiteSpace(loc.AmbientCrowd) && loc.PointsOfInterest.Count == 0 && !scene.PresentNPCs.Any())
                {
                    // Light nudge; real travel mechanics come in Phase 7.
                    pressures.Add(new WorldPressureItem(PressureSeverity.Suggestion, loc.Id,
                        $"(optional): Room has exits but no ambient hint. If this is a 'quiet' area, consider setting ambientCrowd for future visits or use schedule_change on key NPCs to anchor them here.",
                        "Location:DeadEndSuggestion"));
                }
            }
            var time = await _repository.GetTimeAsync(session, effective);

            // Phase 7.4 local pressures (quests + interrupted travel)
            if (scene.ActiveQuests != null)
            {
                AddQuestDeadlinePressures(pressures, scene.ActiveQuests.Select(q => (q.QuestId, q.Title, q.DeadlineDay)), (int)time.TotalDaysElapsed);
            }

            if (scene.PresentNPCs != null)
            {
                foreach (var npc in scene.PresentNPCs)
                {
                    if (npc.CurrentActivity != null && npc.CurrentActivity.Contains("interrupted en route", StringComparison.OrdinalIgnoreCase))
                    {
                        pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, npc.Id,
                            $"Character '{npc.Name}' is stuck: '{npc.CurrentActivity}'. Narrate the encounter resolution then commit e.g. [ {{\"$type\": \"activity\", \"characterId\": \"{npc.Id}\", \"newActivity\": \"...resolved...\", \"updateLocation\": false }}, {{\"$type\": \"travel\", \"characterId\": \"{npc.Id}\", \"destinationLocationId\": \"...\", \"encounterRiskModifier\": -20 }} ] to continue.",
                            "Travel:Interrupted"));
                    }
                }
            }

            if (scene.RelevantFactions != null && scene.RelevantFactions.Any())
            {
                var fIds = scene.RelevantFactions.Select(f => f.FactionId).ToList();
                int minDay = (int)time.TotalDaysElapsed - 2;
                var recentEvents = await session.Query<Event>()
                    .Where(e => e.CampaignName == effective && (e.Category == EventCategory.Simulation || e.Category == EventCategory.SceneCommit) && e.DayLogged >= minDay)
                    .Take(50)
                    .ToListAsync();
                    
                var simEvents = recentEvents.Where(e => e.Category == EventCategory.Simulation).ToList();
                var commitEvents = recentEvents.Where(e => e.Category == EventCategory.SceneCommit).ToList();

                foreach (var ev in simEvents)
                {
                    if (ev.Involved != null && ev.Involved.Any(id => fIds.Contains(id)))
                    {
                        var invFaction = ev.Involved.First(id => fIds.Contains(id));
                        
                        // If the LLM already interacted with this faction ON or AFTER the simulation event, don't nag.
                        if (commitEvents.Any(c => c.Timestamp >= ev.Timestamp && c.Involved != null && c.Involved.Contains(invFaction)))
                        {
                            continue;
                        }
                        
                        if (ev.Summary.Contains("influence", StringComparison.OrdinalIgnoreCase))
                        {
                            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, invFaction, 
                                $"Faction '{invFaction}' recently expanded influence here. Update a local NPC's dialogue or create a rumor. Example:\n[ {{ \"$type\": \"event\", \"summary\": \"Reflected faction influence\", \"involved\": [\"{invFaction}\"] }} ]", 
                                "Faction:PresenceChange"));
                        }
                        else if (ev.Summary.Contains("Hostile") || ev.Summary.Contains("AtWar") || ev.Summary.Contains("war", StringComparison.OrdinalIgnoreCase))
                        {
                            pressures.Add(new WorldPressureItem(PressureSeverity.NarrativePrompt, invFaction, 
                                $"Faction '{invFaction}' is involved in recent hostilities. Consider updating a local NPC's reputation to reflect their stance. Example:\n[ {{ \"$type\": \"faction_reputation\", \"characterId\": \"chars/local\", \"factionId\": \"{invFaction}\", \"delta\": -20 }} ]", 
                                "Faction:Reputation"));
                        }
                    }
                }
            }

            var finalPressures = await _pressureManager.FilterAndCapAsync(session, effective, (int)time.TotalDaysElapsed, pressures);

            var suggestedExamples = new List<string>();
            var questPressureTriggered = finalPressures.Any(p => p.Contains("Quest", StringComparison.OrdinalIgnoreCase) || p.Contains("deadline", StringComparison.OrdinalIgnoreCase));
            if (questPressureTriggered && scene.ActiveQuests != null && scene.ActiveQuests.Any())
            {
                var q = scene.ActiveQuests.First();
                suggestedExamples.Add($"[ {{ \"$type\": \"quest_progress\", \"questId\": \"{q.QuestId}\", \"objectiveIndex\": 0, \"newState\": \"Complete\", \"narrativeNote\": \"We completed the objective.\" }} ]");
            }
            var stuckChar = scene.PresentNPCs?.FirstOrDefault(c => c.CurrentActivity != null && c.CurrentActivity.Contains("interrupted en route", StringComparison.OrdinalIgnoreCase));
            if (stuckChar != null && finalPressures.Any(p => p.Contains("Travel", StringComparison.OrdinalIgnoreCase) || p.Contains("stuck", StringComparison.OrdinalIgnoreCase) || p.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
            {
                suggestedExamples.Add($"[ {{ \"$type\": \"activity\", \"characterId\": \"{stuckChar.Id}\", \"newActivity\": \"Resolved the ambush and continued\", \"updateLocation\": false }}, {{ \"$type\": \"travel\", \"characterId\": \"{stuckChar.Id}\", \"destinationLocationId\": \"locations/actual-dest\", \"encounterRiskModifier\": -30 }} ]");
            }

            scene.SuggestedCommitExamples = suggestedExamples;

            return new ToolResult<SceneView>(true, scene, 
                $"Scene details for {locationId} (campaign: {effective}) retrieved.",
                WorldPressure: finalPressures.Length > 0 ? finalPressures : null);
        }, saveChanges: true);
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = false)]
    [Description(@"UNIVERSAL WRITE TOOL: ALWAYS call this at the end of combat, conversation, discovery, or any narrative beat to atomically mutate the world. 
Accepts a batch of changes (HP, Items, Events, Rumors, Relationships, Needs, Attributes, Activity, Status add/remove, ruleset_action, and the open-world creates/updates). 
Use ActivityChange liberally to keep get_scene in sync with your narrative. 

**When you see ENGINE WARNING or NARRATIVE PROMPT in any get_scene / get_world_state / advance_world response, your immediate follow-up should be a commit using the exact ready JSON example provided (the primary laziness mitigation).**

See the full `get_help` manual for Schrödinger's World patterns, the complete Lazy Tavern walkthrough, transient/keepAlive rules, auto-linking, and many more copy-paste examples.

Supported types for $type: hp, item, status, statusremove, event, rumor, relationship, need, attribute, mood, activity, ruleset_action, location_create, location_update, character_create, schedule_change, item_create.

=== RECOMMENDED PATTERNS (copy-paste friendly) ===

(See get_help for the full expanded list including the tavern creation + promotion flow, one-way link fixes, ambient/PoI flavor without bloat, etc.)

Basic + creating on the fly examples are also shown in the tool description and get_help.")]
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
            if (!result.Success)
            {
                var errorMsg = string.Join("\n", result.Summary);
                return new ToolResult<CommitResult>(false, result, Summary: "Commit failed due to validation errors.", Error: errorMsg);
            }
            await _repository.LogEventAsync(session, new Event { Id = "events/" + Guid.NewGuid(), CampaignName = effective, Summary = narrative, Category = EventCategory.SceneCommit, Involved = result.InvolvedEntities }, effective);
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

            var timeDoc = await _repository.GetTimeAsync(session, effective);
            
            string[]? cappedPressure = null;
            if (result.SimulatorEvents.Count > 0)
            {
                var rawPressures = result.SimulatorEvents
                    .Select(e => new WorldPressureItem(PressureSeverity.Simulation, "Simulation", e, "Simulation:Event"));
                cappedPressure = await _pressureManager.FilterAndCapAsync(session, effective, (int)timeDoc.TotalDaysElapsed, rawPressures);
            }

            return new ToolResult<AdvanceResult>(true, result, 
                $"Advanced {days} days. {result.SimulatorEvents.Count} simulation events triggered.",
                WorldPressure: cappedPressure != null && cappedPressure.Length > 0 ? cappedPressure : null);
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

            // Use repo query (now scoped) + client filter for involved.
            var npcEvents = (await _repository.QueryEventsAsync(session, null, null, 10, effective))
                .Where(e => e.Involved != null && e.Involved.Contains(characterId))
                .OrderByDescending(e => e.Timestamp)
                .Take(10)
                .ToList();

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
    [Description("DEEP DIVE TOOL: Returns the full Faction document (stances, influence, territory, leaders, metadata, DM notes) for a known faction ID. Use this (instead of guessing from get_scene summaries) when you need to roleplay faction reactions, declare war, expand territory, or check player rep impact. Campaign-scoped.")]
    public Task<ToolResult<Faction>> GetFactionContext(
        [Description("Exact faction ID e.g. 'factions/thieves-guild' (use fuzzy search or get_scene first if unsure).")] string factionId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var faction = await _repository.GetFactionAsync(session, factionId, effective);
            if (faction == null)
            {
                var suggestions = await _repository.SuggestFactionsAsync(session, factionId, effective);
                var hint = suggestions.Any() 
                    ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Name})"))
                    : "";
                return new ToolResult<Faction>(false, Error: "NotFound", Summary: $"Faction '{factionId}' not found.{hint} Use exact ID from get_scene or search.");
            }
            return new ToolResult<Faction>(true, faction, $"Full faction context for {faction.Name} (campaign: {effective}).");
        }, saveChanges: false);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description("DEEP DIVE TOOL: Returns the full Quest document (all objectives with states, deadlines, rewards, giver, related locations/factions, DM notes, urgency). Use when get_scene shows an ActiveQuestSummary and you need to advance/fail specific objectives or check stakes. Supports per-objective deadlines from Phase 7.3.")]
    public Task<ToolResult<Quest>> GetQuestDetails(
        [Description("Exact quest ID e.g. 'quests/rats_01'.")] string questId,
        [Description("Optional campaign name. Falls back to currently selected.")] string? campaignName = null)
    {
        var effective = EffectiveCampaign(campaignName);
        return ExecuteAsync(async session => {
            var quest = await _repository.GetQuestAsync(session, questId, effective);
            if (quest == null)
            {
                var suggestions = await _repository.SuggestQuestsAsync(session, questId, effective);
                var hint = suggestions.Any() ? " Did you mean: " + string.Join(", ", suggestions.Select(s => $"{s.Id} ({s.Title})")) : "";
                return new ToolResult<Quest>(false, Error: "NotFound", Summary: $"Quest '{questId}' not found.{hint}");
            }
            return new ToolResult<Quest>(true, quest, $"Quest details for '{quest.Title}' (campaign: {effective}).");
        }, saveChanges: false);
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
            await _repository.UpsertCharacterAsync(s, character, effective);
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
            await _repository.UpsertLocationAsync(s, location, effective);
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
            await _repository.UpsertLoreAsync(s, lore, effective);
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

**KEY PHILOSOPHY (Anti-LLM-Laziness / Schrödinger's World):** 95%+ of the world is ephemeral flavor that lives ONLY in your current narration/context. Only *meaningful* interactions (that will be referenced again, combat, theft, named recurring NPCs, discovered secret doors the party will use) should be anchored via `commit`. The engine owns linking, GC of transients, visit tracking, and nags you *immediately* on the next `get_scene` or `get_world_state` with **exact, copy-paste-ready JSON** when you (or prior LLM turns) were lazy/incomplete. Treat every string in `WorldPressure` that starts with `ENGINE WARNING:` or `NARRATIVE PROMPT:` as a **mandatory high-priority directive**. Paste the example JSON into your next `commit` call. This defeats the ""silly factor"" of being forced to output perfect polymorphic arrays for every tavern bard or crate.

## Core Gameplay Loop
1. **Start of Session**: Call `get_current_campaign` + `get_world_state` (with party location) to sync time, rumors, events, char distress, **and WorldPressure**.
2. **Exploration**: Call `get_scene` on entry. **Immediately action any ENGINE WARNING / NARRATIVE PROMPT in the WorldPressure** (use the exact JSON provided).
3. **Action & Consequence**: Narrate vividly to players. At end of beat (or when something should persist), call `commit` with array of changes. Use `activity` liberally to keep sim in sync.
4. **Time Skips / Travel**: `advance_world` (triggers needs, rumor decay, schedule eval, **TransientEvictionRule** for flavor NPCs).
5. **Deep NPC**: `get_npc_context` + `get_npc_needs`.

**Golden Rule:** If you just narrated something that should ""exist"" next time the party returns or is referenced, `commit` it (via create or update). If it's pure color, use PointsOfInterest + AmbientCrowd (lightweight, no docs created until you decide to promote).

## The Commit Tool (Universal Write)
ALWAYS call at end of combat/conversation/discovery. Atomic array of `$type` mutations. Rate limited + batch capped (50).

Supported `$type`s: `hp`, `item`, `status`, `statusremove`, `event`, `rumor`, `relationship`, `need`, `attribute`, `mood`, `activity`, `ruleset_action`, `location_create`, `location_update`, `character_create`, `schedule_change`, `item_create`.

**RECOMMENDED PATTERNS (copy-paste and adapt):**

Basic update + sync:
[
  { ""$type"": ""event"", ""category"": ""Narrative"", ""summary"": ""Party found the hidden stair."" },
  { ""$type"": ""activity"", ""characterId"": ""chars/guard1"", ""newLocationId"": ""locations/cellar"", ""newActivity"": ""Searching crates nervously"" }
]

**Creating on the fly (the laziness countermeasure - use these instead of pure narration for anything that might matter later):**
[
  { ""$type"": ""location_create"", ""locationId"": ""locations/tavern_cellar"", ""name"": ""Dank Cellar"", ""description"": ""Smells of damp earth..."", ""type"": ""Room"", ""connectedFromLocationId"": ""locations/tavern"", ""connectionDescription"": ""A wooden trapdoor leading down"", ""pointsOfInterest"": [""Suspicious crate"", ""Rat gnawing bone""], ""ambientCrowd"": ""2-3 rats and a drunk sleeping it off"" },
  { ""$type"": ""character_create"", ""characterId"": ""chars/cloaked_figure"", ""name"": ""Cloaked Figure"", ""currentLocationId"": ""locations/tavern_cellar"", ""currentActivity"": ""Watching the party"", ""keepAlive"": false, ""notes"": ""Offered a map for coin."" }
]

Later promote a transient (so it survives GC and participates in AdvanceWorld):
[
  { ""$type"": ""schedule_change"", ""characterId"": ""chars/cloaked_figure"", ""schedule"": { ""defaultLocationId"": ""locations/market_square"", ""routines"": [ { ""condition"": ""Any"", ""locationId"": ""locations/market_square"", ""activity"": ""Haggling"", ""probability"": 0.8 } ] } }
]

Item + transfer patterns, status with modifiers, ruleset_action (see below), etc.

**After you see a pressure in get_scene/get_world_state, your *next* action should usually be a `commit` using the exact snippet provided (adapted with real IDs/names).** Then narrate the outcome. The engine will clear the pressure on subsequent reads.

## Schrödinger's World + Transient / Open-World Patterns (Critical for Laziness Mitigation)
- **Flavor without bloat**: When narrating a crowded tavern, a bustling market, rats in a cellar, or ""a bard playing a lute in the corner"", **do not** immediately `character_create` 20 people. Instead:
  - On initial `location_create` or via `location_update`: populate `pointsOfInterest` (light list of strings returned in get_scene) and/or `ambientCrowd` (string hint, e.g. ""8-15 rough sailors and dockworkers"").
  - The engine will surface a `NARRATIVE PROMPT` in get_scene when the live scene is empty but ambient is expected: this is your cue to spawn 1-3 *interactable* transients via `character_create` if players engage, or just narrate using the hint.
- **Transients auto-GC**: Any character created (or moved via activity) with `schedule: null` AND `keepAlive: false` is transient. When the party leaves the area (get_scene on another loc + `advance_world` days later) and `LastVisitedDay` on the loc is old (>1 day), the `TransientEvictionRule` emits `ActivityChange` deltas that clear `CurrentLocationId`. The doc stays (cheap) for possible later promotion by ID or narrative callback. Use `keepAlive: true` for PCs, companions, or ""favorite"" flavor you want to keep without a full schedule.
- **Auto-Linking prevents soft-locks**: Always supply `connectedFromLocationId` + `connectionDescription` on `location_create`. Engine appends forward + reverse exits (and sets parent). If you forget, next get_scene on the child will give ENGINE WARNING + exact `location_update` JSON to add the missing exit.
- **Promotion path**: Use `schedule_change` (or supply schedule at `character_create` time) to make a transient permanent (it now runs in simulation, ignored by GC).
- **Dead-ends / broken maps**: get_scene will nag with ready `location_update` + `addExit`. Use it.
- **Hallucinated locations**: get_scene never throws for bad ID. Returns stub + strong ENGINE WARNING with ready `location_create` JSON (including connectedFrom suggestion). Paste it.

**Full ""Lazy LLM Tavern"" Walkthrough Example (copy this pattern):**
You (LLM): ""You push open the door to the Rusty Nail. The common room is full of sailors and dockworkers. A one-eyed bard in the corner is singing a shanty about lost ships while plucking a battered lute. The air smells of salt, sweat, and cheap ale. A toothless barman named Bram wipes a mug...""

(You used ambient flavor + PoIs implicitly via narration. No commit yet - correct for pure color.)

Later, party talks to the bard or barman engages:
- Call `get_scene ""locations/rusty-nail""` first (authoritative state).
- Suppose it returns empty PresentNPCs but AmbientCrowd hint (or prior you set none) + NARRATIVE PROMPT pressure: it will literally give you the JSON array.
- Then: `commit` the create for the interactable ones only:
  [
    { ""$type"": ""character_create"", ""characterId"": ""chars/bram-the-barkeep"", ""name"": ""Bram Ironarm"", ""currentLocationId"": ""locations/rusty-nail"", ""currentActivity"": ""Wiping mugs and watching the door"", ""notes"": ""Toothless, one good eye, ex-sailor. Knows harbor gossip."", ""psychology"": { ""wants"": [""quiet night"", ""coin""], ""fears"": [""trouble in his bar""] } },
    { ""$type"": ""character_create"", ""characterId"": ""chars/one-eyed-bard"", ... similar ... },
    { ""$type"": ""event"", ""category"": ""Discovery"", ""summary"": ""Party met Bram and the bard at the Rusty Nail."" }
  ] ""The party enters and interacts with the locals.""

- If later the bard becomes a quest giver recurring: `schedule_change` or add Schedule at birth + `keepAlive`.
- If they just drink and leave: no commit needed for the 12 unnamed sailors. Engine will GC any you did transiently create if area goes cold.

This is how you stay creative *and* keep the world model healthy without perfect JSON for every flavor element.

## Ruleset Actions (Combat & Skill Checks)
... (same as before, keep the examples)

## Status Effects & Stat Modifiers
... (same)

## Phase 7.4 Deep Dives & Suggested Commits
If a scene has `ActiveQuests` or `RelevantFactions`, you can explore them directly via:
- `get_quest_details`: Read the full Quest structure (all objectives, deadlines, rewards).
- `get_faction_context`: Get the full Faction summary, stances, territory, and influence.
Also, if `get_scene` or `get_world_state` returns `SuggestedCommitExamples` array, copy-paste one directly into your `commit` tool (examples frequently contain real IDs from the current state; replace any remaining placeholders like `locations/actual-dest` if needed) to easily resolve stuck characters or progress quests.

## World Pressure (Your Co-DM Nag System)
Pressures appear in **every** `get_world_state`, `get_scene`, and `advance_world` response (in the ToolResult.WorldPressure array, and also embedded in some views).

- `ENGINE WARNING`: Structural/integrity problem (hallucinated loc, no exits, broken link, etc.). **Paste the JSON and fix immediately.** These are the primary defense against laziness and broken worlds.
- `NARRATIVE PROMPT`: Opportunity / flavor cue (empty but ambient expected, no PoIs on a lively spot). Use to decide whether to persist something or just narrate using the hint.
- Simulation / character / rumor pressures: Aging unresolved, dying PCs/NPCs, desperate needs, etc. Many now include mini example commit snippets.

**Never ignore them.** The next `get_scene` after you fix will usually have fewer or none. If you keep seeing the same one, you skipped the commit.

Additional pressures come from `GetCharacterPressureAsync` (HP, bad statuses, high needs) surfaced via get_world_state, plus rule narratives turned into SimulatorEvents on advance.

## Other Tools & Patterns
- `get_npc_context` / `get_npc_needs`: Use before deep roleplay. Merge descriptors happen automatically.
- `search_world`, `recall_history`: For discovery without hallucinating duplicates.
- `define_need_descriptor` + `get_need_descriptors`: For custom needs vocabulary (wanderlust, debt_pressure, etc.).
- World-builder upserts: Fine for initial seeding / major PoIs. During play, prefer `commit` + the runtime creates.
- Combat: start_combat, next_turn, end_combat + ruleset_action inside commit. Statuses applied via commit survive and modify future rolls.

## Common Laziness Traps & How the Engine Helps
- Narrating a whole new dungeon level without creates -> next get_scene on a room ID: instant hallucination pressure + exact create JSON.
- Creating a cellar via create but forgetting the back exit -> pressure on entry.
- Spawning 40 named sailors for one scene -> bloat; use ambient + 1-2 creates only for interactables; GC cleans the rest.
- Forgetting to `activity` change after a scene -> get_scene shows stale locations/activities.
- Ignoring an aging ""Unresolved"" event for 10 days -> pressure in get_world_state with resolution hint.

Call `get_help` any time you (the LLM) are unsure. Re-read the pressures section often.

Remember: the engine is strict on invariants (map connectivity, no silent deletes of important state) so *you* can be creatively lazy about flavor.
";
        return Task.FromResult(new ToolResult<string>(true, manual, "Help manual retrieved."));
    }
}

