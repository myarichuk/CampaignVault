using System.Text;

namespace CampaignVault.Tools;

/// <summary>
/// Generates contextual seeding guidance when entity lookups return null.
/// Helps the LLM understand how to seed missing locations, NPCs, items, etc.
/// Integrated into tool responses as hints, never blocks the response.
/// </summary>
internal static class EntitySeedingAdvisor
{
    public static string? GenerateSuggestion(string? entityId, string? campaignName = null)
    {
        if (string.IsNullOrEmpty(entityId))
        {
            return null;
        }

        var (entityType, entityName) = ParseEntityId(entityId);
        if (entityType == null)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"\n💡 **Entity Not Found:** `{entityId}`");
        sb.AppendLine();
        sb.AppendLine($"This {entityType} doesn't exist yet. If the party wants to go there, interact with it, or it's important to the narrative, consider seeding it:");
        sb.AppendLine();

        switch (entityType)
        {
            case "location":
                sb.AppendLine("**To seed a location:**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `name`, `description`, `type` (Building/District/etc.)");
                sb.AppendLine("   - `parentLocationId` (the parent region/settlement/district)");
                sb.AppendLine("   - `dangerModifier` (adjust from default 0)");
                sb.AppendLine("   - `pointsOfInterest` (2–4 named POIs, e.g., Altar, Herb Screen)");
                sb.AppendLine("   - `pointOfInterestDetails` (descriptions for each POI)");
                sb.AppendLine("2. Use `take_turn` → `activity` to move the party there");
                sb.AppendLine("3. Use `get_entity(locationId, partyPresent:true)` to load the scene for narration");
                sb.AppendLine();
                sb.AppendLine("**References:** dnd-exploration skill (lazy-seeding), SACRED RULES rule 4 (Location Hierarchy & Lazy Seeding)");
                break;

            case "character":
                sb.AppendLine("**To seed an NPC:**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `name`, `currentAppearance`, `distinctiveFeatures`");
                sb.AppendLine("   - `social` profile (Role, Trust, Suspicion, Loyalty, Fear)");
                sb.AppendLine("   - `psychology` profile (motivation, ideology, pride, quirks)");
                sb.AppendLine("   - `needs` (Hunger, Thirst, Fatigue, etc.)");
                sb.AppendLine("2. If permanent, seed a `plotThread` for them (companion arc, personal stakes)");
                sb.AppendLine("3. Use `get_entity(charId)` to load their full profile before roleplaying");
                sb.AppendLine();
                sb.AppendLine("**References:** NPC PROMOTION & LITTLE STORIES, dnd-npc-interaction skill");
                break;

            case "item":
                sb.AppendLine("**To seed an item:**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `name`, `description`, `rarity` (Common/Uncommon/etc.)");
                sb.AppendLine("   - `equipZones`, `capacity` (if container or wearable)");
                sb.AppendLine("   - `heldById` (who owns it, or omit if loose)");
                sb.AppendLine("2. Link it to a clue in a `plotThread` if it's narrative-critical");
                sb.AppendLine("3. Use `get_entity(itemId)` to fetch it before interacting");
                sb.AppendLine();
                sb.AppendLine("**References:** SACRED RULES rule 4 (Mutations), dnd-world-change skill");
                break;

            case "quest":
                sb.AppendLine("**To seed a quest:**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `title`, `description`, `giverNpcId` (who offers it)");
                sb.AppendLine("   - `objectives` (steps to complete)");
                sb.AppendLine("   - `rewards` (XP, items, faction favor)");
                sb.AppendLine("   - `state` (Open/InProgress/Complete)");
                sb.AppendLine("2. Use `take_turn` → `quest_progress` to evolve it during play");
                sb.AppendLine("3. Use `get_entity(questId)` to check current state and objectives");
                sb.AppendLine();
                sb.AppendLine("**References:** dnd-campaign-events skill, SACRED RULES rule 4");
                break;

            case "faction":
                sb.AppendLine("**To seed a faction:**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `name`, `description`, `type` (Noble House/Guild/etc.)");
                sb.AppendLine("   - `influence` (regions/settlements where they operate)");
                sb.AppendLine("   - `factionLeaderId` (chief NPC)");
                sb.AppendLine("   - Initial relationships with other factions");
                sb.AppendLine("2. Use `take_turn` → `faction_state` to track stance/tension changes");
                sb.AppendLine("3. Use `get_entity(factionId)` to load their current posture");
                sb.AppendLine();
                sb.AppendLine("**References:** dnd-campaign-events skill, world pressure system");
                break;

            case "plot-thread":
                sb.AppendLine("**To seed a plot thread (narrative scaffolding):**");
                sb.AppendLine("1. Use `world_build` to create it with:");
                sb.AppendLine("   - `title`, `state` (Dormant/Active/Climax)");
                sb.AppendLine("   - `foreshadowingHooks` (2–4 narratable teasers)");
                sb.AppendLine("   - `clues` (2–4 discoverable entries with ids/descriptions)");
                sb.AppendLine("   - `resolutionCondition` (testable end state)");
                sb.AppendLine("   - `involvedEntityIds` (NPCs/factions/locations)");
                sb.AppendLine("2. Surface clues as the party explores; check for ENGINE WARNING (missing clue entities)");
                sb.AppendLine("3. Use `get_entity(plot-threadId)` to review state and clues");
                sb.AppendLine();
                sb.AppendLine("**References:** SACRED RULES rule 4 (Plot Threads), CLUE VALIDATION & LAZY ENTITY SEEDING, dnd-exploration skill");
                break;

            default:
                sb.AppendLine($"**To seed a {entityType}:**");
                sb.AppendLine("1. Use `world_build` with the entity batch and required fields");
                sb.AppendLine("2. Reference SACRED RULES rule 4 (Mutations) for structure");
                sb.AppendLine("3. Use `get_entity(id)` after seeding to load and interact");
                sb.AppendLine();
                sb.AppendLine("**References:** get_help topic=tools, dnd-world-change skill, recommended-system-prompt.md");
                break;
        }

