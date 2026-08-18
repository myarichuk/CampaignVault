using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Schema;

/// <summary>
/// Strips "description" text from OutputSchema subtrees reached via a "systemStats" property.
/// SystemExtension's field descriptions (race/background template names, feat catalogs, spell-DC
/// derivation formulas) exist to guide the model when WRITING systemStats on a character_update or
/// character_create commit. They carry no value when the same CLR type shows up read-only in a
/// response (NPC/party summaries) — there they're pure token cost with no accuracy benefit, since the
/// model isn't constructing anything from an OutputSchema. Input-side systemStats guidance (in
/// TakeTurnSchemaBuilder) is untouched by this.
/// </summary>
internal static class OutputSchemaTrimmer
{
    public static JsonElement StripSystemStatsDescriptions(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Walk(node, insideSystemStats: false);

        using var doc = JsonDocument.Parse(node!.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static void Walk(JsonNode? node, bool insideSystemStats)
    {
        switch (node)
        {
            case JsonObject obj:
                if (insideSystemStats)
                {
                    obj.Remove("description");
                }

                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    Walk(obj[key], insideSystemStats || key == "systemStats");
                }

                break;

            case JsonArray arr:
                foreach (var item in arr)
                {
                    Walk(item, insideSystemStats);
                }

                break;
        }
    }
}
