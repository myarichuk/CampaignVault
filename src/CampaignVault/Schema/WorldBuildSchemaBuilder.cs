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
        // Per-array descriptions are left out: dispatch order is stated once in the tool description.
        static JsonObject Item(string def, string? description) => description is null
            ? new() { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = $"#/$defs/{def}" } }
            : new() { ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = $"#/$defs/{def}" }, ["description"] = description };

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
            ["locations"] = Item("location", null),
            ["factions"] = Item("faction", null),
            ["creatures"] = Item("creature", null),
            ["spells"] = Item("spell", null),
            ["feats"] = Item("feat", null),
            ["characters"] = Item("character", null),
            ["items"] = Item("item", null),
            ["quests"] = Item("quest", null),
            ["plotThreads"] = Item("plotThread", null),
            ["worldEvents"] = Item("worldEvent", null),
            ["lore"] = Item("lore", null),
            ["rumors"] = Item("rumor", null),
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
                    ["description"] = "Entities to create/update, grouped by kind. Every array is optional."
                },
                ["campaignName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Campaign slug."
                }
            },
            ["required"] = new JsonArray("batch", "campaignName")
        };

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }
}
