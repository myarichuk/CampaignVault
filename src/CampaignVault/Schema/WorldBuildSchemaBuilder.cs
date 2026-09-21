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
        static JsonObject Item(string def, string description) => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["$ref"] = $"#/$defs/{def}" },
            ["description"] = description
        };

        static JsonObject Obj(params (string Name, string Type)[] fields)
        {
            var properties = new JsonObject();
            foreach (var (name, type) in fields)
            {
                properties[name] = new JsonObject { ["type"] = type };
            }

            return new JsonObject { ["type"] = "object", ["properties"] = properties };
        }

        var defs = new JsonObject
        {
            ["location"] = Obj(("id", "string"), ("name", "string"), ("type", "string"), ("parentLocationId", "string"), ("exits", "array")),
            ["faction"] = Obj(("id", "string"), ("name", "string")),
            ["character"] = Obj(("id", "string"), ("name", "string"), ("systemStats", "object")),
            ["item"] = Obj(("id", "string"), ("name", "string"), ("holderId", "string")),
            ["quest"] = Obj(("id", "string"), ("title", "string")),
            ["plotThread"] = Obj(("id", "string"), ("name", "string"), ("clues", "array"), ("foreshadowingHooks", "array")),
            ["creature"] = Obj(("id", "string"), ("name", "string")),
            ["spell"] = Obj(("id", "string"), ("name", "string")),
            ["feat"] = Obj(("id", "string"), ("name", "string")),
            ["worldEvent"] = Obj(("id", "string"), ("name", "string")),
            ["lore"] = Obj(("id", "string"), ("title", "string")),
            ["rumor"] = Obj(("id", "string"), ("subject", "string")),
            ["needDescriptor"] = new JsonObject { ["type"] = "string" },
        };

        var batchProperties = new JsonObject
        {
            ["locations"] = Item("location", "Locations to create or update. Dispatched first (parentLocationId/exits target other locations in this same array)."),
            ["factions"] = Item("faction", "Factions to create or update. Dispatched after locations (territoryLocationIds may reference them)."),
            ["creatures"] = Item("creature", "Homebrew creature stat-block templates to create or update."),
            ["spells"] = Item("spell", "Homebrew spells to create or update."),
            ["feats"] = Item("feat", "Homebrew feats/perks to create or update."),
            ["characters"] = Item("character", "Characters/NPCs to create or update. Dispatched after locations/factions (currentLocationId may reference them). Bootstrap (HP/defense derivation) runs per element."),
            ["items"] = Item("item", "Items to create or update. Dispatched after characters (holderId may reference a character just created in this batch)."),
            ["quests"] = Item("quest", "Quests to create or update. Dispatched after characters/locations/factions (giverId/relatedLocationIds/relatedFactionIds may reference them)."),
            ["plotThreads"] = Item("plotThread", "Plot threads to create or update. Dispatched after characters/locations/factions/quests (involvedEntityIds may reference them)."),
            ["worldEvents"] = Item("worldEvent", "World events to create or update. Dispatched after plot threads (effects/conditions may reference them)."),
            ["lore"] = Item("lore", "Lore entries to create or update."),
            ["rumors"] = Item("rumor", "Rumors to create or update."),
            ["needDescriptors"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject { ["$ref"] = "#/$defs/needDescriptor" },
                ["description"] = "Need name → descriptor text."
            }
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["$defs"] = defs,
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
