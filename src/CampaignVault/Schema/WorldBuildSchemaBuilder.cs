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
        static JsonObject Item(string def) => new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["$ref"] = $"#/$defs/{def}" }
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
            ["location"] = Obj(("id", "string"), ("name", "string"), ("type", "string"), ("parentLocationId", "string")),
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
            ["locations"] = Item("location"),
            ["factions"] = Item("faction"),
            ["creatures"] = Item("creature"),
            ["spells"] = Item("spell"),
            ["feats"] = Item("feat"),
            ["characters"] = Item("character"),
            ["items"] = Item("item"),
            ["quests"] = Item("quest"),
            ["plotThreads"] = Item("plotThread"),
            ["worldEvents"] = Item("worldEvent"),
            ["lore"] = Item("lore"),
            ["rumors"] = Item("rumor"),
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
                    ["description"] = "Batch of entities to create/update, grouped by kind. Each array is optional."
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
