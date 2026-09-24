using System.Text.Json;
using System.Text.Json.Nodes;

namespace CampaignVault.Schema;

/// <summary>
/// Builds the tiered input schema for take_turn, replacing per-request generation with a pre-built,
/// $ref-based schema that separates hot-tier variants (full detail) from cold-tier (minimal info).
/// </summary>
internal static class TakeTurnSchemaBuilder
{
    internal const string StubChangesDescription =
        "World changes to commit. Every item needs '$type'. Verbs and their fields are NOT listed here — consult your " +
        "CampaignVault skills. If unsure or a change fails, call get_commit_schema (no args = index of $types; type='<one $type>' = its fields). " +
        "Send sparse objects: $type plus only the fields you mean, no nulls.";

    public static JsonElement Build(JsonSerializerOptions options, ToolSchemaMode mode = ToolSchemaMode.Full)
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
                ["description"] = "One-sentence summary, stored as the event log entry (truncated at ~500 chars)."
            },
            ["minutesElapsed"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Batch duration in minutes (ignored for rest/travel)."
            },
            ["narrativeImportance"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("Trivial", "Important", "Core"),
                ["description"] = "Default Important; Trivial for banter."
            },
            ["autoRefreshInvolved"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = true,
                ["description"] = "Refresh entities touched by changes."
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
                ["description"] = "Full PC summaries. Only when HP/slots/gold/needs/AC/gear changed."
            },
            ["includeWorldState"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Include WorldState. Expensive; only when pressure matters."
            },
            ["partyLocationId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "PC location after this beat."
            },
            ["fullDetailCharacterId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One NPC in full detail."
            },
            ["memoriesOnlyCharacterId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One NPC's memories only (cheaper than fullDetailCharacterId)."
            },
            ["fullDetailLocationId"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "One location in full detail (use on the travel turn instead of get_entity)."
            },
            ["forceFullReseed"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Full Party/WorldState instead of deltas. Only after your context was compacted."
            },
            ["leanMode"] = new JsonObject
            {
                ["type"] = "boolean",
                ["default"] = false,
                ["description"] = "Delta mode: cap NPC needs at the top 2 movers."
            },
            ["clientPartyFingerprint"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Last response's partyFingerprint, unchanged. Omit if none."
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
        };

        if (mode == ToolSchemaMode.Stub)
        {
            // Constant on purpose: no verb list, so a client-side cached copy can never go stale.
            requestProperties["changes"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["$type"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("$type"),
                    ["additionalProperties"] = true
                },
                ["description"] = StubChangesDescription
            };
        }
        else
        {
            root["$defs"] = BuildDefs(options);
        }

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static JsonObject BuildDefs(JsonSerializerOptions options)
    {
        var defs = new JsonObject();

        // Build the worldChange anyOf with all variants
        // Hot-tier variants get full schema, cold-tier get minimal
        var anyOf = new JsonArray();
        // Same visibility as the get_commit_schema index: engine-only verbs are never authored by the
        // model, and mode-scoped plugin verbs are looked up on demand once their mode is enabled.
        var variants = CommitSchemaModel.Variants.Where(v => !v.IsEngineOnly && v.ModeId is null);

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
            if (field.JsonName == "systemStats")
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
            else if (field.RequiredHint != null)
            {
                // Conditionally-required fields can fail validation and roll back the whole batch even
                // though they're optional in the schema, so this hint always survives — regardless of
                // tier and without the truncation limit applied to ordinary descriptions.
                var prop = new JsonObject
                {
                    ["type"] = GetJsonType(field.ClrType),
                    ["description"] = field.RequiredHint
                };
                AddEnumValues(prop, field.EnumValues);
                properties[field.JsonName] = prop;
            }
            else
            {
                var prop = variant.IsHotTier
                    ? new JsonObject
                    {
                        ["type"] = GetJsonType(field.ClrType),
                        ["description"] = TruncateDescription(field.Description, 50)
                    }
                    : new JsonObject { ["type"] = GetJsonType(field.ClrType) };
                AddEnumValues(prop, field.EnumValues);
                properties[field.JsonName] = prop;
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
        // Almost every WorldChange field is a nullable value type (int?, bool?, SomeEnum?) — every one of
        // those is a distinct Nullable<T> CLR type, so without unwrapping it here none of the checks below
        // ever matched and every nullable scalar/enum field fell through to "object".
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "number";
        if (type.IsEnum) return "string";

        // Check if it's a Dictionary before checking for IEnumerable (Dictionary implements IEnumerable)
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return "object";

        if (type.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(type)) return "array";
        return "object";
    }

    private static void AddEnumValues(JsonObject prop, IReadOnlyList<string>? enumValues)
    {
        if (enumValues == null || enumValues.Count == 0)
        {
            return;
        }

        var array = new JsonArray();
        foreach (var value in enumValues)
        {
            array.Add(value);
        }

        prop["enum"] = array;
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
