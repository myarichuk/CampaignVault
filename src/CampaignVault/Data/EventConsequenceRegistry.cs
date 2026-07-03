using System.Text.Json.Nodes;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// Suggest-only event consequence templates (5a). Does not auto-apply deltas.
/// </summary>
public static class EventConsequenceRegistry
{
    public const string CombatLocationDamageTemplateId = "combat-location-damage:v1";
    public const string DiscoveryLocationStateTemplateId = "discovery-location-state:v1";
    public const string BetrayalRelationshipTemplateId = "betrayal-rel:v1";

    public const string EventConsequenceGroupingKey = "Event:Consequence";

    public static bool TrySuggest(Event evt, out string templateId, out string suggestedCommitJson)
    {
        templateId = string.Empty;
        suggestedCommitJson = string.Empty;

        if (TryCombatLocationDamage(evt, out var combatJson))
        {
            templateId = CombatLocationDamageTemplateId;
            suggestedCommitJson = combatJson;
            return true;
        }

        if (TryDiscoveryLocationState(evt, out var discoveryJson))
        {
            templateId = DiscoveryLocationStateTemplateId;
            suggestedCommitJson = discoveryJson;
            return true;
        }

        if (TryBetrayalRelationship(evt, out var betrayalJson))
        {
            templateId = BetrayalRelationshipTemplateId;
            suggestedCommitJson = betrayalJson;
            return true;
        }

        return false;
    }

    private static bool TryCombatLocationDamage(Event evt, out string json)
    {
        json = string.Empty;
        if (evt.Category != EventCategory.Combat || !IsLocationRelated(evt.RelatedEntityId))
        {
            return false;
        }

        json = BuildLocationUpdateJson(
            evt.RelatedEntityId!,
            "Signs of recent combat — scorched earth, scattered debris, lingering smoke",
            ["scorched", "battle-scarred"]);
        return true;
    }

    private static bool TryDiscoveryLocationState(Event evt, out string json)
    {
        json = string.Empty;
        if (evt.Category != EventCategory.Discovery || !IsLocationRelated(evt.RelatedEntityId))
        {
            return false;
        }

        json = BuildLocationUpdateJson(
            evt.RelatedEntityId!,
            "Area recently explored — disturbed terrain, fresh tracks, overturned stones",
            ["recently-explored"]);
        return true;
    }

    private static bool TryBetrayalRelationship(Event evt, out string json)
    {
        json = string.Empty;
        if (evt.Category != EventCategory.Betrayal || evt.Involved == null || evt.Involved.Count < 2)
        {
            return false;
        }

        var actor = evt.Involved[0];
        var target = evt.Involved[1];
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["$type"] = "relationship",
                ["characterId"] = target,
                ["targetId"] = actor,
                ["delta"] = -15
            },
            new JsonObject
            {
                ["$type"] = "relationship",
                ["characterId"] = actor,
                ["targetId"] = target,
                ["delta"] = -15
            }
        };
        json = arr.ToJsonString();
        return true;
    }

    private static bool IsLocationRelated(string? relatedEntityId) =>
        !string.IsNullOrWhiteSpace(relatedEntityId)
        && relatedEntityId.StartsWith("locations/", StringComparison.OrdinalIgnoreCase);

    private static string BuildLocationUpdateJson(string locationId, string newState, IReadOnlyList<string> tags)
    {
        var arr = new JsonArray
        {
            new JsonObject
            {
                ["$type"] = "location_update",
                ["locationId"] = locationId,
                ["newState"] = newState,
                ["tagsToAdd"] = new JsonArray(tags.Select(t => JsonValue.Create(t)).ToArray())
            }
        };
        return arr.ToJsonString();
    }
}