using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CampaignVault.Models;

namespace CampaignVault.Schema;

/// <summary>
/// Builds the tiered input schema for take_turn, replacing per-request generation with a pre-built,
/// $ref-based schema that separates hot-tier variants (full detail) from cold-tier (minimal info).
/// </summary>
internal static class TakeTurnSchemaBuilder
{
    public static JsonElement Build(JsonSerializerOptions options)
    {
        // For now, return a simplified version of the schema
        // In a full implementation, this would use JsonSchemaExporter to generate per-variant
        // schemas and build the tiered structure with $defs

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["changes"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["$ref"] = "#/$defs/worldChange"
                    },
                    ["description"] = "Array of world changes (mutations) to commit"
                },
                ["narrative"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Narrative summary of what happened"
                },
                ["autoRefreshInvolved"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = true,
                    ["description"] = "Auto-refresh entities touched by changes"
                },
                ["extraCharacterIds"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Additional NPC IDs to refresh"
                },
                ["extraLocationIds"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Additional location IDs to refresh"
                },
                ["includeParty"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Include full Party member summaries"
                },
                ["includeWorldState"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["default"] = false,
                    ["description"] = "Include WorldState in response"
                },
                ["partyLocationId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Location ID for WorldState scoping"
                }
            },
            ["required"] = new JsonArray("changes", "narrative"),
            ["$defs"] = BuildDefs(options)
        };

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonObject BuildDefs(JsonSerializerOptions options)
    {
        var defs = new JsonObject();

        // Build the worldChange anyOf with all variants
        // Hot-tier variants get full schema, cold-tier get minimal
        var anyOf = new JsonArray();
        var variants = CommitSchemaModel.Variants;

        foreach (var variant in variants.OrderBy(v => v.Discriminator))
        {
            var ref_ = new JsonObject
            {
                ["$ref"] = $"#/$defs/{variant.Discriminator}"
            };
            anyOf.Add(ref_);

            // Build minimal def for each variant
            defs[variant.Discriminator] = BuildVariantDef(variant);
        }

        defs["worldChange"] = new JsonObject
        {
            ["anyOf"] = anyOf,
            ["discriminator"] = new JsonObject
            {
                ["propertyName"] = "$type"
            }
        };

        // Shared definitions for frequently repeated structures
        defs["minutesElapsed"] = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = "Minutes of in-game time this beat took"
        };

        defs["systemExtension"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Ruleset-specific system extension",
            ["oneOf"] = new JsonArray(
                new JsonObject { ["$ref"] = "#/$defs/dnd5eExtension" },
                new JsonObject { ["$ref"] = "#/$defs/pf2eExtension" }
            )
        };

        return defs;
    }

    private static JsonObject BuildVariantDef(CommitVariantModel variant)
    {
        var def = new JsonObject
        {
            ["type"] = "object",
            ["description"] = TruncateDescription(variant.Summary, 60),
            ["properties"] = new JsonObject()
        };

        // Add $type discriminator
        var properties = (JsonObject)def["properties"]!;
        properties["$type"] = new JsonObject
        {
            ["const"] = variant.Discriminator,
            ["type"] = "string"
        };

        // Add fields (abbreviated for cold-tier)
        foreach (var field in variant.Fields)
        {
            if (field.JsonName == "minutesElapsed")
            {
                properties[field.JsonName] = new JsonObject { ["$ref"] = "#/$defs/minutesElapsed" };
            }
            else
            {
                properties[field.JsonName] = new JsonObject
                {
                    ["type"] = GetJsonType(field.ClrType),
                    ["description"] = TruncateDescription(field.Description, 50)
                };
            }
        }

        // Set required fields
        var required = new JsonArray();
        required.Add("$type");
        foreach (var field in variant.Fields.Where(f => f.IsRequired))
        {
            required.Add(field.JsonName);
        }
        def["required"] = required;

        return def;
    }

    private static string GetJsonType(Type clrType)
    {
        if (clrType == typeof(string)) return "string";
        if (clrType == typeof(int) || clrType == typeof(long)) return "integer";
        if (clrType == typeof(bool)) return "boolean";
        if (clrType == typeof(decimal) || clrType == typeof(double) || clrType == typeof(float)) return "number";
        if (clrType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(clrType)) return "array";
        return "object";
    }

    private static string? TruncateDescription(string? desc, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(desc)) return null;
        if (desc.Length <= maxLength) return desc;

        var truncated = desc.Substring(0, maxLength);
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > 0) truncated = truncated.Substring(0, lastSpace);
        return truncated + "…";
    }
}
