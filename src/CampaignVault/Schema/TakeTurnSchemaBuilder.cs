using System.Text.Json;
using System.Text.Json.Nodes;

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

        var requestProperties = new JsonObject
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
                ["description"] = "Location ID for WorldState scoping and the capped NPC initiative/memory candidate pool"
            },
            ["fullDetailCharacterId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "NPC ID to fetch in full detail instead of summary. Use sparingly; only one full detail per call."
            },
            ["fullDetailLocationId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Location ID to fetch in full detail instead of summary. Use sparingly; only one full detail per call."
            },
            ["forceFullReseed"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Force a full-detail response (Party/WorldState instead of PartyDelta/WorldStateDelta) and reset the reseed counter. Use after your own context was compacted/summarized."
            }
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["request"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = requestProperties,
                    ["dependentRequired"] = new JsonObject
                    {
                        ["changes"] = new JsonArray("narrative")
                    }
                },
                ["campaignName"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Campaign name (required)"
                }
            },
            ["required"] = new JsonArray("request", "campaignName"),
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

        return defs;
    }

    private static JsonObject BuildVariantDef(CommitVariantModel variant)
    {
        var summary = TruncateDescription(variant.Summary, 60);
        if (!variant.IsHotTier)
        {
            summary = $"{summary} (field details: get_commit_schema type='{variant.Discriminator}')";
        }

        var def = new JsonObject
        {
            ["type"] = "object",
            ["description"] = summary,
            ["properties"] = new JsonObject()
        };

        // Add $type discriminator
        var properties = (JsonObject)def["properties"]!;
        properties["$type"] = new JsonObject
        {
            ["const"] = variant.Discriminator,
            ["type"] = "string"
        };

        // Hot-tier variants (used on nearly every turn) get full per-field descriptions inline.
        // Cold-tier variants keep field names/types (needed to construct a valid payload) but drop
        // descriptions — get_commit_schema already exists as an on-demand lookup for rarely-used
        // types, so this text is a recurring tools/list cost for guidance that's rarely read.
        foreach (var field in variant.Fields)
        {
            if (field.JsonName == "minutesElapsed")
            {
                properties[field.JsonName] = new JsonObject { ["$ref"] = "#/$defs/minutesElapsed" };
            }
            else if (field.JsonName == "systemStats")
            {
                properties[field.JsonName] = variant.IsHotTier
                    ? new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "Ruleset-specific combat stats. Optional \"$system\": \"dnd5e\"|\"pf2e\" discriminator " +
                            "(omit for system-agnostic fields only). Common bootstrap keys: armorClass, strength, dexterity, " +
                            "constitution, intelligence, wisdom, charisma, hitDie, level, classLevels."
                    }
                    : new JsonObject { ["type"] = "object" };
            }
            else
            {
                properties[field.JsonName] = variant.IsHotTier
                    ? new JsonObject
                    {
                        ["type"] = GetJsonType(field.ClrType),
                        ["description"] = TruncateDescription(field.Description, 50)
                    }
                    : new JsonObject { ["type"] = GetJsonType(field.ClrType) };
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
