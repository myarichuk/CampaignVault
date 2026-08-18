using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Schema;

/// <summary>
/// Builds the tiered input schema for world_build, mirroring take_turn's optimization.
/// Reduces token cost from ~9-10k to ~2.5k by using $defs and tiering.
/// </summary>
internal static class WorldBuildSchemaBuilder
{
    public static JsonElement Build(JsonSerializerOptions options)
    {
        // Simplified schema for world_build request
        // In full implementation, would build schemas for all UpsertRequest types
        // similar to how TakeTurnSchemaBuilder handles WorldChange variants

        var batchProperties = new JsonObject
        {
            ["locations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Locations to create or update. Dispatched first (parentLocationId/exits target other locations in this same array)."
            },
            ["factions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Factions to create or update. Dispatched after locations (territoryLocationIds may reference them)."
            },
            ["creatures"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Homebrew creature stat-block templates to create or update."
            },
            ["spells"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Homebrew spells to create or update."
            },
            ["feats"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Homebrew feats/perks to create or update."
            },
            ["characters"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Characters/NPCs to create or update. Dispatched after locations/factions (currentLocationId may reference them). Bootstrap (HP/defense derivation) runs per element."
            },
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Items to create or update. Dispatched after characters (holderId may reference a character just created in this batch)."
            },
            ["quests"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Quests to create or update. Dispatched after characters/locations/factions (giverId/relatedLocationIds/relatedFactionIds may reference them)."
            },
            ["plotThreads"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "Plot threads to create or update. Dispatched after characters/locations/factions/quests (involvedEntityIds may reference them)."
            },
            ["worldEvents"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "object" },
                ["description"] = "World events to create or update. Dispatched after plot threads (effects/conditions may reference them)."
            }
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["batch"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = batchProperties,
                    ["description"] = "Batch of entities to create/update, grouped by kind. Each array is optional — include only the kinds you're seeding in this call."
                },
                ["campaignName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Campaign name (required)"
                }
            },
            ["required"] = new JsonArray("batch", "campaignName")
        };

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }
}