        sb.AppendLine();
        sb.AppendLine("**When NOT to seed:** If the party doesn't care or it's flavor-only, just narrate it—no persistence needed (Schrödinger's World). Seed only when the party wants to interact, or plot demands it.");

        return sb.ToString();
    }

    private static (string? Type, string? Name) ParseEntityId(string entityId)
    {
        // Format: "type/name" (e.g., "locations/temple-of-mercy", "chars/arlen", "quests/slay-goblins")
        var parts = entityId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (null, null);
        }

        var type = parts[0] switch
        {
            "locations" or "locs" => "location",
            "chars" or "characters" => "character",
            "items" or "item" => "item",
            "quests" => "quest",
            "factions" => "faction",
            "rumors" => "rumor",
            "plot-threads" or "plotthreads" => "plot-thread",
            "custom-spells" or "customspells" => "custom spell",
            "custom-creatures" or "customcreatures" => "custom creature",
            "custom-feats" or "customfeats" => "custom feat",
            "lore" => "lore",
            _ => null
        };

        return (type, parts.Length > 1 ? string.Join("/", parts.Skip(1)) : null);
    }

    /// <summary>
    /// Generates a nudge for the LLM to consider seeding world events after creating a new faction.
    /// Guides toward scripted, disruptable consequences (patrols, raids, operations) rather than
    /// improvisation turn-by-turn. Follows the same text-hint pattern as other seeding nudges.
    /// </summary>
    public static string? GenerateWorldEventSeedingHint(Models.Faction faction, string? campaignName = null)
    {
        if (faction == null || string.IsNullOrEmpty(faction.Id))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"\n💡 **Consider seeding world events for {faction.Name}:**");
        sb.AppendLine();
        sb.AppendLine($"Now that `{faction.Name}` exists, you can script their activities and goals as `world_event` entries. This persists their state across days and lets the party disrupt them with consequences:");
        sb.AppendLine();
        sb.AppendLine("**Use `upsert_world_event` to seed 2–3 events showing their goals/activities:**");
        sb.AppendLine();
        sb.AppendLine("- **Recurring operations (TimeBased):** patrols, smuggling runs, resource collection every N days. Stay `Pending` forever; fire on interval. Example: 'nightly tavern recruitment'.");
        sb.AppendLine($"  - Set `triggerType: TimeBased`, `intervalDays: N`, `actorId: {faction.Id}`");
        sb.AppendLine();
        sb.AppendLine("- **Deadline-driven plans (Scheduled):** a raid, theft, or diplomatic move on day X. Transition `Pending → Triggered` when fired, and `Triggered → Resolved` when the DM confirms the outcome.");
        sb.AppendLine($"  - Set `triggerType: Scheduled`, `targetDay: X`, `actorId: {faction.Id}`");
        sb.AppendLine();
        sb.AppendLine("- **Disruptable consequences:** Add a `preventionCondition` (e.g., 'PlotThreadStateIs: defeated_leader') so the party's victories auto-prevent cascading events. Example: 'If the party kills the warlord, cancel the siege.'");
        sb.AppendLine("  - Set `preventionCondition` with a matching entity state or faction influence threshold");
        sb.AppendLine();
        sb.AppendLine("- **Effects:** Each event emits `effects` (rumors, faction stance changes, discoveries) when it fires. Shape the world reactively without re-planning every turn.");
        sb.AppendLine("  - Include `RumorCreate`, `FactionStateChange`, `EventOccurred`, or `KnowledgeUpdate` effects");
        sb.AppendLine();
        sb.AppendLine($"**Example:** `upsert_world_event {{ id: 'world-events/{faction.Name.ToLower()}-raid', title: '{faction.Name} raids the village', actorId: '{faction.Id}', triggerType: Scheduled, targetDay: 10, effects: [{{ kind: RumorCreate, subject: '...', text: '...' }}, {{ kind: EventOccurred, text: '...' }}] }}`");
        sb.AppendLine();
        sb.AppendLine("**When to seed events:**");
        sb.AppendLine("- Major factions with clear goals (warlords, merchant consortiums, religious orders) → always seed events");
        sb.AppendLine("- Flavor factions with passive roles (background NPCs, tavern crowds) → skip events unless a specific plot needs them");
        sb.AppendLine("- Newly awakened threats (an enemy resurfaces, a calamity brewing) → seed events immediately to persist the threat");
        sb.AppendLine();
        sb.AppendLine("**References:** SACRED RULES rule 4 (World Events & Disruptable Consequences), world_build docs");

        return sb.ToString();
    }
}
